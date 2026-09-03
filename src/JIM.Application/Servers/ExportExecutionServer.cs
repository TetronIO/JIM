// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Diagnostics;
using JIM.Application.Services;
using JIM.Application.Staging;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Executes Pending Exports by calling connector export methods.
/// Implements Q5 (preview mode) and Q6 (retry with backoff) decisions.
///
/// Parallelism (Q8 decision): When MaxParallelism > 1, batches are processed concurrently.
/// Each parallel batch gets its own DbContext (EF Core is not thread-safe) and connector
/// instance. Progress reporting is serialised via SemaphoreSlim to protect the caller's
/// shared DbContext. MaxParallelism defaults to 1 (sequential) for safety.
/// See OUTBOUND_SYNC_DESIGN.md and EXPORT_PERFORMANCE_OPTIMISATION.md for details.
/// </summary>
public class ExportExecutionServer
{
    /// <summary>
    /// Default batch size for processing exports. Can be overridden per call.
    /// </summary>
    public const int DefaultBatchSize = 100;

    private JimApplication Application { get; }
    private ISyncRepository SyncRepo { get; }

    internal ExportExecutionServer(JimApplication application, ISyncRepository syncRepo)
    {
        Application = application;
        SyncRepo = syncRepo;
    }

    /// <summary>
    /// Executes all Pending Exports for a Connected System.
    /// </summary>
    /// <param name="connectedSystem">The Connected System to export to</param>
    /// <param name="connector">The connector instance to use for export</param>
    /// <param name="runMode">Whether to preview only or actually sync (Q5 decision)</param>
    /// <returns>Export execution result with preview information</returns>
    public Task<ExportExecutionResult> ExecuteExportsAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        SyncRunMode runMode = SyncRunMode.PreviewAndSync)
    {
        return ExecuteExportsAsync(connectedSystem, connector, runMode, null, CancellationToken.None);
    }

    /// <summary>
    /// Executes all Pending Exports for a Connected System with progress reporting and cancellation support.
    /// </summary>
    /// <param name="connectedSystem">The Connected System to export to</param>
    /// <param name="connector">The connector instance to use for export</param>
    /// <param name="runMode">Whether to preview only or actually sync (Q5 decision)</param>
    /// <param name="options">Optional execution options for batch size and parallelism</param>
    /// <param name="cancellationToken">Cancellation token to stop export processing</param>
    /// <param name="progressCallback">Optional callback for progress reporting</param>
    /// <param name="connectorFactory">Optional factory to create additional connector instances for parallel batches</param>
    /// <param name="repositoryFactory">Optional factory to create disposable per-batch repository scopes for parallel batches; each batch disposes its scope on completion, releasing the scope's DbContext and pooled connection</param>
    /// <param name="batchCompletedCallback">Optional callback invoked after each batch with processed export items.
    /// Enables streaming RPEI creation per-batch instead of accumulating all items across the entire run.
    /// When provided, ProcessedExportItems on the result will be empty — items are consumed per-batch via this callback.</param>
    /// <returns>Export execution result with preview information</returns>
    public async Task<ExportExecutionResult> ExecuteExportsAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        SyncRunMode runMode,
        ExportExecutionOptions? options,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback = null,
        Func<IConnector>? connectorFactory = null,
        Func<ISyncRepositoryScope>? repositoryFactory = null,
        Func<List<ProcessedExportItem>, Task>? batchCompletedCallback = null)
    {
        options ??= new ExportExecutionOptions();
        cancellationToken.ThrowIfCancellationRequested();

        var result = new ExportExecutionResult
        {
            ConnectedSystemId = connectedSystem.Id,
            RunMode = runMode,
            StartedAt = DateTime.UtcNow
        };

        // Get the count of executable exports without loading them all into memory.
        // Exports are loaded in batches to avoid EF change tracker overhead
        // that caused 86s per-batch slowdowns at 100K scale.
        int totalExportCount;
        using (Diagnostics.Diagnostics.Sync.StartSpan("GetExecutableExportCount"))
        {
            totalExportCount = await SyncRepo.GetExecutableExportCountAsync(connectedSystem.Id);
        }
        result.TotalPendingExports = totalExportCount;

        if (totalExportCount == 0)
        {
            Log.Debug("ExecuteExportsAsync: No Pending Exports to execute for system {SystemId}", connectedSystem.Id);
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        Log.Information("ExecuteExportsAsync: Found {Count} Pending Exports to execute for system {SystemName} (BatchSize: {BatchSize}, MaxParallelism: {MaxParallelism})",
            totalExportCount, connectedSystem.Name, options.BatchSize, options.MaxParallelism);

        // Pre-export reconciliation: detect CREATE+DELETE and UPDATE+DELETE pairs that cancel
        // each other out. This catches pairs persisted across different sync runs (the flush-time
        // reconciliation in SyncTaskProcessorBase catches same-page pairs).
        var reconciled = await ReconcileCreateDeletePairsAsync(connectedSystem.Id);
        if (reconciled > 0)
        {
            totalExportCount -= reconciled;
            result.TotalPendingExports = totalExportCount;
            result.ReconciledCount = reconciled;

            if (totalExportCount <= 0)
            {
                Log.Information("ExecuteExportsAsync: All exports reconciled for system {SystemName} — nothing to export",
                    connectedSystem.Name);
                result.CompletedAt = DateTime.UtcNow;
                return result;
            }
        }

        // Report initial progress
        await ReportProgressAsync(progressCallback, new ExportProgressInfo
        {
            Phase = ExportPhase.Preparing,
            TotalExports = totalExportCount,
            ProcessedExports = 0,
            Message = "Preparing exports"
        });

        // If preview only mode, load IDs for preview and stop (Q5 decision)
        if (runMode == SyncRunMode.PreviewOnly)
        {
            var previewExports = await GetExecutableExportsAsync(connectedSystem.Id);
            foreach (var pe in previewExports)
                result.ProcessedPendingExportIds.Add(pe.Id);

            Log.Information("ExecuteExportsAsync: Preview mode - not executing exports for system {SystemName}",
                connectedSystem.Name);
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        // Run Profile Safeguards (#1618): one ledger for the whole run, shared by both passes (the
        // immediate batches below and the deferred-reference pass) and both connector shapes (calls
        // and files), so a limit holds regardless of which pass or path an export is attempted on.
        var changeLimitLedger = new ExportChangeLimitLedger(options.MaxCreates, options.MaxUpdates, options.MaxDeletes);

        // Execute exports using the connector with batch-loading
        await ExecuteExportsViaConnectorAsync(connectedSystem, connector, result, options, changeLimitLedger,
            cancellationToken, progressCallback, connectorFactory, repositoryFactory, batchCompletedCallback);

        // Second pass: retry any exports with deferred references that might now be resolvable
        if (!cancellationToken.IsCancellationRequested)
        {
            await ExecuteDeferredReferencesAsync(connectedSystem, connector, result);

            // Drain any items from deferred reference resolution via callback
            if (batchCompletedCallback != null && result.ProcessedExportItems.Count > 0)
            {
                await batchCompletedCallback(result.ProcessedExportItems);
                result.ProcessedExportItems = [];
            }
        }

        // Run Profile Safeguards (#1618): copy the ledger's final withheld counts onto the result.
        result.CreatesWithheld = changeLimitLedger.Withheld(PendingExportChangeType.Create);
        result.UpdatesWithheld = changeLimitLedger.Withheld(PendingExportChangeType.Update);
        result.DeletesWithheld = changeLimitLedger.Withheld(PendingExportChangeType.Delete);

        result.CompletedAt = DateTime.UtcNow;

        // Synchronisation Integrity: log summary statistics at the end of every batch operation.
        Log.Information("ExecuteExportsAsync: Optimistic export apply summary for {SystemName}: " +
            "{AppliedCount} Pending Exports applied, {SkippedCount} skipped (Delete change type), " +
            "{FailedCount} failed (confirming import will self-heal), {UnresolvedCount} Reference values left unresolved",
            connectedSystem.Name, result.OptimisticApplyAppliedCount, result.OptimisticApplySkippedCount,
            result.OptimisticApplyFailedCount, result.OptimisticApplyUnresolvedReferenceCount);

        // #1121: only worth a line when the run actually provisioned accounts owed a password; every other
        // deployment would otherwise get a pair of zeroes on every export.
        if (result.InitialPasswordsStagedCount > 0 || result.InitialPasswordStagingFailedCount > 0)
        {
            Log.Information("ExecuteExportsAsync: Initial password summary for {SystemName}: {StagedCount} newly provisioned accounts " +
                "recorded as owed an initial password, {FailedCount} that could not be recorded",
                connectedSystem.Name, result.InitialPasswordsStagedCount, result.InitialPasswordStagingFailedCount);
        }

        // Only when it happened: a Connected System with no class membership would otherwise carry a zero on every
        // export, and this points at a configuration gap rather than at a Connected System failure.
        if (result.ClassMembershipRefusedCount > 0)
        {
            Log.Warning("ExecuteExportsAsync: Class membership summary for {SystemName}: {RefusedCount} export(s) refused because a class being added has " +
                "required attributes with no value. Each names the attributes on its own Pending Export; add an Attribute Flow for them, or withdraw the " +
                "auxiliary class selection that brought the class in.",
                connectedSystem.Name, result.ClassMembershipRefusedCount);
        }

        // Report completion
        await ReportProgressAsync(progressCallback, new ExportProgressInfo
        {
            Phase = ExportPhase.Completed,
            TotalExports = result.TotalPendingExports,
            ProcessedExports = result.SuccessCount + result.FailedCount + result.DeferredCount,
            SuccessCount = result.SuccessCount,
            FailedCount = result.FailedCount,
            DeferredCount = result.DeferredCount,
            Message = $"Export completed: {result.SuccessCount} succeeded, {result.FailedCount} failed, {result.DeferredCount} deferred"
        });

        return result;
    }

    /// <summary>
    /// Reports progress to the callback if provided.
    /// </summary>
    private static async Task ReportProgressAsync(Func<ExportProgressInfo, Task>? callback, ExportProgressInfo info)
    {
        if (callback != null)
        {
            await callback(info);
        }
    }

    /// <summary>
    /// Builds the progress reporter handed to a connector (issues #637, #454). The connector supplies
    /// its own phase key and a human-readable message describing what it is doing internally; JIM keeps
    /// ownership of the orchestration phase and the counts, which the connector cannot meaningfully
    /// populate, and of turning a connector phase key into the step an administrator sees.
    /// </summary>
    /// <param name="progressCallback">The caller's progress callback, or null when it wants no progress.</param>
    /// <param name="infoFactory">Wraps a connector sub-phase message into a progress report carrying this
    /// call site's current counts.</param>
    /// <param name="sharedGate">The call site's own progress gate, where it has one, so that connector
    /// emits and JIM's own emits serialise against each other rather than racing on a shared DbContext.</param>
    private static ConnectorProgress CreateConnectorProgress(
        Func<ExportProgressInfo, Task>? progressCallback,
        Func<string, ExportProgressInfo> infoFactory,
        SemaphoreSlim? sharedGate = null)
    {
        if (progressCallback == null)
            return new ConnectorProgress(report: null);

        return new ConnectorProgress(
            report: async message => await progressCallback(infoFactory(message)),
            enterPhase: async (phaseKey, message) =>
            {
                var info = infoFactory(message ?? string.Empty);
                info.ConnectorPhaseKey = phaseKey;
                await progressCallback(info);
            },
            sharedGate: sharedGate);
    }

    /// <summary>
    /// Loads all executable exports for a Connected System, identifies CREATE+DELETE and
    /// UPDATE+DELETE pairs targeting the same CSO, and deletes the reconciled exports from the DB.
    /// Returns the total number of Pending Exports removed.
    /// </summary>
    private async Task<int> ReconcileCreateDeletePairsAsync(int connectedSystemId)
    {
        using var span = Diagnostics.Diagnostics.Sync.StartSpan("ReconcileCreateDeletePairs");

        var exportSummaries = await SyncRepo.GetExecutableExportSummariesAsync(connectedSystemId);
        if (exportSummaries.Count == 0)
            return 0;

        var syncEngine = new SyncEngine();
        var result = syncEngine.ReconcileCreateDeletePairs(exportSummaries);

        if (result.ReconciledPairs.Count == 0)
            return 0;

        // Collect all PE IDs to delete
        var idsToDelete = result.ReconciledPairs
            .SelectMany(p => p.CancelledExportIds)
            .ToList();

        // Delete reconciled PEs by ID using lightweight deletion
        if (idsToDelete.Count > 0)
            await SyncRepo.DeletePendingExportsByIdsAsync(idsToDelete);

        Log.Information("ReconcileCreateDeletePairsAsync: Reconciled {PairCount} pairs, cancelled {CancelledCount} Pending Exports for system {SystemId}",
            result.ReconciledPairs.Count, result.TotalCancelled, connectedSystemId);

        span.SetTag("reconciledPairs", result.ReconciledPairs.Count);
        span.SetTag("cancelledExports", result.TotalCancelled);
        span.SetSuccess();

        return result.TotalCancelled;
    }

    /// <summary>
    /// Gets Pending Exports that are ready to be executed.
    /// Uses database-level filtering for status, retry timing, and max retries (Q6 decision),
    /// then applies in-memory checks for attribute-level eligibility that can't be expressed in SQL.
    /// </summary>
    private async Task<List<PendingExport>> GetExecutableExportsAsync(int connectedSystemId)
    {
        // Database-level filtering handles: status, NextRetryAt, ErrorCount < MaxRetries, ordering
        var eligibleExports = await SyncRepo.GetExecutableExportsAsync(connectedSystemId);

        // In-memory filtering for checks that require navigation property evaluation
        return eligibleExports
            .Where(pe => IsReadyForExecution(pe))
            .ToList();
    }

    /// <summary>
    /// Determines if a Pending Export is ready for execution.
    /// Applies in-memory checks that require navigation property evaluation and can't be expressed
    /// in SQL. Database-level checks (status, retry timing, max retries) are already applied by
    /// GetExecutableExportsAsync.
    /// </summary>
    private static bool IsReadyForExecution(PendingExport pendingExport)
    {
        // For Update operations, we need at least one attribute change to export.
        // This check requires evaluating the AttributeValueChanges navigation property.
        if (pendingExport.ChangeType == PendingExportChangeType.Update)
        {
            var hasExportableAttributeChanges = pendingExport.AttributeValueChanges.Any(ac =>
                ac.Status == PendingExportAttributeChangeStatus.Pending ||
                ac.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed);

            if (!hasExportableAttributeChanges)
            {
                return false;
            }
        }
        // For Create and Delete, we proceed even if there are no attribute changes
        // (Create might have no initial attributes, Delete just needs the operation)

        // Delete exports that have already been exported should not be re-executed.
        // Unlike Create/Update exports which may have attribute changes needing retry,
        // a Delete is an all-or-nothing operation. Once exported (status=Exported), the
        // delete was sent to the target system and should only be cleaned up during
        // import confirmation, not re-executed (which would fail if the object is already gone).
        if (pendingExport.ChangeType == PendingExportChangeType.Delete &&
            pendingExport.Status == PendingExportStatus.Exported)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Executes exports using the connector's export interface with batch-loading.
    /// Exports are loaded in batches via AsNoTracking to avoid EF change tracker overhead.
    /// </summary>
    private async Task ExecuteExportsViaConnectorAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        ExportExecutionResult result,
        ExportExecutionOptions options,
        ExportChangeLimitLedger changeLimitLedger,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback,
        Func<IConnector>? connectorFactory,
        Func<ISyncRepositoryScope>? repositoryFactory,
        Func<List<ProcessedExportItem>, Task>? batchCompletedCallback = null)
    {
        // Check if connector supports export using calls
        if (connector is IConnectorExportUsingCalls callsConnector)
        {
            await ExecuteUsingCallsWithBatchingAsync(connectedSystem, callsConnector, result, options, changeLimitLedger,
                cancellationToken, progressCallback, connectorFactory, repositoryFactory, batchCompletedCallback);
        }
        // File-based connectors load all Pending Exports upfront because the connector writes the
        // full output file in a single pass. Batching the DB load would only help if the write
        // strategy also streamed; see issue #633 for that follow-up.
        else if (connector is IConnectorExportUsingFiles filesConnector)
        {
            var pendingExports = await GetExecutableExportsAsync(connectedSystem.Id);

            // Run Profile Safeguards (#1618): reserve against the ledger before the file is written.
            // Withheld exports are simply excluded here: never touched, never marked, left Pending.
            pendingExports = ReserveAgainstLedger(pendingExports, changeLimitLedger);

            await ExecuteUsingFilesWithBatchingAsync(connectedSystem, filesConnector, pendingExports, result, options, cancellationToken, progressCallback);
        }
        else
        {
            Log.Warning("ExecuteExportsViaConnectorAsync: Connector {ConnectorName} does not support export",
                connector.Name);
        }
    }

    /// <summary>
    /// Run Profile Safeguards (#1618): filters a set of Pending Exports against the run's change-limit
    /// ledger, preserving each change type's queue order. Groups by change type, reserves capacity for
    /// each type's count in one call, then keeps the granted head of each type in its original position;
    /// the rest is dropped from the returned list. A caller must leave whatever is dropped exactly as
    /// found: not marked, not failed, given no execution item.
    /// </summary>
    private static List<PendingExport> ReserveAgainstLedger(List<PendingExport> exports, ExportChangeLimitLedger ledger)
    {
        if (exports.Count == 0)
            return exports;

        var requestedByType = exports
            .GroupBy(pe => pe.ChangeType)
            .ToDictionary(g => g.Key, g => g.Count());

        var grantedByType = requestedByType.ToDictionary(kv => kv.Key, kv => ledger.Reserve(kv.Key, kv.Value));

        // Fast path: nothing was withheld for any type, so the original (already queue-ordered) list
        // can be returned unchanged rather than rebuilt.
        if (grantedByType.All(kv => kv.Value == requestedByType[kv.Key]))
            return exports;

        var consumedByType = new Dictionary<PendingExportChangeType, int>();
        var granted = new List<PendingExport>(exports.Count);
        foreach (var export in exports)
        {
            var consumed = consumedByType.GetValueOrDefault(export.ChangeType);
            if (consumed < grantedByType[export.ChangeType])
            {
                granted.Add(export);
                consumedByType[export.ChangeType] = consumed + 1;
            }
        }
        return granted;
    }

    /// <summary>
    /// Prepares a connector instance for export by injecting required services.
    /// </summary>
    private void PrepareConnectorForExport(IConnectorExportUsingCalls connector)
    {
        // Inject certificate provider for connectors that support it
        if (connector is IConnectorCertificateAware certificateAwareConnector)
        {
            var certificateProvider = new CertificateProviderService(Application);
            certificateAwareConnector.SetCertificateProvider(certificateProvider);
        }

        // Inject credential protection for connectors that support it (for password decryption)
        if (connector is IConnectorCredentialAware credentialAwareConnector)
        {
            // Use pre-configured credential protection if available (from DI in JIM.Web),
            // otherwise create a new instance (for JIM.Worker which doesn't use DI)
            var credentialProtection = Application.CredentialProtection ??
                new CredentialProtectionService(DataProtectionHelper.CreateProvider());
            credentialAwareConnector.SetCredentialProtection(credentialProtection);
        }
    }

    /// <summary>
    /// Executes exports using the IConnectorExportUsingCalls interface with batch-loading.
    /// Loads exports in batches via AsNoTracking to avoid EF change tracker overhead that
    /// caused O(N) DetectChanges scans per batch at 100K scale.
    /// </summary>
    private async Task ExecuteUsingCallsWithBatchingAsync(
        ConnectedSystem connectedSystem,
        IConnectorExportUsingCalls connector,
        ExportExecutionResult result,
        ExportExecutionOptions options,
        ExportChangeLimitLedger changeLimitLedger,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback,
        Func<IConnector>? connectorFactory,
        Func<ISyncRepositoryScope>? repositoryFactory,
        Func<List<ProcessedExportItem>, Task>? batchCompletedCallback = null)
    {
        try
        {
            PrepareConnectorForExport(connector);

            // Open connection for the primary connector
            using (Diagnostics.Diagnostics.Connector.StartSpan("OpenExportConnection"))
            {
                connector.OpenExportConnection(connectedSystem.SettingValues, connectedSystem.PersistedConnectorData);
            }
            Log.Debug("ExecuteUsingCallsWithBatchingAsync: Opened export connection for {SystemName}", connectedSystem.Name);

            // Tracks whether the export phase below completed without throwing. Read from the
            // finally block to decide whether a failure while persisting CloseExportConnection's
            // return value may safely propagate on its own, or must be logged and swallowed so it
            // does not replace/mask an export failure that is already unwinding through the same
            // finally block (see the finally block below for the full rationale).
            var exportPhaseSucceeded = false;

            try
            {
                // Load and process exports in batches to avoid loading all 100K+ entities at once.
                // Batch collection is a single forward sweep using keyset pagination on
                // (CreatedAt, Id). Executed exports drop out of the query mid-run (Update
                // attribute changes transition to ExportedPendingConfirmation; Create/Delete
                // move to Status=Exported, which the query excludes), and deferred
                // reference-bearing exports stay Pending in the database while being
                // collected in memory; a strictly-increasing cursor is immune to both, so
                // nothing is ever re-read. The previous OFFSET implementation restarted its
                // scan from zero for every batch and degraded to O(n²) page loads once
                // thousands of deferred exports accumulated (issue #985).
                //
                // Known trade-off: an export whose NextRetryAt backoff elapses mid-run at a
                // position already behind the cursor is not picked up until the next export
                // run. The OFFSET implementation only ever caught those incidentally.
                var deferredExports = new List<PendingExport>();
                var processedCount = 0;
                var processedIds = new HashSet<Guid>();

                // Sub-phase narration from the connector, carrying the counts JIM owns (issue #637).
                using var connectorProgress = CreateConnectorProgress(progressCallback, subPhase => new ExportProgressInfo
                {
                    Phase = ExportPhase.Executing,
                    TotalExports = result.TotalPendingExports,
                    ProcessedExports = processedCount,
                    Message = subPhase
                });
                DateTime? cursorCreatedAt = null;
                Guid? cursorId = null;
                var exportPhaseStopwatch = System.Diagnostics.Stopwatch.StartNew();

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    List<PendingExport> batch;

                    while (true)
                    {
                        List<PendingExport> rawBatch;
                        using (Diagnostics.Diagnostics.Database.StartSpan("LoadExportBatch")
                            .SetTag("afterCreatedAt", cursorCreatedAt?.ToString("O") ?? "start")
                            .SetTag("take", options.BatchSize))
                        {
                            rawBatch = await SyncRepo.GetExecutableExportBatchAsync(
                                connectedSystem.Id, options.BatchSize, cursorCreatedAt, cursorId);
                        }

                        if (rawBatch.Count == 0)
                        {
                            batch = rawBatch;
                            break;
                        }

                        // Advance the cursor past everything just read, whether or not it
                        // survives the processed filter below.
                        var lastRow = rawBatch[^1];
                        cursorCreatedAt = lastRow.CreatedAt;
                        cursorId = lastRow.Id;

                        // Safety net: a monotonic cursor never re-reads rows, but keep the
                        // in-memory filter as a guard against duplicates.
                        batch = processedIds.Count > 0
                            ? rawBatch.Where(pe => !processedIds.Contains(pe.Id)).ToList()
                            : rawBatch;

                        if (batch.Count > 0)
                            break;

                        // Entire page was already processed (unexpected under keyset paging);
                        // keep sweeping forward.
                    }

                    if (batch.Count == 0)
                        break;

                    // Apply in-memory eligibility filter (same as the old GetExecutableExportsAsync)
                    var eligibleExports = batch.Where(pe => IsReadyForExecution(pe)).ToList();

                    // Track all batch IDs as processed (even ineligible ones, to avoid re-fetching)
                    foreach (var pe in batch)
                        processedIds.Add(pe.Id);

                    // Track eligible export IDs for the result (used by preview mode and tests)
                    foreach (var pe in eligibleExports)
                        result.ProcessedPendingExportIds.Add(pe.Id);

                    // Separate immediate from deferred exports
                    var immediateExports = eligibleExports.Where(pe => !pe.HasUnresolvedReferences).ToList();
                    var batchDeferred = eligibleExports.Where(pe => pe.HasUnresolvedReferences).ToList();
                    deferredExports.AddRange(batchDeferred);

                    // Run Profile Safeguards (#1618): reserve capacity from the ledger before this
                    // batch is marked executing. Withheld exports are dropped from the batch here:
                    // never marked, never failed, given no execution item, left exactly as found
                    // (Pending). Their ids are already in processedIds (see the foreach above), so
                    // the cursor never re-reads them. Counted towards processedCount so the run's
                    // progress window still completes even though nothing was attempted for them.
                    var immediateCountBeforeReservation = immediateExports.Count;
                    immediateExports = ReserveAgainstLedger(immediateExports, changeLimitLedger);
                    var withheldFromBatch = immediateCountBeforeReservation - immediateExports.Count;
                    if (withheldFromBatch > 0)
                        processedCount += withheldFromBatch;

                    if (immediateExports.Count > 0)
                    {
                        // Report progress
                        await ReportProgressAsync(progressCallback, new ExportProgressInfo
                        {
                            Phase = ExportPhase.Executing,
                            TotalExports = result.TotalPendingExports,
                            ProcessedExports = processedCount,
                            CurrentBatchSize = immediateExports.Count,
                            Message = "Exporting"
                        });

                        // Mark batch as executing
                        using (Diagnostics.Diagnostics.Database.StartSpan("MarkBatchAsExecuting")
                            .SetTag("batchSize", immediateExports.Count))
                        {
                            await MarkBatchAsExecutingAsync(immediateExports, SyncRepo);
                        }

                        try
                        {
                            // Execute batch via connector
                            List<ConnectedSystemExportResult> exportResults;
                            using (Diagnostics.Diagnostics.Connector.StartSpan("ExportBatch")
                                .SetTag("connectedSystemId", connectedSystem.Id)
                                .SetTag("batchSize", immediateExports.Count)
                                .SetTag("cumulativeObjectCount", processedCount + immediateExports.Count)
                                .SetTag("wallClockOffsetMs", exportPhaseStopwatch.Elapsed.TotalMilliseconds))
                            {
                                (exportResults, var immediateRefused) = await ExportBatchAsync(connector, connectedSystem, immediateExports, cancellationToken, connectorProgress);
                                result.ClassMembershipRefusedCount += immediateRefused;
                            }

                            // Process results
                            using (Diagnostics.Diagnostics.Database.StartSpan("ProcessBatchSuccess")
                                .SetTag("batchSize", immediateExports.Count))
                            {
                                await ProcessBatchSuccessAsync(immediateExports, exportResults, result, SyncRepo, connectedSystem.EffectiveInitialPasswordTimeToLive);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "ExecuteUsingCallsWithBatchingAsync: Batch failed for {SystemName}", connectedSystem.Name);

                            // Create ProcessedExportItems so the RPEI pipeline can record the failures.
                            // Without this, the activity would silently report Status: Complete.
                            foreach (var export in immediateExports)
                            {
                                MarkExportFailed(export, ex.Message);
                                result.FailedCount++;
                                result.ProcessedExportItems.Add(new ProcessedExportItem
                                {
                                    ChangeType = export.ChangeType,
                                    ConnectedSystemObject = export.ConnectedSystemObject,
                                    AttributeChangeCount = export.AttributeValueChanges.Count,
                                    AttributeValueChanges = export.AttributeValueChanges.ToList(),
                                    Succeeded = false,
                                    ErrorMessage = $"Batch export failed: {ex.Message}",
                                    ErrorCount = export.ErrorCount,
                                    ErrorType = ConnectedSystemExportErrorType.General
                                }.WithCauseFrom(export));
                            }
                            await SyncRepo.UpdatePendingExportsAsync(immediateExports);

                            // Stop processing further batches — the connector is likely broken
                            break;
                        }

                        processedCount += immediateExports.Count;

                        Log.Information("MetricsCheckpoint: Export processed={ObjectsProcessed} elapsed={ElapsedMs}ms total={TotalObjects} cs={ConnectedSystemName}",
                            processedCount, (long)exportPhaseStopwatch.Elapsed.TotalMilliseconds, result.TotalPendingExports, connectedSystem.Name);

                        // Stream processed items to caller per-batch instead of accumulating across
                        // the entire run. At 100K exports this prevents ~125 MB of retained entity data.
                        if (batchCompletedCallback != null && result.ProcessedExportItems.Count > 0)
                        {
                            await batchCompletedCallback(result.ProcessedExportItems);
                            result.ProcessedExportItems = [];
                        }

                        // Clear the change tracker between batches to prevent O(n²) degradation.
                        // All DB writes use raw SQL (MarkBatchAsExecuting, BulkUpdatePendingExports),
                        // so tracked entities serve no purpose after each batch completes. Without
                        // this, Entity() calls in detach loops trigger change detection scans across
                        // all accumulated entities — 40K+ entities after 100 batches.
                        SyncRepo.ClearChangeTracker();
                    }
                    else if (batchDeferred.Count == batch.Count
                        && !await SyncRepo.AnyExecutableNonDeferredExportsAfterAsync(connectedSystem.Id, cursorCreatedAt, cursorId))
                    {
                        // Fast path (issue #985c): the whole batch just loaded was deferred
                        // (reference-bearing) and nothing in it was executable. Continuing to
                        // page through the remainder 100 rows at a time would only ever rebuild
                        // the same deferred list one page slower; at group-heavy scale (10K+
                        // deferred exports) this was the entire cost of the collection loop.
                        // Collect everything beyond the cursor with a single set-based query and
                        // stop scanning; a mixed batch (some immediate, some deferred) always
                        // takes the branch above instead and keeps paging normally.
                        //
                        // The existence probe above is REQUIRED before breaking out: deferred and
                        // executable exports interleave in (CreatedAt, Id) order, so a full batch
                        // of deferred exports does not prove the remainder of the queue is
                        // deferred too. Without the probe, executable exports created after a
                        // contiguous deferred run would silently never execute in this run; a
                        // behaviour regression versus page-by-page scanning. The probe is a cheap
                        // indexed existence check (no Includes, no materialisation); when it
                        // finds executable exports beyond the cursor, this branch is skipped and
                        // normal paging continues for this iteration.
                        using var span = Diagnostics.Diagnostics.Database.StartSpan("CollectRemainingDeferred")
                            .SetTag("afterCreatedAt", cursorCreatedAt?.ToString("O") ?? "start");

                        var remainingDeferred = await SyncRepo.GetRemainingDeferredExportsAsync(
                            connectedSystem.Id, cursorCreatedAt, cursorId);

                        // Safety net: mirror the in-loop processedIds guard above, even though a
                        // strictly-increasing cursor should make duplicates impossible here.
                        if (processedIds.Count > 0)
                            remainingDeferred = remainingDeferred.Where(pe => !processedIds.Contains(pe.Id)).ToList();

                        foreach (var pe in remainingDeferred)
                        {
                            processedIds.Add(pe.Id);
                            result.ProcessedPendingExportIds.Add(pe.Id);
                        }

                        deferredExports.AddRange(remainingDeferred);

                        span.SetTag("collectedCount", remainingDeferred.Count);
                        span.SetSuccess();

                        break;
                    }
                    // Note: no break when a batch has only ineligible/deferred exports and the
                    // fast path above did not trigger (a mixed batch, or executable exports still
                    // exist beyond the cursor); the outer loop continues scanning forward since
                    // later batches (ordered by CreatedAt) may contain eligible exports. The loop
                    // only exits when batch.Count == 0 (database exhausted, handled above) or via
                    // the fast-path break above.
                }

                // Second pass: Exports with unresolved references (deferred)
                if (deferredExports.Count > 0)
                {
                    await ProcessDeferredExportsAsync(connectedSystem, connector, deferredExports, result, options, changeLimitLedger,
                        cancellationToken, progressCallback, connectorFactory, repositoryFactory);

                    // Drain any remaining deferred export items via callback
                    if (batchCompletedCallback != null && result.ProcessedExportItems.Count > 0)
                    {
                        await batchCompletedCallback(result.ProcessedExportItems);
                        result.ProcessedExportItems = [];
                    }
                }

                // Capture created containers before closing connection
                if (connector is IConnectorContainerCreation containerCreator &&
                    containerCreator.CreatedContainerExternalIds.Count > 0)
                {
                    result.CreatedContainerExternalIds.AddRange(containerCreator.CreatedContainerExternalIds);
                    Log.Information("ExecuteUsingCallsWithBatchingAsync: Captured {Count} created container(s) for auto-selection",
                        containerCreator.CreatedContainerExternalIds.Count);
                }

                // Reached only if nothing above threw; used by the finally block below to tell a
                // genuine export failure apart from a clean run.
                exportPhaseSucceeded = true;
            }
            finally
            {
                string? closeReturn;
                using (Diagnostics.Diagnostics.Connector.StartSpan("CloseExportConnection"))
                {
                    // Always close connection
                    closeReturn = connector.CloseExportConnection();
                }
                Log.Debug("ExecuteUsingCallsWithBatchingAsync: Closed export connection for {SystemName}", connectedSystem.Name);

                // Persist connector state the connector chose to override at close, e.g. because
                // opening/using the connection invalidated a previously persisted pin (issue #230).
                // Null (the overwhelmingly common case) means "nothing to override" and must not persist.
                // Application.ConnectedSystems mirrors the accessor SyncServer itself uses
                // (_jim.ConnectedSystems.UpdateConnectedSystemPersistedConnectorDataAsync); no new
                // dependency or layer widening is needed here.
                if (closeReturn != null)
                {
                    try
                    {
                        await Application.ConnectedSystems.UpdateConnectedSystemPersistedConnectorDataAsync(connectedSystem, closeReturn);
                    }
                    catch (Exception persistEx) when (!exportPhaseSucceeded)
                    {
                        // The export itself already failed and that exception is propagating out of
                        // this finally block. A .NET finally block that itself throws replaces the
                        // in-flight exception rather than chaining it, which would silently hide the
                        // export's own failure behind an unrelated persistence error. Log and let the
                        // original export failure continue to unwind instead.
                        Log.Error(persistEx,
                            "ExecuteUsingCallsWithBatchingAsync: Failed to persist connector data returned by CloseExportConnection while the export itself is failing for Connected System {ConnectedSystemId}. The export's own failure takes precedence and will propagate.",
                            connectedSystem.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("ExecuteUsingCallsWithBatchingAsync: Export cancelled for {SystemName}", connectedSystem.Name);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteUsingCallsWithBatchingAsync: Failed to execute exports for {SystemName}", connectedSystem.Name);
            // Connection-level or unexpected errors must propagate so PerformExportAsync
            // can fail the activity via FailActivityWithErrorAsync. Swallowing the exception
            // here would cause the activity to silently report Status: Complete.
            throw;
        }
    }

    /// <summary>
    /// Processes deferred exports (those with unresolved references) after all immediate exports.
    /// </summary>
    private async Task ProcessDeferredExportsAsync(
        ConnectedSystem connectedSystem,
        IConnectorExportUsingCalls connector,
        List<PendingExport> deferredExports,
        ExportExecutionResult result,
        ExportExecutionOptions options,
        ExportChangeLimitLedger changeLimitLedger,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback,
        Func<IConnector>? connectorFactory,
        Func<ISyncRepositoryScope>? repositoryFactory)
    {
        var useParallelBatches = options.MaxParallelism > 1 && connectorFactory != null && repositoryFactory != null;

        // This pass counts its own work: the deferred exports, each finished with once it has been
        // written or confirmed still unwritable. Reporting the export's totals here left the pass
        // reading as complete for the whole time it ran.
        var passTotal = deferredExports.Count;

        await ReportProgressAsync(progressCallback, new ExportProgressInfo
        {
            Phase = ExportPhase.ResolvingReferences,
            TotalExports = result.TotalPendingExports,
            ProcessedExports = result.SuccessCount,
            PassTotal = passTotal,
            PassProcessed = 0,
            Message = $"Resolving {deferredExports.Count} deferred exports"
        });

        // Bulk pre-fetch all referenced CSOs in a single query
        var mvoIds = CollectUnresolvedMvoIds(deferredExports);
        Dictionary<Guid, ConnectedSystemObject> csoLookup;
        using (Diagnostics.Diagnostics.Database.StartSpan("BulkFetchCsosByMvoIds")
            .SetTag("mvoIdCount", mvoIds.Count))
        {
            csoLookup = mvoIds.Count > 0
                ? await SyncRepo.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(mvoIds, connectedSystem.Id)
                : new Dictionary<Guid, ConnectedSystemObject>();
        }

        // Separate resolved from still-unresolved exports. A still-unresolved export that has something
        // it can write now (issue #1398) is written now alongside the resolved ones and stays pending only
        // for the references it still owes; one with nothing writable is deferred whole, as before.
        var resolvedExports = new List<PendingExport>();
        var writeInPartExports = new List<PendingExport>();
        var stillUnresolvedExports = new List<PendingExport>();
        var resolveProcessedCount = 0;

        foreach (var export in deferredExports)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolved = TryResolveReferencesFromLookup(export, csoLookup);
            if (resolved)
            {
                export.HasUnresolvedReferences = false;
                resolvedExports.Add(export);
            }
            else
            {
                stillUnresolvedExports.Add(export);
                if (CanWriteInPart(export))
                    writeInPartExports.Add(export);
            }

            resolveProcessedCount++;

            // Throttle progress reporting — resolution is a microsecond dictionary lookup,
            // but each progress report triggers a SaveChangesAsync round-trip. Report every
            // 50 items or on the final item to avoid hundreds of unnecessary DB writes.
            if (resolveProcessedCount % 50 == 0 || resolveProcessedCount == deferredExports.Count)
            {
                await ReportProgressAsync(progressCallback, new ExportProgressInfo
                {
                    Phase = ExportPhase.ResolvingReferences,
                    TotalExports = result.TotalPendingExports,
                    ProcessedExports = result.SuccessCount + result.FailedCount,
                    // Resolution classifies; it finishes with nothing. Counting it here would run the
                    // window to full and then back down as the writing began, which the estimator
                    // reads as a new phase and the administrator reads as progress being lost.
                    PassTotal = passTotal,
                    PassProcessed = 0,
                    Message = $"Resolving deferred exports ({resolveProcessedCount} / {deferredExports.Count})"
                });
            }
        }

        // What each still-unresolved export owes, and why (issue #1398): a reference whose target object
        // exists but has no anchor yet is merely waiting; one whose target has no object in this system
        // at all cannot resolve as things stand, and is reported per the Connected System's Unresolved
        // Reference Handling. Built before anything is written so the write's own item can carry it.
        var unresolvedReferenceNotes = await BuildUnresolvedReferenceNotesAsync(connectedSystem, stillUnresolvedExports, csoLookup, result);

        // Batch-export resolved deferred exports, and the partial writes alongside them
        var exportsToWrite = resolvedExports.Concat(writeInPartExports).ToList();

        // Run Profile Safeguards (#1618): reserve against the same ledger the first pass used, before
        // the exports are split into batches. A deferred export withheld here is left exactly as the
        // first pass left it: still Pending, its unresolved-reference state untouched (the in-memory
        // resolution above is never persisted for it), and NOT marked deferred - it simply was not
        // attempted this run, so DeferredCount must not count it either. The next Export run resolves
        // and attempts it in the ordinary order.
        exportsToWrite = ReserveAgainstLedger(exportsToWrite, changeLimitLedger);

        if (exportsToWrite.Count > 0)
        {
            // Persist the in-memory reference resolutions BEFORE executing the deferred batches.
            // The parallel path (ProcessBatchesInParallelAsync) re-loads each batch by ID on a
            // fresh per-batch repository/DbContext, which only sees persisted state; without
            // persisting first, those contexts read the still-unresolved rows and send raw
            // Metaverse Object identifiers to the target system (observed against OpenLDAP as
            // "member: value #0 invalid per syntax" when Max Export Parallelism first defaulted
            // above 1). The sequential path passes these in-memory instances straight to the
            // connector and does not strictly need the persist, but doing it unconditionally
            // also means a worker crash mid-export no longer loses completed resolution work.
            // UpdatePendingExportsAsync persists both the parent rows and the attribute value
            // change rows (StringValue/UnresolvedReferenceValue) via raw SQL.
            using (Diagnostics.Diagnostics.Database.StartSpan("PersistResolvedDeferredExports")
                .SetTag("count", exportsToWrite.Count))
            {
                await SyncRepo.UpdatePendingExportsAsync(exportsToWrite);
            }

            // Clear the change tracker before exporting deferred batches.
            // The CSO lookup query above re-loaded entities into the tracker, which causes
            // identity conflicts when the EF fallback paths (used in tests with in-memory DB)
            // try to attach/update the original PE instances.
            SyncRepo.ClearChangeTracker();

            var deferredBatches = exportsToWrite
                .Select((export, index) => new { export, index })
                .GroupBy(x => x.index / options.BatchSize)
                .Select(g => g.Select(x => x.export).ToList())
                .ToList();

            if (useParallelBatches && deferredBatches.Count > 1)
            {
                // Snapshot the immediate-phase counts, mirroring ProcessDeferredBatchesSequentiallyAsync:
                // ProcessBatchSuccessAsync increments the result counters for deferred batches too, so
                // progress reports add deferred-phase progress to this fixed offset rather than
                // resetting to a phase-local count (the "2,884 of 209,984" UI regression).
                var immediateProcessedCount = result.SuccessCount + result.FailedCount;
                await ProcessBatchesInParallelAsync(connectedSystem, connector, deferredBatches, result, options,
                    cancellationToken, progressCallback, connectorFactory!, repositoryFactory!, "ExportDeferredBatch",
                    processedExportsOffset: immediateProcessedCount, passTotal: passTotal,
                    unresolvedReferenceNotes: unresolvedReferenceNotes);
            }
            else
            {
                await ProcessDeferredBatchesSequentiallyAsync(connector, connectedSystem, deferredBatches, result, cancellationToken, progressCallback, passTotal,
                    connectedSystem.EffectiveInitialPasswordTimeToLive, unresolvedReferenceNotes);
            }
        }

        // Mark the exports that wrote nothing as deferred in batch. Those written in part were left
        // pending by ProcessBatchSuccessAsync with the same cadence, and counted as written.
        var deferredWholeExports = stillUnresolvedExports.Except(writeInPartExports).ToList();
        if (stillUnresolvedExports.Count > 0)
        {
            var unresolvedMvoIds = CollectUnresolvedMvoIds(stillUnresolvedExports);
            var resolvedMvoIds = csoLookup.Keys.ToHashSet();
            var missingMvoIds = unresolvedMvoIds.Except(resolvedMvoIds).ToList();

            Log.Information("ProcessDeferredExportsAsync: {StillUnresolved} export(s) have unresolved references: " +
                "{WrittenInPart} written in part and pending for their references, {DeferredWhole} deferred whole. " +
                "{Resolved} resolved this cycle. " +
                "{MissingCount} referenced MVO(s) have no CSO in the target system: [{MissingIds}]",
                stillUnresolvedExports.Count, writeInPartExports.Count, deferredWholeExports.Count, resolvedExports.Count,
                missingMvoIds.Count,
                string.Join(", ", missingMvoIds.Take(10).Select(id => id.ToString())));
        }

        if (deferredWholeExports.Count > 0)
        {
            var markedDeferredCount = 0;

            foreach (var export in deferredWholeExports)
            {
                var exportUnresolvedCount = export.AttributeValueChanges.Count(IsUnresolvedReference);
                var exportTotalChanges = export.AttributeValueChanges.Count;
                Log.Debug("ProcessDeferredExportsAsync: Deferring export {ExportId} for CSO {CsoId} - " +
                    "{UnresolvedCount}/{TotalChanges} attribute changes have unresolved references",
                    export.Id, export.ConnectedSystemObjectId, exportUnresolvedCount, exportTotalChanges);

                await MarkExportDeferredAsync(export);
                result.DeferredCount++;

                // Nothing was written, so there is no write to report; but a reference that can never
                // resolve as things stand is reported on its own item under Error handling (issue #1398),
                // otherwise the export would sit deferred for ever with nothing to say why.
                if (unresolvedReferenceNotes.TryGetValue(export.Id, out var note))
                {
                    result.ProcessedExportItems.Add(new ProcessedExportItem
                    {
                        ChangeType = export.ChangeType,
                        ConnectedSystemObject = export.ConnectedSystemObject,
                        PendingExportId = export.Id,
                        Deferred = true,
                        Succeeded = false,
                        UnresolvedReferenceMessage = note
                    }.WithCauseFrom(export));
                }

                // Confirming an export still cannot be written finishes with it as surely as
                // writing it does, so it counts towards this pass's own work. Throttled to keep the
                // progress writes off the per-export path.
                markedDeferredCount++;
                if (markedDeferredCount % 50 == 0 || markedDeferredCount == deferredWholeExports.Count)
                {
                    await ReportProgressAsync(progressCallback, new ExportProgressInfo
                    {
                        Phase = ExportPhase.ResolvingReferences,
                        TotalExports = result.TotalPendingExports,
                        ProcessedExports = result.SuccessCount + result.FailedCount,
                        PassTotal = passTotal,
                        PassProcessed = exportsToWrite.Count + markedDeferredCount,
                        Message = "Recording exports that are still waiting on their references"
                    });
                }
            }
        }
    }

    /// <summary>
    /// Sorts what each still-unresolved export owes into references that are merely waiting (the
    /// referenced Metaverse Object has a Connected System Object in the target, but no anchor yet) and
    /// references that cannot resolve as things stand (no Connected System Object in the target at all:
    /// out of scope for every rule into this system, or not yet provisioned), counting the latter on the
    /// result and, under Error handling, composing the message the referrer's Run Profile Execution Item
    /// will carry (issue #1398). Mirrors the import side's treatment of its own unresolved references:
    /// Error marks the object, Warn leaves the count for the Activity's summary, Ignore logs only.
    /// </summary>
    /// <returns>Per Pending Export id, the message for its item; empty unless handling is Error.</returns>
    private async Task<Dictionary<Guid, string>> BuildUnresolvedReferenceNotesAsync(
        ConnectedSystem connectedSystem,
        List<PendingExport> stillUnresolvedExports,
        Dictionary<Guid, ConnectedSystemObject> csoLookup,
        ExportExecutionResult result)
    {
        var notes = new Dictionary<Guid, string>();
        if (stillUnresolvedExports.Count == 0)
            return notes;

        // (export, change, referenced MVO) for every reference whose target has no object in this system.
        // A reference whose target IS in the lookup is waiting on its anchor, and the deferred pass gets it.
        var unresolvable = stillUnresolvedExports
            .SelectMany(export => export.AttributeValueChanges
                .Where(IsUnresolvedReference)
                .Select(change => (Export: export, Change: change, MvoId: Guid.TryParse(change.UnresolvedReferenceValue, out var mvoId) ? mvoId : (Guid?)null)))
            .Where(u => u.MvoId.HasValue && !csoLookup.ContainsKey(u.MvoId.Value))
            .Select(u => (u.Export, u.Change, MvoId: u.MvoId!.Value))
            .ToList();

        if (unresolvable.Count == 0)
            return notes;

        result.UnresolvableReferenceCount += unresolvable.Count;
        var handling = connectedSystem.UnresolvedReferenceHandling;

        Log.Information("BuildUnresolvedReferenceNotesAsync: {Count} reference value(s) across {ExportCount} export(s) refer to Metaverse Objects with no " +
            "Connected System Object in {SystemName} and cannot be written as things stand (handling: {Handling}).",
            unresolvable.Count, unresolvable.Select(u => u.Export.Id).Distinct().Count(), connectedSystem.Name, handling);

        if (handling == UnresolvedReferenceHandling.Ignore)
        {
            foreach (var (export, change, mvoId) in unresolvable)
                Log.Debug("BuildUnresolvedReferenceNotesAsync: Export {ExportId} attribute {AttrName} refers to MVO {MvoId}, which has no CSO in the target. Ignored per Connected System setting.",
                    export.Id, change.Attribute?.Name ?? $"AttrId={change.AttributeId}", mvoId);
            return notes;
        }

        // Named, not just numbered: an administrator reading "Manager could not be written: 'Ada Ashcroft'
        // has no object in this system" knows what to do; a bare identifier sends them on a search.
        Dictionary<Guid, string?> names;
        using (Diagnostics.Diagnostics.Database.StartSpan("GetReferencedMetaverseObjectNames")
            .SetTag("count", unresolvable.Count))
        {
            names = await SyncRepo.GetMetaverseObjectDisplayNamesAsync(unresolvable.Select(u => u.MvoId).Distinct().ToList());
        }

        foreach (var group in unresolvable.GroupBy(u => u.Export.Id))
        {
            var export = group.First().Export;
            var described = group.Select(u =>
            {
                var attrName = u.Change.Attribute?.Name ?? $"attribute {u.Change.AttributeId}";
                var name = names.TryGetValue(u.MvoId, out var n) && !string.IsNullOrEmpty(n) ? $"'{n}' ({u.MvoId})" : u.MvoId.ToString();
                return $"{attrName} -> {name}";
            }).ToList();

            const int shown = 3;
            var summary = string.Join("; ", described.Take(shown));
            if (described.Count > shown)
                summary += $"; and {described.Count - shown} more";

            var message = $"{described.Count} reference value(s) could not be written because the referenced Metaverse Object has no " +
                          $"Connected System Object in this Connected System: {summary}. The referenced object is out of scope for every " +
                          "Synchronisation Rule into this system, or has not been provisioned yet. Everything else on this export was written; " +
                          "the reference is retried on the deferred cadence and written when the object appears.";

            switch (handling)
            {
                case UnresolvedReferenceHandling.Error:
                    notes[export.Id] = message;
                    Log.Debug("BuildUnresolvedReferenceNotesAsync: Export {ExportId} for CSO {CsoId}: {Message}",
                        export.Id, export.ConnectedSystemObjectId, message);
                    break;

                case UnresolvedReferenceHandling.Warn:
                default:
                    // The item is deliberately left unmarked; the Activity carries a summary count instead.
                    Log.Warning("BuildUnresolvedReferenceNotesAsync: Export {ExportId} for CSO {CsoId}: {Message}",
                        export.Id, export.ConnectedSystemObjectId, message);
                    break;
            }
        }

        return notes;
    }

    // ProcessBatchesSequentiallyAsync removed — sequential batch processing is now
    // inlined in ExecuteUsingCallsWithBatchingAsync to support batch-loading from the database.

    /// <summary>
    /// Processes deferred batches sequentially using the existing connector and DbContext.
    /// </summary>
    /// <param name="passTotal">
    /// How many deferred exports this pass covers, so its progress is reported against its own work
    /// rather than the export's totals.
    /// </param>
    private async Task ProcessDeferredBatchesSequentiallyAsync(
        IConnectorExportUsingCalls connector,
        ConnectedSystem connectedSystem,
        List<List<PendingExport>> batches,
        ExportExecutionResult result,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback,
        int passTotal,
        TimeSpan initialPasswordTimeToLive,
        IReadOnlyDictionary<Guid, string>? unresolvedReferenceNotes = null)
    {
        // Snapshot the counts from the immediate export phase. ProcessBatchSuccessAsync
        // increments result.SuccessCount/FailedCount for deferred batches too, so using
        // the live counters plus processedCount would double-count completed deferred items.
        var immediateProcessedCount = result.SuccessCount + result.FailedCount;
        var processedCount = 0;

        // Sub-phase narration from the connector, carrying the counts JIM owns (issue #637).
        using var connectorProgress = CreateConnectorProgress(progressCallback, subPhase => new ExportProgressInfo
        {
            Phase = ExportPhase.Executing,
            TotalExports = result.TotalPendingExports,
            ProcessedExports = immediateProcessedCount + processedCount,
            PassTotal = passTotal,
            PassProcessed = processedCount,
            Message = subPhase
        });

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Report progress for deferred batch execution
            await ReportProgressAsync(progressCallback, new ExportProgressInfo
            {
                Phase = ExportPhase.Executing,
                TotalExports = result.TotalPendingExports,
                ProcessedExports = immediateProcessedCount + processedCount,
                PassTotal = passTotal,
                PassProcessed = processedCount,
                CurrentBatchSize = batch.Count,
                Message = "Exporting deferred"
            });

            using (Diagnostics.Diagnostics.Database.StartSpan("MarkDeferredBatchAsExecuting")
                .SetTag("batchSize", batch.Count))
            {
                await MarkBatchAsExecutingAsync(batch, SyncRepo);
            }

            List<ConnectedSystemExportResult> exportResults;
            using (Diagnostics.Diagnostics.Connector.StartSpan("ExportDeferredBatch")
                .SetTag("batchSize", batch.Count))
            {
                (exportResults, var deferredRefused) = await ExportBatchAsync(connector, connectedSystem, batch, cancellationToken, connectorProgress);
                result.ClassMembershipRefusedCount += deferredRefused;
            }

            using (Diagnostics.Diagnostics.Database.StartSpan("ProcessDeferredBatchSuccess")
                .SetTag("batchSize", batch.Count))
            {
                await ProcessBatchSuccessAsync(batch, exportResults, result, SyncRepo, initialPasswordTimeToLive, unresolvedReferenceNotes);
            }

            processedCount += batch.Count;
        }
    }

    /// <summary>
    /// Processes multiple batches concurrently using separate DbContext and connector instances per batch.
    /// Each batch task creates its own IRepository (with its own DbContext) and connector instance.
    /// The batch's Pending Exports are re-loaded by ID from the batch's context to ensure proper
    /// change tracking. Progress reporting is serialised via SemaphoreSlim to protect the caller's
    /// shared DbContext.
    /// </summary>
    private async Task ProcessBatchesInParallelAsync(
        ConnectedSystem connectedSystem,
        IConnectorExportUsingCalls primaryConnector,
        List<List<PendingExport>> batches,
        ExportExecutionResult result,
        ExportExecutionOptions options,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback,
        Func<IConnector> connectorFactory,
        Func<ISyncRepositoryScope> repositoryFactory,
        string spanName,
        int processedExportsOffset = 0,
        Func<List<ProcessedExportItem>, Task>? batchCompletedCallback = null,
        int? passTotal = null,
        IReadOnlyDictionary<Guid, string>? unresolvedReferenceNotes = null)
    {
        Log.Information("ProcessBatchesInParallelAsync: Processing {BatchCount} batches with MaxParallelism={MaxParallelism}",
            batches.Count, options.MaxParallelism);

        // Collect batch ID lists - each batch task will re-load its entities from its own context
        var batchIdLists = batches
            .Select(batch => batch.Select(pe => pe.Id).ToList())
            .ToList();

        // Serialise progress reporting to protect the caller's shared DbContext
        using var progressSemaphore = new SemaphoreSlim(1, 1);
        var processedCount = 0;

        // Sub-phase narration from the batch connectors, on the same gate as the per-batch progress
        // below so that concurrent batches cannot report through the caller's DbContext at once.
        using var connectorProgress = CreateConnectorProgress(progressCallback, subPhase => new ExportProgressInfo
        {
            Phase = ExportPhase.Executing,
            TotalExports = result.TotalPendingExports,
            ProcessedExports = processedExportsOffset + Volatile.Read(ref processedCount),
            PassTotal = passTotal,
            PassProcessed = passTotal.HasValue ? Volatile.Read(ref processedCount) : null,
            Message = subPhase
        }, progressSemaphore);

        // Lock for thread-safe result aggregation
        var resultLock = new object();

        using var throttle = new SemaphoreSlim(options.MaxParallelism, options.MaxParallelism);

        var batchTasks = batchIdLists.Select((batchIds, batchIndex) => Task.Run(async () =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Create a per-batch repository scope with its own context; disposing it when
                // the batch completes releases the context and its pooled connection (undisposed
                // scopes pinned one connection per batch and exhausted the pool at scale).
                using var batchScope = repositoryFactory();
                var batchRepo = batchScope.Repository;

                // Re-load this batch's Pending Exports from the batch's own context
                var batch = await batchRepo.GetPendingExportsByIdsAsync(batchIds);
                if (batch.Count == 0)
                {
                    Log.Warning("ProcessBatchesInParallelAsync: Batch {BatchIndex} returned 0 exports for {IdCount} IDs",
                        batchIndex, batchIds.Count);
                    return;
                }

                // Create and prepare a connector for this batch
                IConnectorExportUsingCalls batchConnector;
                if (batchIndex == 0)
                {
                    // First batch uses the already-opened primary connector
                    batchConnector = primaryConnector;
                }
                else
                {
                    var newConnector = connectorFactory();
                    if (newConnector is not IConnectorExportUsingCalls callsConnector)
                    {
                        Log.Error("ProcessBatchesInParallelAsync: Connector factory returned non-calls connector for batch {BatchIndex}", batchIndex);
                        return;
                    }
                    batchConnector = callsConnector;
                    PrepareConnectorForExport(batchConnector);
                    batchConnector.OpenExportConnection(connectedSystem.SettingValues, connectedSystem.PersistedConnectorData);
                }

                // Tracks whether this batch completed without throwing, mirroring the primary
                // connector's exportPhaseSucceeded flag above: used by the finally block below to
                // decide whether a persistence failure while handling CloseExportConnection's return
                // value may propagate on its own, or must be logged and swallowed so it does not
                // replace/mask a batch failure that is already unwinding through the same finally.
                var batchExportSucceeded = false;

                try
                {
                    // Mark batch as executing (raw SQL - context-independent)
                    await batchRepo.MarkPendingExportsAsExecutingAsync(batch);

                    // Execute batch via connector. The batch was re-loaded from persisted state above,
                    // which is why the reference resolutions and the writable/unresolved split (issue
                    // #1398) are persisted before this path runs: ForConnector decides from what it reads.
                    var (exportResults, batchRefused) = await ExportBatchAsync(batchConnector, connectedSystem, batch, cancellationToken, connectorProgress);

                    // Process results using the batch's own repository
                    var batchResult = new ExportExecutionResult { ClassMembershipRefusedCount = batchRefused };
                    await ProcessBatchSuccessAsync(batch, exportResults, batchResult, batchRepo, connectedSystem.EffectiveInitialPasswordTimeToLive, unresolvedReferenceNotes);

                    // Capture created containers from this batch's connector
                    List<string>? batchContainerIds = null;
                    if (batchConnector is IConnectorContainerCreation containerCreator &&
                        containerCreator.CreatedContainerExternalIds.Count > 0)
                    {
                        batchContainerIds = [..containerCreator.CreatedContainerExternalIds];
                    }

                    // Aggregate results into shared result (thread-safe)
                    // Aggregate scalar results into shared result (thread-safe)
                    lock (resultLock)
                    {
                        result.SuccessCount += batchResult.SuccessCount;
                        result.FailedCount += batchResult.FailedCount;
                        result.DeferredCount += batchResult.DeferredCount;
                        result.PartiallyExportedCount += batchResult.PartiallyExportedCount;
                        result.OptimisticApplyAppliedCount += batchResult.OptimisticApplyAppliedCount;
                        result.OptimisticApplySkippedCount += batchResult.OptimisticApplySkippedCount;
                        result.OptimisticApplyFailedCount += batchResult.OptimisticApplyFailedCount;
                        result.OptimisticApplyUnresolvedReferenceCount += batchResult.OptimisticApplyUnresolvedReferenceCount;
                        result.InitialPasswordsStagedCount += batchResult.InitialPasswordsStagedCount;
                        result.InitialPasswordStagingFailedCount += batchResult.InitialPasswordStagingFailedCount;
                        result.ClassMembershipRefusedCount += batchResult.ClassMembershipRefusedCount;
                        if (batchCompletedCallback == null)
                            result.ProcessedExportItems.AddRange(batchResult.ProcessedExportItems);
                        if (batchContainerIds != null)
                        {
                            result.CreatedContainerExternalIds.AddRange(batchContainerIds);
                        }
                    }

                    // Stream processed items per-batch (outside lock — callback may do async I/O)
                    if (batchCompletedCallback != null && batchResult.ProcessedExportItems.Count > 0)
                    {
                        await batchCompletedCallback(batchResult.ProcessedExportItems);
                    }

                    // Report progress (serialised to protect caller's DbContext)
                    var newProcessedCount = Interlocked.Add(ref processedCount, batch.Count);
                    if (progressCallback != null)
                    {
                        await progressSemaphore.WaitAsync(cancellationToken);
                        try
                        {
                            await progressCallback(new ExportProgressInfo
                            {
                                Phase = ExportPhase.Executing,
                                TotalExports = result.TotalPendingExports,
                                ProcessedExports = processedExportsOffset + newProcessedCount,
                                PassTotal = passTotal,
                                PassProcessed = passTotal.HasValue ? newProcessedCount : null,
                                CurrentBatchSize = batch.Count,
                                Message = passTotal.HasValue ? "Exporting deferred" : "Exporting"
                            });
                        }
                        finally
                        {
                            progressSemaphore.Release();
                        }
                    }

                    // Reached only if nothing above threw; used by the finally block below to tell a
                    // genuine batch failure apart from a clean run.
                    batchExportSucceeded = true;
                }
                finally
                {
                    // Close the batch connector (but not the primary - that's managed by the caller)
                    if (batchIndex != 0)
                    {
                        string? closeReturn;
                        try
                        {
                            closeReturn = batchConnector.CloseExportConnection();
                        }
                        finally
                        {
                            (batchConnector as IDisposable)?.Dispose();
                        }

                        // Persist connector state the connector chose to override at close (issue
                        // #230). Null (the overwhelmingly common case) means "nothing to override".
                        if (closeReturn != null)
                        {
                            try
                            {
                                await Application.ConnectedSystems.UpdateConnectedSystemPersistedConnectorDataAsync(connectedSystem, closeReturn);
                            }
                            catch (Exception persistEx) when (!batchExportSucceeded)
                            {
                                // The batch itself already failed and that exception is propagating out
                                // of this finally block. A .NET finally block that itself throws
                                // replaces the in-flight exception rather than chaining it, which would
                                // silently hide the batch's own failure behind an unrelated persistence
                                // error. Log and let the original batch failure continue to unwind.
                                Log.Error(persistEx,
                                    "ProcessBatchesInParallelAsync: Failed to persist connector data returned by CloseExportConnection while batch {BatchIndex} is failing for Connected System {ConnectedSystemId}. The batch's own failure takes precedence and will propagate.",
                                    batchIndex, connectedSystem.Id);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Let cancellation propagate
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ProcessBatchesInParallelAsync: Batch {BatchIndex} failed", batchIndex);

                // Mark this batch's exports as failed and create ProcessedExportItems
                // so the RPEI pipeline can record the failures. Without ProcessedExportItems,
                // the activity would silently report Status: Complete with no error RPEIs.
                lock (resultLock)
                {
                    result.FailedCount += batchIds.Count;
                    for (var i = 0; i < batchIds.Count; i++)
                    {
                        result.ProcessedExportItems.Add(new ProcessedExportItem
                        {
                            ChangeType = PendingExportChangeType.Update,
                            Succeeded = false,
                            ErrorMessage = $"Batch export failed: {ex.Message}",
                            ErrorCount = 1
                        });
                    }
                }
            }
            finally
            {
                throttle.Release();
            }
        }, cancellationToken)).ToList();

        await Task.WhenAll(batchTasks);
    }

    /// <summary>
    /// Marks a batch of exports as executing using raw SQL for efficiency.
    /// Bypasses EF Core change tracking since this is a simple status update.
    /// </summary>
    private static async Task MarkBatchAsExecutingAsync(List<PendingExport> batch, ISyncRepository repository)
    {
        await repository.MarkPendingExportsAsExecutingAsync(batch);
    }

    /// <summary>
    /// Sends a batch to the Connector, holding back any export that would leave an object invalid at the Connected
    /// System and failing it with the attributes named. Results come back in the batch's own order either way.
    /// </summary>
    /// <remarks>
    /// Refusing here rather than letting the Connected System reject the change gives an administrator something
    /// they can act on: "posixAccount requires gidNumber" rather than whichever error the directory chose to
    /// return. It is the same reasoning as the managed-scope refusal in the LDAP Connector.
    /// </remarks>
    internal static async Task<(List<ConnectedSystemExportResult> Results, int Refused)> ExportBatchAsync(
        IConnectorExportUsingCalls connector,
        ConnectedSystem connectedSystem,
        List<PendingExport> batch,
        CancellationToken cancellationToken,
        IConnectorProgress connectorProgress)
    {
        var refusals = new Dictionary<int, ConnectedSystemExportResult>();
        var sendable = new List<PendingExport>();

        for (var i = 0; i < batch.Count; i++)
        {
            var refusal = ClassMembershipValidator.Check(batch[i], connectedSystem);
            if (refusal == null)
                sendable.Add(batch[i]);
            else
                refusals[i] = refusal;
        }

        if (refusals.Count == 0)
            return (await connector.ExportAsync(batch.Select(ForConnector).ToList(), cancellationToken, connectorProgress), 0);

        Log.Warning("ExportBatchAsync: Refused {RefusedCount} of {BatchCount} export(s) on '{ConnectedSystem}' because a class being added has required attributes with no value.",
            refusals.Count, batch.Count, connectedSystem.Name);

        var sentResults = sendable.Count > 0
            ? await connector.ExportAsync(sendable.Select(ForConnector).ToList(), cancellationToken, connectorProgress)
            : [];

        // Rebuild in the batch's order, because the caller pairs results with exports by index.
        var results = new List<ConnectedSystemExportResult>(batch.Count);
        var sentIndex = 0;
        for (var i = 0; i < batch.Count; i++)
        {
            if (refusals.TryGetValue(i, out var refusal))
                results.Add(refusal);
            else
                results.Add(sentIndex < sentResults.Count ? sentResults[sentIndex++] : ConnectedSystemExportResult.Succeeded());
        }

        return (results, refusals.Count);
    }

    /// <summary>
    /// Processes a batch of exports with their corresponding ConnectedSystemExportResult data.
    /// Uses batch updates for efficiency - pre-fetches attribute definitions and performs
    /// a single SaveChanges for all CSO updates.
    /// Accepts an explicit repository parameter to support both sequential (shared) and parallel (per-batch) paths.
    /// </summary>
    /// <param name="unresolvedReferenceNotes">
    /// Per Pending Export id, the message describing the references it could not write this run because
    /// the referenced object has no Connected System Object in the target (issue #1398), built by
    /// <see cref="BuildUnresolvedReferenceNotesAsync"/> under Error handling only. Carried onto the
    /// export's processed item so the Run Profile Execution Item reports the write and the outstanding
    /// reference together, as the import side does. Null when nothing was noted.
    /// </param>
    private async Task ProcessBatchSuccessAsync(
        List<PendingExport> batch,
        List<ConnectedSystemExportResult> exportResults,
        ExportExecutionResult result,
        ISyncRepository repository,
        TimeSpan initialPasswordTimeToLive,
        IReadOnlyDictionary<Guid, string>? unresolvedReferenceNotes = null)
    {
        var exportsToUpdate = new List<PendingExport>();
        var csosToUpdate = new List<(ConnectedSystemObject cso, ConnectedSystemExportResult exportResult)>();
        var successfulNonDeleteExports = new List<PendingExport>();
        var provisionedAccounts = new List<PendingExport>();

        for (var i = 0; i < batch.Count; i++)
        {
            var export = batch[i];
            var exportResult = i < exportResults.Count ? exportResults[i] : ConnectedSystemExportResult.Succeeded();

            // What the connector was actually handed (see ForConnector), and what it was not: reference
            // changes still awaiting resolution stay behind on a partial write (issue #1398).
            var writtenChanges = WritableChanges(export);
            var stillUnresolvedCount = export.AttributeValueChanges.Count(IsUnresolvedReference);
            var wasCreate = export.ChangeType == PendingExportChangeType.Create;

            if (!exportResult.Success)
            {
                // Export failed - mark as failed
                MarkExportFailed(export, exportResult.ErrorMessage ?? "Export failed");
                exportsToUpdate.Add(export);
                result.FailedCount++;

                // Capture export data for activity tracking (before any state changes)
                result.ProcessedExportItems.Add(new ProcessedExportItem
                {
                    ChangeType = export.ChangeType,
                    ConnectedSystemObject = export.ConnectedSystemObject,
                    PendingExportId = export.Id,
                    AttributeChangeCount = writtenChanges.Count,
                    AttributeValueChanges = writtenChanges,
                    Succeeded = false,
                    ErrorMessage = exportResult.ErrorMessage ?? "Export failed",
                    ErrorCount = export.ErrorCount,
                    ErrorType = exportResult.ErrorType
                }.WithCauseFrom(export));
                continue;
            }

            // Capture export data for activity tracking (before deletion)
            result.ProcessedExportItems.Add(new ProcessedExportItem
            {
                ChangeType = export.ChangeType,
                ConnectedSystemObject = export.ConnectedSystemObject,
                PendingExportId = export.Id,
                AttributeChangeCount = writtenChanges.Count,
                AttributeValueChanges = writtenChanges,
                Succeeded = true,
                UnresolvedReferenceMessage = unresolvedReferenceNotes != null && unresolvedReferenceNotes.TryGetValue(export.Id, out var note) ? note : null
            }.WithCauseFrom(export));

            if (stillUnresolvedCount > 0)
            {
                // Written in part (issue #1398): everything that could go has gone, and the export stays
                // pending for the references it still owes, on the deferred cadence. The row now exists,
                // so from here on it is an Update: sending it as a Create again would insert a second row.
                export.Status = PendingExportStatus.Pending;
                export.HasUnresolvedReferences = true;
                export.LastAttemptedAt = DateTime.UtcNow;
                export.NextRetryAt = DateTime.UtcNow.AddMinutes(5);
                if (wasCreate)
                    export.ChangeType = PendingExportChangeType.Update;
                result.PartiallyExportedCount++;
                Log.Information("ProcessBatchSuccessAsync: Exported {ExportId} for CSO {CsoId} in part: {Written} change(s) written, " +
                    "{Unresolved} reference change(s) still awaiting resolution. Next retry at {NextRetry}",
                    export.Id, export.ConnectedSystemObjectId, writtenChanges.Count, stillUnresolvedCount, export.NextRetryAt);
            }
            else
            {
                export.Status = PendingExportStatus.Exported;
            }

            // For Create exports, update the CSO with the system-assigned external ID and status
            // For Update exports with SecondaryExternalId (e.g., LDAP renames), update the CSO's secondary ID
            if (export.ConnectedSystemObject != null &&
                (wasCreate || !string.IsNullOrEmpty(exportResult.SecondaryExternalId)))
            {
                csosToUpdate.Add((export.ConnectedSystemObject, exportResult));
            }

            // Update attribute change statuses to ExportedPendingConfirmation
            UpdateAttributeChangeStatusesAfterExport(writtenChanges);

            exportsToUpdate.Add(export);
            result.SuccessCount++;
            Log.Debug("ProcessBatchSuccessAsync: Successfully exported {ExportId}, awaiting confirmation via import", export.Id);

            // Issue #1079: Delete exports are skipped entirely by optimistic apply (D6, the CSO
            // obsolete/delete lifecycle owns that path) and one without a CSO has nothing to apply
            // values to; both are tracked as skipped rather than silently dropped.
            if (export.ChangeType == PendingExportChangeType.Delete || export.ConnectedSystemObject == null)
                result.OptimisticApplySkippedCount++;
            else
                successfulNonDeleteExports.Add(export);

            // #1121: an account that has just come into existence may be owed an initial password. Only a
            // Create can be: an Update changes an account that already has one, and resetting that would be a
            // password reset nobody asked for.
            if (wasCreate && export.ConnectedSystemObject != null && export.ProvisioningSyncRuleId.HasValue)
                provisionedAccounts.Add(export);
        }

        // Batch update all Pending Exports
        if (exportsToUpdate.Count > 0)
        {
            using (Diagnostics.Diagnostics.Database.StartSpan("UpdatePendingExports")
                .SetTag("count", exportsToUpdate.Count))
            {
                await repository.UpdatePendingExportsAsync(exportsToUpdate);
            }
        }

        // Batch update CSOs that need external ID or status changes
        if (csosToUpdate.Count > 0)
        {
            await BatchUpdateCsosAfterSuccessfulExportAsync(csosToUpdate, repository);
        }

        // #1121: runs AFTER BatchUpdateCsosAfterSuccessfulExportAsync, because the delivery pass finds the
        // account in the Connected System by the external ID that call has just assigned; staging first would
        // record work against an account JIM could not yet address.
        if (provisionedAccounts.Count > 0)
        {
            await StageInitialPasswordsForBatchAsync(provisionedAccounts, result, repository, initialPasswordTimeToLive);
        }

        // Issue #1079: optimistic export apply. Runs LAST, after BatchUpdateCsosAfterSuccessfulExportAsync,
        // so its external-Id additions are already reflected in each CSO's in-memory AttributeValues
        // (D9's dedupe guarantee depends on this ordering; see D11).
        if (successfulNonDeleteExports.Count > 0)
        {
            await ApplyOptimisticExportUpdatesAsync(successfulNonDeleteExports, result, repository);
        }
    }

    /// <summary>
    /// Records that this batch's newly provisioned accounts are owed an initial password (issue #1121).
    /// <para>
    /// Staged, not delivered. Setting a password is a round trip to the Connected System, and doing it here
    /// would put a second network call inside the loop that is persisting the results of one that has already
    /// succeeded: slow at scale, and structurally able to take a successful export down with it. A later pass
    /// opens one password connection per Connected System and works through what is outstanding.
    /// </para>
    /// <para>
    /// Which rules ask for a password is read now rather than stamped onto the export when it was staged, so
    /// that switching the feature on reaches work already queued, and so that a deployment not using it writes
    /// no rows at all.
    /// </para>
    /// <para>
    /// Failure is contained but not swallowed. The accounts exist in the Connected System and their exports are
    /// already recorded as successful; marking them failed would have JIM retry the Create, duplicating objects
    /// or erroring for ever. Unlike the optimistic apply below, though, nothing self-heals a password nobody
    /// knows is owed, so this logs an Error and counts it on the result for the Activity to report, rather than
    /// passing quietly.
    /// </para>
    /// </summary>
    private static async Task StageInitialPasswordsForBatchAsync(
        List<PendingExport> provisionedAccounts,
        ExportExecutionResult result,
        ISyncRepository repository,
        TimeSpan initialPasswordTimeToLive)
    {
        using var span = Diagnostics.Diagnostics.Database.StartSpan("StageInitialPasswords")
            .SetTag("count", provisionedAccounts.Count);

        // Declared out here so the failure count below is the number of accounts genuinely owed a password,
        // once that is known. A failure in the lookup itself leaves it null, and every provisioned account in
        // the batch is reported as unrecorded because JIM cannot tell which of them needed recording.
        List<PendingInitialPassword>? staging = null;
        try
        {
            var provisioningRuleIds = provisionedAccounts
                .Select(pe => pe.ProvisioningSyncRuleId!.Value)
                .Distinct()
                .ToList();

            var rulesAskingForAPassword = await repository.GetSyncRuleIdsWithInitialPasswordEnabledAsync(provisioningRuleIds);
            if (rulesAskingForAPassword.Count == 0)
                return;

            staging = provisionedAccounts
                .Where(pe => rulesAskingForAPassword.Contains(pe.ProvisioningSyncRuleId!.Value))
                .Select(pe => new PendingInitialPassword
                {
                    ConnectedSystemObjectId = pe.ConnectedSystemObject!.Id,
                    ConnectedSystemId = pe.ConnectedSystemId,
                    SyncRuleId = pe.ProvisioningSyncRuleId!.Value,
                    Status = PendingInitialPasswordStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.Add(initialPasswordTimeToLive)
                })
                .ToList();

            if (staging.Count == 0)
                return;

            await repository.StageInitialPasswordsAsync(staging);
            result.InitialPasswordsStagedCount += staging.Count;
            Log.Debug("StageInitialPasswordsForBatchAsync: Staged initial passwords for {Count} newly provisioned accounts on Connected System {ConnectedSystemId}",
                staging.Count, provisionedAccounts[0].ConnectedSystemId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var unrecorded = staging?.Count ?? provisionedAccounts.Count;
            result.InitialPasswordStagingFailedCount += unrecorded;
            Log.Error(ex, "StageInitialPasswordsForBatchAsync: Could not record that {Count} newly provisioned accounts on Connected System {ConnectedSystemId} " +
                "are owed an initial password. The accounts were created successfully; export them again to stage the passwords",
                unrecorded, provisionedAccounts[0].ConnectedSystemId);
        }
    }

    /// <summary>
    /// Applies a batch's successfully exported attribute values to their Connected System Objects'
    /// in-memory state (issue #1079), so the confirming import's diff finds them already present
    /// instead of re-materialising millions of rows. This is a performance optimisation only, never
    /// authoritative: the export itself already succeeded against the Connected System, so any
    /// failure here (calculation, database persistence, reference resolution) is caught, logged as
    /// a Warning (not Error - integration tooling treats ERR lines as fatal for a run, and this
    /// failure does not end the run; the confirming import self-heals by re-materialising the CSO's
    /// attribute values from the target system), and swallowed. It must never fail the batch, the
    /// Pending Export updates, or the Activity (D7).
    /// </summary>
    private async Task ApplyOptimisticExportUpdatesAsync(
        List<PendingExport> successfulNonDeleteExports,
        ExportExecutionResult result,
        ISyncRepository repository)
    {
        using var span = Diagnostics.Diagnostics.Database.StartSpan("OptimisticApply")
            .SetTag("count", successfulNonDeleteExports.Count);
        // A plain Stopwatch alongside the span: OperationSpan.Duration is only valid after
        // Dispose(), so it cannot drive the in-flight slow-instance Warning below. Added per #1079
        // ("255 slow apply instances totalling 77.5 minutes" was diagnosed from the span alone;
        // making a slow batch visible in the logs too, not just traces, costs one Stopwatch).
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Reference changes resolve entirely from the persisted ResolvedReferenceCsoId column
            // (SPEC-1079B); no database lookup is needed here (the run-scoped D5 fallback this
            // replaced is gone).
            // Issue #1398: a reference change left behind on a partial write was not written, so it
            // must not be applied as though it had been; the calculator sees the export without it.
            var delta = OptimisticExportApplyCalculator.CalculateDelta(
                successfulNonDeleteExports.Select(WithoutUnresolvedReferences).ToList());

            // SPEC-1082 D9: any CSO whose attribute values this optimistic apply is about to mutate
            // outside the Full Import stamp path (D6/D7) must have its stored ImportStateHash and
            // ImportStateFingerprint nulled in the SAME persistence call, so a subsequent Full
            // Import never trusts a hash describing values that no longer match. Computed precisely
            // from the removal value IDs (matched against each export's CSO's current in-memory
            // AttributeValues, before removal) plus every addition's owning CSO.
            var removalIdSet = new HashSet<Guid>(delta.RemovalValueIds);
            var affectedCsoIds = new HashSet<Guid>(delta.Additions.Select(a => a.ConnectedSystemObject.Id));
            if (removalIdSet.Count > 0)
            {
                affectedCsoIds.UnionWith(successfulNonDeleteExports
                    .Select(pe => pe.ConnectedSystemObject)
                    .Where(cso => cso != null && cso.AttributeValues.Any(av => removalIdSet.Contains(av.Id)))
                    .Select(cso => cso!.Id)
                    .Distinct());
            }

            if (delta.Additions.Count > 0 || delta.RemovalValueIds.Count > 0)
                await repository.ApplyExportedAttributeValuesAsync(delta.Additions, delta.RemovalValueIds, affectedCsoIds);

            // D10: keep the in-memory CSO graph consistent so later passes in the same run
            // (deferred references, a repeated batch touching the same CSO) compute idempotently.
            if (delta.RemovalValueIds.Count > 0)
            {
                foreach (var cso in successfulNonDeleteExports
                    .Select(pe => pe.ConnectedSystemObject)
                    .Where(cso => cso != null)
                    .Distinct())
                    cso!.AttributeValues.RemoveAll(av => removalIdSet.Contains(av.Id));
            }

            foreach (var addition in delta.Additions)
                addition.ConnectedSystemObject.AttributeValues.Add(addition);

            result.OptimisticApplyAppliedCount += successfulNonDeleteExports.Count;
            result.OptimisticApplyUnresolvedReferenceCount += delta.UnresolvedReferenceCount;

            stopwatch.Stop();
            Log.Debug("ApplyOptimisticExportUpdatesAsync: Applied optimistic export updates for {Count} Pending Exports in " +
                "{ElapsedMs}ms ({Additions} additions, {Removals} removals, {Unresolved} unresolved references, {Skipped} no-op changes)",
                successfulNonDeleteExports.Count, stopwatch.ElapsedMilliseconds, delta.Additions.Count, delta.RemovalValueIds.Count,
                delta.UnresolvedReferenceCount, delta.SkippedChangeCount);

            // #1079: full-scale validation diagnosed 255 slow OptimisticApply instances totalling
            // 77.5 minutes from the span alone; a Warning here makes a slow batch visible in the
            // logs too (Debug is often filtered out in production), without waiting on trace
            // tooling to notice. 1 second is generous for a healthy batch (typically low
            // milliseconds); this fires only when something is genuinely off.
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(1))
            {
                Log.Warning("ApplyOptimisticExportUpdatesAsync: Slow optimistic apply for Connected System {ConnectedSystemId} - " +
                    "{Count} Pending Exports took {ElapsedMs}ms",
                    successfulNonDeleteExports[0].ConnectedSystemId, successfulNonDeleteExports.Count, stopwatch.ElapsedMilliseconds);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.OptimisticApplyFailedCount += successfulNonDeleteExports.Count;
            Log.Warning(ex, "ApplyOptimisticExportUpdatesAsync: Optimistic export apply failed for Connected System {ConnectedSystemId} " +
                "({Count} Pending Exports); the confirming import will self-heal",
                successfulNonDeleteExports[0].ConnectedSystemId, successfulNonDeleteExports.Count);
        }
    }

    /// <summary>
    /// Batch updates multiple CSOs after successful exports in a single database round-trip.
    /// Pre-fetches all required attribute definitions in one query, then applies changes
    /// and saves all CSO updates together.
    /// Accepts an explicit repository parameter to support both sequential (shared) and parallel (per-batch) paths.
    /// </summary>
    /// <summary>
    /// Stores a connector-returned external ID on an attribute value, in the typed slot the anchor
    /// attribute declares.
    /// </summary>
    /// <remarks>
    /// The typed slot matters more than it looks (#1386): the confirming import's attribute diff is
    /// typed, so a Number anchor it expects in IntValue but finds stored as a string is invisible to
    /// it, and the diff stages a typed duplicate alongside. The object then holds two values for its
    /// External ID attribute, which kills the change-record read on the confirming import; nothing is
    /// ever confirmed, and every subsequent synchronisation cycle exports the same objects again,
    /// duplicating rows in the customer's target database.
    /// <para>
    /// A value that does not parse into its declared type is preserved as text and logged as an error
    /// rather than dropped: it means the connector returned a key of the wrong shape, and the value is
    /// the evidence. The confirming import cannot match it, which it reports per object.
    /// </para>
    /// </remarks>
    internal static void ApplyExternalIdToAttributeValue(
        ConnectedSystemObjectAttributeValue attributeValue,
        AttributeDataType? declaredType,
        string externalId,
        Guid csoId)
    {
        // Clear every slot first: this instance may be reused from an earlier export, and a stale
        // value left in another slot would make the anchor ambiguous.
        attributeValue.StringValue = null;
        attributeValue.GuidValue = null;
        attributeValue.IntValue = null;
        attributeValue.LongValue = null;

        switch (declaredType)
        {
            case AttributeDataType.Number when int.TryParse(externalId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue):
                attributeValue.IntValue = intValue;
                return;
            case AttributeDataType.LongNumber when long.TryParse(externalId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue):
                attributeValue.LongValue = longValue;
                return;
            case AttributeDataType.Guid when Guid.TryParse(externalId, out var guidValue):
                attributeValue.GuidValue = guidValue;
                return;
        }

        if (declaredType is AttributeDataType.Number or AttributeDataType.LongNumber or AttributeDataType.Guid)
        {
            Log.Error("ApplyExternalIdToAttributeValue: the Connector returned '{ExternalId}' as the external ID for " +
                "Connected System Object {CsoId}, but the anchor attribute is declared {DeclaredType} and the value does not " +
                "parse into it. Stored as text; the confirming import will not be able to match this object by its anchor.",
                LogSanitiser.Sanitise(externalId), csoId, declaredType);
        }

        attributeValue.StringValue = externalId;
    }

    private async Task BatchUpdateCsosAfterSuccessfulExportAsync(
        List<(ConnectedSystemObject cso, ConnectedSystemExportResult exportResult)> csosToUpdate,
        ISyncRepository repository)
    {
        // Collect all unique attribute IDs we need to look up (external ID + secondary external ID attributes)
        var attributeIds = new HashSet<int>();
        foreach (var (cso, _) in csosToUpdate)
        {
            if (cso.ExternalIdAttributeId > 0)
                attributeIds.Add(cso.ExternalIdAttributeId);
            if (cso.SecondaryExternalIdAttributeId.HasValue)
                attributeIds.Add(cso.SecondaryExternalIdAttributeId.Value);
        }

        // Pre-fetch all attribute definitions in a single query
        Dictionary<int, ConnectedSystemObjectTypeAttribute> attributeLookup;
        using (Diagnostics.Diagnostics.Database.StartSpan("GetAttributesByIds")
            .SetTag("attributeCount", attributeIds.Count))
        {
            attributeLookup = attributeIds.Count > 0
                ? await repository.GetAttributesByIdsAsync(attributeIds)
                : new Dictionary<int, ConnectedSystemObjectTypeAttribute>();
        }

        // Apply changes to each CSO in-memory, tracking old external ID values for cache invalidation
        var csoUpdates = new List<(ConnectedSystemObject cso, List<ConnectedSystemObjectAttributeValue> newAttributeValues)>();
        var cacheEvictions = new List<(int connectedSystemId, int attributeId, string oldValue)>();
        var cacheAdditions = new List<(int connectedSystemId, int attributeId, string newValue, Guid csoId)>();

        foreach (var (cso, exportResult) in csosToUpdate)
        {
            var newAttributeValues = new List<ConnectedSystemObjectAttributeValue>();
            var needsUpdate = false;

            // Populate external ID attribute if provided in the export result
            if (!string.IsNullOrEmpty(exportResult.ExternalId) && cso.ExternalIdAttributeId > 0)
            {
                attributeLookup.TryGetValue(cso.ExternalIdAttributeId, out var externalIdAttribute);

                var externalIdAttrValue = cso.AttributeValues
                    .FirstOrDefault(av => av.AttributeId == cso.ExternalIdAttributeId);

                // Capture old primary external ID value before overwriting for cache invalidation
                var oldPrimaryIdValue = externalIdAttrValue?.StringValue
                    ?? externalIdAttrValue?.GuidValue?.ToString()
                    ?? externalIdAttrValue?.IntValue?.ToString(CultureInfo.InvariantCulture)
                    ?? externalIdAttrValue?.LongValue?.ToString(CultureInfo.InvariantCulture);

                if (externalIdAttrValue == null)
                {
                    externalIdAttrValue = new ConnectedSystemObjectAttributeValue
                    {
                        ConnectedSystemObject = cso,
                        AttributeId = cso.ExternalIdAttributeId
                    };
                    cso.AttributeValues.Add(externalIdAttrValue);
                    newAttributeValues.Add(externalIdAttrValue);
                }

                ApplyExternalIdToAttributeValue(externalIdAttrValue, externalIdAttribute?.Type, exportResult.ExternalId, cso.Id);

                // Track cache invalidation: evict old value if it differs from the new one
                if (oldPrimaryIdValue != null && !oldPrimaryIdValue.Equals(exportResult.ExternalId, StringComparison.OrdinalIgnoreCase))
                    cacheEvictions.Add((cso.ConnectedSystemId, cso.ExternalIdAttributeId, oldPrimaryIdValue));
                cacheAdditions.Add((cso.ConnectedSystemId, cso.ExternalIdAttributeId, exportResult.ExternalId, cso.Id));

                needsUpdate = true;
                Log.Debug("BatchUpdateCsosAfterSuccessfulExportAsync: Set CSO {CsoId} external ID to {ExternalId} (type: {AttrType})",
                    cso.Id, LogSanitiser.Sanitise(exportResult.ExternalId), externalIdAttribute?.Type.ToString() ?? "Unknown");
            }

            // Update secondary external ID if provided
            if (!string.IsNullOrEmpty(exportResult.SecondaryExternalId) && cso.SecondaryExternalIdAttributeId.HasValue)
            {
                var secondaryExternalIdAttrValue = cso.AttributeValues
                    .FirstOrDefault(av => av.AttributeId == cso.SecondaryExternalIdAttributeId.Value);

                // Capture old secondary external ID value before overwriting for cache invalidation
                var oldSecondaryIdValue = secondaryExternalIdAttrValue?.StringValue;

                if (secondaryExternalIdAttrValue == null)
                {
                    secondaryExternalIdAttrValue = new ConnectedSystemObjectAttributeValue
                    {
                        ConnectedSystemObject = cso,
                        AttributeId = cso.SecondaryExternalIdAttributeId.Value
                    };
                    cso.AttributeValues.Add(secondaryExternalIdAttrValue);
                    newAttributeValues.Add(secondaryExternalIdAttrValue);
                }

                secondaryExternalIdAttrValue.StringValue = exportResult.SecondaryExternalId;

                // Track cache invalidation: evict old value if it differs from the new one
                if (oldSecondaryIdValue != null && !oldSecondaryIdValue.Equals(exportResult.SecondaryExternalId, StringComparison.OrdinalIgnoreCase))
                    cacheEvictions.Add((cso.ConnectedSystemId, cso.SecondaryExternalIdAttributeId.Value, oldSecondaryIdValue));
                cacheAdditions.Add((cso.ConnectedSystemId, cso.SecondaryExternalIdAttributeId.Value, exportResult.SecondaryExternalId, cso.Id));

                needsUpdate = true;
                Log.Debug("BatchUpdateCsosAfterSuccessfulExportAsync: Set CSO {CsoId} secondary external ID to {SecondaryExternalId}",
                    cso.Id, LogSanitiser.Sanitise(exportResult.SecondaryExternalId));
            }

            if (needsUpdate)
            {
                csoUpdates.Add((cso, newAttributeValues));
            }
        }

        // Single batch save for all CSO updates
        if (csoUpdates.Count > 0)
        {
            using (Diagnostics.Diagnostics.Database.StartSpan("BatchUpdateCsoAttributeValues")
                .SetTag("csoCount", csoUpdates.Count))
            {
                await repository.UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(csoUpdates);
            }
            Log.Information("BatchUpdateCsosAfterSuccessfulExportAsync: Batch updated {Count} CSOs", csoUpdates.Count);
        }

        // Update cache after successful persistence: evict stale entries, then add current ones
        foreach (var (connectedSystemId, attributeId, oldValue) in cacheEvictions)
            Application.ConnectedSystems.EvictCsoFromCache(connectedSystemId, attributeId, oldValue);
        foreach (var (connectedSystemId, attributeId, newValue, csoId) in cacheAdditions)
            Application.ConnectedSystems.AddCsoToCache(connectedSystemId, attributeId, newValue, csoId);
    }

    /// <summary>
    /// Marks an export as failed and applies retry logic (Q6 decision).
    /// This is a synchronous version for batch processing - does not save to database.
    /// </summary>
    private static void MarkExportFailed(PendingExport export, string errorMessage, string? stackTrace = null)
    {
        export.ErrorCount++;
        export.LastErrorMessage = errorMessage;
        export.LastErrorStackTrace = stackTrace;
        export.LastAttemptedAt = DateTime.UtcNow;
        export.NextRetryAt = CalculateNextRetryTime(export.ErrorCount);

        // If max retries exceeded, mark as Failed (Q6 decision - requires manual intervention)
        if (export.ErrorCount >= export.MaxRetries)
        {
            export.Status = PendingExportStatus.Failed;
            Log.Warning("MarkExportFailed: Export {ExportId} has exceeded max retries ({MaxRetries}). Requires manual intervention.",
                export.Id, export.MaxRetries);
        }
        else
        {
            // Keep as Pending while we're still retrying
            export.Status = PendingExportStatus.Pending;
            Log.Warning("MarkExportFailed: Export {ExportId} failed (attempt {Attempt}/{MaxRetries}). Next retry at {NextRetry}. Error: {Error}",
                export.Id, export.ErrorCount, export.MaxRetries, export.NextRetryAt, LogSanitiser.Sanitise(errorMessage));
        }
    }

    /// <summary>
    /// Updates the CSO after a successful export.
    /// For Create exports, transitions the CSO from PendingProvisioning to Normal status
    /// and populates the external ID attribute with the system-assigned value.
    /// </summary>
    private async Task UpdateCsoAfterSuccessfulExportAsync(ConnectedSystemObject cso, ConnectedSystemExportResult? exportResult = null)
    {
        var needsUpdate = false;
        var newAttributeValues = new List<ConnectedSystemObjectAttributeValue>();

        // Note: We do NOT transition CSO status from PendingProvisioning to Normal here.
        // The CSO should remain PendingProvisioning until the confirming import verifies
        // that the object actually exists in the target system. This allows the confirming
        // import to match the CSO by secondary external ID (e.g., distinguishedName) since
        // the primary external ID (e.g., objectGUID) is typically system-assigned and not
        // known until the confirming import.

        // Populate external ID attribute if provided in the export result
        if (exportResult != null && !string.IsNullOrEmpty(exportResult.ExternalId) && cso.ExternalIdAttributeId > 0)
        {
            // Get the attribute definition to determine the correct data type
            var externalIdAttribute = await SyncRepo.GetAttributeAsync(cso.ExternalIdAttributeId);

            // Find or create the external ID attribute value
            var externalIdAttrValue = cso.AttributeValues
                .FirstOrDefault(av => av.AttributeId == cso.ExternalIdAttributeId);

            if (externalIdAttrValue == null)
            {
                // Create new attribute value for external ID
                externalIdAttrValue = new ConnectedSystemObjectAttributeValue
                {
                    ConnectedSystemObject = cso,
                    AttributeId = cso.ExternalIdAttributeId
                };
                cso.AttributeValues.Add(externalIdAttrValue);
                newAttributeValues.Add(externalIdAttrValue);
            }

            // Set the external ID value based on the attribute's data type
            // This ensures consistency with how import stores values
            if (externalIdAttribute?.Type == AttributeDataType.Guid && Guid.TryParse(exportResult.ExternalId, out var guidValue))
            {
                externalIdAttrValue.GuidValue = guidValue;
                externalIdAttrValue.StringValue = null;
            }
            else
            {
                // For Text or other types, or if attribute type is unknown, store as string
                externalIdAttrValue.StringValue = exportResult.ExternalId;
                externalIdAttrValue.GuidValue = null;
            }

            needsUpdate = true;
            Log.Information("UpdateCsoAfterSuccessfulExportAsync: Set CSO {CsoId} external ID to {ExternalId} (type: {AttrType})",
                cso.Id, LogSanitiser.Sanitise(exportResult.ExternalId), externalIdAttribute?.Type.ToString() ?? "Unknown");
        }

        // Update secondary external ID if provided
        if (exportResult != null && !string.IsNullOrEmpty(exportResult.SecondaryExternalId) && cso.SecondaryExternalIdAttributeId.HasValue)
        {
            var secondaryExternalIdAttrValue = cso.AttributeValues
                .FirstOrDefault(av => av.AttributeId == cso.SecondaryExternalIdAttributeId.Value);

            if (secondaryExternalIdAttrValue == null)
            {
                secondaryExternalIdAttrValue = new ConnectedSystemObjectAttributeValue
                {
                    ConnectedSystemObject = cso,
                    AttributeId = cso.SecondaryExternalIdAttributeId.Value
                };
                cso.AttributeValues.Add(secondaryExternalIdAttrValue);
                newAttributeValues.Add(secondaryExternalIdAttrValue);
            }

            secondaryExternalIdAttrValue.StringValue = exportResult.SecondaryExternalId;
            needsUpdate = true;
            Log.Debug("UpdateCsoAfterSuccessfulExportAsync: Set CSO {CsoId} secondary external ID to {SecondaryExternalId}",
                cso.Id, LogSanitiser.Sanitise(exportResult.SecondaryExternalId));
        }

        if (needsUpdate)
        {
            // Explicitly add new attribute values to ensure they are tracked by EF Core
            // This handles the case where the CSO was loaded without attribute values (PendingProvisioning)
            // and we're adding new values that need to be persisted
            await SyncRepo.UpdateConnectedSystemObjectWithNewAttributeValuesAsync(cso, newAttributeValues);
            Log.Information("UpdateCsoAfterSuccessfulExportAsync: Updated CSO {CsoId}", cso.Id);
        }
    }

    /// <summary>
    /// Updates the status of attribute changes after a successful export.
    /// Changes with Pending or ExportedNotConfirmed status are transitioned to ExportedPendingConfirmation.
    /// </summary>
    private static void UpdateAttributeChangeStatusesAfterExport(PendingExport export) =>
        UpdateAttributeChangeStatusesAfterExport(export.AttributeValueChanges);

    /// <summary>
    /// Marks the changes that were actually handed to the connector as awaiting confirmation. Passed the
    /// written subset explicitly (issue #1398) so a reference change left behind on a partial write is
    /// not marked as sent when it was not.
    /// </summary>
    private static void UpdateAttributeChangeStatusesAfterExport(IEnumerable<PendingExportAttributeValueChange> writtenChanges)
    {
        var now = DateTime.UtcNow;

        foreach (var attrChange in writtenChanges)
        {
            // Only update changes that were pending or being retried
            if (attrChange.Status == PendingExportAttributeChangeStatus.Pending ||
                attrChange.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed)
            {
                attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
                attrChange.ExportAttemptCount++;
                attrChange.LastExportedAt = now;

                Log.Debug("UpdateAttributeChangeStatusesAfterExport: Attribute {AttrId} status set to ExportedPendingConfirmation (attempt {Attempt})",
                    attrChange.AttributeId, attrChange.ExportAttemptCount);
            }
        }
    }

    /// <summary>
    /// Executes exports using the IConnectorExportUsingFiles interface with batching.
    /// </summary>
    private async Task ExecuteUsingFilesWithBatchingAsync(
        ConnectedSystem connectedSystem,
        IConnectorExportUsingFiles connector,
        List<PendingExport> pendingExports,
        ExportExecutionResult result,
        ExportExecutionOptions options,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            await ReportProgressAsync(progressCallback, new ExportProgressInfo
            {
                Phase = ExportPhase.Executing,
                TotalExports = result.TotalPendingExports,
                ProcessedExports = 0,
                Message = $"Exporting {pendingExports.Count} changes to file"
            });

            // File-based export - execute all at once (file connectors typically batch internally).
            // The counts cannot move while the connector holds the call, so its sub-phase narration is
            // the only thing that tells an operator the run is alive (issue #637).
            using var connectorProgress = CreateConnectorProgress(progressCallback, subPhase => new ExportProgressInfo
            {
                Phase = ExportPhase.Executing,
                TotalExports = result.TotalPendingExports,
                ProcessedExports = result.SuccessCount + result.FailedCount,
                Message = subPhase
            });

            var exportResults = await connector.ExportAsync(connectedSystem.SettingValues, pendingExports, cancellationToken, connectorProgress);

            // Check if the connector supports auto-confirm and the setting is enabled
            var autoConfirm = false;
            if (connector is IConnectorCapabilities caps && caps.SupportsAutoConfirmExport)
            {
                var autoConfirmSetting = connectedSystem.SettingValues
                    .SingleOrDefault(s => s.Setting.Name == "Auto-Confirm Exports");
                autoConfirm = autoConfirmSetting?.CheckboxValue ?? true; // default true when capability exists
            }

            // Process exports and collect for batch operations
            var exportsToUpdate = new List<PendingExport>();
            var exportsToDelete = new List<PendingExport>();
            var csosToUpdate = new List<(ConnectedSystemObject cso, ConnectedSystemExportResult exportResult)>();

            for (var i = 0; i < pendingExports.Count; i++)
            {
                var export = pendingExports[i];
                var exportResult = i < exportResults.Count ? exportResults[i] : ConnectedSystemExportResult.Succeeded();

                if (!exportResult.Success)
                {
                    MarkExportFailed(export, exportResult.ErrorMessage ?? "Export failed");
                    exportsToUpdate.Add(export);
                    result.FailedCount++;

                    // Capture export data for activity tracking
                    result.ProcessedExportItems.Add(new ProcessedExportItem
                    {
                        ChangeType = export.ChangeType,
                        ConnectedSystemObject = export.ConnectedSystemObject,
                        AttributeChangeCount = export.AttributeValueChanges.Count,
                        AttributeValueChanges = export.AttributeValueChanges.ToList(),
                        Succeeded = false,
                        ErrorMessage = exportResult.ErrorMessage ?? "Export failed",
                        ErrorCount = export.ErrorCount,
                        ErrorType = exportResult.ErrorType
                    }.WithCauseFrom(export));
                    continue;
                }

                // Capture export data for activity tracking (before deletion or status update)
                result.ProcessedExportItems.Add(new ProcessedExportItem
                {
                    ChangeType = export.ChangeType,
                    ConnectedSystemObject = export.ConnectedSystemObject,
                    AttributeChangeCount = export.AttributeValueChanges.Count,
                    AttributeValueChanges = export.AttributeValueChanges.ToList(),
                    Succeeded = true
                }.WithCauseFrom(export));

                // For Create exports, update the CSO status from PendingProvisioning to Normal
                if (export.ChangeType == PendingExportChangeType.Create && export.ConnectedSystemObject != null)
                {
                    csosToUpdate.Add((export.ConnectedSystemObject, exportResult));
                }

                // Update attribute change statuses to ExportedPendingConfirmation
                UpdateAttributeChangeStatusesAfterExport(export);

                // Issue #1079 (optimistic export apply): deliberately NOT wired up here. This
                // path's batch loader (GetExecutableExportsAsync) does not include the CSO's
                // current AttributeValues, unlike the calls-path loader (GetExecutableExportBatchAsync);
                // widening that include for every file export at scale is a memory-profile trade-off
                // not taken here. File-connector exports keep today's behaviour: the confirming
                // import re-materialises the CSO's attribute values as before.

                if (autoConfirm)
                {
                    // Auto-confirm: for file-based exports where the file system is the source of truth,
                    // we can consider the export confirmed immediately
                    exportsToDelete.Add(export);
                }
                else
                {
                    // Standard behaviour: mark as exported, will be confirmed on next import
                    export.Status = PendingExportStatus.Exported;
                    export.LastAttemptedAt = DateTime.UtcNow;
                    exportsToUpdate.Add(export);
                }
                result.SuccessCount++;
            }

            // Batch update exports that need updating
            if (exportsToUpdate.Count > 0)
            {
                using (Diagnostics.Diagnostics.Database.StartSpan("UpdatePendingExports")
                    .SetTag("count", exportsToUpdate.Count))
                {
                    await SyncRepo.UpdatePendingExportsAsync(exportsToUpdate);
                }
            }

            // Batch delete exports that are auto-confirmed
            if (exportsToDelete.Count > 0)
            {
                using (Diagnostics.Diagnostics.Database.StartSpan("DeletePendingExports")
                    .SetTag("count", exportsToDelete.Count))
                {
                    await SyncRepo.DeletePendingExportsAsync(exportsToDelete);
                }
            }

            // Batch update CSOs that need external ID or status changes
            if (csosToUpdate.Count > 0)
            {
                await BatchUpdateCsosAfterSuccessfulExportAsync(csosToUpdate, SyncRepo);
            }

            Log.Information("ExecuteUsingFilesWithBatchingAsync: Exported {Count} changes to file for {SystemName}",
                pendingExports.Count, connectedSystem.Name);
        }
        catch (OperationCanceledException)
        {
            Log.Information("ExecuteUsingFilesWithBatchingAsync: Export cancelled for {SystemName}", connectedSystem.Name);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteUsingFilesWithBatchingAsync: Failed to export to file for {SystemName}", connectedSystem.Name);

            // Mark all as failed using batch update
            var now = DateTime.UtcNow;
            foreach (var export in pendingExports)
            {
                export.ErrorCount++;
                export.LastErrorMessage = ex.Message;
                export.LastErrorStackTrace = ex.StackTrace;
                export.LastAttemptedAt = now;
                export.NextRetryAt = CalculateNextRetryTime(export.ErrorCount);
                export.Status = PendingExportStatus.ExportNotConfirmed;

                // Create ProcessedExportItem so the RPEI pipeline can record the failure.
                // Without this, no RPEIs are generated and the activity silently reports
                // Status: Complete despite all exports having failed.
                result.ProcessedExportItems.Add(new ProcessedExportItem
                {
                    ChangeType = export.ChangeType,
                    ConnectedSystemObject = export.ConnectedSystemObject,
                    AttributeChangeCount = export.AttributeValueChanges.Count,
                    AttributeValueChanges = export.AttributeValueChanges.ToList(),
                    Succeeded = false,
                    ErrorMessage = $"Export failed: {ex.Message}",
                    ErrorCount = export.ErrorCount,
                    ErrorType = ConnectedSystemExportErrorType.General
                }.WithCauseFrom(export));
            }
            using (Diagnostics.Diagnostics.Database.StartSpan("UpdateFailedExports")
                .SetTag("count", pendingExports.Count))
            {
                await SyncRepo.UpdatePendingExportsAsync(pendingExports);
            }

            result.FailedCount = pendingExports.Count;
        }
    }

    /// <summary>
    /// Whether an attribute change still carries a reference that has not been resolved to a value the
    /// Connected System understands.
    /// </summary>
    internal static bool IsUnresolvedReference(PendingExportAttributeValueChange change) =>
        !string.IsNullOrEmpty(change.UnresolvedReferenceValue);

    /// <summary>
    /// Whether an attribute change is one the connector should be handed on this run: not a reference
    /// still awaiting resolution (issue #1398), and not a value already sent and awaiting the confirming
    /// import (re-sending an awaiting value is at best a no-op and, for a multi-valued LDAP add, an
    /// error). Everything else, a failed change included, is sent exactly as it always was.
    /// </summary>
    internal static bool IsWritableNow(PendingExportAttributeValueChange change) =>
        !IsUnresolvedReference(change) &&
        change.Status != PendingExportAttributeChangeStatus.ExportedPendingConfirmation;

    /// <summary>
    /// The attribute changes a connector is handed for a Pending Export on this run. See
    /// <see cref="IsWritableNow"/>.
    /// </summary>
    internal static List<PendingExportAttributeValueChange> WritableChanges(PendingExport pendingExport) =>
        pendingExport.AttributeValueChanges.Where(IsWritableNow).ToList();

    /// <summary>
    /// The view of a Pending Export handed to a connector: the export as it stands, carrying only the
    /// changes that can be written now (issue #1398). Returns the instance itself when every change is
    /// writable, which is the ordinary case, and a shallow copy sharing the Connected System Object and
    /// everything else otherwise. Connectors read the export and never mutate it, and every result is
    /// processed against the original instance, so the copy never has to be reconciled back.
    /// </summary>
    internal static PendingExport ForConnector(PendingExport pendingExport) =>
        pendingExport.AttributeValueChanges.All(IsWritableNow)
            ? pendingExport
            : WithChanges(pendingExport, WritableChanges(pendingExport));

    /// <summary>
    /// A shallow copy of a Pending Export carrying a different set of attribute changes and sharing
    /// everything else (the Connected System Object included) with the original.
    /// </summary>
    private static PendingExport WithChanges(PendingExport pendingExport, List<PendingExportAttributeValueChange> changes) => new()
    {
        Id = pendingExport.Id,
        ConnectedSystem = pendingExport.ConnectedSystem,
        ConnectedSystemId = pendingExport.ConnectedSystemId,
        ConnectedSystemObject = pendingExport.ConnectedSystemObject,
        ConnectedSystemObjectId = pendingExport.ConnectedSystemObjectId,
        ChangeType = pendingExport.ChangeType,
        Status = pendingExport.Status,
        ErrorCount = pendingExport.ErrorCount,
        MaxRetries = pendingExport.MaxRetries,
        LastAttemptedAt = pendingExport.LastAttemptedAt,
        NextRetryAt = pendingExport.NextRetryAt,
        LastErrorMessage = pendingExport.LastErrorMessage,
        LastErrorStackTrace = pendingExport.LastErrorStackTrace,
        SourceMetaverseObject = pendingExport.SourceMetaverseObject,
        SourceMetaverseObjectId = pendingExport.SourceMetaverseObjectId,
        HasUnresolvedReferences = pendingExport.HasUnresolvedReferences,
        ProvisioningSyncRule = pendingExport.ProvisioningSyncRule,
        ProvisioningSyncRuleId = pendingExport.ProvisioningSyncRuleId,
        CreatedAt = pendingExport.CreatedAt,
        AttributeValueChanges = changes
    };

    /// <summary>
    /// The export as optimistic apply should see it (issue #1398): without the reference changes it
    /// still owes. Values sent on an earlier partial write and now awaiting confirmation are kept, since
    /// re-applying them is a no-op by construction (add-if-absent, set-if-different).
    /// </summary>
    private static PendingExport WithoutUnresolvedReferences(PendingExport pendingExport) =>
        pendingExport.AttributeValueChanges.Any(IsUnresolvedReference)
            ? WithChanges(pendingExport, pendingExport.AttributeValueChanges.Where(c => !IsUnresolvedReference(c)).ToList())
            : pendingExport;

    /// <summary>
    /// Whether a Pending Export that could not be fully resolved still has something worth sending
    /// now (issue #1398): a Create inserts its row without the reference columns, an Update writes the
    /// members it can. One with nothing writable stays deferred whole, exactly as before.
    /// </summary>
    internal static bool CanWriteInPart(PendingExport pendingExport) =>
        pendingExport.AttributeValueChanges.Any(IsWritableNow);

    /// <summary>
    /// Collects all unresolved MVO IDs from a list of Pending Exports.
    /// Used to pre-fetch CSO mappings in bulk before resolving references.
    /// </summary>
    private static HashSet<Guid> CollectUnresolvedMvoIds(IEnumerable<PendingExport> exports)
    {
        var mvoIds = new HashSet<Guid>();
        foreach (var export in exports)
        {
            foreach (var attrChange in export.AttributeValueChanges)
            {
                if (!string.IsNullOrEmpty(attrChange.UnresolvedReferenceValue) &&
                    Guid.TryParse(attrChange.UnresolvedReferenceValue, out var mvoId))
                {
                    mvoIds.Add(mvoId);
                }
            }
        }
        return mvoIds;
    }

    /// <summary>
    /// Attempts to resolve unresolved reference attributes in a Pending Export using a pre-fetched CSO lookup.
    /// For LDAP systems, references like 'member' need to be resolved to Distinguished Names (DN),
    /// not the primary external ID (objectGUID). We use the secondary external ID when available.
    /// </summary>
    internal static bool TryResolveReferencesFromLookup(PendingExport pendingExport, Dictionary<Guid, ConnectedSystemObject> csoLookup)
    {
        var allResolved = true;

        foreach (var attrChange in pendingExport.AttributeValueChanges)
        {
            if (string.IsNullOrEmpty(attrChange.UnresolvedReferenceValue))
                continue;

            // The unresolved reference value contains an MVO ID
            if (!Guid.TryParse(attrChange.UnresolvedReferenceValue, out var referencedMvoId))
                continue;

            // Look up the CSO from the pre-fetched dictionary
            if (csoLookup.TryGetValue(referencedMvoId, out var referencedCso))
            {
                // For reference attributes, prefer the secondary external ID (e.g., DN for LDAP)
                // as this is what the Connected System uses for references.
                // Fall back to primary external ID if secondary is not available.
                var secondaryExternalIdAttr = referencedCso.AttributeValues
                    .FirstOrDefault(av => av.Attribute?.IsSecondaryExternalId == true);

                var externalIdAttr = referencedCso.AttributeValues
                    .FirstOrDefault(av => av.Attribute?.IsExternalId == true);

                // Use secondary external ID (DN) if available, otherwise fall back to primary
                var resolvedAttr = secondaryExternalIdAttr ?? externalIdAttr;
                var resolvedValue = resolvedAttr?.ToReferenceValueString();

                if (resolvedValue != null)
                {
                    attrChange.StringValue = resolvedValue;
                    attrChange.UnresolvedReferenceValue = null;

                    // Issue #1079 (optimistic export apply, persisted per SPEC-1079B): the
                    // referenced CSO is in hand right here, so stamp its Id. This is the single
                    // resolution site; the column then persists with the rest of the change, so
                    // optimistic apply can populate ConnectedSystemObjectAttributeValue.ReferenceValueId
                    // without a further database round-trip, this run or any later one.
                    attrChange.ResolvedReferenceCsoId = referencedCso.Id;

                    Log.Debug("Resolved reference for MVO {MvoId} to {Value} using {IdType}",
                        referencedMvoId,
                        LogSanitiser.Sanitise(attrChange.StringValue),
                        secondaryExternalIdAttr != null ? "secondary external ID (DN)" : "primary external ID");
                }
                else
                {
                    // The referenced object exists but its anchor is not yet known: typically a
                    // database-generated anchor whose own export has not been confirmed. Stamping
                    // the change resolved here would send a null anchor to the connector (#1398);
                    // leaving it unresolved keeps the export deferred until the anchor arrives.
                    Log.Debug("TryResolveReferencesFromLookup: Cannot resolve reference for PE {PeId}: " +
                        "CSO {CsoId} (MVO {MvoId}) does not hold an anchor value yet. Export stays deferred.",
                        pendingExport.Id, referencedCso.Id, referencedMvoId);
                    allResolved = false;
                }
            }
            else
            {
                // Still unresolved - CSO doesn't exist yet in target system
                Log.Debug("TryResolveReferencesFromLookup: Cannot resolve reference for PE {PeId}: " +
                    "MVO {MvoId} has no CSO in target system. " +
                    "Attribute: {AttrName}, UnresolvedValue: {UnresolvedValue}",
                    pendingExport.Id, referencedMvoId,
                    attrChange.Attribute?.Name ?? $"AttrId={attrChange.AttributeId}",
                    LogSanitiser.Sanitise(attrChange.UnresolvedReferenceValue));
                allResolved = false;
            }
        }

        return allResolved;
    }

    /// <summary>
    /// Executes a second pass for deferred references that might now be resolvable.
    /// Pre-fetches all referenced CSOs in a single query to avoid N+1 lookups.
    /// </summary>
    private async Task ExecuteDeferredReferencesAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        ExportExecutionResult result)
    {
        // Get any exports that were marked as having unresolved references. Filtered in SQL:
        // loading every Pending Export here cost ~11 minutes at 500k scale with zero
        // deferred exports (#1102).
        List<PendingExport> unresolvedExports;
        using (Diagnostics.Diagnostics.Database.StartSpan("GetPendingExportsForDeferredResolution"))
        {
            unresolvedExports = await SyncRepo.GetPendingExportsWithUnresolvedReferencesAsync(connectedSystem.Id);
        }

        if (unresolvedExports.Count == 0)
            return;

        Log.Information("ExecuteDeferredReferencesAsync: Checking {Count} deferred export(s) from previous cycles for reference resolution",
            unresolvedExports.Count);

        // Bulk pre-fetch all referenced CSOs in a single query
        var mvoIds = CollectUnresolvedMvoIds(unresolvedExports);
        Dictionary<Guid, ConnectedSystemObject> csoLookup;
        using (Diagnostics.Diagnostics.Database.StartSpan("BulkFetchCsosByMvoIds")
            .SetTag("mvoIdCount", mvoIds.Count))
        {
            csoLookup = mvoIds.Count > 0
                ? await SyncRepo.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(mvoIds, connectedSystem.Id)
                : new Dictionary<Guid, ConnectedSystemObject>();
        }

        // Resolve references using the pre-fetched lookup and collect resolved exports for batch update
        var resolvedExports = new List<PendingExport>();
        var stillUnresolvedCount = 0;
        foreach (var export in unresolvedExports)
        {
            var resolved = TryResolveReferencesFromLookup(export, csoLookup);
            if (resolved)
            {
                export.HasUnresolvedReferences = false;
                resolvedExports.Add(export);
                Log.Debug("ExecuteDeferredReferencesAsync: Resolved references for export {ExportId}", export.Id);
            }
            else
            {
                stillUnresolvedCount++;
                var unresolvedRefCount = export.AttributeValueChanges
                    .Count(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue));
                Log.Warning("ExecuteDeferredReferencesAsync: Export {ExportId} for CSO {CsoId} still has " +
                    "{UnresolvedCount} unresolved reference(s) after second-pass resolution attempt",
                    export.Id, export.ConnectedSystemObjectId, unresolvedRefCount);
            }
        }

        Log.Information("ExecuteDeferredReferencesAsync: Second-pass resolution complete. " +
            "{Resolved}/{Total} deferred export(s) resolved, {StillUnresolved} still pending",
            resolvedExports.Count, unresolvedExports.Count, stillUnresolvedCount);

        // Batch update all resolved exports in a single SaveChanges
        if (resolvedExports.Count > 0)
        {
            using (Diagnostics.Diagnostics.Database.StartSpan("UpdateResolvedDeferredExports")
                .SetTag("count", resolvedExports.Count))
            {
                await SyncRepo.UpdatePendingExportsAsync(resolvedExports);
            }
        }
    }

    /// <summary>
    /// Processes a successful export execution with ConnectedSystemExportResult data.
    /// </summary>
    private async Task ProcessExportSuccessAsync(PendingExport export, ConnectedSystemExportResult exportResult, ExportExecutionResult result)
    {
        if (!exportResult.Success)
        {
            await MarkExportFailedAsync(export, exportResult.ErrorMessage ?? "Export failed");
            result.FailedCount++;

            // Capture export data for activity tracking (before any state changes)
            result.ProcessedExportItems.Add(new ProcessedExportItem
            {
                ChangeType = export.ChangeType,
                ConnectedSystemObject = export.ConnectedSystemObject,
                AttributeChangeCount = export.AttributeValueChanges.Count,
                AttributeValueChanges = export.AttributeValueChanges.ToList(),
                Succeeded = false,
                ErrorMessage = exportResult.ErrorMessage ?? "Export failed",
                ErrorCount = export.ErrorCount,
                ErrorType = exportResult.ErrorType
            }.WithCauseFrom(export));
            return;
        }

        // Capture export data for activity tracking (before deletion)
        result.ProcessedExportItems.Add(new ProcessedExportItem
        {
            ChangeType = export.ChangeType,
            ConnectedSystemObject = export.ConnectedSystemObject,
            AttributeChangeCount = export.AttributeValueChanges.Count,
            AttributeValueChanges = export.AttributeValueChanges.ToList(),
            Succeeded = true
        }.WithCauseFrom(export));

        export.Status = PendingExportStatus.Exported;

        // For Create exports, update the CSO with external ID and status
        if (export.ChangeType == PendingExportChangeType.Create && export.ConnectedSystemObject != null)
        {
            await UpdateCsoAfterSuccessfulExportAsync(export.ConnectedSystemObject, exportResult);
        }

        // Update attribute change statuses to ExportedPendingConfirmation
        // They will be confirmed (and deleted) or marked for retry during the next import
        UpdateAttributeChangeStatusesAfterExport(export);

        await SyncRepo.UpdatePendingExportAsync(export);

        result.SuccessCount++;
        Log.Debug("ProcessExportSuccessAsync: Successfully exported {ExportId}, awaiting confirmation via import", export.Id);
    }

    /// <summary>
    /// Marks an export as deferred (has unresolved references).
    /// Does not increment error count since this is expected behaviour.
    /// </summary>
    private async Task MarkExportDeferredAsync(PendingExport export)
    {
        export.Status = PendingExportStatus.Pending;
        export.HasUnresolvedReferences = true;
        export.LastAttemptedAt = DateTime.UtcNow;
        // Use a shorter retry interval for deferred references
        export.NextRetryAt = DateTime.UtcNow.AddMinutes(5);

        var unresolvedRefCount = export.AttributeValueChanges
            .Count(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue));
        var totalChanges = export.AttributeValueChanges.Count;

        await SyncRepo.UpdatePendingExportAsync(export);
        Log.Information("MarkExportDeferredAsync: Export {ExportId} for CSO {CsoId} deferred - " +
            "{UnresolvedCount}/{TotalChanges} attribute changes have unresolved references. Next retry at {NextRetry}",
            export.Id, export.ConnectedSystemObjectId, unresolvedRefCount, totalChanges, export.NextRetryAt);
    }

    /// <summary>
    /// Marks an export as failed and applies retry logic (Q6 decision).
    /// </summary>
    private async Task MarkExportFailedAsync(PendingExport export, string errorMessage, string? stackTrace = null)
    {
        export.ErrorCount++;
        export.LastErrorMessage = errorMessage;
        export.LastErrorStackTrace = stackTrace;
        export.LastAttemptedAt = DateTime.UtcNow;
        export.NextRetryAt = CalculateNextRetryTime(export.ErrorCount);

        // If max retries exceeded, mark as Failed (Q6 decision - requires manual intervention)
        if (export.ErrorCount >= export.MaxRetries)
        {
            export.Status = PendingExportStatus.Failed;
            Log.Warning("MarkExportFailedAsync: Export {ExportId} has exceeded max retries ({MaxRetries}). Requires manual intervention.",
                export.Id, export.MaxRetries);
        }
        else
        {
            // Keep as Pending while we're still retrying
            // ExportNotConfirmed is for when export succeeded but some values didn't persist
            export.Status = PendingExportStatus.Pending;
            Log.Warning("MarkExportFailedAsync: Export {ExportId} failed (attempt {Attempt}/{MaxRetries}). Next retry at {NextRetry}. Error: {Error}",
                export.Id, export.ErrorCount, export.MaxRetries, export.NextRetryAt, LogSanitiser.Sanitise(errorMessage));
        }

        await SyncRepo.UpdatePendingExportAsync(export);
    }

    /// <summary>
    /// Calculates the next retry time using exponential backoff (Q6 decision).
    /// Uses 2^n minutes where n is the error count, capped at 1 hour.
    /// </summary>
    private static DateTime CalculateNextRetryTime(int errorCount)
    {
        // Exponential backoff: 2, 4, 8, 16, 32, 60 (max) minutes
        var minutes = Math.Min(Math.Pow(2, errorCount), 60);
        return DateTime.UtcNow.AddMinutes(minutes);
    }

    /// <summary>
    /// Gets the count of Pending Exports that require manual intervention (exceeded max retries).
    /// </summary>
    public async Task<int> GetFailedExportsCountAsync(int connectedSystemId)
    {
        var pendingExports = await SyncRepo.GetPendingExportsAsync(connectedSystemId);
        return pendingExports.Count(pe => pe.Status == PendingExportStatus.Failed);
    }

    /// <summary>
    /// Retries all failed exports for a Connected System (manual intervention).
    /// Resets error count and status.
    /// </summary>
    public async Task RetryFailedExportsAsync(int connectedSystemId)
    {
        var pendingExports = await SyncRepo.GetPendingExportsAsync(connectedSystemId);
        var failedExports = pendingExports.Where(pe => pe.Status == PendingExportStatus.Failed).ToList();

        foreach (var export in failedExports)
        {
            export.ErrorCount = 0;
            export.Status = PendingExportStatus.Pending;
            export.NextRetryAt = null;
            export.LastErrorMessage = null;
            export.LastErrorStackTrace = null;
            await SyncRepo.UpdatePendingExportAsync(export);
        }

        Log.Information("RetryFailedExportsAsync: Reset {Count} failed exports for system {SystemId}",
            failedExports.Count, connectedSystemId);
    }
}

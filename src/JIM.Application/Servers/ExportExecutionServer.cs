using JIM.Application.Diagnostics;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Executes pending exports by calling connector export methods.
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
    /// Executes all pending exports for a connected system.
    /// </summary>
    /// <param name="connectedSystem">The connected system to export to</param>
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
    /// Executes all pending exports for a connected system with progress reporting and cancellation support.
    /// </summary>
    /// <param name="connectedSystem">The connected system to export to</param>
    /// <param name="connector">The connector instance to use for export</param>
    /// <param name="runMode">Whether to preview only or actually sync (Q5 decision)</param>
    /// <param name="options">Optional execution options for batch size and parallelism</param>
    /// <param name="cancellationToken">Cancellation token to stop export processing</param>
    /// <param name="progressCallback">Optional callback for progress reporting</param>
    /// <param name="connectorFactory">Optional factory to create additional connector instances for parallel batches</param>
    /// <param name="repositoryFactory">Optional factory to create per-batch IRepository instances for parallel batches</param>
    /// <returns>Export execution result with preview information</returns>
    public async Task<ExportExecutionResult> ExecuteExportsAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        SyncRunMode runMode,
        ExportExecutionOptions? options,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback = null,
        Func<IConnector>? connectorFactory = null,
        Func<ISyncRepository>? repositoryFactory = null)
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
            Log.Debug("ExecuteExportsAsync: No pending exports to execute for system {SystemId}", connectedSystem.Id);
            result.CompletedAt = DateTime.UtcNow;
            return result;
        }

        Log.Information("ExecuteExportsAsync: Found {Count} pending exports to execute for system {SystemName} (BatchSize: {BatchSize}, MaxParallelism: {MaxParallelism})",
            totalExportCount, connectedSystem.Name, options.BatchSize, options.MaxParallelism);

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

        // Execute exports using the connector with batch-loading
        await ExecuteExportsViaConnectorAsync(connectedSystem, connector, result, options,
            cancellationToken, progressCallback, connectorFactory, repositoryFactory);

        // Second pass: retry any exports with deferred references that might now be resolvable
        if (!cancellationToken.IsCancellationRequested)
        {
            await ExecuteDeferredReferencesAsync(connectedSystem, connector, result);
        }

        result.CompletedAt = DateTime.UtcNow;

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
    /// Gets pending exports that are ready to be executed.
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
    /// Determines if a pending export is ready for execution.
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
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback,
        Func<IConnector>? connectorFactory,
        Func<ISyncRepository>? repositoryFactory)
    {
        // Check if connector supports export using calls
        if (connector is IConnectorExportUsingCalls callsConnector)
        {
            await ExecuteUsingCallsWithBatchingAsync(connectedSystem, callsConnector, result, options,
                cancellationToken, progressCallback, connectorFactory, repositoryFactory);
        }
        // Check if connector supports export using files — still loads all exports upfront
        // (file-based connectors write all data in one pass so batch-loading doesn't help)
        else if (connector is IConnectorExportUsingFiles filesConnector)
        {
            var pendingExports = await GetExecutableExportsAsync(connectedSystem.Id);
            await ExecuteUsingFilesWithBatchingAsync(connectedSystem, filesConnector, pendingExports, result, options, cancellationToken, progressCallback);
        }
        else
        {
            Log.Warning("ExecuteExportsViaConnectorAsync: Connector {ConnectorName} does not support export",
                connector.Name);
        }
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
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback,
        Func<IConnector>? connectorFactory,
        Func<ISyncRepository>? repositoryFactory)
    {
        try
        {
            PrepareConnectorForExport(connector);

            // Open connection for the primary connector
            using (Diagnostics.Diagnostics.Connector.StartSpan("OpenExportConnection"))
            {
                connector.OpenExportConnection(connectedSystem.SettingValues);
            }
            Log.Debug("ExecuteUsingCallsWithBatchingAsync: Opened export connection for {SystemName}", connectedSystem.Name);

            try
            {
                // Load and process exports in batches to avoid loading all 100K+ entities at once.
                // After processing, Update exports drop from the query (attribute changes
                // transition to ExportedPendingConfirmation). Create exports may re-enter with
                // Status=Exported. We track processed IDs and scan forward through query results
                // to find unprocessed exports in each iteration.
                var deferredExports = new List<PendingExport>();
                var processedCount = 0;
                var processedIds = new HashSet<Guid>();

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Scan forward through query results to find unprocessed exports.
                    // Most processed exports drop from the query (Update type), so skip=0
                    // typically returns fresh exports. For Create exports that re-enter,
                    // we page forward until we find unprocessed ones or exhaust the query.
                    List<PendingExport> batch;
                    var scanSkip = 0;

                    while (true)
                    {
                        List<PendingExport> rawBatch;
                        using (Diagnostics.Diagnostics.Database.StartSpan("LoadExportBatch")
                            .SetTag("skip", scanSkip)
                            .SetTag("take", options.BatchSize))
                        {
                            rawBatch = await SyncRepo
                                .GetExecutableExportBatchAsync(connectedSystem.Id, scanSkip, options.BatchSize);
                        }

                        if (rawBatch.Count == 0)
                        {
                            batch = rawBatch;
                            break;
                        }

                        // Filter out already-processed exports
                        batch = processedIds.Count > 0
                            ? rawBatch.Where(pe => !processedIds.Contains(pe.Id)).ToList()
                            : rawBatch;

                        if (batch.Count > 0)
                            break;

                        // All exports in this page were already processed; scan forward
                        scanSkip += rawBatch.Count;
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

                        // Execute batch via connector
                        List<ConnectedSystemExportResult> exportResults;
                        using (Diagnostics.Diagnostics.Connector.StartSpan("ExportBatch")
                            .SetTag("batchSize", immediateExports.Count))
                        {
                            exportResults = await connector.ExportAsync(immediateExports, cancellationToken);
                        }

                        // Process results
                        using (Diagnostics.Diagnostics.Database.StartSpan("ProcessBatchSuccess")
                            .SetTag("batchSize", immediateExports.Count))
                        {
                            await ProcessBatchSuccessAsync(immediateExports, exportResults, result,
                                SyncRepo);
                        }

                        processedCount += immediateExports.Count;

                        // Clear the change tracker between batches to prevent O(n²) degradation.
                        // All DB writes use raw SQL (MarkBatchAsExecuting, BulkUpdatePendingExports),
                        // so tracked entities serve no purpose after each batch completes. Without
                        // this, Entity() calls in detach loops trigger change detection scans across
                        // all accumulated entities — 40K+ entities after 100 batches.
                        SyncRepo.ClearChangeTracker();
                    }
                    // Note: no break when a batch has only ineligible/deferred exports — the outer
                    // loop continues scanning forward since later batches (ordered by CreatedAt) may
                    // contain eligible exports. The loop only exits when batch.Count == 0 (database
                    // exhausted), handled above at line 352.
                }

                // Second pass: Exports with unresolved references (deferred)
                if (deferredExports.Count > 0)
                {
                    await ProcessDeferredExportsAsync(connectedSystem, connector, deferredExports, result, options,
                        cancellationToken, progressCallback, connectorFactory, repositoryFactory);
                }

                // Capture created containers before closing connection
                if (connector is IConnectorContainerCreation containerCreator &&
                    containerCreator.CreatedContainerExternalIds.Count > 0)
                {
                    result.CreatedContainerExternalIds.AddRange(containerCreator.CreatedContainerExternalIds);
                    Log.Information("ExecuteUsingCallsWithBatchingAsync: Captured {Count} created container(s) for auto-selection",
                        containerCreator.CreatedContainerExternalIds.Count);
                }
            }
            finally
            {
                // Always close connection
                using (Diagnostics.Diagnostics.Connector.StartSpan("CloseExportConnection"))
                {
                    connector.CloseExportConnection();
                }
                Log.Debug("ExecuteUsingCallsWithBatchingAsync: Closed export connection for {SystemName}", connectedSystem.Name);
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
            // Individual batch failures are already handled in ProcessBatchSuccessAsync.
            // This catch is for connection-level or unexpected errors.
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
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback,
        Func<IConnector>? connectorFactory,
        Func<ISyncRepository>? repositoryFactory)
    {
        var useParallelBatches = options.MaxParallelism > 1 && connectorFactory != null && repositoryFactory != null;

        await ReportProgressAsync(progressCallback, new ExportProgressInfo
        {
            Phase = ExportPhase.ResolvingReferences,
            TotalExports = result.TotalPendingExports,
            ProcessedExports = result.SuccessCount,
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

        // Separate resolved from still-unresolved exports
        var resolvedExports = new List<PendingExport>();
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
            }

            resolveProcessedCount++;
            await ReportProgressAsync(progressCallback, new ExportProgressInfo
            {
                Phase = ExportPhase.ResolvingReferences,
                TotalExports = result.TotalPendingExports,
                ProcessedExports = result.SuccessCount + resolveProcessedCount,
                Message = $"Resolving deferred exports ({resolveProcessedCount} / {deferredExports.Count})"
            });
        }

        // Batch-export resolved deferred exports
        if (resolvedExports.Count > 0)
        {
            // Clear the change tracker before exporting deferred batches.
            // The CSO lookup query above re-loaded entities into the tracker, which causes
            // identity conflicts when the EF fallback paths (used in tests with in-memory DB)
            // try to attach/update the original PE instances.
            SyncRepo.ClearChangeTracker();

            var deferredBatches = resolvedExports
                .Select((export, index) => new { export, index })
                .GroupBy(x => x.index / options.BatchSize)
                .Select(g => g.Select(x => x.export).ToList())
                .ToList();

            if (useParallelBatches && deferredBatches.Count > 1)
            {
                await ProcessBatchesInParallelAsync(connectedSystem, connector, deferredBatches, result, options,
                    cancellationToken, progressCallback, connectorFactory!, repositoryFactory!, "ExportDeferredBatch");
            }
            else
            {
                await ProcessDeferredBatchesSequentiallyAsync(connector, deferredBatches, result, cancellationToken, progressCallback);
            }
        }

        // Mark still-unresolved exports as deferred in batch
        if (stillUnresolvedExports.Count > 0)
        {
            var unresolvedMvoIds = CollectUnresolvedMvoIds(stillUnresolvedExports);
            var resolvedMvoIds = csoLookup.Keys.ToHashSet();
            var missingMvoIds = unresolvedMvoIds.Except(resolvedMvoIds).ToList();

            Log.Information("ProcessDeferredExportsAsync: {StillUnresolved} export(s) have unresolved references and will be deferred. " +
                "{Resolved} resolved, {TotalDeferred} total deferred this cycle. " +
                "{MissingCount} referenced MVO(s) have no CSO in the target system yet: [{MissingIds}]",
                stillUnresolvedExports.Count, resolvedExports.Count, stillUnresolvedExports.Count,
                missingMvoIds.Count,
                string.Join(", ", missingMvoIds.Take(10).Select(id => id.ToString())));

            foreach (var export in stillUnresolvedExports)
            {
                var exportUnresolvedCount = export.AttributeValueChanges
                    .Count(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue));
                var exportTotalChanges = export.AttributeValueChanges.Count;
                Log.Debug("ProcessDeferredExportsAsync: Deferring export {ExportId} for CSO {CsoId} - " +
                    "{UnresolvedCount}/{TotalChanges} attribute changes have unresolved references",
                    export.Id, export.ConnectedSystemObjectId, exportUnresolvedCount, exportTotalChanges);

                await MarkExportDeferredAsync(export);
                result.DeferredCount++;
            }
        }
    }

    // ProcessBatchesSequentiallyAsync removed — sequential batch processing is now
    // inlined in ExecuteUsingCallsWithBatchingAsync to support batch-loading from the database.

    /// <summary>
    /// Processes deferred batches sequentially using the existing connector and DbContext.
    /// </summary>
    private async Task ProcessDeferredBatchesSequentiallyAsync(
        IConnectorExportUsingCalls connector,
        List<List<PendingExport>> batches,
        ExportExecutionResult result,
        CancellationToken cancellationToken,
        Func<ExportProgressInfo, Task>? progressCallback)
    {
        var processedCount = 0;
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Report progress for deferred batch execution
            await ReportProgressAsync(progressCallback, new ExportProgressInfo
            {
                Phase = ExportPhase.Executing,
                TotalExports = result.TotalPendingExports,
                ProcessedExports = result.SuccessCount + result.FailedCount + processedCount,
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
                exportResults = await connector.ExportAsync(batch, cancellationToken);
            }

            using (Diagnostics.Diagnostics.Database.StartSpan("ProcessDeferredBatchSuccess")
                .SetTag("batchSize", batch.Count))
            {
                await ProcessBatchSuccessAsync(batch, exportResults, result, SyncRepo);
            }

            processedCount += batch.Count;
        }
    }

    /// <summary>
    /// Processes multiple batches concurrently using separate DbContext and connector instances per batch.
    /// Each batch task creates its own IRepository (with its own DbContext) and connector instance.
    /// The batch's pending exports are re-loaded by ID from the batch's context to ensure proper
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
        Func<ISyncRepository> repositoryFactory,
        string spanName)
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

        // Lock for thread-safe result aggregation
        var resultLock = new object();

        using var throttle = new SemaphoreSlim(options.MaxParallelism, options.MaxParallelism);

        var batchTasks = batchIdLists.Select((batchIds, batchIndex) => Task.Run(async () =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Create per-batch repository with its own context
                var batchRepo = repositoryFactory();

                // Re-load this batch's pending exports from the batch's own context
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
                    batchConnector.OpenExportConnection(connectedSystem.SettingValues);
                }

                try
                {
                    // Mark batch as executing (raw SQL - context-independent)
                    await batchRepo.MarkPendingExportsAsExecutingAsync(batch);

                    // Execute batch via connector
                    var exportResults = await batchConnector.ExportAsync(batch, cancellationToken);

                    // Process results using the batch's own repository
                    var batchResult = new ExportExecutionResult();
                    await ProcessBatchSuccessAsync(batch, exportResults, batchResult, batchRepo);

                    // Capture created containers from this batch's connector
                    List<string>? batchContainerIds = null;
                    if (batchConnector is IConnectorContainerCreation containerCreator &&
                        containerCreator.CreatedContainerExternalIds.Count > 0)
                    {
                        batchContainerIds = [..containerCreator.CreatedContainerExternalIds];
                    }

                    // Aggregate results into shared result (thread-safe)
                    lock (resultLock)
                    {
                        result.SuccessCount += batchResult.SuccessCount;
                        result.FailedCount += batchResult.FailedCount;
                        result.DeferredCount += batchResult.DeferredCount;
                        result.ProcessedExportItems.AddRange(batchResult.ProcessedExportItems);
                        if (batchContainerIds != null)
                        {
                            result.CreatedContainerExternalIds.AddRange(batchContainerIds);
                        }
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
                                ProcessedExports = newProcessedCount,
                                CurrentBatchSize = batch.Count,
                                Message = "Exporting"
                            });
                        }
                        finally
                        {
                            progressSemaphore.Release();
                        }
                    }
                }
                finally
                {
                    // Close the batch connector (but not the primary - that's managed by the caller)
                    if (batchIndex != 0)
                    {
                        batchConnector.CloseExportConnection();
                        (batchConnector as IDisposable)?.Dispose();
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

                // Mark this batch's exports as failed
                lock (resultLock)
                {
                    result.FailedCount += batchIds.Count;
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
    /// Processes a batch of exports with their corresponding ConnectedSystemExportResult data.
    /// Uses batch updates for efficiency - pre-fetches attribute definitions and performs
    /// a single SaveChanges for all CSO updates.
    /// Accepts an explicit repository parameter to support both sequential (shared) and parallel (per-batch) paths.
    /// </summary>
    private async Task ProcessBatchSuccessAsync(
        List<PendingExport> batch,
        List<ConnectedSystemExportResult> exportResults,
        ExportExecutionResult result,
        ISyncRepository repository)
    {
        var exportsToUpdate = new List<PendingExport>();
        var csosToUpdate = new List<(ConnectedSystemObject cso, ConnectedSystemExportResult exportResult)>();

        for (var i = 0; i < batch.Count; i++)
        {
            var export = batch[i];
            var exportResult = i < exportResults.Count ? exportResults[i] : ConnectedSystemExportResult.Succeeded();

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
                    AttributeChangeCount = export.AttributeValueChanges.Count,
                    AttributeValueChanges = export.AttributeValueChanges.ToList(),
                    Succeeded = false,
                    ErrorMessage = exportResult.ErrorMessage ?? "Export failed",
                    ErrorCount = export.ErrorCount,
                    ErrorType = exportResult.ErrorType
                });
                continue;
            }

            // Capture export data for activity tracking (before deletion)
            result.ProcessedExportItems.Add(new ProcessedExportItem
            {
                ChangeType = export.ChangeType,
                ConnectedSystemObject = export.ConnectedSystemObject,
                AttributeChangeCount = export.AttributeValueChanges.Count,
                AttributeValueChanges = export.AttributeValueChanges.ToList(),
                Succeeded = true
            });

            export.Status = PendingExportStatus.Exported;

            // For Create exports, update the CSO with the system-assigned external ID and status
            // For Update exports with SecondaryExternalId (e.g., LDAP renames), update the CSO's secondary ID
            if (export.ConnectedSystemObject != null &&
                (export.ChangeType == PendingExportChangeType.Create ||
                 !string.IsNullOrEmpty(exportResult.SecondaryExternalId)))
            {
                csosToUpdate.Add((export.ConnectedSystemObject, exportResult));
            }

            // Update attribute change statuses to ExportedPendingConfirmation
            UpdateAttributeChangeStatusesAfterExport(export);

            exportsToUpdate.Add(export);
            result.SuccessCount++;
            Log.Debug("ProcessBatchSuccessAsync: Successfully exported {ExportId}, awaiting confirmation via import", export.Id);
        }

        // Batch update all pending exports
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
    }

    /// <summary>
    /// Batch updates multiple CSOs after successful exports in a single database round-trip.
    /// Pre-fetches all required attribute definitions in one query, then applies changes
    /// and saves all CSO updates together.
    /// Accepts an explicit repository parameter to support both sequential (shared) and parallel (per-batch) paths.
    /// </summary>
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
                var oldPrimaryIdValue = externalIdAttrValue?.StringValue ?? externalIdAttrValue?.GuidValue?.ToString();

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

                if (externalIdAttribute?.Type == AttributeDataType.Guid && Guid.TryParse(exportResult.ExternalId, out var guidValue))
                {
                    externalIdAttrValue.GuidValue = guidValue;
                    externalIdAttrValue.StringValue = null;
                }
                else
                {
                    externalIdAttrValue.StringValue = exportResult.ExternalId;
                    externalIdAttrValue.GuidValue = null;
                }

                // Track cache invalidation: evict old value if it differs from the new one
                if (oldPrimaryIdValue != null && !oldPrimaryIdValue.Equals(exportResult.ExternalId, StringComparison.OrdinalIgnoreCase))
                    cacheEvictions.Add((cso.ConnectedSystemId, cso.ExternalIdAttributeId, oldPrimaryIdValue));
                cacheAdditions.Add((cso.ConnectedSystemId, cso.ExternalIdAttributeId, exportResult.ExternalId, cso.Id));

                needsUpdate = true;
                Log.Debug("BatchUpdateCsosAfterSuccessfulExportAsync: Set CSO {CsoId} external ID to {ExternalId} (type: {AttrType})",
                    cso.Id, exportResult.ExternalId, externalIdAttribute?.Type.ToString() ?? "Unknown");
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
                    cso.Id, exportResult.SecondaryExternalId);
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
            SyncRepo.EvictCsoFromCache(connectedSystemId, attributeId, oldValue);
        foreach (var (connectedSystemId, attributeId, newValue, csoId) in cacheAdditions)
            SyncRepo.AddCsoToCache(connectedSystemId, attributeId, newValue, csoId);
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
                export.Id, export.ErrorCount, export.MaxRetries, export.NextRetryAt, errorMessage);
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
                cso.Id, exportResult.ExternalId, externalIdAttribute?.Type.ToString() ?? "Unknown");
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
                cso.Id, exportResult.SecondaryExternalId);
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
    private static void UpdateAttributeChangeStatusesAfterExport(PendingExport export)
    {
        var now = DateTime.UtcNow;

        foreach (var attrChange in export.AttributeValueChanges)
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

            // File-based export - execute all at once (file connectors typically batch internally)
            var exportResults = await connector.ExportAsync(connectedSystem.SettingValues, pendingExports, cancellationToken);

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
                    });
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
                });

                // For Create exports, update the CSO status from PendingProvisioning to Normal
                if (export.ChangeType == PendingExportChangeType.Create && export.ConnectedSystemObject != null)
                {
                    csosToUpdate.Add((export.ConnectedSystemObject, exportResult));
                }

                // Update attribute change statuses to ExportedPendingConfirmation
                UpdateAttributeChangeStatusesAfterExport(export);

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
    /// Collects all unresolved MVO IDs from a list of pending exports.
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
    /// Attempts to resolve unresolved reference attributes in a pending export using a pre-fetched CSO lookup.
    /// For LDAP systems, references like 'member' need to be resolved to Distinguished Names (DN),
    /// not the primary external ID (objectGUID). We use the secondary external ID when available.
    /// </summary>
    private static bool TryResolveReferencesFromLookup(PendingExport pendingExport, Dictionary<Guid, ConnectedSystemObject> csoLookup)
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
                // as this is what the connected system uses for references.
                // Fall back to primary external ID if secondary is not available.
                var secondaryExternalIdAttr = referencedCso.AttributeValues
                    .FirstOrDefault(av => av.Attribute?.IsSecondaryExternalId == true);

                var externalIdAttr = referencedCso.AttributeValues
                    .FirstOrDefault(av => av.Attribute?.IsExternalId == true);

                // Use secondary external ID (DN) if available, otherwise fall back to primary
                var resolvedAttr = secondaryExternalIdAttr ?? externalIdAttr;

                if (resolvedAttr != null)
                {
                    attrChange.StringValue = resolvedAttr.StringValue ??
                                             resolvedAttr.GuidValue?.ToString() ??
                                             resolvedAttr.IntValue?.ToString();
                    attrChange.UnresolvedReferenceValue = null;

                    Log.Debug("Resolved reference for MVO {MvoId} to {Value} using {IdType}",
                        referencedMvoId,
                        attrChange.StringValue,
                        secondaryExternalIdAttr != null ? "secondary external ID (DN)" : "primary external ID");
                }
                else
                {
                    Log.Warning("Could not resolve reference for MVO {MvoId}: CSO {CsoId} has no external ID attribute",
                        referencedMvoId, referencedCso.Id);
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
                    attrChange.UnresolvedReferenceValue);
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
        // Get any exports that were marked as having unresolved references
        List<PendingExport> deferredExports;
        using (Diagnostics.Diagnostics.Database.StartSpan("GetPendingExportsForDeferredResolution"))
        {
            deferredExports = await SyncRepo.GetPendingExportsAsync(connectedSystem.Id);
        }
        var unresolvedExports = deferredExports
            .Where(pe => pe.HasUnresolvedReferences && pe.Status == PendingExportStatus.Pending)
            .ToList();

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
            });
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
        });

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
                export.Id, export.ErrorCount, export.MaxRetries, export.NextRetryAt, errorMessage);
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
    /// Gets the count of pending exports that require manual intervention (exceeded max retries).
    /// </summary>
    public async Task<int> GetFailedExportsCountAsync(int connectedSystemId)
    {
        var pendingExports = await SyncRepo.GetPendingExportsAsync(connectedSystemId);
        return pendingExports.Count(pe => pe.Status == PendingExportStatus.Failed);
    }

    /// <summary>
    /// Retries all failed exports for a connected system (manual intervention).
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

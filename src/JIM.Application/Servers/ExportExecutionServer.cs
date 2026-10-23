// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Diagnostics;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Application.Staging;
using JIM.Application.UniqueValues;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
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

    /// <summary>
    /// Every Pending Export change type, for iterating the Run Profile Safeguards (#1618) ledger
    /// decision per type without re-declaring the three values at each call site.
    /// </summary>
    private static readonly PendingExportChangeType[] AllChangeTypes =
    [
        PendingExportChangeType.Create,
        PendingExportChangeType.Update,
        PendingExportChangeType.Delete
    ];

    private JimApplication Application { get; }
    private ISyncRepository SyncRepo { get; }
    private readonly ISyncEngine _syncEngine = new SyncEngine();

    /// <summary>
    /// The current export run's Collision Remediation (Unique Value Generation, #242, release 4), set for the duration
    /// of a calls-based <see cref="ExecuteExportsAsync(ConnectedSystem, IConnector, SyncRunMode, ExportExecutionOptions?, CancellationToken, Func{ExportProgressInfo, Task}?, Func{IConnector}?, Func{ISyncRepositoryScope}?, Func{List{ProcessedExportItem}, Task}?, UniqueValueReservationSet?)"/>
    /// and read by every batch that run processes, its parallel batches included. A server instance belongs to one
    /// unit of work, so it never carries two runs at once.
    /// </summary>
    private CollisionRemediationRun? _collisionRemediation;

    /// <summary>
    /// The run's caller, told of the containers each Connector created as its connection closes (#2197), so it can select
    /// them whether or not the run completes.
    /// </summary>
    private Action<IReadOnlyList<string>>? _containersCreated;

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
    /// <param name="uniqueValueReservations">The worker's process-wide reservation set, which values Collision
    /// Remediation draws are claimed in (Unique Value Generation, #242, release 4); a run-local set when null.</param>
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
        Func<List<ProcessedExportItem>, Task>? batchCompletedCallback = null,
        UniqueValueReservationSet? uniqueValueReservations = null,
        Action<IReadOnlyList<string>>? containersCreated = null)
    {
        options ??= new ExportExecutionOptions();
        cancellationToken.ThrowIfCancellationRequested();

        var result = new ExportExecutionResult
        {
            ConnectedSystemId = connectedSystem.Id,
            RunMode = runMode,
            StartedAt = DateTime.UtcNow
        };

        // A queued change must not outlive the reason it was queued. Configuration can change between the
        // synchronisation that queued a change and this export (an Attribute Flow removed or disabled, a Synchronisation
        // Rule disabled or deleted), and an account can leave scope with the Disconnect action after a change was queued
        // for it; none of those revisit what is already queued, so it is checked here, once, before anything is counted
        // or sent. A preview must not change the queue.
        if (runMode != SyncRunMode.PreviewOnly)
            (result.QueuedChangesWithdrawnCount, result.PendingExportsWithdrawnCount) =
                await WithdrawQueuedChangesWithoutAuthorityAsync(connectedSystem.Id, connectedSystem.Name);

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

        // Run Profile Safeguards (#1618): decide once, up front, which change types this run may
        // attempt at all. Reading the executable count per change type costs one extra query, so it
        // is skipped entirely when the Run Profile carries no limits; an uncapped Export run performs
        // no additional database work. The ledger is shared by both passes (the immediate batches
        // below and the deferred-reference pass) and both connector shapes (calls and files), so a
        // withheld type stays withheld regardless of which pass or path meets it.
        var hasAnyChangeLimit = options.MaxCreates.HasValue || options.MaxUpdates.HasValue || options.MaxDeletes.HasValue;
        var executablePendingCountsByType = hasAnyChangeLimit
            ? await SyncRepo.GetExecutableExportCountsByChangeTypeAsync(connectedSystem.Id)
            : new Dictionary<PendingExportChangeType, int>();
        var changeLimitLedger = new ExportChangeLimitLedger(options.MaxCreates, options.MaxUpdates, options.MaxDeletes, executablePendingCountsByType);

        // The withheld totals are decided now, at construction, not accumulated as the run
        // progresses; count them towards processedCount up front too (see the paging loop below) so
        // the run's progress window still completes even though a withheld type is never attempted.

        // Collision Remediation (Unique Value Generation, #242, release 4) acts on a calls-based export's rejections; a
        // file-based export records the classified error only. Metaverse Object change tracking is read once, here, on
        // the main context, because the parallel batches that may revise a value must not touch it.
        if (connector is IConnectorExportUsingCalls)
        {
            var classifies = connectedSystem.ConnectorDefinition?.SupportsUniquenessRejectionClassification == true;
            var recordMetaverseChanges = classifies && await Application.ServiceSettings.GetMvoChangeTrackingEnabledAsync();
            _collisionRemediation = new CollisionRemediationRun(Application, connectedSystem, uniqueValueReservations ?? new UniqueValueReservationSet(), recordMetaverseChanges);
        }

        _containersCreated = containersCreated;
        try
        {
            // Execute exports using the connector with batch-loading
            await ExecuteExportsViaConnectorAsync(connectedSystem, connector, result, options, changeLimitLedger,
                cancellationToken, progressCallback, connectorFactory, repositoryFactory, batchCompletedCallback);

            // Every batch has finished, so no corrected value will draw another sequence number: hand back the rest
            // of any block a correction reserved (#2044).
            if (_collisionRemediation != null)
                await _collisionRemediation.ReturnUnusedSequenceNumbersAsync(SyncRepo);
        }
        finally
        {
            _collisionRemediation?.Complete();
            _collisionRemediation = null;
            _containersCreated = null;
        }

        // Second pass: retry any exports with deferred references that might now be resolvable
        if (!cancellationToken.IsCancellationRequested)
            await ExecuteDeferredReferencesAsync(connectedSystem, connector, result);

        // Hand on whatever is still held, cancelled or not: the items of deferred reference resolution, and those of a
        // batch that failed when nothing after it handed them on (#2083). Taken off the result before the callback, so a
        // callback that fails part-way is never handed the same items twice.
        if (batchCompletedCallback != null && result.ProcessedExportItems.Count > 0)
        {
            var remainingItems = result.ProcessedExportItems;
            result.ProcessedExportItems = [];
            await batchCompletedCallback(remainingItems);
        }

        // Run Profile Safeguards (#1618): copy the ledger's final withheld counts onto the result.
        result.CreatesWithheld = changeLimitLedger.Withheld(PendingExportChangeType.Create);
        result.UpdatesWithheld = changeLimitLedger.Withheld(PendingExportChangeType.Update);
        result.DeletesWithheld = changeLimitLedger.Withheld(PendingExportChangeType.Delete);

        result.CompletedAt = DateTime.UtcNow;

        // Synchronisation Integrity: log summary statistics at the end of every batch operation.
        Log.Information("ExecuteExportsAsync: Optimistic export apply summary for {SystemName}: " +
            "{AppliedCount} Pending Exports applied, {SkippedCount} skipped (Delete change type), " +
            "{FailedCount} failed, of which {UnrecordedCount} were auto-confirmed and left to be sent again (the rest self-heal on the confirming import), " +
            "{UnresolvedCount} Reference values left unresolved",
            connectedSystem.Name, result.OptimisticApplyAppliedCount, result.OptimisticApplySkippedCount,
            result.OptimisticApplyFailedCount, result.UnrecordedExportCount, result.OptimisticApplyUnresolvedReferenceCount);

        // #1121: only worth a line when the run actually provisioned accounts owed a password; every other
        // deployment would otherwise get a pair of zeroes on every export.
        if (result.InitialPasswordsStagedCount > 0 || result.InitialPasswordStagingFailedCount > 0 || result.InitialPasswordMarkingFailedCount > 0
            || result.InitialPasswordsCarriedCount > 0)
        {
            Log.Information("ExecuteExportsAsync: Initial password summary for {SystemName}: {StagedCount} newly provisioned accounts " +
                "recorded as owed an initial password, {CarriedCount} given a password change already waiting for them as their first password, " +
                "{FailedCount} that could not be recorded, {MarkingFailedCount} whose written enabled state could not be recorded on the password still owed",
                connectedSystem.Name, result.InitialPasswordsStagedCount, result.InitialPasswordsCarriedCount,
                result.InitialPasswordStagingFailedCount, result.InitialPasswordMarkingFailedCount);
        }

        // Only when it happened: most export runs meet no value already in use.
        if (result.GeneratedValuesRemediatedCount > 0 || result.GeneratedValuesNeedingDecisionCount > 0)
        {
            Log.Information("ExecuteExportsAsync: Collision Remediation summary for {SystemName}: {RemediatedCount} generated value(s) corrected " +
                "after being rejected as already in use (their exports stay queued for the next synchronisation), {NeedsDecisionCount} " +
                "needing an administrator's decision (their exports are parked)",
                connectedSystem.Name, result.GeneratedValuesRemediatedCount, result.GeneratedValuesNeedingDecisionCount);
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

        // Only when it happened, which should be never: export staging gives a single-valued attribute exactly one
        // change (#2182), so an export carrying two is a fault in JIM rather than a configuration gap, and is said so.
        if (result.SingleValuedContradictionRefusedCount > 0)
        {
            Log.Error("ExecuteExportsAsync: Single-valued attribute summary for {SystemName}: {RefusedCount} export(s) refused because each carries more " +
                "than one change for a single-valued attribute, which is a fault in JIM rather than in its configuration. Each names the attributes on its " +
                "own Pending Export, and none was sent, so no Connected System Object was changed by it.",
                connectedSystem.Name, result.SingleValuedContradictionRefusedCount);
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
    /// Withdraws queued changes on a Connected System's Update Pending Exports that nothing authorises any more (see
    /// <see cref="ISyncEngine.SelectQueuedChangesWithoutAuthority"/>), deleting any Pending Export that leaves empty.
    /// Runs before every export, and after every configuration change that can take authority away from a queued
    /// change (an export Synchronisation Rule or Attribute Flow disabled, removed or deleted), so the Pending Exports
    /// page reflects the change at once. One candidate query; objects with nothing stale cost nothing further.
    /// </summary>
    /// <remarks>
    /// Safe to run while an export of the same Connected System is in progress: exports a connector is executing are
    /// never touched, and an export's writes to Pending Export rows are updates, which a concurrent withdrawal turns
    /// into no-ops. The narrow window between an export loading a batch and marking it executing can still send a
    /// change withdrawn meanwhile, which is exactly what happened before this check existed.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System whose queue to check.</param>
    /// <param name="connectedSystemName">Its name, for the log; the id is logged when it is not to hand.</param>
    /// <returns>How many queued changes were withdrawn and how many emptied Pending Exports were deleted.</returns>
    public async Task<(int ChangesWithdrawn, int PendingExportsDeleted)> WithdrawQueuedChangesWithoutAuthorityAsync(
        int connectedSystemId, string? connectedSystemName = null)
    {
        var syncRules = await SyncRepo.GetSyncRulesAsync(connectedSystemId, includeDisabled: true);
        var classMembershipAttributeIds = ClassMembershipAttributeIds(syncRules);

        var candidates = await SyncRepo.GetUpdatePendingExportsWithQueuedChangesPossiblyWithoutAuthorityAsync(connectedSystemId, classMembershipAttributeIds);
        if (candidates.Count == 0)
            return (0, 0);

        var syncRulesById = syncRules.ToDictionary(r => r.Id);
        var withdrawnChangeIds = new List<Guid>();
        foreach (var pendingExport in candidates)
        {
            var isJoined = pendingExport.ConnectedSystemObject == null || pendingExport.ConnectedSystemObject.MetaverseObjectId != null;
            var withdrawn = _syncEngine.SelectQueuedChangesWithoutAuthority(pendingExport, isJoined, syncRulesById, classMembershipAttributeIds);
            if (withdrawn.Count == 0)
                continue;

            withdrawnChangeIds.AddRange(withdrawn.Select(c => c.Id));
            Log.Information("WithdrawQueuedChangesWithoutAuthorityAsync: Withdrew {Count} queued change(s) from Pending Export {PendingExportId} " +
                "(Connected System Object {CsoId}): {Reason}",
                withdrawn.Count, pendingExport.Id, pendingExport.ConnectedSystemObjectId,
                isJoined ? "no enabled Synchronisation Rule or Attribute Flow authorises them any more" : "the account is no longer joined");
        }

        if (withdrawnChangeIds.Count == 0)
            return (0, 0);

        var (changesWithdrawn, pendingExportsDeleted) = await SyncRepo.WithdrawPendingExportAttributeChangesAsync(withdrawnChangeIds);

        // Synchronisation Integrity: summary statistics for the batch operation.
        Log.Information("WithdrawQueuedChangesWithoutAuthorityAsync: {SystemName}: withdrew {ChangeCount} queued change(s) no longer authorised " +
            "by an enabled Synchronisation Rule, Attribute Flow or join, deleting {PendingExportCount} Pending Export(s) left empty",
            connectedSystemName ?? $"Connected System {connectedSystemId}", changesWithdrawn, pendingExportsDeleted);

        return (changesWithdrawn, pendingExportsDeleted);
    }

    /// <summary>
    /// The class membership attribute of each Object Type the Connected System's Synchronisation Rules cover. Class
    /// membership is JIM-computed, so it has no Attribute Flow of its own.
    /// </summary>
    private static HashSet<int> ClassMembershipAttributeIds(IEnumerable<SyncRule> syncRules)
    {
        var ids = new HashSet<int>();
        foreach (var objectType in syncRules.Select(r => r.ConnectedSystemObjectType).OfType<ConnectedSystemObjectType>().DistinctBy(t => t.Id))
        {
            var attributeName = objectType.ClassMembershipAttributeName();
            if (string.IsNullOrEmpty(attributeName))
                continue;

            var attribute = objectType.Attributes?.FirstOrDefault(a => a.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase));
            if (attribute != null)
                ids.Add(attribute.Id);
        }
        return ids;
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
    /// Gets Pending Exports that are ready to be executed.
    /// Uses database-level filtering for status, then applies in-memory checks for attribute-level eligibility that
    /// can't be expressed in SQL. No count or age of failed attempts takes an export out of the queue (#2119).
    /// </summary>
    private async Task<List<PendingExport>> GetExecutableExportsAsync(int connectedSystemId)
    {
        // Database-level filtering handles: status, ordering
        var eligibleExports = await SyncRepo.GetExecutableExportsAsync(connectedSystemId);

        // In-memory filtering for checks that require navigation property evaluation
        return eligibleExports
            .Where(pe => IsReadyForExecution(pe))
            .ToList();
    }

    /// <summary>
    /// Determines if a Pending Export is ready for execution.
    /// Applies in-memory checks that require navigation property evaluation and can't be expressed
    /// in SQL. Database-level checks (status) are already applied by GetExecutableExportsAsync.
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

        // A Create that has already been exported is awaiting confirmation by import: re-sending it
        // would ask the connector to create an object that already exists there, which most connectors
        // reject. Any attribute changes appended while it waits (see ExportEvaluationServer's
        // append-not-replace staging for a PendingProvisioning CSO whose Create has already been sent)
        // travel later as an Update, once SyncEngine.Reconciliation confirms the Create and flips the
        // row's ChangeType (SyncEngine.Reconciliation.cs). ExportNotConfirmed (a retry after a failed or
        // ambiguous send) stays exportable: unlike Exported, it means the Create genuinely needs to go
        // out again.
        if (pendingExport.ChangeType == PendingExportChangeType.Create &&
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
            PrepareConnectorForExport(connector, connectedSystem);
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

            PrepareConnectorForExport(connector, connectedSystem);
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
    /// Prepares a connector instance for export by injecting required services and stating the Connected
    /// System's managed scope. The one place every export connector is prepared, the primary and each parallel
    /// batch's alike: a batch connector left unprepared would export without the state the primary has (#1764).
    /// </summary>
    private void PrepareConnectorForExport(IConnector connector, ConnectedSystem connectedSystem)
    {
        // Tell the Connector which containers the administrator manages, so it can refuse to write outside them.
        // Container selection used to apply only on the way in, so an Attribute Flow that moved an object into an
        // unselected container wrote it where JIM could not read it back: the export went unconfirmed, the next
        // Full Import treated the object as deleted, and synchronisation then disconnected and re-provisioned it.
        // Stated only when there is a selection to state; a Connected System with none permits everything, exactly
        // as before.
        // Selections and exclusions both, because both decide where JIM may write: an export into an excluded
        // branch is as unreadable on the way back as one into a container that was never selected (#1255).
        if (connector is IConnectorManagedScope scopedConnector)
        {
            var managedContainers = connectedSystem.GetScopeDecidingContainers();
            if (managedContainers.Count > 0)
            {
                scopedConnector.SetManagedScope(managedContainers);
                Log.Debug("PrepareConnectorForExport: Stated a managed scope of {ContainerCount} container(s) to the {Connector} connector",
                    managedContainers.Count, connector.Name);
            }
        }

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
            // Tracks whether the export phase below completed without throwing. Read from the
            // finally block to decide whether a failure while persisting CloseExportConnection's
            // return value may safely propagate on its own, or must be logged and swallowed so it
            // does not replace/mask an export failure that is already unwinding through the same
            // finally block (see the finally block below for the full rationale).
            var exportPhaseSucceeded = false;

            try
            {
                // Open connection for the primary connector (prepared for export by the caller). Opened
                // inside the try, so a connection that fails to open is still closed by the finally block
                // below and what the connector returns at close is still persisted (#1875): failing to
                // connect is exactly when it has state to hand back, such as a pinned domain controller
                // the failure invalidated (issue #230).
                using (Diagnostics.Diagnostics.Connector.StartSpan("OpenExportConnection"))
                {
                    connector.OpenExportConnection(connectedSystem.SettingValues, connectedSystem.PersistedConnectorData);
                }
                Log.Debug("ExecuteUsingCallsWithBatchingAsync: Opened export connection for {SystemName}", connectedSystem.Name);

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
                var deferredExports = new List<PendingExport>();
                var processedCount = 0;
                var processedIds = new HashSet<Guid>();

                // Run Profile Safeguards (#1618): the change types withheld for the whole run,
                // decided once by the ledger before this loop starts. Excluded at the database level
                // (below) so paging never reads, and never has to skip past, a withheld row; their
                // total is added to processedCount once, here, rather than accumulated per batch,
                // since none of them will ever be seen by this loop to accumulate from.
                var excludedChangeTypes = AllChangeTypes.Where(changeLimitLedger.IsWithheld).ToList();
                processedCount += AllChangeTypes.Sum(changeLimitLedger.Withheld);

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
                var exhausted = false;
                var exportPhaseStopwatch = System.Diagnostics.Stopwatch.StartNew();

                // Reads forward to the next batch of exports that can be written now, collecting the deferred
                // (reference-bearing) ones it passes on the way; null once nothing executable remains. Shared by the
                // sequential loop and the parallel workers below, which call it one at a time on the caller's context.
                async Task<List<PendingExport>?> ReadNextImmediateBatchAsync()
                {
                    while (!exhausted)
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
                                    connectedSystem.Id, options.BatchSize, cursorCreatedAt, cursorId, excludedChangeTypes);
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
                        {
                            exhausted = true;
                            break;
                        }

                        // Apply in-memory eligibility filter (same as the old GetExecutableExportsAsync)
                        var eligibleExports = batch.Where(pe => IsReadyForExecution(pe)).ToList();

                        // Track all batch IDs as processed (even ineligible ones, to avoid re-fetching)
                        processedIds.UnionWith(batch.Select(pe => pe.Id));

                        // Track eligible export IDs for the result (used by preview mode and tests)
                        result.ProcessedPendingExportIds.AddRange(eligibleExports.Select(pe => pe.Id));

                        // Separate immediate from deferred exports
                        var immediateExports = eligibleExports.Where(pe => !pe.HasUnresolvedReferences).ToList();
                        var batchDeferred = eligibleExports.Where(pe => pe.HasUnresolvedReferences).ToList();
                        deferredExports.AddRange(batchDeferred);

                        // Run Profile Safeguards (#1618): a defensive guard, not the primary mechanism -
                        // excludedChangeTypes above already keeps a withheld type out of every batch this
                        // loop reads. Whatever it drops is left exactly as found (Pending), not marked,
                        // failed, or given an execution item; its withheld total was already added to
                        // processedCount once, up front, not accumulated here.
                        immediateExports = ReserveAgainstLedger(immediateExports, changeLimitLedger);

                        if (immediateExports.Count > 0)
                            return immediateExports;

                        if (batchDeferred.Count == batch.Count
                            && !await SyncRepo.AnyExecutableNonDeferredExportsAfterAsync(connectedSystem.Id, cursorCreatedAt, cursorId))
                        {
                            // Fast path (issue #985c): the whole batch just loaded was deferred
                            // (reference-bearing) and nothing in it was executable. Continuing to
                            // page through the remainder 100 rows at a time would only ever rebuild
                            // the same deferred list one page slower; at group-heavy scale (10K+
                            // deferred exports) this was the entire cost of the collection loop.
                            // Collect everything beyond the cursor with a single set-based query and
                            // stop scanning; a mixed batch (some immediate, some deferred) always
                            // returns above instead and keeps paging normally.
                            //
                            // The existence probe above is REQUIRED before stopping: deferred and
                            // executable exports interleave in (CreatedAt, Id) order, so a full batch
                            // of deferred exports does not prove the remainder of the queue is
                            // deferred too. Without the probe, executable exports created after a
                            // contiguous deferred run would silently never execute in this run; a
                            // behaviour regression versus page-by-page scanning. The probe is a cheap
                            // indexed existence check (no Includes, no materialisation); when it
                            // finds executable exports beyond the cursor, normal paging continues.
                            using var span = Diagnostics.Diagnostics.Database.StartSpan("CollectRemainingDeferred")
                                .SetTag("afterCreatedAt", cursorCreatedAt?.ToString("O") ?? "start");

                            var remainingDeferred = await SyncRepo.GetRemainingDeferredExportsAsync(
                                connectedSystem.Id, cursorCreatedAt, cursorId);

                            // Safety net: mirror the processedIds guard above, even though a
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

                            exhausted = true;
                            break;
                        }

                        // Note: a batch with only ineligible/deferred exports, where the fast path above
                        // did not apply (a mixed batch, or executable exports still exist beyond the
                        // cursor), keeps the scan going forward, since later batches (ordered by
                        // CreatedAt) may contain eligible exports.
                    }

                    return null;
                }

                // Batches run concurrently when the Connected System allows more than one at a time (Max Export
                // Parallelism, or the Connector's recommendation) and there is more than one batch to run.
                var useParallelBatches = options.MaxParallelism > 1 && connectorFactory != null && repositoryFactory != null
                    && result.TotalPendingExports > options.BatchSize;

                if (useParallelBatches)
                {
                    await ProcessBatchesInParallelAsync(connectedSystem, connector,
                        async () =>
                        {
                            var next = await ReadNextImmediateBatchAsync();

                            // Each batch is re-loaded by the worker that runs it, so what was read here serves only to
                            // choose the batch. Clear the change tracker to keep it from growing across the run (see
                            // the sequential loop below).
                            SyncRepo.ClearChangeTracker();
                            return next;
                        },
                        stopOnBatchFailure: true, result, options, cancellationToken, progressCallback,
                        connectorFactory!, repositoryFactory!, "ExportBatch",
                        processedExportsOffset: processedCount,
                        batchCompletedCallback: batchCompletedCallback,
                        batchExecuted: batchCount =>
                        {
                            var executedSoFar = Interlocked.Add(ref processedCount, batchCount);
                            Log.Information("MetricsCheckpoint: Export processed={ObjectsProcessed} elapsed={ElapsedMs}ms total={TotalObjects} cs={ConnectedSystemName}",
                                executedSoFar, (long)exportPhaseStopwatch.Elapsed.TotalMilliseconds, result.TotalPendingExports, connectedSystem.Name);
                        });
                }
                else
                {
                    while (await ReadNextImmediateBatchAsync() is { } immediateExports)
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
                        ExecutingBatchMark executingMark;
                        using (Diagnostics.Diagnostics.Database.StartSpan("MarkBatchAsExecuting")
                            .SetTag("batchSize", immediateExports.Count))
                        {
                            executingMark = await MarkBatchAsExecutingAsync(immediateExports, SyncRepo);
                        }

                        try
                        {
                            // Which of the batch's exports a password will follow, decided once for the batch: Creates from
                            // the rule, read again when the passwords are staged below (#2039), and Updates from what is
                            // still owed to the account (#2158).
                            var initialPasswordRuleIds = await FlagExportsFollowedByAPasswordAsync(immediateExports, SyncRepo, connectedSystem,
                                connector is IConnectorPasswordManagement);

                            // Execute batch via connector
                            List<ConnectedSystemExportResult> exportResults;
                            using (Diagnostics.Diagnostics.Connector.StartSpan("ExportBatch")
                                .SetTag("connectedSystemId", connectedSystem.Id)
                                .SetTag("batchSize", immediateExports.Count)
                                .SetTag("cumulativeObjectCount", processedCount + immediateExports.Count)
                                .SetTag("wallClockOffsetMs", exportPhaseStopwatch.Elapsed.TotalMilliseconds))
                            {
                                (exportResults, var immediateClassRefused, var immediateContradictions) = await ExportBatchAsync(connector, connectedSystem, immediateExports, cancellationToken, connectorProgress);
                                result.ClassMembershipRefusedCount += immediateClassRefused;
                                result.SingleValuedContradictionRefusedCount += immediateContradictions;
                            }

                            // Process results
                            using (Diagnostics.Diagnostics.Database.StartSpan("ProcessBatchSuccess")
                                .SetTag("batchSize", immediateExports.Count))
                            {
                                await ProcessBatchSuccessAsync(immediateExports, exportResults, executingMark, cancellationToken, result, SyncRepo, connectedSystem.EffectiveInitialPasswordTimeToLive, connectedSystem.Name, initialPasswordRuleIds);
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            // The run was cancelled while the Connector held the batch, and it gave no results (#2083).
                            await ReleaseBatchAfterCancellationAsync(immediateExports, executingMark, SyncRepo, connectedSystem.Name);
                            throw;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                        {
                            // Any other failure, including an OperationCanceledException nobody asked for (an HTTP client's
                            // timeout), is a failed attempt at the batch rather than the run being cancelled (#2083).
                            Log.Error(ex, "ExecuteUsingCallsWithBatchingAsync: Batch failed for {SystemName}", connectedSystem.Name);

                            // Create ProcessedExportItems so the RPEI pipeline can record the failures.
                            // Without this, the activity would silently report Status: Complete.
                            await RecordBatchFailureAsync(immediateExports, executingMark, ex, result, SyncRepo, connectedSystem.Name);

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
                            var batchItems = result.ProcessedExportItems;
                            result.ProcessedExportItems = [];
                            await batchCompletedCallback(batchItems);
                        }

                        // Clear the change tracker between batches to prevent O(n²) degradation.
                        // All DB writes use raw SQL (MarkBatchAsExecuting, BulkUpdatePendingExports),
                        // so tracked entities serve no purpose after each batch completes. Without
                        // this, Entity() calls in detach loops trigger change detection scans across
                        // all accumulated entities — 40K+ entities after 100 batches.
                        SyncRepo.ClearChangeTracker();
                    }
                }

                // Second pass: Exports with unresolved references (deferred)
                if (deferredExports.Count > 0)
                {
                    await ProcessDeferredExportsAsync(connectedSystem, connector, deferredExports, result, options, changeLimitLedger,
                        cancellationToken, progressCallback, connectorFactory, repositoryFactory);

                    // Drain any remaining deferred export items via callback
                    if (batchCompletedCallback != null && result.ProcessedExportItems.Count > 0)
                    {
                        var deferredItems = result.ProcessedExportItems;
                        result.ProcessedExportItems = [];
                        await batchCompletedCallback(deferredItems);
                    }
                }

                // Reached only if nothing above threw; used by the finally block below to tell a
                // genuine export failure apart from a clean run.
                exportPhaseSucceeded = true;
            }
            finally
            {
                // Before the connection closes, which forgets them, and whether or not the run completed (#2197).
                CaptureCreatedContainers(connector, result, resultLock: null);

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Information("ExecuteUsingCallsWithBatchingAsync: Export cancelled for {SystemName}", connectedSystem.Name);
            await HandOnProcessedExportItemsAsync(result, batchCompletedCallback, connectedSystem.Name);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ExecuteUsingCallsWithBatchingAsync: Failed to execute exports for {SystemName}", connectedSystem.Name);
            await HandOnProcessedExportItemsAsync(result, batchCompletedCallback, connectedSystem.Name);
            // Connection-level or unexpected errors must propagate so PerformExportAsync
            // can fail the activity via FailActivityWithErrorAsync. Swallowing the exception
            // here would cause the activity to silently report Status: Complete.
            throw;
        }
    }

    /// <summary>
    /// Hands on the execution items still held when the run stops early (#2083). The deferred pass's items are handed on
    /// only once the pass ends, so a run cancelled or failing part-way through it would otherwise never record the items
    /// of the batches it had already written, though their Pending Exports were. Never throws, so it cannot mask the
    /// exception that is unwinding.
    /// </summary>
    private static async Task HandOnProcessedExportItemsAsync(
        ExportExecutionResult result,
        Func<List<ProcessedExportItem>, Task>? batchCompletedCallback,
        string connectedSystemName)
    {
        if (batchCompletedCallback == null || result.ProcessedExportItems.Count == 0)
            return;

        // Taken off the result first, so a callback that fails part-way is never handed the same items again.
        var items = result.ProcessedExportItems;
        result.ProcessedExportItems = [];
        try
        {
            await batchCompletedCallback(items);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "HandOnProcessedExportItemsAsync: Could not record the execution items of the {Count} change(s) already exported to {ConnectedSystemName} " +
                "before the run stopped", items.Count, connectedSystemName);
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

                // The deferred batches are already in hand; each is taken once. A failed deferred batch does not stop the
                // others, as it never has on this pass: each batch's references were resolved independently.
                var remainingBatches = new Queue<List<PendingExport>>(deferredBatches);
                await ProcessBatchesInParallelAsync(connectedSystem, connector,
                    () => Task.FromResult(remainingBatches.TryDequeue(out var next) ? next : null),
                    stopOnBatchFailure: false, result, options,
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

            ExecutingBatchMark executingMark;
            using (Diagnostics.Diagnostics.Database.StartSpan("MarkDeferredBatchAsExecuting")
                .SetTag("batchSize", batch.Count))
            {
                executingMark = await MarkBatchAsExecutingAsync(batch, SyncRepo);
            }

            try
            {
                // A deferred export is followed by a password exactly as one sent on the first pass is (#2039, #2158).
                var initialPasswordRuleIds = await FlagExportsFollowedByAPasswordAsync(batch, SyncRepo, connectedSystem,
                    connector is IConnectorPasswordManagement);

                List<ConnectedSystemExportResult> exportResults;
                using (Diagnostics.Diagnostics.Connector.StartSpan("ExportDeferredBatch")
                    .SetTag("batchSize", batch.Count))
                {
                    (exportResults, var deferredClassRefused, var deferredContradictions) = await ExportBatchAsync(connector, connectedSystem, batch, cancellationToken, connectorProgress);
                    result.ClassMembershipRefusedCount += deferredClassRefused;
                    result.SingleValuedContradictionRefusedCount += deferredContradictions;
                }

                using (Diagnostics.Diagnostics.Database.StartSpan("ProcessDeferredBatchSuccess")
                    .SetTag("batchSize", batch.Count))
                {
                    await ProcessBatchSuccessAsync(batch, exportResults, executingMark, cancellationToken, result, SyncRepo, initialPasswordTimeToLive, connectedSystem.Name, initialPasswordRuleIds, unresolvedReferenceNotes);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // As on the first pass (#2083): the batch goes back as it was, and the run stops.
                await ReleaseBatchAfterCancellationAsync(batch, executingMark, SyncRepo, connectedSystem.Name);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // As on the first pass (#2083): a failed attempt at the batch, and the rest of the pass waits for the next run,
                // its batches never marked.
                Log.Error(ex, "ProcessDeferredBatchesSequentiallyAsync: Deferred batch failed for {SystemName}", connectedSystem.Name);
                await RecordBatchFailureAsync(batch, executingMark, ex, result, SyncRepo, connectedSystem.Name);
                break;
            }

            processedCount += batch.Count;
        }
    }

    /// <summary>
    /// Executes batches concurrently on up to <see cref="ExportExecutionOptions.MaxParallelism"/> workers. Each worker
    /// keeps one connector for every batch it runs: worker 0 uses the primary connector, every other worker opens one of
    /// its own from the factory on its first batch. A connector whose batch fails is discarded and the worker opens a fresh
    /// one for its next batch. Each batch runs on a repository scope (and so a DbContext) of its own, re-loading its
    /// Pending Exports by id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything that touches the caller's own DbContext (taking the next batch, progress, the connector's narration, the
    /// per-batch callback and persisting what a connector returns at close) goes through one gate, so the caller's context
    /// is never used by two workers at once.
    /// </para>
    /// <para>
    /// Batches are taken on demand rather than read ahead: each worker starts on a batch of its own, taken in order so the
    /// primary connector gets the first, then takes the next one whenever it is free. With
    /// <paramref name="stopOnBatchFailure"/> set, a failed batch stops every worker taking another; batches already running
    /// finish and are recorded, and nothing is read that is not then sent, exactly as when the sequential path stops at
    /// a failed batch.
    /// </para>
    /// </remarks>
    /// <param name="takeNextBatch">Returns the next batch to execute, or null when there are no more. Called under the
    /// caller's context gate, one call at a time.</param>
    /// <param name="stopOnBatchFailure">Whether a failed batch stops further batches being taken.</param>
    /// <param name="spanName">The connector span each batch's export is recorded under.</param>
    /// <param name="batchExecuted">Told how many Pending Exports each batch held once its outcome is recorded.</param>
    private async Task ProcessBatchesInParallelAsync(
        ConnectedSystem connectedSystem,
        IConnectorExportUsingCalls primaryConnector,
        Func<Task<List<PendingExport>?>> takeNextBatch,
        bool stopOnBatchFailure,
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
        IReadOnlyDictionary<Guid, string>? unresolvedReferenceNotes = null,
        Action<int>? batchExecuted = null)
    {
        // The one gate on the caller's DbContext; see the remarks above.
        using var callerContextGate = new SemaphoreSlim(1, 1);
        var processedCount = 0;
        var stopTaking = 0;
        var batchCounter = 0;

        // Sub-phase narration from the batch connectors, on the same gate as everything else that reaches the caller's
        // DbContext, so that concurrent batches cannot report through it at once.
        using var connectorProgress = CreateConnectorProgress(progressCallback, subPhase => new ExportProgressInfo
        {
            Phase = ExportPhase.Executing,
            TotalExports = result.TotalPendingExports,
            ProcessedExports = processedExportsOffset + Volatile.Read(ref processedCount),
            PassTotal = passTotal,
            PassProcessed = passTotal.HasValue ? Volatile.Read(ref processedCount) : null,
            Message = subPhase
        }, callerContextGate);

        // Lock for thread-safe result aggregation
        var resultLock = new object();

        async Task<T> UnderCallerContextGateAsync<T>(Func<Task<T>> action)
        {
            await callerContextGate.WaitAsync(cancellationToken);
            try
            {
                return await action();
            }
            finally
            {
                callerContextGate.Release();
            }
        }

        async Task<List<PendingExport>?> TakeNextBatchAsync()
        {
            if (Volatile.Read(ref stopTaking) == 1)
                return null;

            return await UnderCallerContextGateAsync(async () =>
            {
                // Checked again under the gate: a worker may have stopped the run while this one waited for it.
                if (Volatile.Read(ref stopTaking) == 1)
                    return null;

                try
                {
                    return await takeNextBatch();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Reading the next batch failed: nothing more can be read, so no worker takes another.
                    Volatile.Write(ref stopTaking, 1);
                    throw;
                }
            });
        }

        // Each worker starts on a batch of its own, taken in order, so worker 0 (on the primary connector) runs the first.
        var firstBatches = new List<List<PendingExport>>();
        for (var i = 0; i < options.MaxParallelism; i++)
        {
            var batch = await TakeNextBatchAsync();
            if (batch == null)
                break;
            firstBatches.Add(batch);
        }

        Log.Information("ProcessBatchesInParallelAsync: Processing batches with up to {Workers} worker(s) (MaxParallelism={MaxParallelism}, stop on a failed batch: {StopOnBatchFailure})",
            firstBatches.Count, options.MaxParallelism, stopOnBatchFailure);

        var workers = firstBatches
            .Select((firstBatch, workerIndex) => Task.Run(() => RunWorkerAsync(workerIndex, firstBatch), cancellationToken))
            .ToList();

        await Task.WhenAll(workers);

        async Task RunWorkerAsync(int workerIndex, List<PendingExport> firstBatch)
        {
            // This worker's connector, kept for every batch it runs. Worker 0 starts on the primary connector, which the
            // caller opened and will close; any other connector is the worker's own, opened on first use and closed here.
            IConnectorExportUsingCalls? connector = workerIndex == 0 ? primaryConnector : null;
            IConnector? ownedConnector = null;

            var completed = false;
            try
            {
                var batch = firstBatch;
                while (batch != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var batchIndex = Interlocked.Increment(ref batchCounter) - 1;
                    if (!await ExecuteBatchAsync(batchIndex, batch))
                    {
                        // A connector whose batch failed may be broken: this worker opens a fresh one for its next batch.
                        await DiscardConnectorAsync(unwinding: false);
                        if (stopOnBatchFailure)
                            Volatile.Write(ref stopTaking, 1);
                    }

                    batch = await TakeNextBatchAsync();
                }

                completed = true;
            }
            finally
            {
                // Whatever stopped this worker early (cancellation, or a failure it could not record) stops the others
                // taking more; the batches they are running finish. Its connector is closed either way, without masking
                // anything that is unwinding.
                if (!completed)
                    Volatile.Write(ref stopTaking, 1);
                await DiscardConnectorAsync(unwinding: !completed);
            }

            // Executes one batch on this worker's connector. True when the batch's outcome was recorded as executed (its
            // individual exports may still have failed); false when the batch failed as a whole.
            async Task<bool> ExecuteBatchAsync(int batchIndex, List<PendingExport> callersBatch)
            {
                var batchIds = callersBatch.Select(pe => pe.Id).ToList();
                try
                {
                    // A per-batch repository scope with its own context; disposing it when the batch completes releases
                    // the context and its pooled connection (undisposed scopes pinned one connection per batch and
                    // exhausted the pool at scale).
                    using var batchScope = repositoryFactory();
                    var batchRepo = batchScope.Repository;

                    // Re-load this batch's Pending Exports from the batch's own context
                    var batch = await batchRepo.GetPendingExportsByIdsAsync(batchIds);
                    if (batch.Count == 0)
                    {
                        Log.Warning("ProcessBatchesInParallelAsync: Batch {BatchIndex} returned 0 exports for {IdCount} IDs",
                            batchIndex, batchIds.Count);
                        return true;
                    }

                    // Declared outside the try so the catches below, which record an interrupted batch through this batch's
                    // own repository while its scope is still alive, know what the batch was before it was sent (#2083).
                    // Null until the batch is marked: one whose Connector could not be opened was never touched.
                    ExecutingBatchMark? executingMark = null;
                    var batchResult = new ExportExecutionResult();
                    var batchResultMerged = false;

                    try
                    {
                        // The worker's own connector is prepared and opened inside the try, so one that fails to open
                        // fails this batch and is still closed and disposed when it is discarded, and what it returns at
                        // close (a pin the failure invalidated, issue #230) is still persisted (#1875).
                        if (connector == null)
                        {
                            ownedConnector = connectorFactory();
                            if (ownedConnector is not IConnectorExportUsingCalls callsConnector)
                                throw new InvalidOperationException($"The connector factory returned a {ownedConnector.GetType().Name}, which cannot export using calls.");

                            connector = callsConnector;
                            PrepareConnectorForExport(ownedConnector, connectedSystem);
                            connector.OpenExportConnection(connectedSystem.SettingValues, connectedSystem.PersistedConnectorData);
                        }

                        // Mark batch as executing (raw SQL - context-independent)
                        using (Diagnostics.Diagnostics.Database.StartSpan("MarkBatchAsExecuting")
                            .SetTag("batchSize", batch.Count))
                        {
                            executingMark = await MarkBatchAsExecutingAsync(batch, batchRepo);
                        }

                        // Decided on the instances this batch re-loaded, through the batch's own repository: a flag
                        // set on the caller's copies would never reach this batch's connector (#2039, #2158).
                        var initialPasswordRuleIds = await FlagExportsFollowedByAPasswordAsync(batch, batchRepo, connectedSystem,
                            connector is IConnectorPasswordManagement);

                        // Execute batch via connector. The batch was re-loaded from persisted state above, which is why the
                        // deferred pass persists its reference resolutions and the writable/unresolved split (issue #1398)
                        // before this path runs: ForConnector decides from what it reads.
                        List<ConnectedSystemExportResult> exportResults;
                        using (Diagnostics.Diagnostics.Connector.StartSpan(spanName)
                            .SetTag("connectedSystemId", connectedSystem.Id)
                            .SetTag("batchSize", batch.Count)
                            .SetTag("worker", workerIndex))
                        {
                            (exportResults, var batchClassRefused, var batchContradictions) = await ExportBatchAsync(connector, connectedSystem, batch, cancellationToken, connectorProgress);
                            batchResult.ClassMembershipRefusedCount = batchClassRefused;
                            batchResult.SingleValuedContradictionRefusedCount = batchContradictions;
                        }

                        // Process results using the batch's own repository
                        using (Diagnostics.Diagnostics.Database.StartSpan("ProcessBatchSuccess")
                            .SetTag("batchSize", batch.Count))
                        {
                            await ProcessBatchSuccessAsync(batch, exportResults, executingMark, cancellationToken, batchResult, batchRepo,
                                connectedSystem.EffectiveInitialPasswordTimeToLive, connectedSystem.Name, initialPasswordRuleIds, unresolvedReferenceNotes);
                        }

                        // Aggregate results into shared result (thread-safe)
                        lock (resultLock)
                        {
                            AccumulateBatchResult(result, batchResult, includeItems: batchCompletedCallback == null);
                        }
                        batchResultMerged = true;

                        // Stream processed items per batch, through the caller's context
                        if (batchCompletedCallback != null && batchResult.ProcessedExportItems.Count > 0)
                            await UnderCallerContextGateAsync(async () => { await batchCompletedCallback(batchResult.ProcessedExportItems); return true; });

                        await RecordBatchExecutedAsync(batch.Count);
                        return true;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // The run was cancelled while the Connector held this batch, and it gave no results (#2083).
                        if (executingMark != null)
                            await ReleaseBatchAfterCancellationAsync(batch, executingMark, batchRepo, connectedSystem.Name);
                        throw;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // A failed attempt at this batch (#2083), recorded through the batch's own repository while its scope
                        // is alive: its Pending Exports still Executing get one failed attempt each, with an execution item
                        // naming their object. Results this batch already merged stand; the failure is reported on a result
                        // of its own.
                        Log.Error(ex, "ProcessBatchesInParallelAsync: Batch {BatchIndex} failed", batchIndex);
                        var failureResult = batchResultMerged ? new ExportExecutionResult() : batchResult;
                        await RecordBatchFailureAsync(batch, executingMark, ex, failureResult, batchRepo, connectedSystem.Name);

                        lock (resultLock)
                        {
                            AccumulateBatchResult(result, failureResult, includeItems: batchCompletedCallback == null);
                        }

                        if (batchCompletedCallback != null && failureResult.ProcessedExportItems.Count > 0)
                        {
                            try
                            {
                                await UnderCallerContextGateAsync(async () => { await batchCompletedCallback(failureResult.ProcessedExportItems); return true; });
                            }
                            catch (Exception callbackEx) when (callbackEx is not OperationCanceledException)
                            {
                                Log.Error(callbackEx, "ProcessBatchesInParallelAsync: Could not record the execution items of failed batch {BatchIndex}", batchIndex);
                            }
                        }

                        await RecordBatchExecutedAsync(batch.Count);
                        return false;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Reached when the batch could not even be loaded (its repository scope or the reload failed), so no
                    // Pending Export of it was touched and none can be named; a batch that was loaded records its own
                    // failure above. Its exports are left for the next run.
                    Log.Error(ex, "ProcessBatchesInParallelAsync: Batch {BatchIndex} failed", batchIndex);

                    // Create ProcessedExportItems so the RPEI pipeline can record the failures. Without them, the activity
                    // would silently report Status: Complete with no error RPEIs. Each names its change from the caller's
                    // copy of the batch, since this batch's own reload is what failed.
                    lock (resultLock)
                    {
                        result.FailedCount += batchIds.Count;
                        result.ProcessedExportItems.AddRange(callersBatch.Select(export => new ProcessedExportItem
                        {
                            ChangeType = export.ChangeType,
                            ConnectedSystemObject = export.ConnectedSystemObject,
                            PendingExportId = export.Id,
                            AttributeChangeCount = export.AttributeValueChanges.Count,
                            AttributeValueChanges = export.AttributeValueChanges.ToList(),
                            Succeeded = false,
                            ErrorMessage = $"Batch export failed: {ex.Message}",
                            ErrorCount = export.ErrorCount,
                            ErrorType = ConnectedSystemExportErrorType.General
                        }.WithCauseFrom(export)));
                    }
                    return false;
                }
            }

            // Counts a batch as finished, and reports progress through the caller's context.
            async Task RecordBatchExecutedAsync(int batchCount)
            {
                var newProcessedCount = Interlocked.Add(ref processedCount, batchCount);
                batchExecuted?.Invoke(batchCount);

                if (progressCallback == null)
                    return;

                await UnderCallerContextGateAsync(async () =>
                {
                    await progressCallback(new ExportProgressInfo
                    {
                        Phase = ExportPhase.Executing,
                        TotalExports = result.TotalPendingExports,
                        ProcessedExports = processedExportsOffset + newProcessedCount,
                        PassTotal = passTotal,
                        PassProcessed = passTotal.HasValue ? newProcessedCount : null,
                        CurrentBatchSize = batchCount,
                        Message = passTotal.HasValue ? "Exporting deferred" : "Exporting"
                    });
                    return true;
                });
            }

            // Lets go of this worker's connector. The primary is only forgotten (the caller closes it); a connector of the
            // worker's own is closed and disposed, what it created captured first and what it returns at close persisted.
            async Task DiscardConnectorAsync(bool unwinding)
            {
                connector = null;
                var toClose = ownedConnector;
                ownedConnector = null;
                if (toClose is not IConnectorExportUsingCalls closing)
                    return;

                // Before its connection closes, which forgets them, whether or not its batches succeeded (#2197).
                CaptureCreatedContainers(toClose, result, resultLock);

                string? closeReturn;
                try
                {
                    try
                    {
                        closeReturn = closing.CloseExportConnection();
                    }
                    finally
                    {
                        (toClose as IDisposable)?.Dispose();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Every batch this connector ran has its outcome recorded, and only closing its connection failed
                    // afterwards (#2083): reported once, as an item of its own, rather than as a batch failing again.
                    Log.Error(ex, "ProcessBatchesInParallelAsync: Worker {WorkerIndex}'s batch results were recorded, but closing its connection failed", workerIndex);
                    lock (resultLock)
                    {
                        result.ProcessedExportItems.Add(new ProcessedExportItem
                        {
                            ChangeType = PendingExportChangeType.Update,
                            Succeeded = false,
                            ErrorMessage = $"A batch's results were recorded, but closing its connection failed: {ex.Message}",
                            ErrorType = ConnectedSystemExportErrorType.General
                        });
                    }
                    return;
                }

                // Persist connector state the connector chose to override at close (issue #230), through the caller's
                // context. Null (the overwhelmingly common case) means "nothing to override".
                if (closeReturn == null)
                    return;

                try
                {
                    await UnderCallerContextGateAsync(async () =>
                    {
                        await Application.ConnectedSystems.UpdateConnectedSystemPersistedConnectorDataAsync(connectedSystem, closeReturn);
                        return true;
                    });
                }
                catch (Exception persistEx) when (unwinding)
                {
                    // Something is already unwinding through this worker. Throwing here would replace it rather than chain
                    // it and hide the real failure behind an unrelated persistence error, so log and let it continue.
                    Log.Error(persistEx,
                        "ProcessBatchesInParallelAsync: Failed to persist connector data returned by CloseExportConnection while worker {WorkerIndex} is failing for Connected System {ConnectedSystemId}. The worker's own failure takes precedence and will propagate.",
                        workerIndex, connectedSystem.Id);
                }
            }
        }
    }

    /// <summary>
    /// Adds one parallel batch's counts, and its execution items when they are not streamed, to the run's result. The
    /// caller holds the result's lock.
    /// </summary>
    private static void AccumulateBatchResult(ExportExecutionResult into, ExportExecutionResult batchResult, bool includeItems)
    {
        into.SuccessCount += batchResult.SuccessCount;
        into.FailedCount += batchResult.FailedCount;
        into.DeferredCount += batchResult.DeferredCount;
        into.PartiallyExportedCount += batchResult.PartiallyExportedCount;
        into.OptimisticApplyAppliedCount += batchResult.OptimisticApplyAppliedCount;
        into.OptimisticApplySkippedCount += batchResult.OptimisticApplySkippedCount;
        into.OptimisticApplyFailedCount += batchResult.OptimisticApplyFailedCount;
        into.OptimisticApplyUnresolvedReferenceCount += batchResult.OptimisticApplyUnresolvedReferenceCount;
        into.InitialPasswordsStagedCount += batchResult.InitialPasswordsStagedCount;
        into.InitialPasswordStagingFailedCount += batchResult.InitialPasswordStagingFailedCount;
        into.InitialPasswordMarkingFailedCount += batchResult.InitialPasswordMarkingFailedCount;
        into.InitialPasswordsCarriedCount += batchResult.InitialPasswordsCarriedCount;
        into.ClassMembershipRefusedCount += batchResult.ClassMembershipRefusedCount;
        into.SingleValuedContradictionRefusedCount += batchResult.SingleValuedContradictionRefusedCount;
        into.GeneratedValuesRemediatedCount += batchResult.GeneratedValuesRemediatedCount;
        into.GeneratedValuesNeedingDecisionCount += batchResult.GeneratedValuesNeedingDecisionCount;
        if (includeItems)
            into.ProcessedExportItems.AddRange(batchResult.ProcessedExportItems);
    }

    /// <summary>
    /// Marks a batch of exports as executing using raw SQL for efficiency.
    /// Bypasses EF Core change tracking since this is a simple status update.
    /// </summary>
    /// <returns>
    /// Each export's retry state as it stood just before the mark (#2083): what an export the run never got to is put back
    /// to, and what a failed attempt is counted from; and the time of the mark, which putting it back is guarded on (#2197).
    /// </returns>
    private static async Task<ExecutingBatchMark> MarkBatchAsExecutingAsync(List<PendingExport> batch, ISyncRepository repository)
    {
        var before = batch.ToDictionary(pe => pe.Id, PendingExportRetryState.Of);
        var markedAt = await repository.MarkPendingExportsAsExecutingAsync(batch);
        return new ExecutingBatchMark(before, markedAt);
    }

    /// <summary>
    /// Puts a batch interrupted by the run being cancelled back as it was before it was sent (#2083), so the next run
    /// carries on without a Worker restart. Only rows still Executing are written: a result already recorded stands. No
    /// attempt is counted, because the Connector gave no results to count.
    /// <para>
    /// Never throws: a failure here is logged and the rows are left for the Worker's startup recovery, so it cannot mask
    /// the cancellation that is unwinding.
    /// </para>
    /// </summary>
    private static async Task ReleaseBatchAfterCancellationAsync(
        List<PendingExport> batch,
        ExecutingBatchMark mark,
        ISyncRepository repository,
        string connectedSystemName)
    {
        var before = mark.StatesBefore;
        try
        {
            var released = await repository.ReleaseExecutingPendingExportsAsync(before.Values.ToList(), mark.MarkedAt);
            foreach (var export in batch.Where(pe => released.Contains(pe.Id)))
                before[export.Id].ApplyTo(export);

            if (released.Count > 0)
            {
                // A Connector that stops on cancellation reports what it wrote and marks the rest not attempted; one that
                // throws instead gives JIM no results at all, so anything it had already written is not recorded and may be
                // refused as already present when the next run sends it again.
                Log.Warning("ReleaseBatchAfterCancellationAsync: The export run to {ConnectedSystemName} was cancelled while the Connector held a batch, " +
                    "and the Connector returned no results for it. {Count} Pending Export(s) were put back as they were, to be sent by the next run: {PendingExportIds}. " +
                    "Anything the Connector had already written for them may be refused as already present.",
                    connectedSystemName, released.Count, DescribeIds(released));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "ReleaseBatchAfterCancellationAsync: Could not put back the {Count} Pending Export(s) of the cancelled batch to {ConnectedSystemName}; " +
                "they stay Executing until the Worker next starts: {PendingExportIds}",
                before.Count, connectedSystemName, DescribeIds(before.Keys));
        }
    }

    /// <summary>
    /// Records a batch that failed as a whole (the Connector threw, or processing its results did) as one failed attempt
    /// at each of its changes still Executing (#2083), with an execution item naming each change's object. A change whose
    /// result was already recorded keeps it, so a Create whose success was recorded is never put back to be sent again;
    /// when every result was recorded and only JIM's own bookkeeping failed afterwards, one batch-level item reports the
    /// error. Results processing that fails before the batch's results are persisted loses them: the whole batch is then
    /// recorded as failed, so a Create the Connector had written is sent again.
    /// <para>
    /// <paramref name="before"/> is null when the batch failed before it was marked Executing (its Connector could not be
    /// opened): its rows were never touched and are left for the next run, and each change still gets an item.
    /// </para>
    /// <para>
    /// A failure after part of the batch's results were counted, but before they were persisted, can leave this run's
    /// success counters high; the execution items stay exact, one per Pending Export, because an earlier item for a
    /// change written here as failed is replaced.
    /// </para>
    /// </summary>
    private static async Task RecordBatchFailureAsync(
        List<PendingExport> batch,
        ExecutingBatchMark? mark,
        Exception exception,
        ExportExecutionResult result,
        ISyncRepository repository,
        string connectedSystemName)
    {
        var itemMessage = $"Batch export failed: {exception.Message}";

        IReadOnlySet<Guid>? failed = null;
        if (mark != null)
        {
            var before = mark.StatesBefore;
            var attemptedAt = DateTime.UtcNow;
            var failedAttempts = batch
                .Where(pe => before.ContainsKey(pe.Id))
                .ToDictionary(pe => pe.Id, pe => before[pe.Id].AfterFailedAttempt(exception.Message, stackTrace: null, attemptedAt));
            try
            {
                failed = await repository.ReleaseExecutingPendingExportsAsync(failedAttempts.Values.ToList(), mark.MarkedAt);
                foreach (var export in batch.Where(pe => failed.Contains(pe.Id)))
                {
                    failedAttempts[export.Id].ApplyTo(export);
                    LogFailedAttempt(failedAttempts[export.Id]);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "RecordBatchFailureAsync: Could not record the failed attempt on the {Count} Pending Export(s) of the failed batch to {ConnectedSystemName}; " +
                    "they stay Executing until the Worker next starts: {PendingExportIds}",
                    batch.Count, connectedSystemName, DescribeIds(batch.Select(pe => pe.Id)));
            }
        }

        // The changes to report as failed: those just written as a failed attempt, or, when nothing could be written (never
        // marked, or the write itself failed), every change in the batch, so the failure is never silent.
        var reported = failed == null ? batch : batch.Where(pe => failed.Contains(pe.Id)).ToList();
        var reportedIds = reported.Select(pe => pe.Id).ToHashSet();
        result.ProcessedExportItems.RemoveAll(item => item.PendingExportId is { } id && reportedIds.Contains(id));

        foreach (var export in reported)
        {
            result.FailedCount++;
            result.ProcessedExportItems.Add(new ProcessedExportItem
            {
                ChangeType = export.ChangeType,
                ConnectedSystemObject = export.ConnectedSystemObject,
                PendingExportId = export.Id,
                AttributeChangeCount = export.AttributeValueChanges.Count,
                AttributeValueChanges = export.AttributeValueChanges.ToList(),
                Succeeded = false,
                ErrorMessage = itemMessage,
                ErrorCount = export.ErrorCount,
                ErrorType = ConnectedSystemExportErrorType.General
            }.WithCauseFrom(export));
        }

        if (reported.Count == 0)
        {
            result.ProcessedExportItems.Add(new ProcessedExportItem
            {
                ChangeType = PendingExportChangeType.Update,
                Succeeded = false,
                ErrorMessage = $"The batch's results were recorded, but JIM failed while finishing them: {exception.Message}",
                ErrorType = ConnectedSystemExportErrorType.General
            });
        }

        Log.Error("RecordBatchFailureAsync: A batch of {BatchCount} export(s) to {ConnectedSystemName} failed: {Error}. {FailedCount} recorded as a failed attempt, " +
            "{RecordedCount} already had their results recorded",
            batch.Count, connectedSystemName, LogSanitiser.Sanitise(exception.Message), reported.Count, failed == null ? 0 : batch.Count - failed.Count);
    }

    /// <summary>
    /// Up to ten ids, for a log line naming the Pending Exports an interrupted batch left behind.
    /// </summary>
    private static string DescribeIds(IEnumerable<Guid> ids)
    {
        var list = ids.ToList();
        var shown = string.Join(", ", list.Take(10));
        return list.Count > 10 ? $"{shown} and {list.Count - 10} more" : shown;
    }

    /// <summary>
    /// Sends a batch to the Connector, holding back any export that would leave an object invalid at the Connected
    /// System, or that carries more than one change for a single-valued attribute, and failing it with the attributes
    /// named. Results come back in the batch's own order either way, with the two kinds of refusal counted apart.
    /// </summary>
    /// <remarks>
    /// Refusing here rather than letting the Connected System reject the change gives an administrator something
    /// they can act on: "posixAccount requires gidNumber" rather than whichever error the directory chose to
    /// return. It is the same reasoning as the managed-scope refusal in the LDAP Connector. An export contradicting
    /// itself over a single-valued attribute (#2182) is refused for a different reason: it is a fault in JIM, and a
    /// Connector could apply it in an order that clears the value and report success. The two are counted apart
    /// because they point at different remedies: a class membership refusal at configuration, a contradiction at JIM.
    /// </remarks>
    internal static async Task<(List<ConnectedSystemExportResult> Results, int ClassMembershipRefused, int ContradictionsRefused)> ExportBatchAsync(
        IConnectorExportUsingCalls connector,
        ConnectedSystem connectedSystem,
        List<PendingExport> batch,
        CancellationToken cancellationToken,
        IConnectorProgress connectorProgress)
    {
        var refusals = new Dictionary<int, ConnectedSystemExportResult>();
        var sendable = new List<PendingExport>();
        var contradictions = 0;

        for (var i = 0; i < batch.Count; i++)
        {
            // An export that contradicts itself over a single-valued attribute is refused first (#2182): it is a fault
            // in JIM rather than configuration, and no class check is meaningful over changes that cannot all apply.
            var contradiction = SingleValuedAttributeChangeValidator.Check(ForConnector(batch[i]));
            var refusal = contradiction ?? ClassMembershipValidator.Check(batch[i], connectedSystem);
            if (refusal == null)
            {
                sendable.Add(batch[i]);
                continue;
            }

            refusals[i] = refusal;
            if (contradiction != null)
                contradictions++;
        }

        if (refusals.Count == 0)
            return (await connector.ExportAsync(batch.Select(ForConnector).ToList(), cancellationToken, connectorProgress), 0, 0);

        Log.Warning("ExportBatchAsync: Refused {RefusedCount} of {BatchCount} export(s) on '{ConnectedSystem}' before sending them: " +
            "{ContradictionCount} carrying more than one change for a single-valued attribute, {ClassMembershipCount} adding a class whose required " +
            "attributes have no value. The reason is recorded against each object.",
            refusals.Count, batch.Count, connectedSystem.Name, contradictions, refusals.Count - contradictions);

        var sentResults = sendable.Count > 0
            ? await connector.ExportAsync(sendable.Select(ForConnector).ToList(), cancellationToken, connectorProgress)
            : [];

        return (InBatchOrder(batch.Count, refusals, sentResults, cancellationToken), refusals.Count - contradictions, contradictions);
    }

    /// <summary>
    /// The refusals that keep each export carrying more than one change for a single-valued attribute (#2182) back from
    /// the Connector, keyed by the export's position in <paramref name="exports"/>.
    /// </summary>
    private static Dictionary<int, ConnectedSystemExportResult> RefuseSelfContradictingExports(IList<PendingExport> exports)
    {
        var refusals = new Dictionary<int, ConnectedSystemExportResult>();
        for (var i = 0; i < exports.Count; i++)
        {
            var refusal = SingleValuedAttributeChangeValidator.Check(exports[i]);
            if (refusal != null)
                refusals[i] = refusal;
        }

        return refusals;
    }

    /// <summary>
    /// One result per export of a batch, in the batch's own order, from the refusals held back and the results of the
    /// exports that were sent. The caller pairs results with exports by index. An export the Connector returned no result
    /// for is given one by <see cref="NoResultFromTheConnector"/>.
    /// </summary>
    private static List<ConnectedSystemExportResult> InBatchOrder(
        int batchCount,
        Dictionary<int, ConnectedSystemExportResult> refusals,
        IList<ConnectedSystemExportResult> sentResults,
        CancellationToken cancellationToken)
    {
        var results = new List<ConnectedSystemExportResult>(batchCount);
        var sentIndex = 0;
        for (var i = 0; i < batchCount; i++)
        {
            if (refusals.TryGetValue(i, out var refusal))
                results.Add(refusal);
            else
                results.Add(sentIndex < sentResults.Count ? sentResults[sentIndex++] : NoResultFromTheConnector(cancellationToken));
        }

        return results;
    }

    /// <summary>
    /// The result of an export the Connector returned no result for (#2197). The Connector has not said what became of it,
    /// so it is never read as a success, which would confirm a write nobody made: once the run has been cancelled it is
    /// taken to be a change the Connector never started, and put back as it was (#2083); otherwise it is a failed attempt.
    /// </summary>
    private static ConnectedSystemExportResult NoResultFromTheConnector(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? ConnectedSystemExportResult.NotAttempted()
            : ConnectedSystemExportResult.Failed("The Connector returned no result for this change, so JIM cannot tell whether it was written; it will be sent again.");

    /// <summary>
    /// Decides whether a Delete export that just succeeded is the terminal step for a Connected System
    /// Object whose provisioning was never confirmed by an import.
    /// <para>
    /// A Connected System Object is created <c>PendingProvisioning</c> alongside a Create Pending Export.
    /// If the Create is exported (the target object now exists) but the Metaverse Object is withdrawn
    /// before any confirming import, JIM stages a Delete Pending Export for the still-<c>PendingProvisioning</c>
    /// object; when that Delete exports successfully, the target object is gone, but the Connected System
    /// Object itself would otherwise be stranded forever: import deletion detection deliberately excludes
    /// <c>PendingProvisioning</c> objects (<c>ConnectedSystemRepository.BuildDeletionDetectionQuery</c>), so
    /// nothing ever obsoletes or deletes it, and it never gets another Pending Export to retry.
    /// </para>
    /// <para>
    /// A successful export of the Delete is the only confirmation such an object can ever get, so callers
    /// that see this return true must remove the Connected System Object and its Pending Export immediately
    /// rather than leaving the export <c>Exported</c> (see <see cref="RemoveUnconfirmedProvisioningCsosAsync"/>).
    /// Connected System Objects with Status <c>Normal</c> are unaffected: their existing lifecycle (import
    /// marks Obsolete, sync deletes) still applies. A failed Delete export never reaches this check; callers
    /// must only call it once a Delete has already succeeded.
    /// </para>
    /// </summary>
    private static bool IsUnconfirmedProvisioningDeleteSuccess(PendingExport export)
    {
        return export.ChangeType == PendingExportChangeType.Delete
            && export.ConnectedSystemObject != null
            && export.ConnectedSystemObject.Status == ConnectedSystemObjectStatus.PendingProvisioning;
    }

    /// <summary>
    /// Removes the Connected System Objects (and their now-superfluous Pending Exports) for Delete exports
    /// that succeeded against a Connected System Object whose provisioning was never confirmed; see
    /// <see cref="IsUnconfirmedProvisioningDeleteSuccess"/> for the rule. Deletes by id via
    /// <see cref="ISyncRepository.DeleteConnectedSystemObjectsByIdsAsync"/> rather than the tracked-graph
    /// <see cref="ISyncRepository.DeleteConnectedSystemObjectsAsync"/>: export batches load Pending Exports
    /// <c>AsNoTracking()</c> with the Connected System Object graph included, and attaching that untracked
    /// graph to a tracked <c>RemoveRange</c> can throw on the duplicate-key attach.
    /// </summary>
    /// <param name="qualifyingExports">
    /// Delete exports that satisfy <see cref="IsUnconfirmedProvisioningDeleteSuccess"/>. Each caller is
    /// responsible for flagging its own <see cref="ProcessedExportItem.ConnectedSystemObjectRemoved"/> and
    /// for excluding these exports from its own Pending Export update batch, since the row is being deleted
    /// here, not updated.
    /// </param>
    private static async Task RemoveUnconfirmedProvisioningCsosAsync(List<PendingExport> qualifyingExports, ISyncRepository repository)
    {
        if (qualifyingExports.Count == 0)
            return;

        // The FK scalar, not the navigation: export batches load these AsNoTracking, and the scalar is
        // always populated from the row data regardless of Include shape (src/CLAUDE.md > "Prefer FK
        // Scalars Over Navigation Checks Under AsNoTracking").
        var csoIds = qualifyingExports
            .Select(export => export.ConnectedSystemObjectId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (csoIds.Count == 0)
            return;

        // Delete the Pending Export(s) first: once the CSO is gone there is nothing left for it to
        // describe, and DeleteConnectedSystemObjectsByIdsAsync does not touch PendingExports itself
        // (its own FK to this table is SetNull, which would otherwise just orphan the row).
        await repository.DeletePendingExportsByConnectedSystemObjectIdsAsync(csoIds);
        var removedCount = await repository.DeleteConnectedSystemObjectsByIdsAsync(csoIds);

        // Synchronisation Integrity: log summary statistics (count plus CSO ids) at the end of this batch
        // operation, same as every other batch write in this server.
        Log.Information("RemoveUnconfirmedProvisioningCsosAsync: {Count} Connected System Object(s) removed because their Delete export " +
            "has just succeeded and no confirming import will follow it (their provisioning was never confirmed by an import, or the " +
            "export auto-confirmed): [{CsoIds}]",
            removedCount, string.Join(", ", csoIds));
    }

    /// <summary>
    /// Processes a batch of exports with their corresponding ConnectedSystemExportResult data.
    /// Uses batch updates for efficiency - pre-fetches attribute definitions and performs
    /// a single SaveChanges for all CSO updates.
    /// Accepts an explicit repository parameter to support both sequential (shared) and parallel (per-batch) paths.
    /// </summary>
    /// <param name="connectedSystemName">
    /// The Connected System's name, carried onto the parent Activity a provisioned password change stages
    /// (#1697), so the queue page and the Activity list can name the target without a join.
    /// </param>
    /// <param name="initialPasswordRuleIds">
    /// The batch's provisioning Synchronisation Rules that ask for an Initial Password, as
    /// <see cref="FlagExportsFollowedByAPasswordAsync"/> read them before the batch was sent (#2039), so the
    /// passwords staged here are the ones the Connector was told would follow. Null when that lookup failed.
    /// </param>
    /// <param name="cancellationToken">
    /// The run's cancellation, read only to tell a not-attempted result (#2083) the run's cancellation explains from one
    /// it does not: a Connector reporting a change not attempted while the run carries on is failing to export it.
    /// </param>
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
        ExecutingBatchMark executingMark,
        CancellationToken cancellationToken,
        ExportExecutionResult result,
        ISyncRepository repository,
        TimeSpan initialPasswordTimeToLive,
        string connectedSystemName,
        IReadOnlySet<int>? initialPasswordRuleIds,
        IReadOnlyDictionary<Guid, string>? unresolvedReferenceNotes = null)
    {
        var exportsToUpdate = new List<PendingExport>();
        var csosToUpdate = new List<(ConnectedSystemObject cso, ConnectedSystemExportResult exportResult)>();
        var successfulNonDeleteExports = new List<PendingExport>();
        var provisionedAccounts = new List<PendingExport>();
        var unconfirmedProvisioningCsoDeletes = new List<PendingExport>();
        var notAttempted = new List<PendingExport>();
        var notAttemptedWithoutCancellation = 0;

        // The accounts whose enabled state an export in this batch wrote (#2064), as the Connector reported it: a
        // Create's staged first password is marked from it below, so delivering it leaves the enabled state to the
        // Attribute Flow.
        var reportedWritingTheEnabledState = batch.Zip(exportResults)
            .Where(sent => sent.Second.Success && sent.Second.SetsAccountEnabledState && sent.First.ConnectedSystemObject != null)
            .Select(sent => sent.First)
            .ToList();
        var accountsWithEnabledStateWritten = reportedWritingTheEnabledState
            .Select(pe => pe.ConnectedSystemObject!.Id)
            .ToHashSet();

        // Any other export's owed first password (a leaver disabled, or an account disabled on deprovisioning, before
        // the password landed) is marked here, first, straight from what the Connector reported (#2173). A delivery
        // waits while an export to its account is in flight, so the mark has to be in place before the batch leaves
        // Executing; and the directory has already changed, so nothing in processing the results, which can throw, may
        // stand between the write and the mark. A Delete against an account whose provisioning is unconfirmed is
        // covered too, though its object is removed below rather than left awaiting confirmation.
        var owedAccountsWithEnabledStateWritten = reportedWritingTheEnabledState
            .Where(pe => pe.ChangeType != PendingExportChangeType.Create)
            .Select(pe => pe.ConnectedSystemObject!.Id)
            .ToHashSet();
        if (owedAccountsWithEnabledStateWritten.Count > 0)
        {
            await MarkOwedInitialPasswordsAsSetByAttributeFlowAsync(batch[0].ConnectedSystemId, owedAccountsWithEnabledStateWritten, result, repository);
        }

        // Collision Remediation's derived-input marks for this batch, made on the first import-mode revision and flushed
        // once below, after the batch's writes (#1750 Phase 4).
        DerivedInputMarkBatch? derivedInputMarks = null;
        var collisionRemediation = _collisionRemediation;
        var collisionCandidates = new List<(PendingExport Export, ConnectedSystemExportResult Rejection, List<PendingExportAttributeValueChange> WrittenChanges)>();

        for (var i = 0; i < batch.Count; i++)
        {
            var export = batch[i];
            var exportResult = i < exportResults.Count ? exportResults[i] : NoResultFromTheConnector(cancellationToken);

            // What the connector was actually handed (see ForConnector), and what it was not: reference
            // changes still awaiting resolution stay behind on a partial write (issue #1398).
            var writtenChanges = WritableChanges(export);
            var stillUnresolvedCount = export.AttributeValueChanges.Count(IsUnresolvedReference);
            var wasCreate = export.ChangeType == PendingExportChangeType.Create;

            // Never started, because the run was cancelled (#2083): put back below exactly as it was, uncounted and with no
            // execution item, since nothing was sent and nothing failed.
            if (exportResult.IsNotAttempted && cancellationToken.IsCancellationRequested)
            {
                notAttempted.Add(export);
                continue;
            }

            // A Connector that reports a change not attempted while the run carries on is not exporting it, and putting it
            // back silently would repeat that every run unseen: it is a failed attempt like any other.
            if (exportResult.IsNotAttempted)
            {
                exportResult = ConnectedSystemExportResult.Failed(
                    "The Connector reported this change as not attempted, although the export run was not cancelled.");
                notAttemptedWithoutCancellation++;
            }

            if (!exportResult.Success)
            {
                // A value already in use (Unique Value Generation, #242, release 4): Collision Remediation may correct a
                // generated value behind it, or park the export for an administrator's decision. It reads and writes, so
                // it can fail, and runs only once the batch's other results are recorded (#2197).
                if (exportResult.ErrorType == ConnectedSystemExportErrorType.UniqueValueAlreadyInUse && collisionRemediation != null)
                {
                    collisionCandidates.Add((export, exportResult, writtenChanges));
                    continue;
                }

                RecordFailedAttempt(export, exportResult, writtenChanges, result);
                exportsToUpdate.Add(export);
                continue;
            }

            // A Delete that just succeeded against a CSO whose provisioning was
            // never confirmed by an import is the terminal step in that object's life; see
            // IsUnconfirmedProvisioningDeleteSuccess for the full rationale.
            var isUnconfirmedProvisioningDelete = IsUnconfirmedProvisioningDeleteSuccess(export);

            // Capture export data for activity tracking (before deletion)
            result.ProcessedExportItems.Add(new ProcessedExportItem
            {
                ChangeType = export.ChangeType,
                ConnectedSystemObject = export.ConnectedSystemObject,
                PendingExportId = export.Id,
                AttributeChangeCount = writtenChanges.Count,
                AttributeValueChanges = writtenChanges,
                Succeeded = true,
                UnresolvedReferenceMessage = unresolvedReferenceNotes != null && unresolvedReferenceNotes.TryGetValue(export.Id, out var note) ? note : null,
                ConnectedSystemObjectRemoved = isUnconfirmedProvisioningDelete
            }.WithCauseFrom(export));

            if (isUnconfirmedProvisioningDelete)
            {
                // Remove the CSO and this Pending Export now, rather than leaving the export Exported:
                // not added to exportsToUpdate because the row is about to be deleted, not updated.
                // Removal itself is batched after the loop via RemoveUnconfirmedProvisioningCsosAsync.
                unconfirmedProvisioningCsoDeletes.Add(export);
                result.SuccessCount++;
                // Issue #1079: Delete exports are always skipped by optimistic apply (D6); this one
                // never reaches the shared skip-counting below because it continues past it.
                result.OptimisticApplySkippedCount++;
                Log.Debug("ProcessBatchSuccessAsync: Export {ExportId} deleted CSO {CsoId} whose provisioning was never confirmed by an import; " +
                    "the Connected System Object will be removed", export.Id, export.ConnectedSystemObjectId);
                continue;
            }

            if (stillUnresolvedCount > 0)
            {
                // Written in part (issue #1398): everything that could go has gone, and the export stays
                // pending for the references it still owes, attempted again at the next export run. The row now
                // exists, so from here on it is an Update: sending it as a Create again would insert a second row.
                export.Status = PendingExportStatus.Pending;
                export.HasUnresolvedReferences = true;
                export.LastAttemptedAt = DateTime.UtcNow;
                export.ClearFailure();
                if (wasCreate)
                    export.ChangeType = PendingExportChangeType.Update;
                result.PartiallyExportedCount++;
                Log.Information("ProcessBatchSuccessAsync: Exported {ExportId} for CSO {CsoId} in part: {Written} change(s) written, " +
                    "{Unresolved} reference change(s) still awaiting resolution; attempted again at the next export run",
                    export.Id, export.ConnectedSystemObjectId, writtenChanges.Count, stillUnresolvedCount);
            }
            else
            {
                export.Status = PendingExportStatus.Exported;
                export.ClearFailure();
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

            // Which of those the Connector wrote with an interim value until an Initial Password is delivered (#2039),
            // persisted with the statuses below so the confirming import holds rather than retries them.
            MarkChangesAwaitingInitialPassword(export, writtenChanges, exportResult, connectedSystemName);

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

        // The changes never started are put back first, so nothing that fails while the rest are persisted can count an
        // attempt against them (#2083).
        if (notAttempted.Count > 0)
        {
            var notAttemptedStates = notAttempted.ToDictionary(pe => pe.Id, pe => executingMark.StatesBefore[pe.Id]);
            var released = await repository.ReleaseExecutingPendingExportsAsync(notAttemptedStates.Values.ToList(), executingMark.MarkedAt);
            foreach (var export in notAttempted.Where(pe => released.Contains(pe.Id)))
                notAttemptedStates[export.Id].ApplyTo(export);
            Log.Information("ProcessBatchSuccessAsync: {Count} change(s) to {ConnectedSystemName} were not attempted because the run was cancelled; " +
                "they are put back as they were, for the next run", released.Count, connectedSystemName);
        }

        if (notAttemptedWithoutCancellation > 0)
        {
            Log.Error("ProcessBatchSuccessAsync: The Connector for {ConnectedSystemName} reported {Count} change(s) as not attempted although the run was not cancelled; " +
                "each is counted as a failed attempt", connectedSystemName, notAttemptedWithoutCancellation);
        }

        // A Create whose rule asks for an Initial Password stays Executing until that password is queued (#2270). A
        // password change waiting on the account waits only while its Create is in flight (#2160); written now, the Create
        // would let one be delivered and removed before staging below looks for it, and the Initial Password queued in its
        // place would overwrite the person's own password.
        var heldUntilStaged = initialPasswordRuleIds is { Count: > 0 }
            ? provisionedAccounts.Where(pe => initialPasswordRuleIds.Contains(pe.ProvisioningSyncRuleId!.Value)).ToHashSet()
            : [];
        var writtenNow = heldUntilStaged.Count > 0
            ? exportsToUpdate.Where(pe => !heldUntilStaged.Contains(pe)).ToList()
            : exportsToUpdate;

        // The batch's results are recorded first, before anything that processes them further can fail (#2197), and a
        // Create's result together with the External ID it assigned: the status alone would leave an object JIM cannot
        // address, and losing both would have a Create already written sent again. A failure here records nothing, and
        // the batch is reported failed as a whole.
        await using (var transaction = await repository.BeginTransactionAsync())
        {
            if (csosToUpdate.Count > 0)
                await BatchUpdateCsosAfterSuccessfulExportAsync(csosToUpdate, repository);
            await WritePendingExportsAsync(writtenNow, repository);
            if (transaction != null)
                await transaction.CommitAsync();
        }

        try
        {
            // Each value already in use is decided, and recorded, on its own, so one that cannot be decided fails alone.
            foreach (var (export, rejection, writtenChanges) in collisionCandidates)
            {
                var collision = await collisionRemediation!.HandleAsync(export, rejection, repository,
                    () => derivedInputMarks ??= new DerivedInputMarkBatch(collisionRemediation.DerivedFlowGraph, "Collision Remediation"));
                if (collision != null)
                    RecordCollision(export, rejection, writtenChanges, collision, result);
                else
                    RecordFailedAttempt(export, rejection, writtenChanges, result);
                await WritePendingExportsAsync([export], repository);
            }

            // An "Allow the rename" answers one rejection; an export that has now succeeded for that account makes it moot.
            await ClearRenameAuthorisationsAfterSuccessfulExportAsync(successfulNonDeleteExports, repository);

            // Remove CSOs (and their now-superfluous Pending Exports) whose Delete just
            // confirmed provisioning that was never otherwise confirmed.
            if (unconfirmedProvisioningCsoDeletes.Count > 0)
            {
                await RemoveUnconfirmedProvisioningCsosAsync(unconfirmedProvisioningCsoDeletes, repository);
            }

            // #1121: runs AFTER BatchUpdateCsosAfterSuccessfulExportAsync (with the batch's results, above), because the delivery pass finds the
            // account in the Connected System by the external ID that call has just assigned; staging first would
            // record work against an account JIM could not yet address.
            if (provisionedAccounts.Count > 0)
            {
                await StageInitialPasswordsForBatchAsync(provisionedAccounts, initialPasswordRuleIds, accountsWithEnabledStateWritten,
                    result, repository, initialPasswordTimeToLive, connectedSystemName);
            }

            // Only now, with their Initial Passwords queued, do the held Creates stop counting as in flight (#2270).
            await WritePendingExportsAsync(heldUntilStaged.ToList(), repository);
            heldUntilStaged.Clear();

            // Issue #1079: optimistic export apply. Runs LAST, after BatchUpdateCsosAfterSuccessfulExportAsync,
            // so its external-Id additions are already reflected in each CSO's in-memory AttributeValues
            // (D9's dedupe guarantee depends on this ordering; see D11).
            if (successfulNonDeleteExports.Count > 0)
            {
                await ApplyOptimisticExportUpdatesAsync(successfulNonDeleteExports, result, repository);
            }

            // Collision Remediation revised Metaverse values outside any synchronisation: mark the hosting systems of every
            // derived flow reading them, once for the whole batch, so dependants (an email, a User Principal Name) re-derive
            // at their next synchronisation (#1750 Phase 4).
            if (derivedInputMarks != null)
            {
                await derivedInputMarks.FlushAsync(repository);
                derivedInputMarks.LogSummary();
            }
        }
        catch (Exception) when (heldUntilStaged.Count > 0)
        {
            // Their success is recorded, so whatever failed after it they must still leave Executing: one left there would
            // be put back and sent again, creating the account twice (#2083). Written here rather than in a finally so a
            // failure of this write is logged without hiding the one that brought the batch down.
            try
            {
                await WritePendingExportsAsync(heldUntilStaged.ToList(), repository);
            }
            catch (Exception writeException) when (writeException is not OperationCanceledException)
            {
                Log.Error(writeException, "ProcessBatchSuccessAsync: Could not record {Count} successful Create(s) to {ConnectedSystemName} after the batch failed; " +
                    "the batch's failure is recorded against them instead, as when any batch's results cannot be written", heldUntilStaged.Count, connectedSystemName);
            }

            throw;
        }
    }

    /// <summary>
    /// Takes the containers <paramref name="connector"/> created, before its connection closes and forgets them (#2197):
    /// onto the run's result, and to the caller, which selects them even when the run does not complete. A container
    /// left unselected leaves the accounts created in it unconfirmed, and later exports there refused as outside the
    /// managed scope.
    /// </summary>
    private void CaptureCreatedContainers(object connector, ExportExecutionResult result, object? resultLock)
    {
        if (connector is not IConnectorContainerCreation containerCreator || containerCreator.CreatedContainerExternalIds.Count == 0)
            return;

        var created = containerCreator.CreatedContainerExternalIds.ToList();
        if (resultLock == null)
            result.CreatedContainerExternalIds.AddRange(created);
        else
            lock (resultLock)
                result.CreatedContainerExternalIds.AddRange(created);

        _containersCreated?.Invoke(created);
        Log.Information("CaptureCreatedContainers: Captured {Count} created container(s) for auto-selection", created.Count);
    }

    /// <summary>
    /// Records an export the Connector refused, or whose result could not be had, as a failed attempt with an execution
    /// item. The caller persists it.
    /// </summary>
    private static void RecordFailedAttempt(PendingExport export, ConnectedSystemExportResult exportResult,
        List<PendingExportAttributeValueChange> writtenChanges, ExportExecutionResult result)
    {
        MarkExportFailed(export, exportResult.ErrorMessage ?? "Export failed");
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
            ErrorType = exportResult.ErrorType,
            RejectedAttributeName = exportResult.RejectedAttributeName
        }.WithCauseFrom(export));
    }

    /// <summary>
    /// Records an export rejected for a value already in use that Collision Remediation decided (#242, release 4): not a
    /// failed attempt, so its error count is not consumed. The caller persists it.
    /// </summary>
    private static void RecordCollision(PendingExport export, ConnectedSystemExportResult rejection,
        List<PendingExportAttributeValueChange> writtenChanges, GeneratedValueCollisionOutcome collision, ExportExecutionResult result)
    {
        if (collision.Handling == GeneratedValueCollisionHandling.Remediated)
        {
            result.DeferredCount++;
            result.GeneratedValuesRemediatedCount++;
        }
        else
        {
            result.FailedCount++;
            result.GeneratedValuesNeedingDecisionCount++;
        }

        var collisionChanges = CollisionAttributeChanges(collision) ?? writtenChanges;
        result.ProcessedExportItems.Add(new ProcessedExportItem
        {
            ChangeType = export.ChangeType,
            ConnectedSystemObject = export.ConnectedSystemObject,
            PendingExportId = export.Id,
            AttributeChangeCount = collisionChanges.Count,
            AttributeValueChanges = collisionChanges,
            Succeeded = false,
            ErrorMessage = collision.Handling == GeneratedValueCollisionHandling.NeedsDecision ? collision.Message : null,
            ErrorCount = export.ErrorCount,
            ErrorType = rejection.ErrorType,
            RejectedAttributeName = rejection.RejectedAttributeName,
            GeneratedValueCollision = collision,
            ExecutionItemId = collision.ExecutionItemId
        }.WithCauseFrom(export));
    }

    private static async Task WritePendingExportsAsync(List<PendingExport> exports, ISyncRepository repository)
    {
        if (exports.Count == 0)
            return;

        using (Diagnostics.Diagnostics.Database.StartSpan("UpdatePendingExports")
            .SetTag("count", exports.Count))
        {
            await repository.UpdatePendingExportsAsync(exports);
        }
    }

    /// <summary>
    /// Clears every "Allow the rename" authorisation the batch's successful exports have made moot (Unique Value
    /// Generation, #242, release 4). An allowance is given for one value held after one Connected System rejected it, and
    /// releases that export; if the export then succeeds (the clash was fixed some other way), nothing else would ever
    /// spend the allowance, and a later, unrelated rejection would rename a live account without asking again. A
    /// Connected System Object has at most one Pending Export, so a successful export for the account is necessarily the
    /// released one, carrying the value or the values derived from it; matching on the account rather than on the value
    /// also covers a rejection of a derived value (a User Principal Name built from the generated Account Name). One
    /// set-based statement per batch, through the batch's own repository so the parallel path is safe.
    /// </summary>
    private static async Task ClearRenameAuthorisationsAfterSuccessfulExportAsync(List<PendingExport> successfulNonDeleteExports, ISyncRepository repository)
    {
        if (successfulNonDeleteExports.Count == 0)
            return;

        var connectedSystemId = successfulNonDeleteExports[0].ConnectedSystemId;
        var connectedSystemObjectIds = successfulNonDeleteExports
            .Where(e => e.ConnectedSystemObjectId.HasValue)
            .Select(e => e.ConnectedSystemObjectId!.Value)
            .ToHashSet();
        var metaverseObjectIds = successfulNonDeleteExports
            .Select(e => e.ConnectedSystemObject?.MetaverseObjectId ?? e.SourceMetaverseObjectId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        var cleared = await repository.ClearRenameAuthorisationsAfterSuccessfulExportAsync(connectedSystemId, metaverseObjectIds, connectedSystemObjectIds);
        if (cleared > 0)
            Log.Information("ProcessBatchSuccessAsync: {Count} rename allowance(s) for Connected System {ConnectedSystemId} cleared: the export they answered has now succeeded",
                cleared, connectedSystemId);
    }

    /// <summary>
    /// The attribute rows a Collision Remediation's export item records: the carrying attribute set from the rejected
    /// value to the corrected one. Null (record the export's own changes) when a decision is needed, or when the export
    /// carried only a value derived from the generated one.
    /// </summary>
    private static List<PendingExportAttributeValueChange>? CollisionAttributeChanges(GeneratedValueCollisionOutcome collision)
    {
        if (collision.Handling != GeneratedValueCollisionHandling.Remediated || collision.CarryingAttribute == null)
            return null;

        var attribute = collision.CarryingAttribute;
        return
        [
            new PendingExportAttributeValueChange { Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, StringValue = collision.RejectedValue, ChangeType = PendingExportAttributeChangeType.Remove },
            new PendingExportAttributeValueChange { Id = Guid.NewGuid(), Attribute = attribute, AttributeId = attribute.Id, StringValue = collision.NewValue, ChangeType = PendingExportAttributeChangeType.Update }
        ];
    }

    /// <summary>
    /// Works out, once for the batch about to be sent, which of its exports a password will follow, and says so on each
    /// of them (<see cref="PendingExport.InitialPasswordFollows"/>), so a Connector whose target refuses to enable an
    /// account that has no password can hold the enable back for the delivery to complete. Returns the batch's
    /// provisioning Synchronisation Rules that ask for an Initial Password, for <see cref="StageInitialPasswordsForBatchAsync"/>
    /// to stage the passwords from the same answer: the lookup moved ahead of the Connector call rather than being repeated.
    /// <list type="bullet">
    /// <item>A Create is flagged when its provisioning Synchronisation Rule asks for an Initial Password (#2039): the
    /// account is created disabled and enabled once the password is set.</item>
    /// <item>An Update is flagged when its account's person is still owed a password on this Connected System (#2158),
    /// whatever its origin: queued, retrying or parked, the confirming import's own hold test
    /// (<see cref="ISyncRepository.GetMetaverseObjectIdsOwedPasswordAsync"/>), so the flag and the hold never disagree.
    /// A Connector then holds an enable its target refuses for want of a password, and the delivery completes it.</item>
    /// <item>Every other export is not flagged.</item>
    /// </list>
    /// <para>
    /// Every export in the batch is decided, the ones no password follows included, so the flag always reflects this
    /// batch's answer. Each lookup is made at most once per batch, and only when it can matter: the rule lookup when the
    /// batch holds a Create from a provisioning rule, and the owed lookup when it holds an Update to an account joined to a
    /// Metaverse Object and the Connector can set passwords (one that cannot has no password queued to complete anything).
    /// </para>
    /// <para>
    /// A failed lookup is contained, as it was when staging made it. A failed rule lookup flags no Create, so each is sent
    /// as its Attribute Flows say, and null is returned so staging reports every account it creates as owed a password
    /// nobody recorded. A failed owed lookup flags no Update, so a refused enable is reported on the export as it would be
    /// without the flag. Failing the batch instead would stop an export over a question about passwords.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlySet<int>?> FlagExportsFollowedByAPasswordAsync(
        List<PendingExport> batch,
        ISyncRepository repository,
        ConnectedSystem connectedSystem,
        bool connectorSetsPasswords)
    {
        var provisioningRuleIds = batch
            .Where(IsProvisioningCreate)
            .Select(pe => pe.ProvisioningSyncRuleId!.Value)
            .Distinct()
            .ToList();

        var rulesAskingForAPassword = provisioningRuleIds.Count == 0
            ? new HashSet<int>()
            : await TryReadRulesAskingForAPasswordAsync(provisioningRuleIds, repository, connectedSystem.Name);

        var updatedPeople = connectorSetsPasswords
            ? batch
                .Where(pe => pe.ChangeType == PendingExportChangeType.Update)
                .Select(pe => pe.ConnectedSystemObject?.MetaverseObjectId)
                .OfType<Guid>()
                .Distinct()
                .ToList()
            : [];

        var peopleOwedAPassword = updatedPeople.Count == 0
            ? new HashSet<Guid>()
            : await TryReadPeopleOwedAPasswordAsync(updatedPeople, repository, connectedSystem);

        foreach (var pendingExport in batch)
        {
            pendingExport.InitialPasswordFollows = pendingExport.ChangeType switch
            {
                PendingExportChangeType.Create => rulesAskingForAPassword != null
                    && pendingExport.ProvisioningSyncRuleId is { } provisioningRuleId
                    && rulesAskingForAPassword.Contains(provisioningRuleId),
                PendingExportChangeType.Update => peopleOwedAPassword != null
                    && pendingExport.ConnectedSystemObject?.MetaverseObjectId is { } metaverseObjectId
                    && peopleOwedAPassword.Contains(metaverseObjectId),
                _ => false
            };
        }

        return rulesAskingForAPassword;
    }

    /// <summary>
    /// Which of the given Synchronisation Rules ask for an Initial Password, or null when that could not be read; see
    /// <see cref="FlagExportsFollowedByAPasswordAsync"/> for why a failure is contained.
    /// </summary>
    private static async Task<HashSet<int>?> TryReadRulesAskingForAPasswordAsync(
        List<int> provisioningRuleIds,
        ISyncRepository repository,
        string connectedSystemName)
    {
        try
        {
            return await repository.GetSyncRuleIdsWithInitialPasswordEnabledAsync(provisioningRuleIds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "FlagExportsFollowedByAPasswordAsync: Could not read which Synchronisation Rules ask for an Initial Password " +
                "before sending a batch to {ConnectedSystem}. Its Creates are sent as their Attribute Flows say, and any account they create " +
                "is reported as owed a password nobody recorded", LogSanitiser.Sanitise(connectedSystemName));
            return null;
        }
    }

    /// <summary>
    /// Which of the given Metaverse Objects are still owed a password on the Connected System, or null when that could
    /// not be read; see <see cref="FlagExportsFollowedByAPasswordAsync"/> for why a failure is contained.
    /// </summary>
    private static async Task<HashSet<Guid>?> TryReadPeopleOwedAPasswordAsync(
        List<Guid> metaverseObjectIds,
        ISyncRepository repository,
        ConnectedSystem connectedSystem)
    {
        try
        {
            return await repository.GetMetaverseObjectIdsOwedPasswordAsync(connectedSystem.Id, metaverseObjectIds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "FlagExportsFollowedByAPasswordAsync: Could not read which accounts are still owed a password before sending a batch " +
                "to {ConnectedSystem}. Its Updates are sent as their Attribute Flows say, so one enabling an account the target will not enable " +
                "without a password is reported on its export rather than held until the password is delivered",
                LogSanitiser.Sanitise(connectedSystem.Name));
            return null;
        }
    }

    /// <summary>
    /// A Create recording the Synchronisation Rule whose provisioning decision produced it: the only export an
    /// Initial Password can follow.
    /// </summary>
    private static bool IsProvisioningCreate(PendingExport pendingExport) =>
        pendingExport.ChangeType == PendingExportChangeType.Create && pendingExport.ProvisioningSyncRuleId.HasValue;

    /// <summary>
    /// Marks each written change with whether the Connector reports it wrote that attribute with an interim value until
    /// an Initial Password is delivered (<see cref="ConnectedSystemExportResult.AttributesAwaitingInitialPassword"/>,
    /// #2039), matching attribute names case-insensitively as directories do. The mark is what makes the confirming
    /// import hold such a change while the password is owed, rather than retrying a value the target refuses until the
    /// password arrives. Every written change is decided, the unreported ones cleared: the mark describes this write,
    /// and a change marked on an earlier attempt and sent again must not keep a hold this write did not ask for.
    /// <para>
    /// A Connector may only report this for an export JIM said a password follows
    /// (<see cref="PendingExport.InitialPasswordFollows"/>): a Create whose provisioning rule asks for an Initial
    /// Password, or an Update to an account still owed a password (#2158). A report on any other export breaks that
    /// contract: it is logged as an error and nothing is marked, so the change is reconciled as an ordinary one and any
    /// mismatch is reported, instead of being held for a password nobody will deliver.
    /// </para>
    /// </summary>
    private static void MarkChangesAwaitingInitialPassword(
        PendingExport export,
        List<PendingExportAttributeValueChange> writtenChanges,
        ConnectedSystemExportResult exportResult,
        string connectedSystemName)
    {
        var reported = exportResult.AttributesAwaitingInitialPassword ?? [];
        if (reported.Count > 0 && !export.InitialPasswordFollows)
        {
            Log.Error("ProcessBatchSuccessAsync: The Connector for {ConnectedSystem} reported attribute(s) [{Attributes}] written with an interim value " +
                "on Pending Export {ExportId}, but JIM did not say a password follows it. The changes are not marked, and are reconciled " +
                "as ordinary changes; this is a defect in the Connector",
                LogSanitiser.Sanitise(connectedSystemName), LogSanitiser.Sanitise(string.Join(", ", reported)), export.Id);
        }

        var reportedNames = export.InitialPasswordFollows
            ? new HashSet<string>(reported, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in writtenChanges)
            change.AwaitsInitialPassword = change.Attribute?.Name is { } name && reportedNames.Contains(name);

        if (reportedNames.Count == 0)
            return;

        var unmatched = reportedNames
            .Where(name => !writtenChanges.Any(change => string.Equals(change.Attribute?.Name, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (unmatched.Count > 0)
        {
            Log.Warning("ProcessBatchSuccessAsync: The Connector for {ConnectedSystem} reported attribute(s) [{Attributes}] written with an interim value " +
                "on Pending Export {ExportId}, but no change written for it names them; nothing was marked for those",
                LogSanitiser.Sanitise(connectedSystemName), LogSanitiser.Sanitise(string.Join(", ", unmatched)), export.Id);
        }

        Log.Debug("ProcessBatchSuccessAsync: {Count} change(s) on Pending Export {ExportId} for CSO {CsoId} written with an interim value " +
            "until an Initial Password is delivered", writtenChanges.Count(change => change.AwaitsInitialPassword), export.Id, export.ConnectedSystemObjectId);
    }

    /// <summary>
    /// Records that this batch's newly provisioned accounts are owed a first password (issue #1121, #1697): one
    /// row on the queued password pipeline per account, delivered by the Password Delivery Service rather than
    /// here.
    /// <para>
    /// Staged, not delivered. Setting a password is a round trip to the Connected System, and doing it here
    /// would put a second network call inside the loop that is persisting the results of one that has already
    /// succeeded: slow at scale, and structurally able to take a successful export down with it. A later pass
    /// opens one password connection per Connected System and works through what is outstanding.
    /// </para>
    /// <para>
    /// Which rules ask for a password is read for the batch just before it is sent (see
    /// <see cref="FlagExportsFollowedByAPasswordAsync"/>), rather than stamped onto the export when it was
    /// staged, so that switching the feature on reaches work already queued, and so that a deployment not using it
    /// writes no rows at all. Staging reuses that one lookup, so the accounts given a password are exactly the ones the
    /// Connector was told one would follow (#2039). A live user-set or propagated password change that will reach the
    /// account is delivered as its first password instead, carrying the Initial Password's promises about the account
    /// (#2160); the queue's own staging statement decides that, not this method, and every outcome is recorded on an
    /// Activity.
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
        IReadOnlySet<int>? rulesAskingForAPassword,
        IReadOnlySet<Guid> accountsWithEnabledStateWritten,
        ExportExecutionResult result,
        ISyncRepository repository,
        TimeSpan initialPasswordTimeToLive,
        string connectedSystemName)
    {
        using var span = Diagnostics.Diagnostics.Database.StartSpan("StageInitialPasswords")
            .SetTag("count", provisionedAccounts.Count);

        // The batch's lookup failed before it was sent, so JIM cannot tell which of these accounts needed recording:
        // every one is reported as unrecorded, exactly as when this method made the lookup itself and it failed.
        if (rulesAskingForAPassword == null)
        {
            result.InitialPasswordStagingFailedCount += provisionedAccounts.Count;
            Log.Error("StageInitialPasswordsForBatchAsync: Could not record whether {Count} newly provisioned accounts on Connected System {ConnectedSystemId} " +
                "are owed an initial password, because which Synchronisation Rules ask for one could not be read. The accounts were created " +
                "successfully, and exporting again will not queue a password; set one on each that needs it with the set-password action. " +
                "Connected System Objects: {ConnectedSystemObjectIds}",
                provisionedAccounts.Count, provisionedAccounts[0].ConnectedSystemId,
                DescribeConnectedSystemObjectIds(provisionedAccounts.Select(pe => pe.ConnectedSystemObject!.Id)));
            return;
        }

        if (rulesAskingForAPassword.Count == 0)
            return;

        // Declared out here so the failure count below is the number of accounts genuinely owed a password,
        // once that is known, and so a failure after the rows were written is not reported as passwords never queued.
        List<PendingPasswordChange>? staging = null;
        List<ProvisionedPasswordStagingOutcome>? outcomes = null;
        try
        {
            var eligible = provisionedAccounts
                .Where(pe => rulesAskingForAPassword.Contains(pe.ProvisioningSyncRuleId!.Value))
                .ToList();

            if (eligible.Count == 0)
                return;

            var now = DateTime.UtcNow;
            staging = new List<PendingPasswordChange>(eligible.Count);
            var unaddressableCount = 0;

            foreach (var pe in eligible)
            {
                var metaverseObjectId = pe.ConnectedSystemObject!.MetaverseObjectId;
                if (metaverseObjectId == null)
                {
                    // The queue's coalescing key is (Metaverse Object, Connected System); an account with no
                    // Metaverse Object could never be found again to deliver, supersede or expire.
                    unaddressableCount++;
                    Log.Error("StageInitialPasswordsForBatchAsync: Connected System Object {CsoId} on Connected System {ConnectedSystemId} " +
                        "was provisioned but has no Metaverse Object; its initial password cannot be queued",
                        pe.ConnectedSystemObject!.Id, pe.ConnectedSystemId);
                    continue;
                }

                staging.Add(new PendingPasswordChange
                {
                    Id = Guid.NewGuid(),
                    MetaverseObjectId = metaverseObjectId.Value,
                    ConnectedSystemId = pe.ConnectedSystemId,
                    ConnectedSystemObjectId = pe.ConnectedSystemObject!.Id,
                    SyncRuleId = pe.ProvisioningSyncRuleId!.Value,
                    Origin = PendingPasswordChangeOrigin.Provisioned,
                    EncryptedPassword = null,
                    // ExpiryBehaviour is left at its default: a placeholder, since a provisioned row generates
                    // its password at delivery time from the rule's own settings, which are read fresh then.
                    Status = PendingPasswordChangeStatus.Pending,
                    CreatedAt = now,
                    ExpiresAt = now.Add(initialPasswordTimeToLive),
                    ActivityId = Guid.NewGuid(),
                    // #2064: the Create wrote the account's enabled state, so an Attribute Flow decides it and the
                    // delivery leaves it alone (a pre-hire the flow creates disabled stays disabled).
                    EnabledStateSetByAttributeFlow = accountsWithEnabledStateWritten.Contains(pe.ConnectedSystemObject!.Id),
                    // #2160: the account this row owes its first password, which travels with the obligation should a
                    // waiting password change be delivered in its place.
                    ProvisionedConnectedSystemObjectId = pe.ConnectedSystemObject!.Id
                });
            }

            result.InitialPasswordStagingFailedCount += unaddressableCount;

            if (staging.Count == 0)
                return;

            // Looked up by the id each change was offered under: a Superseded outcome mutates the change's own
            // Id to the row it took over (#1697), so RequestedId is the only key still pointing back here.
            var byRequestedId = staging.ToDictionary(c => c.Id);

            outcomes = await repository.StageProvisionedPasswordChangesAsync(staging);

            var activities = new List<Activity>(outcomes.Count);
            var stagedCount = 0;
            var carriedCount = 0;
            foreach (var outcome in outcomes)
            {
                var change = byRequestedId[outcome.RequestedId];

                // System-initiated and already complete, mirroring what PasswordSynchronisationServer.SetPasswordAsync
                // writes for an administrator's own set: nobody is waiting at a screen for this to finish, because
                // delivery happens on a later, unrelated pass. Every outcome gets one (#2160), so an Initial Password
                // that was not queued is never left unexplained.
                var activity = new Activity
                {
                    Id = outcome.Disposition is ProvisionedPasswordStagingDisposition.Inserted or ProvisionedPasswordStagingDisposition.Superseded
                        ? change.ActivityId
                        : Guid.NewGuid(),
                    TargetType = ActivityTargetType.PasswordSynchronisation,
                    TargetOperationType = ActivityTargetOperationType.SetPassword,
                    TargetContext = nameof(PendingPasswordChangeOrigin.Provisioned),
                    TargetName = connectedSystemName,
                    MetaverseObjectId = change.MetaverseObjectId,
                    ConnectedSystemId = change.ConnectedSystemId,
                    ConnectedSystemObjectId = change.ProvisionedConnectedSystemObjectId,
                    InitiatedByType = ActivityInitiatorType.System,
                    InitiatedByName = "System",
                    Status = ActivityStatus.Complete,
                    Created = now,
                    Executed = now,
                    ExecutionTime = TimeSpan.Zero,
                    TotalActivityTime = TimeSpan.Zero
                };
                activities.Add(activity);

                switch (outcome.Disposition)
                {
                    case ProvisionedPasswordStagingDisposition.Coalesced:
                        carriedCount++;
                        activity.Message = InitialPasswordStagingMessages.Carried(connectedSystemName, outcome);
                        break;

                    default:
                        stagedCount++;
                        var replaced = InitialPasswordStagingMessages.ReplacedWaitingChange(connectedSystemName, outcome);
                        activity.Message = replaced == null
                            ? $"Initial password queued for delivery to {connectedSystemName}."
                            : $"Initial password queued for delivery to {connectedSystemName}, in place of a password change for this person " +
                              $"that was waiting there: {replaced}";
                        if (replaced == null)
                            break;

                        // The person's own change lost this system: told so beneath its own record, where its other
                        // systems' outcomes are, rather than vanishing from it.
                        activity.Status = ActivityStatus.CompleteWithWarning;
                        if (outcome.ExistingActivityId is { } replacedChangeActivityId)
                        {
                            var notSet = $"Password not set on {connectedSystemName}: replaced by the Initial Password of a Connected System Object " +
                                         $"JIM has just provisioned there, because this change {replaced}";
                            activities.Add(new Activity
                            {
                                Id = Guid.NewGuid(),
                                ParentActivityId = replacedChangeActivityId,
                                TargetType = ActivityTargetType.PasswordSynchronisation,
                                TargetOperationType = ActivityTargetOperationType.SetPassword,
                                TargetContext = connectedSystemName,
                                TargetName = connectedSystemName,
                                MetaverseObjectId = change.MetaverseObjectId,
                                ConnectedSystemId = change.ConnectedSystemId,
                                ConnectedSystemObjectId = change.ProvisionedConnectedSystemObjectId,
                                InitiatedByType = ActivityInitiatorType.System,
                                InitiatedByName = "System",
                                Status = ActivityStatus.CompleteWithError,
                                Message = notSet,
                                ErrorMessage = notSet,
                                Created = now,
                                Executed = now,
                                ExecutionTime = TimeSpan.Zero,
                                TotalActivityTime = TimeSpan.Zero
                            });
                        }

                        break;
                }
            }

            result.InitialPasswordsCarriedCount += carriedCount;
            if (activities.Count > 0)
                await repository.CreateActivitiesAsync(activities);

            result.InitialPasswordsStagedCount += stagedCount;
            Log.Debug("StageInitialPasswordsForBatchAsync: Staged initial passwords for {Count} newly provisioned accounts on Connected System {ConnectedSystemId}, " +
                "and gave {Carried} a password change already waiting for them as their first password",
                stagedCount, provisionedAccounts[0].ConnectedSystemId, carriedCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (outcomes != null)
            {
                // The passwords are queued and will be delivered; only the Activities recording that they were queued
                // could not be written, so nothing is owed that nobody knows about.
                Log.Warning(ex, "StageInitialPasswordsForBatchAsync: Queued initial passwords for {Count} newly provisioned accounts on Connected System {ConnectedSystemId}, " +
                    "but could not record their Activities", outcomes.Count, provisionedAccounts[0].ConnectedSystemId);
                return;
            }

            var unrecordedAccounts = staging?.Select(c => c.ConnectedSystemObjectId!.Value).ToList()
                                     ?? provisionedAccounts.Select(pe => pe.ConnectedSystemObject!.Id).ToList();
            result.InitialPasswordStagingFailedCount += unrecordedAccounts.Count;
            Log.Error(ex, "StageInitialPasswordsForBatchAsync: Could not record that {Count} newly provisioned accounts on Connected System {ConnectedSystemId} " +
                "are owed an initial password. The accounts were created successfully, and exporting again will not queue a password; set one on " +
                "each with the set-password action. Connected System Objects: {ConnectedSystemObjectIds}",
                unrecordedAccounts.Count, provisionedAccounts[0].ConnectedSystemId, DescribeConnectedSystemObjectIds(unrecordedAccounts));
        }
    }

    /// <summary>
    /// Marks the owed first passwords of accounts whose enabled state an export other than their Create has just
    /// written (#2064): a leaver disabled, or an account disabled on deprovisioning, before its Initial Password was
    /// delivered. Delivering that password then leaves the enabled state to the Attribute Flow, so it never re-enables
    /// the account.
    /// <para>
    /// Contained like staging: the directory has already changed and the exports are recorded as successful, so a
    /// failure here must not fail them. It is logged as an Error and counted rather than passing quietly, because until
    /// Enforce State corrects it, a delivery to one of these accounts would follow the Synchronisation Rule's Enable
    /// Account instead of the flow.
    /// </para>
    /// </summary>
    private static async Task MarkOwedInitialPasswordsAsSetByAttributeFlowAsync(
        int connectedSystemId,
        IReadOnlyCollection<Guid> connectedSystemObjectIds,
        ExportExecutionResult result,
        ISyncRepository repository)
    {
        try
        {
            var marked = await repository.MarkOwedInitialPasswordsAsSetByAttributeFlowAsync(connectedSystemId, connectedSystemObjectIds);
            if (marked > 0)
                Log.Debug("MarkOwedInitialPasswordsAsSetByAttributeFlowAsync: Marked {Marked} owed initial password(s) on Connected System {ConnectedSystemId} " +
                    "whose account's enabled state an export has now written; their delivery will leave it to the Attribute Flow",
                    marked, connectedSystemId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.InitialPasswordMarkingFailedCount += connectedSystemObjectIds.Count;
            Log.Error(ex, "MarkOwedInitialPasswordsAsSetByAttributeFlowAsync: Could not record that exports wrote the enabled state of {Count} account(s) " +
                "on Connected System {ConnectedSystemId}. The exports succeeded; an initial password still owed to any of them will follow its " +
                "Synchronisation Rule's setting rather than the Attribute Flow until the flow's value is reasserted. Connected System Objects: {ConnectedSystemObjectIds}",
                connectedSystemObjectIds.Count, connectedSystemId, DescribeConnectedSystemObjectIds(connectedSystemObjectIds));
        }
    }

    /// <summary>
    /// The Connected System Object ids a failure log names, so an administrator told "the service log names the
    /// accounts" can find them. Capped, because a batch can hold thousands; Guids carry nothing personal.
    /// </summary>
    private static string DescribeConnectedSystemObjectIds(IEnumerable<Guid> connectedSystemObjectIds)
    {
        const int shown = 100;
        var ids = connectedSystemObjectIds.ToList();
        var listed = string.Join(", ", ids.Take(shown));
        return ids.Count > shown ? $"{listed} and {ids.Count - shown:N0} more" : listed;
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
    /// <returns>
    /// The exports whose values were not all recorded: every one of them when recording failed, otherwise those carrying a
    /// change whose value is held in another data type's column than its attribute's (#2183). The calls path does not
    /// need the answer, because its confirming import self-heals; an auto-confirmed file export does (#1936), because
    /// nothing ever imports from its target.
    /// </returns>
    private async Task<List<PendingExport>> ApplyOptimisticExportUpdatesAsync(
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
            // #2039: nor was a change the Connector wrote with an interim value until an Initial
            // Password is delivered, so that is left out too (see AsWrittenForOptimisticApply).
            var delta = OptimisticExportApplyCalculator.CalculateDelta(
                successfulNonDeleteExports.Select(AsWrittenForOptimisticApply).ToList());

            if (delta.UnrecordedChanges.Count > 0)
                LogUnrecordedChanges(delta.UnrecordedChanges, successfulNonDeleteExports[0].ConnectedSystemId);

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

            var unrecordedExportIds = delta.UnrecordedChanges.Select(c => c.PendingExportId).ToHashSet();
            return successfulNonDeleteExports.Where(pe => unrecordedExportIds.Contains(pe.Id)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.OptimisticApplyFailedCount += successfulNonDeleteExports.Count;
            Log.Warning(ex, "ApplyOptimisticExportUpdatesAsync: Optimistic export apply failed for Connected System {ConnectedSystemId} " +
                "({Count} Pending Exports); a confirming import will self-heal, and auto-confirmed exports are left to be sent again",
                successfulNonDeleteExports[0].ConnectedSystemId, successfulNonDeleteExports.Count);
            return successfulNonDeleteExports;
        }
    }

    /// <summary>
    /// Logs, once per batch, the exported changes optimistic apply could not record (#2183): each was written with its
    /// value in another data type's column than its attribute's, which only a fault elsewhere produces, since export
    /// staging converts expression results to the attribute's type (#2179). A Warning, because the target now holds a
    /// value JIM has no record of; the Activity of an auto-confirmed export says so too, and names this log as where
    /// the cause is.
    /// </summary>
    private static void LogUnrecordedChanges(IReadOnlyCollection<OptimisticExportUnrecordedChange> unrecordedChanges, int connectedSystemId)
    {
        var attributes = string.Join(", ", unrecordedChanges
            .Select(c => $"{LogSanitiser.Sanitise(c.AttributeName)} ({c.AttributeType}, given {c.HeldDataType?.ToString() ?? "nothing"})")
            .Distinct());
        Log.Warning("ApplyOptimisticExportUpdatesAsync: {Count} exported changes to Connected System {ConnectedSystemId} could not be " +
            "recorded, because each gave its attribute a value of another data type (an export Attribute Flow expression " +
            "returning another type does this): {Attributes}. A confirming import records what the target holds; an " +
            "auto-confirmed export is left unconfirmed. Connected System Objects: {ConnectedSystemObjectIds}",
            unrecordedChanges.Count, connectedSystemId, attributes,
            DescribeConnectedSystemObjectIds(unrecordedChanges.Select(c => c.ConnectedSystemObjectId).Distinct()));
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

        // Apply changes to each CSO in-memory
        var csoUpdates = new List<(ConnectedSystemObject cso, List<ConnectedSystemObjectAttributeValue> newAttributeValues)>();

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

                needsUpdate = true;
                Log.Debug("BatchUpdateCsosAfterSuccessfulExportAsync: Set CSO {CsoId} external ID to {ExternalId} (type: {AttrType})",
                    cso.Id, LogSanitiser.Sanitise(exportResult.ExternalId), externalIdAttribute?.Type.ToString() ?? "Unknown");
            }

            // Update secondary external ID if provided
            if (!string.IsNullOrEmpty(exportResult.SecondaryExternalId) && cso.SecondaryExternalIdAttributeId.HasValue)
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
    }

    /// <summary>
    /// Records a failed attempt on an export (#2119): it stays Pending and is attempted again at the next export run.
    /// This is a synchronous version for batch processing - does not save to database.
    /// </summary>
    private static void MarkExportFailed(PendingExport export, string errorMessage, string? stackTrace = null)
    {
        var failed = PendingExportRetryState.Of(export).AfterFailedAttempt(errorMessage, stackTrace, DateTime.UtcNow);
        failed.ApplyTo(export);
        LogFailedAttempt(failed);
    }

    /// <summary>
    /// Logs a failed attempt once it has been applied.
    /// </summary>
    private static void LogFailedAttempt(PendingExportRetryState failed)
    {
        Log.Warning("MarkExportFailed: Export {ExportId} failed (attempt {Attempt}, failing since {FailingSince}); it is attempted again at the next export run. Error: {Error}",
            failed.PendingExportId, failed.ErrorCount, failed.FailingSince, LogSanitiser.Sanitise(failed.LastErrorMessage ?? string.Empty));
    }

    /// <summary>
    /// Issue #1936: an auto-confirmed file export whose values could not be recorded on its Connected System Object is
    /// not confirmed. It is marked <see cref="PendingExportStatus.ExportNotConfirmed"/>, with the changes it sent
    /// marked <see cref="PendingExportAttributeChangeStatus.ExportedNotConfirmed"/>, which is the state
    /// <c>IsReadyForExecution</c> treats as "send again" for a Create and an Update alike. The next export run then
    /// writes the same row again (the File Connector merges into the existing file, so a repeat is harmless) and
    /// retries the recording. Confirming it regardless would leave JIM's view of the target missing those values,
    /// with no import ever coming to repair it, so a value later cleared would never be cleared in the target. An
    /// export carrying a value held as another data type than its attribute's (#2183) is left the same way; its recording
    /// fails again on every retry until the Attribute Flow stages the attribute's own type, so the warning repeats on each
    /// run instead of the export being confirmed with nothing recorded.
    /// </summary>
    /// <param name="unrecordedExports">The exports whose values were not all recorded.</param>
    /// <param name="exportsToDelete">The auto-confirmed exports about to be deleted; those left unconfirmed are taken out.</param>
    /// <param name="result">Counts the exports left unconfirmed, for the Activity's warning.</param>
    private async Task LeaveUnrecordedExportsUnconfirmedAsync(
        List<PendingExport> unrecordedExports,
        List<PendingExport> exportsToDelete,
        ExportExecutionResult result)
    {
        var now = DateTime.UtcNow;
        foreach (var export in unrecordedExports)
        {
            foreach (var change in export.AttributeValueChanges.Where(c => c.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation))
                change.Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed;

            export.Status = PendingExportStatus.ExportNotConfirmed;
            export.LastAttemptedAt = now;
        }

        var unrecorded = unrecordedExports.ToHashSet();
        exportsToDelete.RemoveAll(unrecorded.Contains);

        using (Diagnostics.Diagnostics.Database.StartSpan("UpdateUnrecordedPendingExports")
            .SetTag("count", unrecordedExports.Count))
        {
            await SyncRepo.UpdatePendingExportsAsync(unrecordedExports);
        }

        result.UnrecordedExportCount += unrecordedExports.Count;
        Log.Warning("LeaveUnrecordedExportsUnconfirmedAsync: {Count} auto-confirmed exports to Connected System {ConnectedSystemId} were written, " +
            "but their values could not be recorded; they are left unconfirmed and will be sent again on the next export run",
            unrecordedExports.Count, unrecordedExports[0].ConnectedSystemId);
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

            // An export contradicting itself over a single-valued attribute is refused before the file is written, as
            // ExportBatchAsync refuses one on the call-based path (#2182): the File Connector applies changes in turn, so
            // an add followed by a removal would leave the column empty and be reported as a success.
            var refusals = RefuseSelfContradictingExports(pendingExports);
            List<ConnectedSystemExportResult> exportResults;
            if (refusals.Count == 0)
            {
                exportResults = await connector.ExportAsync(connectedSystem.SettingValues, pendingExports, cancellationToken, connectorProgress);
            }
            else
            {
                Log.Warning("ExecuteUsingFilesWithBatchingAsync: Refused {RefusedCount} of {ExportCount} export(s) on '{ConnectedSystem}' before writing them: " +
                    "each carries more than one change for a single-valued attribute. The reason is recorded against each object.",
                    refusals.Count, pendingExports.Count, connectedSystem.Name);
                result.SingleValuedContradictionRefusedCount += refusals.Count;

                List<PendingExport> sendable = [.. pendingExports.Where((_, index) => !refusals.ContainsKey(index))];
                var sentResults = sendable.Count > 0
                    ? await connector.ExportAsync(connectedSystem.SettingValues, sendable, cancellationToken, connectorProgress)
                    : [];
                // This path has no change put back as not attempted, so a missing result is always a failed attempt.
                exportResults = InBatchOrder(pendingExports.Count, refusals, sentResults, CancellationToken.None);
            }

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
            var unconfirmedProvisioningCsoDeletes = new List<PendingExport>();
            var successfulNonDeleteExports = new List<PendingExport>();

            for (var i = 0; i < pendingExports.Count; i++)
            {
                var export = pendingExports[i];
                var exportResult = i < exportResults.Count ? exportResults[i] : NoResultFromTheConnector(CancellationToken.None);

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
                        ErrorType = exportResult.ErrorType,
                        RejectedAttributeName = exportResult.RejectedAttributeName
                    }.WithCauseFrom(export));
                    continue;
                }

                // A Delete that just succeeded against a CSO whose provisioning was
                // never confirmed by an import is the terminal step in that object's life; see
                // IsUnconfirmedProvisioningDeleteSuccess for the full rationale. Issue #1936: so is any
                // Delete this run auto-confirms, whatever the CSO's status, because auto-confirm is the
                // confirmation; an object an import once confirmed (a system that ran Bidirectional before
                // Export Only) was otherwise left behind for ever, its row gone from the file.
                var isUnconfirmedProvisioningDelete = IsUnconfirmedProvisioningDeleteSuccess(export) ||
                    (autoConfirm && export.ChangeType == PendingExportChangeType.Delete && export.ConnectedSystemObject != null);

                // Capture export data for activity tracking (before deletion or status update)
                result.ProcessedExportItems.Add(new ProcessedExportItem
                {
                    ChangeType = export.ChangeType,
                    ConnectedSystemObject = export.ConnectedSystemObject,
                    AttributeChangeCount = export.AttributeValueChanges.Count,
                    AttributeValueChanges = export.AttributeValueChanges.ToList(),
                    Succeeded = true,
                    ConnectedSystemObjectRemoved = isUnconfirmedProvisioningDelete
                }.WithCauseFrom(export));

                if (isUnconfirmedProvisioningDelete)
                {
                    // Remove the CSO and this Pending Export now, rather than leaving the export Exported
                    // (auto-confirm) or auto-confirmed-delete (standard). Neither exportsToUpdate nor
                    // exportsToDelete gets this export: removal is batched after the loop via
                    // RemoveUnconfirmedProvisioningCsosAsync, which deletes the Pending Export by CSO id
                    // as part of removing the CSO itself.
                    unconfirmedProvisioningCsoDeletes.Add(export);
                    result.SuccessCount++;
                    Log.Debug("ExecuteUsingFilesWithBatchingAsync: Export {ExportId} deleted CSO {CsoId} whose provisioning was never confirmed by an import; " +
                        "the Connected System Object will be removed", export.Id, export.ConnectedSystemObjectId);
                    continue;
                }

                // For Create exports, update the CSO status from PendingProvisioning to Normal
                if (export.ChangeType == PendingExportChangeType.Create && export.ConnectedSystemObject != null)
                {
                    csosToUpdate.Add((export.ConnectedSystemObject, exportResult));
                }

                // Update attribute change statuses to ExportedPendingConfirmation
                UpdateAttributeChangeStatusesAfterExport(export);

                // Issue #1936: the values this export wrote are recorded on its CSO after the loop, as the calls
                // path does (#1079). Here it is not only an optimisation: a target that auto-confirms and is never
                // imported from (the File Connector's Export Only mode) has no other way to learn what it holds.
                // GetExecutableExportsAsync already loads each CSO's current values, because the File Connector
                // writes full rows from them, so recording costs no extra load.
                if (export.ChangeType == PendingExportChangeType.Delete || export.ConnectedSystemObject == null)
                    result.OptimisticApplySkippedCount++;
                else
                    successfulNonDeleteExports.Add(export);

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
                    export.ClearFailure();
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

            // Remove CSOs (and their now-superfluous Pending Exports) whose Delete
            // just confirmed provisioning that was never otherwise confirmed.
            if (unconfirmedProvisioningCsoDeletes.Count > 0)
            {
                await RemoveUnconfirmedProvisioningCsosAsync(unconfirmedProvisioningCsoDeletes, SyncRepo);
            }

            // Batch update CSOs that need external ID or status changes
            if (csosToUpdate.Count > 0)
            {
                await BatchUpdateCsosAfterSuccessfulExportAsync(csosToUpdate, SyncRepo);
            }

            // Issue #1936: record the exported values. LAST among the CSO writes, after
            // BatchUpdateCsosAfterSuccessfulExportAsync, so its external-Id additions are already reflected in each
            // CSO's AttributeValues (#1079's D9/D11 ordering), and BEFORE the auto-confirmed exports are deleted,
            // because confirming an export whose values were not recorded would leave nothing to repair it. That covers
            // a recording that failed outright and an export carrying a value it could not record (#2183).
            var unrecordedExports = successfulNonDeleteExports.Count == 0
                ? []
                : await ApplyOptimisticExportUpdatesAsync(successfulNonDeleteExports, result, SyncRepo);

            if (autoConfirm && unrecordedExports.Count > 0)
                await LeaveUnrecordedExportsUnconfirmedAsync(unrecordedExports, exportsToDelete, result);

            await ClearRenameAuthorisationsAfterSuccessfulExportAsync(successfulNonDeleteExports, SyncRepo);

            // Batch delete exports that are auto-confirmed
            if (exportsToDelete.Count > 0)
            {
                using (Diagnostics.Diagnostics.Database.StartSpan("DeletePendingExports")
                    .SetTag("count", exportsToDelete.Count))
                {
                    await SyncRepo.DeletePendingExportsAsync(exportsToDelete);
                }
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

            // Mark all as failed using batch update; each is attempted again at the next export run (#2119)
            var now = DateTime.UtcNow;
            foreach (var export in pendingExports)
            {
                PendingExportRetryState.Of(export).AfterFailedAttempt(ex.Message, ex.StackTrace, now).ApplyTo(export);
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
        FailingSince = pendingExport.FailingSince,
        LastAttemptedAt = pendingExport.LastAttemptedAt,
        LastErrorMessage = pendingExport.LastErrorMessage,
        LastErrorStackTrace = pendingExport.LastErrorStackTrace,
        SourceMetaverseObject = pendingExport.SourceMetaverseObject,
        SourceMetaverseObjectId = pendingExport.SourceMetaverseObjectId,
        HasUnresolvedReferences = pendingExport.HasUnresolvedReferences,
        ProvisioningSyncRule = pendingExport.ProvisioningSyncRule,
        ProvisioningSyncRuleId = pendingExport.ProvisioningSyncRuleId,
        InitialPasswordFollows = pendingExport.InitialPasswordFollows,
        CreatedAt = pendingExport.CreatedAt,
        AttributeValueChanges = changes
    };

    /// <summary>
    /// The export as optimistic apply should see it: only the changes whose asserted value the target was actually
    /// given (<see cref="IsWrittenAsAsserted"/>). Values sent on an earlier partial write and now awaiting confirmation
    /// are kept, since re-applying them is a no-op by construction (add-if-absent, set-if-different). Returns the
    /// export itself when every change qualifies, the ordinary case.
    /// </summary>
    private static PendingExport AsWrittenForOptimisticApply(PendingExport pendingExport) =>
        pendingExport.AttributeValueChanges.All(IsWrittenAsAsserted)
            ? pendingExport
            : WithChanges(pendingExport, pendingExport.AttributeValueChanges.Where(IsWrittenAsAsserted).ToList());

    /// <summary>
    /// Whether the target was given the value a change asserts, so optimistic apply may record it on the Connected System
    /// Object. Two kinds were not: a reference change still awaiting resolution (issue #1398), which was not written at
    /// all, and a change the Connector wrote with an interim value until an Initial Password is delivered
    /// (<see cref="PendingExportAttributeValueChange.AwaitsInitialPassword"/>, #2039). Recording the latter's asserted
    /// value would claim the target holds a value it does not, and a later export evaluation would then find it already
    /// current and withdraw the held change. Such an attribute records nothing until the confirming import reports what
    /// the target really holds.
    /// </summary>
    private static bool IsWrittenAsAsserted(PendingExportAttributeValueChange change) =>
        !IsUnresolvedReference(change) && !change.AwaitsInitialPassword;

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
    /// Marks an export as deferred (has unresolved references).
    /// Does not increment error count since this is expected behaviour.
    /// </summary>
    private async Task MarkExportDeferredAsync(PendingExport export)
    {
        export.Status = PendingExportStatus.Pending;
        export.HasUnresolvedReferences = true;
        export.LastAttemptedAt = DateTime.UtcNow;

        var unresolvedRefCount = export.AttributeValueChanges
            .Count(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue));
        var totalChanges = export.AttributeValueChanges.Count;

        await SyncRepo.UpdatePendingExportAsync(export);
        Log.Information("MarkExportDeferredAsync: Export {ExportId} for CSO {CsoId} deferred - " +
            "{UnresolvedCount}/{TotalChanges} attribute changes have unresolved references; attempted again at the next export run",
            export.Id, export.ConnectedSystemObjectId, unresolvedRefCount, totalChanges);
    }

    /// <summary>
    /// Recovers Pending Exports left stranded in Status Executing by a worker crash or restart mid-export.
    /// Called once at Worker startup, alongside the existing stale worker task recovery: at startup nothing
    /// can genuinely be exporting, so every Executing row is a leftover from before the crash/restart. See
    /// <see cref="ISyncRepository.RecoverStrandedExecutingPendingExportsAsync"/> for the full rationale.
    /// </summary>
    /// <returns>The number of Pending Exports recovered.</returns>
    public Task<int> RecoverStrandedExecutingPendingExportsAsync()
        => SyncRepo.RecoverStrandedExecutingPendingExportsAsync();
}

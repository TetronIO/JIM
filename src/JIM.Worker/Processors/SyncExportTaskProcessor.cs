// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Application.Interfaces;
using JIM.Application.Utilities;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.PostgresData;
using Serilog;

namespace JIM.Worker.Processors;

/// <summary>
/// Processes Export Run Profiles by executing Pending Exports via connectors.
/// Implements Q5 (preview mode) and Q6 (retry with backoff) decisions.
/// </summary>
public class SyncExportTaskProcessor
{
    private readonly ISyncServer _syncServer;
    private readonly ISyncRepository _syncRepo;
    private readonly Func<ISyncRepositoryScope>? _syncRepoFactory;
    private readonly IConnector _connector;
    private readonly IConnectorFactory _connectorFactory;
    private readonly ConnectedSystem _connectedSystem;
    private readonly ConnectedSystemRunProfile _runProfile;
    private readonly Activity _activity;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly SyncRunMode _runMode;
    private readonly ActivityInitiatorType _initiatedByType;
    private readonly Guid? _initiatedById;
    private readonly string? _initiatedByName;

    /// <summary>
    /// Controls how much detail is recorded for sync outcome graphs on each RPEI.
    /// Loaded once at export start from service settings.
    /// </summary>
    private ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel _syncOutcomeTrackingLevel =
        ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None;

    /// <summary>
    /// Controls whether CSO change history records are created for export RPEIs.
    /// Loaded once at export start from service settings.
    /// </summary>
    private bool _csoChangeTrackingEnabled;

    /// <summary>
    /// Narrates the run as steps an administrator can follow (#454). Never null; callers that do
    /// not track phases get a reporter that records nothing.
    /// </summary>
    private readonly ActivityPhaseReporter _phases;

    public SyncExportTaskProcessor(
        ISyncServer syncServer,
        ISyncRepository syncRepository,
        IConnector connector,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        WorkerTask workerTask,
        CancellationTokenSource cancellationTokenSource,
        SyncRunMode runMode = SyncRunMode.PreviewAndSync,
        Func<ISyncRepositoryScope>? syncRepoFactory = null,
        IConnectorFactory? connectorFactory = null,
        ActivityPhaseReporter? phaseReporter = null)
    {
        _syncServer = syncServer;
        _syncRepo = syncRepository;
        _syncRepoFactory = syncRepoFactory;
        _connector = connector;
        _connectorFactory = connectorFactory ?? new ConnectorFactory();
        _connectedSystem = connectedSystem;
        _runProfile = runProfile;
        _activity = workerTask.Activity;
        _cancellationTokenSource = cancellationTokenSource;
        _runMode = runMode;
        _initiatedByType = workerTask.InitiatedByType;
        _initiatedById = workerTask.InitiatedById;
        _initiatedByName = workerTask.InitiatedByName;
        _phases = phaseReporter ?? ActivityPhaseReporter.None;
    }

    /// <summary>
    /// Executes the export Run Profile.
    /// </summary>
    public async Task PerformExportAsync()
    {
        using var exportSpan = Diagnostics.Sync.StartSpan("Export");
        exportSpan.SetTag("connectedSystemId", _connectedSystem.Id);
        exportSpan.SetTag("connectedSystemName", _connectedSystem.Name);
        exportSpan.SetTag("connectorType", _connectedSystem.ConnectorDefinition.Name);
        exportSpan.SetTag("runMode", _runMode.ToString());

        Log.Information("PerformExportAsync: Starting export for {SystemName} (RunMode: {RunMode})",
            _connectedSystem.Name, _runMode);

        await _phases.EnterAsync(RunPhaseKeys.ExportPrepare);

        // Load settings once at start of export
        _syncOutcomeTrackingLevel = await _syncServer.GetSyncOutcomeTrackingLevelAsync();
        _csoChangeTrackingEnabled = await _syncServer.GetCsoChangeTrackingEnabledAsync();

        // Get count of executable exports for progress tracking.
        // Uses the same filtered query as ExportExecutionServer to ensure the denominator
        // (ObjectsToProcess) matches the numerator (ProcessedExports from progress callbacks).
        int pendingExportCount;
        using (Diagnostics.Sync.StartSpan("GetPendingExportsCount"))
        {
            pendingExportCount = await _syncRepo.GetExecutableExportCountAsync(_connectedSystem.Id);
        }
        _activity.ObjectsToProcess = pendingExportCount;
        _activity.ObjectsProcessed = 0;
        await _syncRepo.UpdateActivityAsync(_activity);

        if (pendingExportCount == 0)
        {
            Log.Information("PerformExportAsync: No Pending Exports for {SystemName}", _connectedSystem.Name);
            await _syncRepo.UpdateActivityMessageAsync(_activity, "No exports to process");

            // #1121: still worth a delivery pass. An account whose initial password could not be set last time
            // is waiting on a retry, and a run with nothing to export is exactly what an administrator does
            // after granting the missing right or bringing the directory back up.
            await DeliverOutstandingInitialPasswordsAsync();
            return;
        }

        // Check if connector supports export
        if (_connector is not (IConnectorExportUsingCalls or IConnectorExportUsingFiles))
        {
            var errorMessage = $"Connector {_connector.Name} does not support export operations";
            Log.Error("PerformExportAsync: {Error}", errorMessage);
            await _syncServer.FailActivityWithErrorAsync(_activity, errorMessage);
            return;
        }

        // Check for cancellation before starting
        if (_cancellationTokenSource.IsCancellationRequested)
        {
            Log.Information("PerformExportAsync: Cancellation requested before export started");
            await _syncRepo.UpdateActivityMessageAsync(_activity, "Cancelled before export");
            return;
        }

        try
        {
            // Resolve the degree of export batch parallelism (issue #985d): an explicit
            // Max Export Parallelism setting always wins; otherwise the connector may recommend
            // a directory-aware degree of parallelism (e.g. LdapConnector, mirroring its own
            // Export Concurrency auto-tune); otherwise fall back to sequential.
            var resolvedParallelism = ExportParallelismResolver.Resolve(
                _connectedSystem.MaxExportParallelism,
                _connector,
                _connectedSystem.SettingValues,
                _connectedSystem.Name);

            // Execute exports using the ExportExecutionServer with progress reporting
            var options = new ExportExecutionOptions
            {
                BatchSize = 100,
                MaxParallelism = resolvedParallelism
            };

            var throughput = new ThroughputTracker();
            ExportExecutionResult result;
            await _phases.EnterAsync(RunPhaseKeys.ExportExecute, $"Exporting {pendingExportCount:N0} changes");
            using (Diagnostics.Connector.StartSpan("ExecuteExports").SetTag("pendingExportCount", pendingExportCount))
            {
                result = await _syncServer.ExecuteExportsAsync(
                    _connectedSystem,
                    _connector,
                    _runMode,
                    options,
                    _cancellationTokenSource.Token,
                    async progressInfo =>
                    {
                        // The counters are what the portal renders the count, rate and time remaining
                        // from, so the Connector's message travels on its own; repeating the counts
                        // in it printed the same numbers twice on the Activity.
                        _activity.ObjectsProcessed = progressInfo.ProcessedExports;

                        // A report carrying a Connector phase key is the Connector saying it has moved
                        // to one of the steps it declared, so it advances the stepper too (#454).
                        if (!string.IsNullOrEmpty(progressInfo.ConnectorPhaseKey))
                        {
                            await _phases.EnterConnectorPhaseAsync(progressInfo.ConnectorPhaseKey, progressInfo.Message);
                            return;
                        }

                        await _syncRepo.UpdateActivityMessageAsync(_activity, progressInfo.Message ?? string.Empty);
                    },
                    connectorFactory: CreateConnectorForParallelBatch,
                    repositoryFactory: _syncRepoFactory,
                    batchCompletedCallback: async batchItems =>
                    {
                        // Stream RPEI creation per-batch instead of accumulating 100K+ items
                        // across the entire run. This bounds memory to batch size (~100 items).
                        await PersistBatchRpeisAsync(batchItems, _connectedSystem.Id);
                    });
            }

            exportSpan.SetTag("successCount", result.SuccessCount);
            exportSpan.SetTag("failedCount", result.FailedCount);
            exportSpan.SetTag("deferredCount", result.DeferredCount);

            // Finalise activity with completion message and stats (RPEIs already persisted per-batch)
            using (Diagnostics.Sync.StartSpan("ProcessExportResult"))
            {
                await ProcessExportResultAsync(result, throughput);
            }

            // Auto-select any containers created during export.
            // This creates a child activity with its own message — do not update the parent's message.
            if (result.CreatedContainerExternalIds.Count > 0)
            {
                Log.Information("PerformExportAsync: Export created {Count} new container(s), triggering auto-selection",
                    result.CreatedContainerExternalIds.Count);

                using (Diagnostics.Sync.StartSpan("AutoSelectContainers").SetTag("containerCount", result.CreatedContainerExternalIds.Count))
                {
                    await _syncServer.RefreshAndAutoSelectContainersWithTriadAsync(
                        _connectedSystem,
                        _connector,
                        result.CreatedContainerExternalIds,
                        _initiatedByType,
                        _initiatedById,
                        _initiatedByName,
                        _activity);
                }
            }

            // #1121: after the export phase, because delivery needs the accounts to exist and to carry the
            // external ids the Create results assigned. Inside the try so a failure here is reported the same
            // way any other part of the run is.
            await DeliverOutstandingInitialPasswordsAsync();

            exportSpan.SetSuccess();
        }
        catch (OperationCanceledException)
        {
            Log.Information("PerformExportAsync: Export cancelled for {SystemName}", _connectedSystem.Name);
            await _syncRepo.UpdateActivityMessageAsync(_activity, "Export cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PerformExportAsync: Error during export for {SystemName}", _connectedSystem.Name);
            await _syncServer.FailActivityWithErrorAsync(_activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Gives the accounts this Connected System has provisioned the initial passwords they are owed (#1121).
    /// <para>
    /// Runs over everything outstanding, not only what this run staged, so an export run is also the retry
    /// vehicle for an account whose password could not be set last time.
    /// </para>
    /// </summary>
    private async Task DeliverOutstandingInitialPasswordsAsync()
    {
        // A preview run answers "what would happen"; setting a password is not a preview of anything, and an
        // account given one cannot be un-given it.
        if (_runMode != SyncRunMode.PreviewAndSync)
            return;

        using var span = Diagnostics.Sync.StartSpan("DeliverInitialPasswords")
            .SetTag("connectedSystemId", _connectedSystem.Id);

        var result = await _syncServer.DeliverOutstandingInitialPasswordsAsync(
            _connectedSystem, _connector, _cancellationTokenSource.Token);

        span.SetTag("attempted", result.AttemptedCount);
        span.SetTag("delivered", result.DeliveredCount);
        span.SetTag("parked", result.ParkedCount);

        if (!result.HasSomethingToReport)
            return;

        await _syncRepo.UpdateActivityMessageAsync(_activity, DescribeInitialPasswordOutcome(result));
    }

    /// <summary>
    /// Puts an initial password pass into words for the Activity, leading with whatever needs an administrator.
    /// </summary>
    private static string DescribeInitialPasswordOutcome(InitialPasswordRunResult result)
    {
        if (result.ConnectorCannotSetPasswords)
            return "Initial passwords: this Connected System's Connector cannot set passwords";

        if (result.CouldNotOpenPasswordConnection)
            return $"Initial passwords: the password connection could not be opened; {result.PasswordConnectionErrorMessage}";

        var parts = new List<string> { $"{result.DeliveredCount:N0} delivered" };
        if (result.ParkedCount > 0)
            parts.Add($"{result.ParkedCount:N0} needing attention");
        if (result.RetryingCount > 0)
            parts.Add($"{result.RetryingCount:N0} to retry");

        return $"Initial passwords: {string.Join(", ", parts)}";
    }

    /// <summary>
    /// Creates RPEIs from a batch of processed export items, bulk-inserts them, and releases memory.
    /// Called per-batch via the batchCompletedCallback, bounding memory to batch size (~100 items)
    /// instead of accumulating 100K+ items across the entire export run.
    /// </summary>
    private async Task PersistBatchRpeisAsync(List<ProcessedExportItem> batchItems, int connectedSystemId)
    {
        if (batchItems.Count == 0)
            return;

        foreach (var exportItem in batchItems)
        {
            var executionItem = new ActivityRunProfileExecutionItem
            {
                Activity = _activity,
                ActivityId = _activity.Id,
                ObjectChangeType = exportItem.ChangeType switch
                {
                    PendingExportChangeType.Delete => ObjectChangeType.Deprovisioned,
                    _ => ObjectChangeType.Exported
                },
            };

            // Link to the Connected System Object if available.
            // Set the scalar FK alongside the navigation: RPEIs are persisted via raw SQL / COPY
            // (BulkInsertRpeisRawAsync), which does not trigger EF's automatic FK fix-up from the
            // navigation. Without the explicit assignment the column is inserted as NULL, breaking
            // the audit-trail link from the Activity into the CSO detail page (#683).
            if (exportItem.ConnectedSystemObject != null)
            {
                executionItem.ConnectedSystemObject = exportItem.ConnectedSystemObject;
                executionItem.ConnectedSystemObjectId = exportItem.ConnectedSystemObject.Id;
                executionItem.SnapshotCsoDisplayFields(exportItem.ConnectedSystemObject);
            }

            // Fallback display name from attribute value changes
            executionItem.DisplayNameSnapshot ??= exportItem.AttributeValueChanges
                .FirstOrDefault(avc => avc.Attribute?.Name?.Equals("displayname", StringComparison.OrdinalIgnoreCase) == true)
                ?.StringValue;

            // Set error information if the export failed
            if (!exportItem.Succeeded && !string.IsNullOrEmpty(exportItem.ErrorMessage))
            {
                executionItem.ErrorType = exportItem.ErrorType switch
                {
                    ConnectedSystemExportErrorType.InvalidGeneratedExternalId => ActivityRunProfileExecutionItemErrorType.InvalidGeneratedExternalId,
                    _ => ActivityRunProfileExecutionItemErrorType.UnhandledError,
                };
                executionItem.ErrorMessage = exportItem.ErrorCount > 1
                    ? $"Export failed after {exportItem.ErrorCount} attempts: {exportItem.ErrorMessage}"
                    : exportItem.ErrorCount == 1
                        ? $"Export failed: {exportItem.ErrorMessage}"
                        : exportItem.ErrorMessage;
            }

            // Build sync outcome
            if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
            {
                var outcomeType = exportItem.ChangeType switch
                {
                    PendingExportChangeType.Delete => ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned,
                    _ => ActivityRunProfileExecutionItemSyncOutcomeType.Exported
                };
                SyncOutcomeBuilder.AddRootOutcome(executionItem, outcomeType,
                    detailCount: exportItem.AttributeChangeCount > 0 ? exportItem.AttributeChangeCount : null);
            }

            // Create CSO change record for export change history
            if (_csoChangeTrackingEnabled && exportItem.AttributeValueChanges.Count > 0)
            {
                var change = ExportChangeHistoryBuilder.BuildFromProcessedExportItem(
                    exportItem,
                    connectedSystemId,
                    executionItem,
                    _initiatedByType,
                    _initiatedById,
                    _initiatedByName);
                executionItem.ConnectedSystemObjectChange = change;
            }

            _activity.RunProfileExecutionItems.Add(executionItem);
        }

        // Bulk insert this batch's RPEIs
        var batchRpeis = _activity.RunProfileExecutionItems.ToList();
        if (batchRpeis.Count > 0)
        {
            foreach (var rpei in batchRpeis)
            {
                rpei.ActivityId = _activity.Id;
                if (rpei.Id == Guid.Empty)
                    rpei.Id = Guid.NewGuid();
                if (rpei.ConnectedSystemObjectChange != null)
                    rpei.ConnectedSystemObjectChange.ActivityRunProfileExecutionItemId = rpei.Id;
                if (rpei.ConnectedSystemObject != null)
                    rpei.SnapshotCsoDisplayFields(rpei.ConnectedSystemObject);
            }

            if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
            {
                foreach (var rpei in batchRpeis)
                    SyncOutcomeBuilder.BuildOutcomeSummary(rpei);
            }

            var hasRawSqlSupport = await _syncRepo.BulkInsertRpeisAsync(batchRpeis);

            if (_csoChangeTrackingEnabled)
                await _syncRepo.PersistRpeiCsoChangesAsync(batchRpeis);

            if (hasRawSqlSupport)
            {
                Worker.AccumulateActivitySummaryStats(_activity, batchRpeis);
                _activity.RunProfileExecutionItems.Clear();
            }
        }
    }

    /// <summary>
    /// Finalises the export activity with completion message and stats.
    /// RPEIs are already persisted per-batch via PersistBatchRpeisAsync callback.
    /// </summary>
    private async Task ProcessExportResultAsync(ExportExecutionResult result, ThroughputTracker throughput)
    {
        // Resolve reference FKs on the change records this export wrote, plus any left over from the
        // preceding sync stage for this system (both persist reference DNs with ReferenceValueId
        // nulled for cross-batch FK safety). Paying the debt here keeps it from accumulating for the
        // next import's fixup, which previously inherited millions of unresolved rows at scale and
        // blew the bulk command timeout (Scale500k25kGroups, 2026-07-18). The fixup applies bounded
        // batches, so its statements stay inside the timeout regardless of volume.
        if (_csoChangeTrackingEnabled && _runMode != SyncRunMode.PreviewOnly)
        {
            await _phases.EnterAsync(RunPhaseKeys.ExportResolveReferences);
            var changeRecordsResolved = await _syncRepo.FixupCrossBatchChangeRecordReferenceIdsAsync(_connectedSystem.Id);
            if (changeRecordsResolved > 0)
                Log.Information("ProcessExportResultAsync: Resolved {Count} cross-batch change record reference FKs after export completion.", changeRecordsResolved);
        }

        // Update activity progress
        _activity.ObjectsProcessed = result.TotalPendingExports;

        // Set completion message based on mode and results
        string completionMessage;
        if (_runMode == SyncRunMode.PreviewOnly)
        {
            completionMessage = $"Preview complete: {result.TotalPendingExports} export(s) would be processed";
        }
        else
        {
            var processed = result.SuccessCount + result.FailedCount + result.DeferredCount;
            completionMessage = $"Export complete: {result.SuccessCount} succeeded, {result.FailedCount} failed, {result.DeferredCount} deferred" +
                throughput.FormatCompletion(processed);
        }

        await _syncRepo.UpdateActivityMessageAsync(_activity, completionMessage);
        await _syncRepo.UpdateActivityAsync(_activity);

        // Log summary
        if (result.FailedCount > 0)
        {
            Log.Warning("ProcessExportResultAsync: Export completed with failures. {Success} succeeded, {Failed} failed, {Deferred} deferred",
                result.SuccessCount, result.FailedCount, result.DeferredCount);
        }
        else
        {
            Log.Information("ProcessExportResultAsync: Export completed successfully. {Success} succeeded, {Deferred} deferred",
                result.SuccessCount, result.DeferredCount);
        }
    }

    /// <summary>
    /// Creates a new connector instance for use by a parallel export batch.
    /// Each parallel batch needs its own connector to avoid thread-safety issues
    /// with shared connection state (e.g., LdapConnection).
    /// </summary>
    private IConnector CreateConnectorForParallelBatch()
    {
        return _connectorFactory.Create(_connectedSystem.ConnectorDefinition.Name);
    }

    /// <summary>
    /// Gets a preview of exports that would be executed without actually running them.
    /// </summary>
    public async Task<ExportExecutionResult> GetExportPreviewAsync()
    {
        Log.Information("GetExportPreviewAsync: Generating preview for {SystemName}", _connectedSystem.Name);

        // Always use preview mode for this method
        return await _syncServer.ExecuteExportsAsync(_connectedSystem, _connector, SyncRunMode.PreviewOnly);
    }
}

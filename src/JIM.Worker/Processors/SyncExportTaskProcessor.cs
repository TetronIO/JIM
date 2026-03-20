using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Application.Utilities;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Connectors.LDAP;
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
/// Processes Export run profiles by executing pending exports via connectors.
/// Implements Q5 (preview mode) and Q6 (retry with backoff) decisions.
/// </summary>
public class SyncExportTaskProcessor
{
    private readonly ISyncServer _syncServer;
    private readonly ISyncRepository _syncRepo;
    private readonly Func<ISyncRepository>? _syncRepoFactory;
    private readonly IConnector _connector;
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

    public SyncExportTaskProcessor(
        ISyncServer syncServer,
        ISyncRepository syncRepository,
        IConnector connector,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        WorkerTask workerTask,
        CancellationTokenSource cancellationTokenSource,
        SyncRunMode runMode = SyncRunMode.PreviewAndSync,
        Func<ISyncRepository>? syncRepoFactory = null)
    {
        _syncServer = syncServer;
        _syncRepo = syncRepository;
        _syncRepoFactory = syncRepoFactory;
        _connector = connector;
        _connectedSystem = connectedSystem;
        _runProfile = runProfile;
        _activity = workerTask.Activity;
        _cancellationTokenSource = cancellationTokenSource;
        _runMode = runMode;
        _initiatedByType = workerTask.InitiatedByType;
        _initiatedById = workerTask.InitiatedById;
        _initiatedByName = workerTask.InitiatedByName;
    }

    /// <summary>
    /// Executes the export run profile.
    /// </summary>
    public async Task PerformExportAsync()
    {
        using var exportSpan = Diagnostics.Sync.StartSpan("Export");
        exportSpan.SetTag("connectedSystemId", _connectedSystem.Id);
        exportSpan.SetTag("connectedSystemName", _connectedSystem.Name);
        exportSpan.SetTag("runMode", _runMode.ToString());

        Log.Information("PerformExportAsync: Starting export for {SystemName} (RunMode: {RunMode})",
            _connectedSystem.Name, _runMode);

        await _syncRepo.UpdateActivityMessageAsync(_activity, "Preparing export");

        // Load settings once at start of export
        _syncOutcomeTrackingLevel = await _syncRepo.GetSyncOutcomeTrackingLevelAsync();
        _csoChangeTrackingEnabled = await _syncRepo.GetCsoChangeTrackingEnabledAsync();

        // Get count of pending exports for progress tracking
        int pendingExportCount;
        using (Diagnostics.Sync.StartSpan("GetPendingExportsCount"))
        {
            pendingExportCount = await _syncRepo.GetPendingExportsCountAsync(_connectedSystem.Id);
        }
        _activity.ObjectsToProcess = pendingExportCount;
        _activity.ObjectsProcessed = 0;
        await _syncRepo.UpdateActivityAsync(_activity);

        if (pendingExportCount == 0)
        {
            Log.Information("PerformExportAsync: No pending exports for {SystemName}", _connectedSystem.Name);
            await _syncRepo.UpdateActivityMessageAsync(_activity, "No exports to process");
            return;
        }

        // Check if connector supports export
        if (_connector is not (IConnectorExportUsingCalls or IConnectorExportUsingFiles))
        {
            var errorMessage = $"Connector {_connector.Name} does not support export operations";
            Log.Error("PerformExportAsync: {Error}", errorMessage);
            await _syncRepo.FailActivityWithErrorAsync(_activity, errorMessage);
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
            // Execute exports using the ExportExecutionServer with progress reporting
            var options = new ExportExecutionOptions
            {
                BatchSize = 100,
                MaxParallelism = _connectedSystem.MaxExportParallelism ?? 1
            };

            ExportExecutionResult result;
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
                        // Update activity with progress
                        _activity.ObjectsProcessed = progressInfo.ProcessedExports;
                        await _syncRepo.UpdateActivityMessageAsync(_activity, progressInfo.Message);
                        await _syncRepo.UpdateActivityAsync(_activity);
                    },
                    connectorFactory: CreateConnectorForParallelBatch,
                    repositoryFactory: _syncRepoFactory);
            }

            exportSpan.SetTag("successCount", result.SuccessCount);
            exportSpan.SetTag("failedCount", result.FailedCount);
            exportSpan.SetTag("deferredCount", result.DeferredCount);

            // Update activity with final results
            using (Diagnostics.Sync.StartSpan("ProcessExportResult").SetTag("itemCount", result.ProcessedExportItems.Count))
            {
                await ProcessExportResultAsync(result);
            }

            // Auto-select any containers created during export.
            // This creates a child activity with its own message — do not update the parent's message.
            if (result.CreatedContainerExternalIds.Count > 0)
            {
                Log.Information("PerformExportAsync: Export created {Count} new container(s), triggering auto-selection",
                    result.CreatedContainerExternalIds.Count);

                using (Diagnostics.Sync.StartSpan("AutoSelectContainers").SetTag("containerCount", result.CreatedContainerExternalIds.Count))
                {
                    await _syncRepo.RefreshAndAutoSelectContainersWithTriadAsync(
                        _connectedSystem,
                        _connector,
                        result.CreatedContainerExternalIds,
                        _initiatedByType,
                        _initiatedById,
                        _initiatedByName,
                        _activity);
                }
            }

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
            await _syncRepo.FailActivityWithErrorAsync(_activity, ex);
            throw;
        }
    }

    /// <summary>
    /// Processes the export execution result and updates the activity accordingly.
    /// Creates ActivityRunProfileExecutionItem records for each processed export,
    /// including error information for failed exports.
    /// </summary>
    private async Task ProcessExportResultAsync(ExportExecutionResult result)
    {
        // Use ProcessedExportItems which are captured before pending exports are deleted
        foreach (var exportItem in result.ProcessedExportItems)
        {
            // Build a description of the export
            var description = _runMode == SyncRunMode.PreviewOnly
                ? $"Preview: {exportItem.ChangeType} with {exportItem.AttributeChangeCount} attribute change(s)"
                : $"Export: {exportItem.ChangeType} with {exportItem.AttributeChangeCount} attribute change(s)";

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

            // Link to the Connected System Object if available
            if (exportItem.ConnectedSystemObject != null)
            {
                executionItem.ConnectedSystemObject = exportItem.ConnectedSystemObject;

                // Snapshot CSO display fields for durability - ensures the RPEI retains
                // display data even if the CSO is later deleted via FK cascade
                executionItem.SnapshotCsoDisplayFields(exportItem.ConnectedSystemObject);
            }

            // If DisplayNameSnapshot is still null (e.g. Create-type export where the CSO is a
            // stub with no displayname attribute), fall back to the pending export's attribute
            // value changes which carry the full set of outbound attribute values.
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
                if (exportItem.ErrorCount > 1)
                {
                    // Export has been retried - show the retry count
                    executionItem.ErrorMessage = $"Export failed after {exportItem.ErrorCount} attempts: {exportItem.ErrorMessage}";
                }
                else if (exportItem.ErrorCount == 1)
                {
                    // First failure - don't confuse users with "attempt 1"
                    executionItem.ErrorMessage = $"Export failed: {exportItem.ErrorMessage}";
                }
                else
                {
                    executionItem.ErrorMessage = exportItem.ErrorMessage;
                }
            }

            // Build sync outcome for export result
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

            // Create CSO change record for export change history (if enabled)
            if (_csoChangeTrackingEnabled && exportItem.AttributeValueChanges.Count > 0)
            {
                var change = ExportChangeHistoryBuilder.BuildFromProcessedExportItem(
                    exportItem,
                    result.ConnectedSystemId,
                    executionItem,
                    _initiatedByType,
                    _initiatedById,
                    _initiatedByName);
                executionItem.ConnectedSystemObjectChange = change;
            }

            _activity.RunProfileExecutionItems.Add(executionItem);
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
            completionMessage = $"Export complete: {result.SuccessCount} succeeded, {result.FailedCount} failed, {result.DeferredCount} deferred";
        }

        // Bulk insert RPEIs via raw SQL (bypasses change tracker for performance)
        var exportRpeis = _activity.RunProfileExecutionItems.ToList();
        var hasRawSqlSupport = false;
        if (exportRpeis.Count > 0)
        {
            foreach (var rpei in exportRpeis)
            {
                rpei.ActivityId = _activity.Id;
                if (rpei.Id == Guid.Empty)
                    rpei.Id = Guid.NewGuid();

                // Fix up the scalar FK on the CSO change record to match the newly assigned RPEI ID.
                // The change was created before the RPEI ID was assigned, so the FK is still Guid.Empty.
                if (rpei.ConnectedSystemObjectChange != null)
                    rpei.ConnectedSystemObjectChange.ActivityRunProfileExecutionItemId = rpei.Id;

                // Snapshot CSO display fields for historical preservation (defence-in-depth)
                if (rpei.ConnectedSystemObject != null)
                    rpei.SnapshotCsoDisplayFields(rpei.ConnectedSystemObject);
            }

            // Build outcome summaries before persisting
            if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
            {
                foreach (var rpei in exportRpeis)
                    SyncOutcomeBuilder.BuildOutcomeSummary(rpei);
            }

            hasRawSqlSupport = await _syncRepo.BulkInsertRpeisAsync(exportRpeis);

            // Persist CSO change records separately — raw SQL bulk insert only covers
            // RPEI scalar columns, not the ConnectedSystemObjectChange navigation graph.
            if (_csoChangeTrackingEnabled)
                await _syncRepo.PersistRpeiCsoChangesAsync(exportRpeis);
        }

        if (hasRawSqlSupport)
        {
            // Production: accumulate summary stats before clearing RPEIs from memory.
            Worker.AccumulateActivitySummaryStats(_activity, exportRpeis);
            _activity.RunProfileExecutionItems.Clear();
        }
        // Test environments (EF fallback): keep RPEIs on the Activity for test assertions.
        // Stats are computed at activity completion by CalculateActivitySummaryStats.

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
        if (_connectedSystem.ConnectorDefinition.Name == ConnectorConstants.LdapConnectorName)
            return new LdapConnector();
        if (_connectedSystem.ConnectorDefinition.Name == ConnectorConstants.FileConnectorName)
            return new FileConnector();

        throw new NotSupportedException(
            $"{_connectedSystem.ConnectorDefinition.Name} connector does not support parallel batch export.");
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

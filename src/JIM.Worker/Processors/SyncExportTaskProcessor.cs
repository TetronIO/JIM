using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Data;
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
    private readonly JimApplication _jim;
    private readonly IConnector _connector;
    private readonly ConnectedSystem _connectedSystem;
    private readonly ConnectedSystemRunProfile _runProfile;
    private readonly Activity _activity;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly SyncRunMode _runMode;
    private readonly ActivityInitiatorType _initiatedByType;
    private readonly Guid? _initiatedById;
    private readonly string? _initiatedByName;

    public SyncExportTaskProcessor(
        JimApplication jimApplication,
        IConnector connector,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        WorkerTask workerTask,
        CancellationTokenSource cancellationTokenSource,
        SyncRunMode runMode = SyncRunMode.PreviewAndSync)
    {
        _jim = jimApplication;
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

        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Preparing export");

        // Get count of pending exports for progress tracking
        int pendingExportCount;
        using (Diagnostics.Sync.StartSpan("GetPendingExportsCount"))
        {
            pendingExportCount = await _jim.ConnectedSystems.GetPendingExportsCountAsync(_connectedSystem.Id);
        }
        _activity.ObjectsToProcess = pendingExportCount;
        _activity.ObjectsProcessed = 0;
        await _jim.Activities.UpdateActivityAsync(_activity);

        if (pendingExportCount == 0)
        {
            Log.Information("PerformExportAsync: No pending exports for {SystemName}", _connectedSystem.Name);
            await _jim.Activities.UpdateActivityMessageAsync(_activity, "No exports to process");
            return;
        }

        // Check if connector supports export
        if (_connector is not (IConnectorExportUsingCalls or IConnectorExportUsingFiles))
        {
            var errorMessage = $"Connector {_connector.Name} does not support export operations";
            Log.Error("PerformExportAsync: {Error}", errorMessage);
            await _jim.Activities.FailActivityWithErrorAsync(_activity, errorMessage);
            return;
        }

        // Check for cancellation before starting
        if (_cancellationTokenSource.IsCancellationRequested)
        {
            Log.Information("PerformExportAsync: Cancellation requested before export started");
            await _jim.Activities.UpdateActivityMessageAsync(_activity, "Cancelled before export");
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
                result = await _jim.ExportExecution.ExecuteExportsAsync(
                    _connectedSystem,
                    _connector,
                    _runMode,
                    options,
                    _cancellationTokenSource.Token,
                    async progressInfo =>
                    {
                        // Update activity with progress
                        _activity.ObjectsProcessed = progressInfo.ProcessedExports;
                        await _jim.Activities.UpdateActivityMessageAsync(_activity, progressInfo.Message);
                        await _jim.Activities.UpdateActivityAsync(_activity);
                    },
                    connectorFactory: CreateConnectorForParallelBatch,
                    repositoryFactory: () => new PostgresDataRepository(new JimDbContext()));
            }

            exportSpan.SetTag("successCount", result.SuccessCount);
            exportSpan.SetTag("failedCount", result.FailedCount);
            exportSpan.SetTag("deferredCount", result.DeferredCount);

            // Update activity with final results
            using (Diagnostics.Sync.StartSpan("ProcessExportResult").SetTag("itemCount", result.ProcessedExportItems.Count))
            {
                await ProcessExportResultAsync(result);
            }

            // Auto-select any containers created during export
            if (result.CreatedContainerExternalIds.Count > 0)
            {
                Log.Information("PerformExportAsync: Export created {Count} new container(s), triggering auto-selection",
                    result.CreatedContainerExternalIds.Count);

                await _jim.Activities.UpdateActivityMessageAsync(_activity,
                    $"Auto-selecting {result.CreatedContainerExternalIds.Count} container(s) created during export");

                using (Diagnostics.Sync.StartSpan("AutoSelectContainers").SetTag("containerCount", result.CreatedContainerExternalIds.Count))
                {
                    await _jim.ConnectedSystems.RefreshAndAutoSelectContainersWithTriadAsync(
                        _connectedSystem,
                        _connector,
                        result.CreatedContainerExternalIds,
                        _initiatedByType,
                        _initiatedById,
                        _initiatedByName,
                        _activity);
                }

                // Update completion message to include container count
                var updatedMessage = $"{_activity.Message} | {result.CreatedContainerExternalIds.Count} container(s) auto-selected";
                await _jim.Activities.UpdateActivityMessageAsync(_activity, updatedMessage);
            }

            exportSpan.SetSuccess();
        }
        catch (OperationCanceledException)
        {
            Log.Information("PerformExportAsync: Export cancelled for {SystemName}", _connectedSystem.Name);
            await _jim.Activities.UpdateActivityMessageAsync(_activity, "Export cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PerformExportAsync: Error during export for {SystemName}", _connectedSystem.Name);
            await _jim.Activities.FailActivityWithErrorAsync(_activity, ex);
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
                    PendingExportChangeType.Create => ObjectChangeType.Provisioned,
                    PendingExportChangeType.Update => ObjectChangeType.Exported,
                    PendingExportChangeType.Delete => ObjectChangeType.Deprovisioned,
                    _ => ObjectChangeType.Exported
                },
                DataSnapshot = description
            };

            // Link to the Connected System Object if available
            if (exportItem.ConnectedSystemObject != null)
            {
                executionItem.ConnectedSystemObject = exportItem.ConnectedSystemObject;

                // Snapshot the external ID for durability - ensures the RPEI retains the
                // external ID even if the CSO is later deleted via FK cascade
                executionItem.ExternalIdSnapshot = exportItem.ConnectedSystemObject
                    .ExternalIdAttributeValue?.ToStringNoName();
            }

            // Set error information if the export failed
            if (!exportItem.Succeeded && !string.IsNullOrEmpty(exportItem.ErrorMessage))
            {
                executionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError;
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
        if (exportRpeis.Count > 0)
        {
            foreach (var rpei in exportRpeis)
            {
                rpei.ActivityId = _activity.Id;
                if (rpei.Id == Guid.Empty)
                    rpei.Id = Guid.NewGuid();
            }
            await _jim.Activities.BulkInsertRpeisAsync(exportRpeis);
        }

        // RPEIs remain in _activity.RunProfileExecutionItems for CalculateActivitySummaryStats
        await _jim.Activities.UpdateActivityMessageAsync(_activity, completionMessage);
        await _jim.Activities.UpdateActivityAsync(_activity);

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
        return await _jim.ExportExecution.ExecuteExportsAsync(_connectedSystem, _connector, SyncRunMode.PreviewOnly);
    }
}

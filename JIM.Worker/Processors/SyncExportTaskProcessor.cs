using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
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
    private readonly MetaverseObject? _initiatedByMetaverseObject;
    private readonly ApiKey? _initiatedByApiKey;

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
        _initiatedByMetaverseObject = workerTask.InitiatedByMetaverseObject;
        _initiatedByApiKey = workerTask.InitiatedByApiKey;
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
        var pendingExportCount = await _jim.ConnectedSystems.GetPendingExportsCountAsync(_connectedSystem.Id);
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
                MaxParallelism = 4
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
                    });
            }

            exportSpan.SetTag("successCount", result.SuccessCount);
            exportSpan.SetTag("failedCount", result.FailedCount);
            exportSpan.SetTag("deferredCount", result.DeferredCount);

            // Update activity with final results
            await ProcessExportResultAsync(result);

            // Auto-select any containers created during export
            if (result.CreatedContainerDns.Count > 0)
            {
                Log.Information("PerformExportAsync: Export created {Count} new container(s), triggering auto-selection",
                    result.CreatedContainerDns.Count);

                await _jim.Activities.UpdateActivityMessageAsync(_activity,
                    $"Auto-selecting {result.CreatedContainerDns.Count} container(s) created during export");

                await _jim.ConnectedSystems.RefreshAndAutoSelectContainersAsync(
                    _connectedSystem,
                    result.CreatedContainerDns,
                    _initiatedByApiKey,
                    _initiatedByMetaverseObject,
                    _activity);

                // Update completion message to include container count
                var updatedMessage = $"{_activity.Message} | {result.CreatedContainerDns.Count} container(s) auto-selected";
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
                    PendingExportChangeType.Create => ObjectChangeType.Create,
                    PendingExportChangeType.Update => ObjectChangeType.Update,
                    PendingExportChangeType.Delete => ObjectChangeType.Delete,
                    _ => ObjectChangeType.Update
                },
                DataSnapshot = description
            };

            // Link to the Connected System Object if available
            if (exportItem.ConnectedSystemObject != null)
            {
                executionItem.ConnectedSystemObject = exportItem.ConnectedSystemObject;
            }

            // Set error information if the export failed
            if (!exportItem.Succeeded && !string.IsNullOrEmpty(exportItem.ErrorMessage))
            {
                executionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError;
                if (exportItem.ErrorCount > 0)
                {
                    executionItem.ErrorMessage = $"Export failed (attempt {exportItem.ErrorCount}): {exportItem.ErrorMessage}";
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
    /// Gets a preview of exports that would be executed without actually running them.
    /// </summary>
    public async Task<ExportExecutionResult> GetExportPreviewAsync()
    {
        Log.Information("GetExportPreviewAsync: Generating preview for {SystemName}", _connectedSystem.Name);

        // Always use preview mode for this method
        return await _jim.ExportExecution.ExecuteExportsAsync(_connectedSystem, _connector, SyncRunMode.PreviewOnly);
    }
}

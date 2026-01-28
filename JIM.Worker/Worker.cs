using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Application.Services;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.PostgresData;
using JIM.Worker.Processors;
using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Json;
namespace JIM.Worker;

// **************************************************************************************
// Junctional Identity Manager - Background Worker
//
// Needs to:
// - Loop until asked to close down
// - Check the database for manual synchronisation tasks
// - Check the database for scheduled synchronisation tasks
// - Check the database for data generation template tasks
// - Create tasks for the jobs, keeping track of cancellation tokens
// - Execute the tasks
//
// The sync tasks will need to perform:
// - Import data from connected systems via connectors
// - Reconcile updates to objects internally, i.e. connector space and metaverse objects
// - Export data to connected systems via connectors
//
// Required environment variables:
// -------------------------------
// JIM_LOG_LEVEL
// JIM_LOG_PATH
// JIM_DB_HOSTNAME - validated by data layer
// JIM_DB_NAME - validated by data layer
// JIM_DB_USERNAME - validated by data layer
// JIM_DB_PASSWORD - validated by data layer
//
// Design Pattern:
// https://docs.microsoft.com/en-us/dotnet/core/extensions/workers
//
// **************************************************************************************

public class Worker : BackgroundService
{
    /// <summary>
    /// The worker tasks currently being executed.
    /// </summary>
    private List<TaskTask> CurrentTasks { get; } = new();
    private readonly object _currentTasksLock = new();
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        InitialiseLogging();

        // Enable performance diagnostics - logs span completions with timing info
        // Threshold of 100ms means operations taking longer will be logged at Warning level
        using var diagnosticListener = Diagnostics.EnableLogging(slowOperationThresholdMs: 100);

        Log.Information("Starting JIM.Worker...");

        // Create credential protection service for encrypting/decrypting secrets
        // This uses the shared key storage to ensure consistency with JIM.Web
        var credentialProtection = new CredentialProtectionService(DataProtectionHelper.CreateProvider());

        // as JIM.Worker is the first JimApplication client to start, it's responsible for ensuring the database is initialised.
        // other JimApplication clients will need to check if the app is ready before completing their initialisation.
        // JimApplication instances are ephemeral and should be disposed as soon as a request/batch of work is complete (for database tracking reasons).
        using var mainLoopJim = new JimApplication(new PostgresDataRepository(new JimDbContext()));
        mainLoopJim.CredentialProtection = credentialProtection;
        await mainLoopJim.InitialiseDatabaseAsync();

        // first of all check if there's any tasks that have been requested for cancellation but have not yet been processed.
        // this scenario is expected to be for when the worker unexpectedly quits and can't execute cancellations.
        foreach (var taskToCancel in await mainLoopJim.Tasking.GetWorkerTasksThatNeedCancellingAsync())
            await mainLoopJim.Tasking.CancelWorkerTaskAsync(taskToCancel);

        // DEV: Unsupported scenario:
        // todo: job is being processed in database but is not in the current tasks, i.e. it's no longer being processed. clear it out

        while (!stoppingToken.IsCancellationRequested)
        {
            // if processing no tasks:
            //      get the next batch of parallel tasks and execute them all at once or the next sequential task and execute that
            // if processing tasks:
            //      get the worker tasks for those being processed
            //      foreach: if the status is cancellation requested, then cancel the task

            if (CurrentTasks.Count > 0)
            {
                // check the database to see if we need to cancel any tasks we're currently processing...
                var workerTaskIds = CurrentTasks.Select(t => t.TaskId).ToArray();
                var workerTasksToCancel = await mainLoopJim.Tasking.GetWorkerTasksThatNeedCancellingAsync(workerTaskIds);
                foreach (var workerTaskToCancel in workerTasksToCancel)
                {
                    var taskTask = CurrentTasks.SingleOrDefault(t => t.TaskId == workerTaskToCancel.Id);
                    if (taskTask != null)
                    {
                        Log.Information($"ExecuteAsync: Cancelling task {workerTaskToCancel.Id}...");
                        taskTask.CancellationTokenSource.Cancel();
                        await mainLoopJim.Tasking.CancelWorkerTaskAsync(workerTaskToCancel);
                        CurrentTasks.Remove(taskTask);
                    }
                    else
                    {
                        Log.Debug($"ExecuteAsync: No need to cancel task id {workerTaskToCancel.Id} as it seems to have finished processing.");
                    }
                }
            }
            else
            {
                // look for new tasks to process...
                var newWorkerTasksToProcess = await mainLoopJim.Tasking.GetNextWorkerTasksToProcessAsync();
                if (newWorkerTasksToProcess.Count == 0)
                {
                    Log.Debug("ExecuteAsync: No tasks on queue. Sleeping...");

                    // During idle time, perform housekeeping tasks like orphan MVO cleanup
                    await PerformHousekeepingAsync(mainLoopJim);

                    await Task.Delay(2000, stoppingToken);
                }
                else
                {
                    foreach (var mainLoopNewWorkerTask in newWorkerTasksToProcess)
                    {
                        var cancellationTokenSource = new CancellationTokenSource();
                        var task = Task.Run(async () =>
                        {
                            // create an instance of JIM, specific to the processing of this task.
                            // we can't use the main-loop instance, due to Entity Framework having connection sharing issues.
                            // IMPORTANT: taskJim must be disposed to release database connections and prevent deadlocks.
                            using var taskJim = new JimApplication(new PostgresDataRepository(new JimDbContext()));
                            taskJim.CredentialProtection = credentialProtection;

                            // we want to re-retrieve the worker task using this instance of JIM, so there's no chance of any cross-JIM-instance issues
                            var newWorkerTask = await taskJim.Tasking.GetWorkerTaskAsync(mainLoopNewWorkerTask.Id) ??
                                                throw new InvalidDataException($"WorkerTask '{mainLoopNewWorkerTask.Id}' could not be retrieved.");

                            newWorkerTask.Activity.Executed = DateTime.UtcNow;
                            await taskJim.Activities.UpdateActivityAsync(newWorkerTask.Activity);

                            switch (newWorkerTask)
                            {
                                case DataGenerationTemplateWorkerTask dataGenTemplateServiceTask:
                                {
                                    Log.Information("ExecuteAsync: DataGenerationTemplateServiceTask received for template id: " + dataGenTemplateServiceTask.TemplateId);
                                    var dataGenerationTemplate = await taskJim.DataGeneration.GetTemplateAsync(dataGenTemplateServiceTask.TemplateId);
                                    if (dataGenerationTemplate == null)
                                    {
                                        Log.Warning($"ExecuteAsync: data generation template {dataGenTemplateServiceTask.TemplateId} not found.");
                                    }
                                    else
                                    {
                                        try
                                        {
                                            // Progress callback to update activity with generation progress and message
                                            async Task ProgressCallback(int totalObjects, int objectsProcessed, string? message)
                                            {
                                                newWorkerTask.Activity.ObjectsToProcess = totalObjects;
                                                newWorkerTask.Activity.ObjectsProcessed = objectsProcessed;
                                                newWorkerTask.Activity.Message = message;
                                                await taskJim.Activities.UpdateActivityAsync(newWorkerTask.Activity);
                                            }

                                            // Get settings for progress updates and batch size
                                            var progressUpdateInterval = await taskJim.ServiceSettings.GetSettingValueAsync(
                                                Constants.SettingKeys.ProgressUpdateInterval,
                                                TimeSpan.FromSeconds(1));
                                            var batchSize = await taskJim.ServiceSettings.GetSyncPageSizeAsync();
                                            Log.Information("ExecuteAsync: Data generation progress update interval: {Interval}, batch size: {BatchSize}",
                                                progressUpdateInterval, batchSize);

                                            var objectsCreated = await taskJim.DataGeneration.ExecuteTemplateAsync(
                                                dataGenTemplateServiceTask.TemplateId,
                                                cancellationTokenSource.Token,
                                                ProgressCallback,
                                                progressUpdateInterval,
                                                batchSize);
                                            newWorkerTask.Activity.TotalObjectCreates = objectsCreated;
                                            await taskJim.Activities.CompleteActivityAsync(newWorkerTask.Activity);
                                        }
                                        catch (Exception ex)
                                        {
                                            await taskJim.Activities.FailActivityWithErrorAsync(newWorkerTask.Activity, ex);
                                            Log.Error(ex, "ExecuteAsync: Unhandled exception whilst executing data generation template: " + dataGenTemplateServiceTask.TemplateId);
                                        }
                                        finally
                                        {
                                            Log.Information($"ExecuteAsync: Completed data generation template ({dataGenTemplateServiceTask.TemplateId}) execution in {newWorkerTask.Activity.ExecutionTime}.");
                                        }
                                    }

                                    break;
                                }
                                case SynchronisationWorkerTask syncWorkerTask:
                                {
                                    var initiatedByDisplay = newWorkerTask.InitiatedByMetaverseObject?.DisplayName ?? newWorkerTask.InitiatedByName ?? "Unknown";
                                    Log.Information("ExecuteAsync: SynchronisationWorkerTask received for run profile id: {RunProfileId}, initiated by: {InitiatedBy}",
                                        syncWorkerTask.ConnectedSystemRunProfileId, initiatedByDisplay);
                                    {
                                        var connectedSystem = await taskJim.ConnectedSystems.GetConnectedSystemAsync(syncWorkerTask.ConnectedSystemId);
                                        if (connectedSystem != null)
                                        {
                                            // work out what connector we need to use
                                            // todo: run through built-in connectors first, then do a lookup for user-supplied connectors
                                            IConnector connector;
                                            if (connectedSystem.ConnectorDefinition.Name == ConnectorConstants.LdapConnectorName)
                                                connector = new LdapConnector();
                                            else if (connectedSystem.ConnectorDefinition.Name == ConnectorConstants.FileConnectorName)
                                                connector = new FileConnector();
                                            else
                                                throw new NotSupportedException($"{connectedSystem.ConnectorDefinition.Name} connector not yet supported for worker processing.");

                                            // work out what type of run profile we're being asked to run
                                            var runProfile = connectedSystem.RunProfiles?.SingleOrDefault(rp => rp.Id == syncWorkerTask.ConnectedSystemRunProfileId);
                                            if (runProfile != null)
                                            {
                                                try
                                                {
                                                    switch (runProfile.RunType)
                                                    {
                                                        // hand processing of the sync task to a dedicated task processor to keep the worker abstract of specific tasks
                                                        case ConnectedSystemRunType.FullImport:
                                                        {
                                                            var syncImportTaskProcessor = new SyncImportTaskProcessor(taskJim, connector, connectedSystem, runProfile, newWorkerTask, cancellationTokenSource);
                                                            await syncImportTaskProcessor.PerformFullImportAsync();
                                                            break;
                                                        }
                                                        case ConnectedSystemRunType.DeltaImport:
                                                        {
                                                            // Delta Import uses the import processor just like Full Import.
                                                            // The connector's ImportAsync method checks the run profile type
                                                            // to determine whether to do full or delta import.
                                                            var syncDeltaImportTaskProcessor = new SyncImportTaskProcessor(taskJim, connector, connectedSystem, runProfile, newWorkerTask, cancellationTokenSource);
                                                            await syncDeltaImportTaskProcessor.PerformFullImportAsync();
                                                            break;
                                                        }
                                                        case ConnectedSystemRunType.FullSynchronisation:
                                                        {
                                                            var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(taskJim, connectedSystem, runProfile, newWorkerTask.Activity, cancellationTokenSource);
                                                            await syncFullSyncTaskProcessor.PerformFullSyncAsync();
                                                            break;
                                                        }
                                                        case ConnectedSystemRunType.Export:
                                                        {
                                                            var syncExportTaskProcessor = new SyncExportTaskProcessor(taskJim, connector, connectedSystem, runProfile, newWorkerTask, cancellationTokenSource);
                                                            await syncExportTaskProcessor.PerformExportAsync();
                                                            break;
                                                        }
                                                        case ConnectedSystemRunType.DeltaSynchronisation:
                                                        {
                                                            var syncDeltaSyncTaskProcessor = new SyncDeltaSyncTaskProcessor(taskJim, connectedSystem, runProfile, newWorkerTask.Activity, cancellationTokenSource);
                                                            await syncDeltaSyncTaskProcessor.PerformDeltaSyncAsync();
                                                            break;
                                                        }
                                                        default:
                                                            Log.Error($"ExecuteAsync: Unsupported run type: {runProfile.RunType}");
                                                            break;
                                                    }

                                                    // task completed. determine final status, depending on how the run profile execution went
                                                    await CompleteActivityBasedOnExecutionResultsAsync(taskJim, newWorkerTask.Activity);
                                                }
                                                catch (Exception ex)
                                                {
                                                    // we log unhandled exceptions to the history to enable sync operators/admins to be able to easily view
                                                    // issues with connectors through JIM, rather than an admin having to dig through server logs.
                                                    await SafeFailActivityAsync(taskJim, newWorkerTask.Activity, ex, "Unhandled exception whilst executing sync run");
                                                }
                                                finally
                                                {
                                                    // record how long the sync run took, whether it was successful, or not.
                                                    Log.Information($"ExecuteAsync: Completed processing of {newWorkerTask.Activity.TargetName} sync run in {newWorkerTask.Activity.ExecutionTime}.");
                                                }
                                            }
                                            else
                                            {
                                                Log.Warning($"ExecuteAsync: sync task specifies run profile id {syncWorkerTask.ConnectedSystemRunProfileId} but no such profile found on connected system id {syncWorkerTask.ConnectedSystemId}.");
                                            }
                                        }
                                        else
                                        {
                                            Log.Warning($"ExecuteAsync: sync task specifies connected system id {syncWorkerTask.ConnectedSystemId} but no such connected system found.");
                                        }
                                    }
                                    break;
                                }
                                case ClearConnectedSystemObjectsWorkerTask clearConnectedSystemObjectsTask:
                                {
                                    Log.Information("ExecuteAsync: ClearConnectedSystemObjectsTask received for connected system id: " + clearConnectedSystemObjectsTask.ConnectedSystemId);
                                    if (clearConnectedSystemObjectsTask.InitiatedByType == ActivityInitiatorType.NotSet)
                                    {
                                        Log.Error($"ExecuteAsync: ClearConnectedSystemObjectsTask {clearConnectedSystemObjectsTask.Id} is missing initiator information. Cannot continue processing worker task.");
                                    }
                                    else
                                    {
                                        // we need a little more information on the connected system, so retrieve it
                                        var connectedSystem = await taskJim.ConnectedSystems.GetConnectedSystemAsync(clearConnectedSystemObjectsTask.ConnectedSystemId);
                                        if (connectedSystem == null)
                                        {
                                            Log.Warning($"ExecuteAsync: Connected system id {clearConnectedSystemObjectsTask.ConnectedSystemId} doesn't exist. Cannot continue.");
                                            return;
                                        }

                                        try
                                        {
                                            // initiate clearing the connected system
                                            await taskJim.ConnectedSystems.ClearConnectedSystemObjectsAsync(
                                                clearConnectedSystemObjectsTask.ConnectedSystemId,
                                                clearConnectedSystemObjectsTask.DeleteChangeHistory);

                                            // task completed successfully, complete the activity
                                            await taskJim.Activities.CompleteActivityAsync(newWorkerTask.Activity);
                                        }
                                        catch (Exception ex)
                                        {
                                            await taskJim.Activities.FailActivityWithErrorAsync(newWorkerTask.Activity, ex);
                                            Log.Error(ex, "ExecuteAsync: Unhandled exception whilst executing clear connected system task.");
                                        }
                                        finally
                                        {
                                            // record how long the sync run took, whether it was successful, or not.
                                            Log.Information($"ExecuteAsync: Completed clearing the connected system ({clearConnectedSystemObjectsTask.ConnectedSystemId}) in {newWorkerTask.Activity.ExecutionTime}.");
                                        }
                                    }

                                    break;
                                }
                                case DeleteConnectedSystemWorkerTask deleteConnectedSystemTask:
                                {
                                    Log.Information("ExecuteAsync: DeleteConnectedSystemWorkerTask received for connected system id: {ConnectedSystemId}, EvaluateMvoDeletionRules: {EvaluateMvo}, DeleteChangeHistory: {DeleteHistory}",
                                        deleteConnectedSystemTask.ConnectedSystemId, deleteConnectedSystemTask.EvaluateMvoDeletionRules, deleteConnectedSystemTask.DeleteChangeHistory);

                                    try
                                    {
                                        // Execute the deletion (marks orphaned MVOs for deletion before deleting CS)
                                        await taskJim.ConnectedSystems.ExecuteDeletionAsync(
                                            deleteConnectedSystemTask.ConnectedSystemId,
                                            deleteConnectedSystemTask.EvaluateMvoDeletionRules,
                                            deleteConnectedSystemTask.DeleteChangeHistory);

                                        // Task completed successfully, complete the activity
                                        await taskJim.Activities.CompleteActivityAsync(newWorkerTask.Activity);
                                    }
                                    catch (Exception ex)
                                    {
                                        // Reset Connected System status so deletion can be retried
                                        try
                                        {
                                            var connectedSystem = await taskJim.ConnectedSystems.GetConnectedSystemAsync(deleteConnectedSystemTask.ConnectedSystemId);
                                            if (connectedSystem != null && connectedSystem.Status == ConnectedSystemStatus.Deleting)
                                            {
                                                connectedSystem.Status = ConnectedSystemStatus.Active;
                                                await taskJim.ConnectedSystems.UpdateConnectedSystemAsync(connectedSystem, (MetaverseObject?)null);
                                                Log.Warning("ExecuteAsync: Reset Connected System {Id} status to Active after deletion failure", deleteConnectedSystemTask.ConnectedSystemId);
                                            }
                                        }
                                        catch (Exception resetEx)
                                        {
                                            Log.Error(resetEx, "ExecuteAsync: Failed to reset Connected System {Id} status after deletion failure", deleteConnectedSystemTask.ConnectedSystemId);
                                        }

                                        await taskJim.Activities.FailActivityWithErrorAsync(newWorkerTask.Activity, ex);
                                        Log.Error(ex, "ExecuteAsync: Unhandled exception whilst executing delete connected system task.");
                                    }
                                    finally
                                    {
                                        Log.Information("ExecuteAsync: Completed deleting connected system ({ConnectedSystemId}) in {ExecutionTime}",
                                            deleteConnectedSystemTask.ConnectedSystemId, newWorkerTask.Activity.ExecutionTime);
                                    }

                                    break;
                                }
                            }
                        
                            // very important: we must mark the task as complete once we're done
                            await taskJim.Tasking.CompleteWorkerTaskAsync(newWorkerTask);

                            // remove from the current tasks list after locking it for thread safety
                            lock (_currentTasksLock)
                                CurrentTasks.RemoveAll(q => q.TaskId == newWorkerTask.Id);

                        }, cancellationTokenSource.Token);

                        CurrentTasks.Add(new TaskTask(mainLoopNewWorkerTask.Id, task, cancellationTokenSource));
                    }
                }
            }
        }

        if (stoppingToken.IsCancellationRequested)
        {
            foreach (var currentTask in CurrentTasks)
            {
                Log.Debug($"ExecuteAsync: Cancelling task {currentTask.TaskId} as worker has been cancelled.");
                currentTask.CancellationTokenSource.Cancel();
            }
        }
    }

    #region private methods

    /// <summary>
    /// Tracks when the last housekeeping run occurred to avoid running too frequently.
    /// </summary>
    private DateTime _lastHousekeepingRun = DateTime.MinValue;

    /// <summary>
    /// Tracks when the last history retention cleanup occurred.
    /// History cleanup runs on a longer interval (every 6 hours) as it only
    /// deals with records that are 90+ days old and doesn't need frequent checks.
    /// </summary>
    private DateTime _lastHistoryCleanupRun = DateTime.MinValue;

    /// <summary>
    /// Performs housekeeping tasks during worker idle time.
    /// Currently includes: orphaned MVO cleanup based on deletion rules.
    /// </summary>
    private async Task PerformHousekeepingAsync(JimApplication jim)
    {
        // Only run housekeeping every 60 seconds to avoid unnecessary database queries
        if ((DateTime.UtcNow - _lastHousekeepingRun).TotalSeconds < 60)
            return;

        _lastHousekeepingRun = DateTime.UtcNow;

        try
        {
            // Get MVOs that are eligible for deletion (grace period has passed)
            var mvosToDelete = await jim.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync(maxResults: 50);

            if (mvosToDelete.Count > 0)
            {
                Log.Information("PerformHousekeepingAsync: Found {Count} MVOs eligible for deletion", mvosToDelete.Count);

                foreach (var mvo in mvosToDelete)
                {
                    try
                    {
                        Log.Information("PerformHousekeepingAsync: Deleting MVO {MvoId} ({DisplayName}) - disconnected at {DisconnectedDate}, rule: {DeletionRule}",
                            mvo.Id, mvo.DisplayName ?? "No display name", mvo.LastConnectorDisconnectedDate, mvo.Type?.DeletionRule);

                        // Evaluate export rules for the MVO deletion (create delete pending exports for provisioned CSOs)
                        // WhenAuthoritativeSourceDisconnected MVOs may still have target CSOs that need delete exports
                        await jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);

                        // Delete the MVO using the initiator info captured when it was marked for deletion
                        // This preserves the audit trail - the original initiator is recorded, not housekeeping
                        await jim.Metaverse.DeleteMetaverseObjectAsync(
                            mvo,
                            mvo.DeletionInitiatedByType,
                            mvo.DeletionInitiatedById,
                            mvo.DeletionInitiatedByName);

                        Log.Information("PerformHousekeepingAsync: Successfully deleted MVO {MvoId}", mvo.Id);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "PerformHousekeepingAsync: Failed to delete MVO {MvoId}", mvo.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PerformHousekeepingAsync: Error during housekeeping");
        }

        // History retention cleanup runs on its own schedule (every 6 hours)
        if ((DateTime.UtcNow - _lastHistoryCleanupRun).TotalHours >= 6)
        {
            _lastHistoryCleanupRun = DateTime.UtcNow;
            await PerformChangeHistoryCleanupAsync(jim);
        }
    }

    /// <summary>
    /// Performs change history and activity cleanup based on retention policy.
    /// Runs as part of housekeeping during worker idle time.
    /// </summary>
    private async Task PerformChangeHistoryCleanupAsync(JimApplication jim)
    {
        try
        {
            // Get retention settings
            var retentionPeriod = await jim.ServiceSettings.GetHistoryRetentionPeriodAsync();
            var batchSize = await jim.ServiceSettings.GetHistoryCleanupBatchSizeAsync();

            var cutoffDate = DateTime.UtcNow - retentionPeriod;

            // Perform cleanup (creates its own Activity for audit)
            var result = await jim.ChangeHistory.DeleteExpiredChangeHistoryAsync(cutoffDate, batchSize);

            // Log results if anything was deleted
            if (result.CsoChangesDeleted > 0 || result.MvoChangesDeleted > 0 || result.ActivitiesDeleted > 0)
            {
                Log.Information("PerformChangeHistoryCleanupAsync: Deleted {CsoCount} CSO changes, {MvoCount} MVO changes, {ActivityCount} activities",
                    result.CsoChangesDeleted, result.MvoChangesDeleted, result.ActivitiesDeleted);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PerformChangeHistoryCleanupAsync: Error during change history cleanup");
        }
    }

    /// <summary>
    /// Completes an activity based on the execution results of its run profile execution items.
    /// Determines whether to mark as complete, complete with warning, or failed based on error counts.
    /// Also calculates and persists summary stats for display in the activity list view.
    /// This method is wrapped in robust error handling to ensure activities are always finalised.
    /// </summary>
    private async Task CompleteActivityBasedOnExecutionResultsAsync(JimApplication jim, Activity activity)
    {
        try
        {
            // Calculate summary stats from RPEIs for activity list display
            CalculateActivitySummaryStats(activity);

            // Note: .All() returns true for empty collections, so we must check for Any() first
            var hasItems = activity.RunProfileExecutionItems.Count > 0;
            var hasErrors = activity.RunProfileExecutionItems.Any(q => q.ErrorType.HasValue && q.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet);
            var allErrors = hasItems && activity.RunProfileExecutionItems.All(q => q.ErrorType.HasValue && q.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet);

            if (allErrors)
            {
                await jim.Activities.FailActivityWithErrorAsync(activity, "All run profile execution items experienced an error. Review the items for more information.");
                Log.Information("CompleteActivityBasedOnExecutionResultsAsync: Activity {ActivityId} failed - all items had errors", activity.Id);
            }
            else if (hasErrors)
            {
                await jim.Activities.CompleteActivityWithWarningAsync(activity);
                Log.Information("CompleteActivityBasedOnExecutionResultsAsync: Activity {ActivityId} completed with warnings", activity.Id);
            }
            else
            {
                await jim.Activities.CompleteActivityAsync(activity);
                Log.Debug("CompleteActivityBasedOnExecutionResultsAsync: Activity {ActivityId} completed successfully", activity.Id);
            }
        }
        catch (Exception ex)
        {
            // If completing the activity fails, try to fail it with the error
            Log.Error(ex, "CompleteActivityBasedOnExecutionResultsAsync: Failed to complete activity {ActivityId}, attempting to mark as failed", activity.Id);
            await SafeFailActivityAsync(jim, activity, ex, "Failed to complete activity after sync run");
        }
    }

    /// <summary>
    /// Calculates aggregate summary stats from Run Profile Execution Items for activity list display.
    /// Creates = Added (import) + Projected (sync) + Provisioned (export)
    /// Updates = Updated (import) + Joined (sync) + Exported (export)
    /// Flows = AttributeFlow (sync only)
    /// Deletes = Deleted (import) + Disconnected (sync) + Deprovisioned (export)
    /// Errors = Any RPEI with an error type set
    /// </summary>
    private static void CalculateActivitySummaryStats(Activity activity)
    {
        var rpeis = activity.RunProfileExecutionItems;

        // Creates: Added (import), Projected (sync), Provisioned (export)
        activity.TotalObjectCreates = rpeis.Count(r =>
            r.ObjectChangeType is ObjectChangeType.Added or ObjectChangeType.Projected or ObjectChangeType.Provisioned);

        // Updates: Updated (import), Joined (sync), Exported (export)
        activity.TotalObjectUpdates = rpeis.Count(r =>
            r.ObjectChangeType is ObjectChangeType.Updated or ObjectChangeType.Joined or ObjectChangeType.Exported);

        // Flows: AttributeFlow, DriftCorrection (sync only) - data flowing through existing connections
        // DriftCorrection is included as it represents corrective attribute changes being staged for export
        activity.TotalObjectFlows = rpeis.Count(r =>
            r.ObjectChangeType is ObjectChangeType.AttributeFlow or ObjectChangeType.DriftCorrection);

        // Deletes: Deleted (import), Disconnected (sync), Deprovisioned (export)
        activity.TotalObjectDeletes = rpeis.Count(r =>
            r.ObjectChangeType is ObjectChangeType.Deleted or ObjectChangeType.Disconnected or ObjectChangeType.Deprovisioned);

        // Errors: Any RPEI with an error
        activity.TotalObjectErrors = rpeis.Count(r =>
            r.ErrorType.HasValue && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet);

        Log.Verbose("CalculateActivitySummaryStats: Activity {ActivityId} - Creates={Creates}, Updates={Updates}, Flows={Flows}, Deletes={Deletes}, Errors={Errors}",
            activity.Id, activity.TotalObjectCreates, activity.TotalObjectUpdates, activity.TotalObjectFlows, activity.TotalObjectDeletes, activity.TotalObjectErrors);
    }

    /// <summary>
    /// Safely attempts to fail an activity with an error. If the primary failure attempt fails,
    /// it will attempt a direct database update as a last resort to ensure the activity is not left in InProgress state.
    /// Activities must never be left in InProgress state as this blocks the integration test scripts and monitoring systems.
    /// </summary>
    private async Task SafeFailActivityAsync(JimApplication jim, Activity activity, Exception originalException, string context)
    {
        Log.Error(originalException, "SafeFailActivityAsync: {Context} for activity {ActivityId}", context, activity.Id);

        try
        {
            // First attempt: Use the normal activity failure method
            await jim.Activities.FailActivityWithErrorAsync(activity, originalException);
            Log.Information("SafeFailActivityAsync: Successfully marked activity {ActivityId} as failed", activity.Id);
        }
        catch (Exception failEx)
        {
            Log.Error(failEx, "SafeFailActivityAsync: Failed to mark activity {ActivityId} as failed via normal method, attempting direct update", activity.Id);

            try
            {
                // Second attempt: Try to update the activity status directly
                // This handles EF Core tracking issues or DbContext disposal problems
                activity.Status = ActivityStatus.FailedWithError;
                activity.ErrorMessage = $"{context}: {originalException.Message}";
                activity.ErrorStackTrace = originalException.StackTrace;
                activity.ExecutionTime = DateTime.UtcNow - activity.Executed;
                activity.TotalActivityTime = DateTime.UtcNow - activity.Created;

                await jim.Repository.Activity.UpdateActivityAsync(activity);
                Log.Warning("SafeFailActivityAsync: Marked activity {ActivityId} as failed via direct repository update", activity.Id);
            }
            catch (Exception directEx)
            {
                Log.Fatal(directEx, "SafeFailActivityAsync: CRITICAL - Could not update activity {ActivityId} status. Activity will remain stuck in InProgress state. " +
                    "Original error: {OriginalError}. Failure error: {FailError}",
                    activity.Id, originalException.Message, failEx.Message);

                // Last resort: Create a new DbContext and try to update the activity
                try
                {
                    using var emergencyContext = new JimDbContext();
                    var emergencyJim = new JimApplication(new PostgresDataRepository(emergencyContext));
                    var freshActivity = await emergencyJim.Activities.GetActivityAsync(activity.Id);
                    if (freshActivity != null && freshActivity.Status == ActivityStatus.InProgress)
                    {
                        freshActivity.Status = ActivityStatus.FailedWithError;
                        freshActivity.ErrorMessage = $"EMERGENCY UPDATE: {context}: {originalException.Message}";
                        freshActivity.ExecutionTime = DateTime.UtcNow - freshActivity.Executed;
                        freshActivity.TotalActivityTime = DateTime.UtcNow - freshActivity.Created;
                        await emergencyJim.Activities.UpdateActivityAsync(freshActivity);
                        Log.Warning("SafeFailActivityAsync: EMERGENCY - Marked activity {ActivityId} as failed via emergency DbContext", activity.Id);
                    }
                }
                catch (Exception emergencyEx)
                {
                    Log.Fatal(emergencyEx, "SafeFailActivityAsync: EMERGENCY UPDATE FAILED for activity {ActivityId}. Manual database intervention required.", activity.Id);
                }
            }
        }
    }

    private static void InitialiseLogging()
    {
        var loggerConfiguration = new LoggerConfiguration();
        var loggingMinimumLevel = Environment.GetEnvironmentVariable(Constants.Config.LogLevel);
        var loggingPath = Environment.GetEnvironmentVariable(Constants.Config.LogPath);

        if (loggingMinimumLevel == null)
            throw new ApplicationException($"{Constants.Config.LogLevel} environment variable not found. Cannot continue");
        if (loggingPath == null)
            throw new ApplicationException($"{Constants.Config.LogPath} environment variable not found. Cannot continue");

        switch (loggingMinimumLevel)
        {
            case "Verbose":
                loggerConfiguration.MinimumLevel.Verbose();
                break;
            case "Debug":
                loggerConfiguration.MinimumLevel.Debug();
                break;
            case "Information":
                loggerConfiguration.MinimumLevel.Information();
                break;
            case "Warning":
                loggerConfiguration.MinimumLevel.Warning();
                break;
            case "Error":
                loggerConfiguration.MinimumLevel.Error();
                break;
            case "Fatal":
                loggerConfiguration.MinimumLevel.Fatal();
                break;
        }

        loggerConfiguration.Enrich.FromLogContext();
        loggerConfiguration.WriteTo.File(
            formatter: new RenderedCompactJsonFormatter(),
            path: Path.Combine(loggingPath, "jim.worker..log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 31,  // Keep 31 days of logs for integration test analysis
            fileSizeLimitBytes: 500 * 1024 * 1024,  // 500MB per file max
            rollOnFileSizeLimit: true);
        loggerConfiguration.WriteTo.Console();
        Log.Logger = loggerConfiguration.CreateLogger();
    }
    #endregion
}

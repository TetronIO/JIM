using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Connectors.LDAP;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.PostgresData;
using JIM.Worker.Processors;
using Serilog;
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
        Log.Information("Starting JIM.Worker...");

        // as JIM.Worker is the first JimApplication client to start, it's responsible for ensuring the database is initialised.
        // other JimApplication clients will need to check if the app is ready before completing their initialisation.
        // JimApplication instances are ephemeral and should be disposed as soon as a request/batch of work is complete (for database tracking reasons).
        var mainLoopJim = new JimApplication(new PostgresDataRepository(new JimDbContext()));
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
                            var taskJim = new JimApplication(new PostgresDataRepository(new JimDbContext()));

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
                                            await taskJim.DataGeneration.ExecuteTemplateAsync(dataGenTemplateServiceTask.TemplateId, cancellationTokenSource.Token);
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
                                    var initiatedByDisplay = newWorkerTask.InitiatedBy?.DisplayName ?? newWorkerTask.InitiatedByName ?? "Unknown";
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
                                                            var syncImportTaskProcessor = new SyncImportTaskProcessor(taskJim, connector, connectedSystem, runProfile, newWorkerTask.InitiatedBy, newWorkerTask.Activity, cancellationTokenSource);
                                                            await syncImportTaskProcessor.PerformFullImportAsync();
                                                            break;
                                                        }
                                                        case ConnectedSystemRunType.DeltaImport:
                                                        case ConnectedSystemRunType.FullSynchronisation:
                                                            var syncFullSyncTaskProcessor = new SyncFullSyncTaskProcessor(taskJim, connectedSystem, runProfile, newWorkerTask.Activity, cancellationTokenSource);
                                                            await syncFullSyncTaskProcessor.PerformFullSyncAsync();
                                                            break;
                                                        case ConnectedSystemRunType.Export:
                                                        {
                                                            var syncExportTaskProcessor = new SyncExportTaskProcessor(taskJim, connector, connectedSystem, runProfile, newWorkerTask.Activity, cancellationTokenSource);
                                                            await syncExportTaskProcessor.PerformExportAsync();
                                                            break;
                                                        }
                                                        case ConnectedSystemRunType.DeltaSynchronisation:
                                                            Log.Error($"ExecuteAsync: Not supporting run type: {runProfile.RunType} yet.");
                                                            break;
                                                        default:
                                                            Log.Error($"ExecuteAsync: Unsupported run type: {runProfile.RunType}");
                                                            break;
                                                    }

                                                    // task completed. determine final status, depending on how the run profile execution went
                                                    if (newWorkerTask.Activity.RunProfileExecutionItems.All(q => q.ErrorType.HasValue && q.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet))
                                                        await taskJim.Activities.FailActivityWithErrorAsync(newWorkerTask.Activity, "All run profile execution items experienced an error. Review the items for more information.");
                                                    else if (newWorkerTask.Activity.RunProfileExecutionItems.Any(q => q.ErrorType.HasValue && q.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet))
                                                        await taskJim.Activities.CompleteActivityWithWarningAsync(newWorkerTask.Activity);
                                                    else
                                                        await taskJim.Activities.CompleteActivityAsync(newWorkerTask.Activity);
                                                }
                                                catch (Exception ex)
                                                {
                                                    // we log unhandled exceptions to the history to enable sync operators/admins to be able to easily view
                                                    // issues with connectors through JIM, rather than an admin having to dig through server logs.
                                                    await taskJim.Activities.FailActivityWithErrorAsync(newWorkerTask.Activity, ex);
                                                    Log.Error(ex, "ExecuteAsync: Unhandled exception whilst executing sync run.");
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
                                    if (clearConnectedSystemObjectsTask.InitiatedBy == null)
                                    {
                                        Log.Error($"ExecuteAsync: ClearConnectedSystemObjectsTask {clearConnectedSystemObjectsTask.Id} is missing an InitiatedBy value. Cannot continue processing worker task.");
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
                                            await taskJim.ConnectedSystems.ClearConnectedSystemObjectsAsync(clearConnectedSystemObjectsTask.ConnectedSystemId);

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
        loggerConfiguration.WriteTo.File(Path.Combine(loggingPath, "jim.worker..log"), rollingInterval: RollingInterval.Day);
        loggerConfiguration.WriteTo.Console();
        Log.Logger = loggerConfiguration.CreateLogger();
    }
    #endregion
}

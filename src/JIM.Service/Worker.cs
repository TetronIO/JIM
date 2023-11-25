using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.LDAP;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.PostgresData;
using JIM.Service.Processors;
using Serilog;

namespace JIM.Service
{
    // **************************************************************************************
    // Junctional Identity Manager - Sync Service
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
    // LOGGING_LEVEL
    // LOGGING_PATH
    // DB_HOSTNAME - validated by data layer
    // DB_NAME - validated by data layer
    // DB_USERNAME - validated by data layer
    // DB_PASSWORD - validated by data layer
    //
    // Design Pattern:
    // https://docs.microsoft.com/en-us/dotnet/core/extensions/workers
    //
    // **************************************************************************************

    public class Worker : BackgroundService
    {
        /// <summary>
        /// The service tasks currently being executed.
        /// </summary>
        private List<TaskTask> CurrentTasks { get; set; } = new List<TaskTask>();
        private readonly object _currentTasksLock = new();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            InitialiseLogging();
            Log.Information("Starting JIM.Service...");

            // as JIM.Service is the first JimApplication client to start, it's responsible for ensuring the database is intialised.
            // other JimAppication clients will need to check if the app is ready before completing their initialisation.
            // JimApplication instances are ephemeral and should be disposed as soon as a request/batch of work is complete (for database tracking reasons).
            var mainLoopJim = new JimApplication(new PostgresDataRepository());
            await mainLoopJim.InitialiseDatabaseAsync();

            // first of all check if there's any tasks that have been requested for cancellation but have not yet been processed.
            // this scenario is expected to be for when the worker unexpectedly quits and can't execute cancellations.
            foreach (var taskToCancel in await mainLoopJim.Tasking.GetServiceTasksThatNeedCancellingAsync())
                await mainLoopJim.Tasking.CancelServiceTaskAsync(taskToCancel);

            // DEV: Unsupported scenario:
            // todo: job is being processed in database but is not in the current tasks, i.e. it's no longer being processed. clear it out

            while (!stoppingToken.IsCancellationRequested)
            {
                // if processing no tasks:
                //      get the next batch of parallel tasks and execute them all at once or the next sequential task and execute that
                // if processing tasks:
                //      get the service tasks for those being processed
                //      foreach: if the status is cancellation requested, then cancel the task

                if (CurrentTasks.Count > 0)
                {
                    // check the database to see if we need to cancel any tasks we're currently processing...
                    var serviceTaskIds = CurrentTasks.Select(t => t.TaskId).ToArray();
                    var serviceTasksToCancel = await mainLoopJim.Tasking.GetServiceTasksThatNeedCancellingAsync(serviceTaskIds);
                    foreach (var serviceTaskToCancel in serviceTasksToCancel)
                    {
                        var taskTask = CurrentTasks.SingleOrDefault(t => t.TaskId == serviceTaskToCancel.Id);
                        if (taskTask != null)
                        {
                            Log.Information($"ExecuteAsync: Cancelling task {serviceTaskToCancel.Id}...");
                            taskTask.CancellationTokenSource.Cancel();
                            await mainLoopJim.Tasking.CancelServiceTaskAsync(serviceTaskToCancel);
                            CurrentTasks.Remove(taskTask);
                        }
                        else
                        {
                            Log.Debug($"ExecuteAsync: No need to cancel task id {serviceTaskToCancel.Id} as it seems to have finished processing.");
                        }
                    }
                }
                else
                {
                    // look for new tasks to process...
                    var newServiceTasksToProcess = await mainLoopJim.Tasking.GetNextServiceTasksToProcessAsync();
                    if (newServiceTasksToProcess.Count == 0)
                    {
                        Log.Debug("ExecuteAsync: No tasks on queue. Sleeping...");
                        await Task.Delay(2000, stoppingToken);
                    }
                    else
                    {
                        foreach (var newServiceTask in newServiceTasksToProcess)
                        {
                            var cancellationTokenSource = new CancellationTokenSource();
                            var task = Task.Run(async () =>
                            {
                                var taskJim = new JimApplication(new PostgresDataRepository());
                                
                                // re-retrieve the initiated-by object using this new task jim instance, otherwise referncing an object from another jim instance
                                // will result in entityframework thinking the object is new and tries to insert a duplicate into the database.
                                // obviously this is sub-optimal. is there a better way? attach the object to the new jim instance db context?
                                MetaverseObject? initiatedBy = null;
                                if (newServiceTask.InitiatedBy != null)
                                    initiatedBy = await taskJim.Metaverse.GetMetaverseObjectAsync(newServiceTask.InitiatedBy.Id);

                                // mark the activity as being executed, i.e. when the work actually started
                                // retrieve the activity from this instance of JIM to avoid EF errors..
                                var activity = await taskJim.Activities.GetActivityAsync(newServiceTask.Activity.Id) ?? 
                                    throw new InvalidDataException($"ExecuteAsync: Activity id {newServiceTask.Activity.Id} could not be retrieved. This should not be possible.");

                                activity.Executed = DateTime.UtcNow;
                                await taskJim.Activities.UpdateActivityAsync(activity);
                                newServiceTask.Activity = activity; // re-associating for consistency and so subsequent uses of this activity on this JIM instance do not fail

                                if (newServiceTask is DataGenerationTemplateServiceTask dataGenTemplateServiceTask)
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
                                            await taskJim.Activities.CompleteActivityAsync(newServiceTask.Activity);
                                        }
                                        catch (Exception ex)
                                        {
                                            await taskJim.Activities.FailActivityWithErrorAsync(newServiceTask.Activity, ex);
                                            Log.Error(ex, "ExecuteAsync: Unhandled exception whilst executing data generation template: " + dataGenTemplateServiceTask.TemplateId);
                                        }
                                        finally
                                        {
                                            Log.Information($"ExecuteAsync: Completed data generation template ({dataGenTemplateServiceTask.TemplateId}) execution in {newServiceTask.Activity.ExecutionTime}.");
                                        }
                                    }
                                }
                                else if (newServiceTask is SynchronisationServiceTask syncServiceTask)
                                {
                                    Log.Information("ExecuteAsync: SynchronisationServiceTask received for run profile id: " + syncServiceTask.ConnectedSystemRunProfileId);
                                    if (syncServiceTask.InitiatedBy == null)
                                    {
                                        Log.Error("ExecuteAsync: syncServiceTask.InitiatedBy was null. Cannot execute sync task");
                                    }
                                    else
                                    {
                                        var connectedSystem = await taskJim.ConnectedSystems.GetConnectedSystemAsync(syncServiceTask.ConnectedSystemId);
                                        if (connectedSystem != null)
                                        {
                                            // work out what connector we need to use
                                            // todo: run through built-in connectors first, then do a lookup for user-supplied connectors
                                            IConnector connector;
                                            if (connectedSystem.ConnectorDefinition.Name == ConnectorConstants.LdapConnectorName)
                                                connector = new LdapConnector();
                                            else
                                                throw new NotSupportedException($"{connectedSystem.ConnectorDefinition.Name} connector not yet supported for service processing.");

                                            // work out what type of run profile we're being asked to run
                                            var runProfile = connectedSystem.RunProfiles?.SingleOrDefault(rp => rp.Id == syncServiceTask.ConnectedSystemRunProfileId);
                                            if (runProfile != null)
                                            {
                                                try
                                                {
                                                    // hand processing of the sync task to a dedicated task processor to keep the worker abstract of specific tasks
                                                    if (runProfile.RunType == ConnectedSystemRunType.FullImport)
                                                    {
                                                        var synchronisationImportTaskProcessor = new SynchronisationImportTaskProcessor(taskJim, connector, connectedSystem, runProfile, syncServiceTask.InitiatedBy, newServiceTask.Activity, cancellationTokenSource);
                                                        await synchronisationImportTaskProcessor.PerformFullImportAsync();
                                                    }
                                                    else if (runProfile.RunType == ConnectedSystemRunType.DeltaImport)
                                                    {
                                                        Log.Error($"ExecuteAsync: Not supporting run type: {runProfile.RunType} yet.");
                                                    }
                                                    else if (runProfile.RunType == ConnectedSystemRunType.Export)
                                                    {
                                                        Log.Error($"ExecuteAsync: Not supporting run type: {runProfile.RunType} yet.");
                                                    }
                                                    else if (runProfile.RunType == ConnectedSystemRunType.FullSynchronisation)
                                                    {
                                                        Log.Error($"ExecuteAsync: Not supporting run type: {runProfile.RunType} yet.");
                                                    }
                                                    else if (runProfile.RunType == ConnectedSystemRunType.DeltaSynchronisation)
                                                    {
                                                        Log.Error($"ExecuteAsync: Not supporting run type: {runProfile.RunType} yet.");
                                                    }
                                                    else
                                                    {
                                                        Log.Error($"ExecuteAsync: Unsupported run type: {runProfile.RunType}");
                                                    }

                                                    // task completed successfully
                                                    await taskJim.Activities.CompleteActivityAsync(newServiceTask.Activity);
                                                }
                                                catch (Exception ex)
                                                {
                                                    // we log unhandled exceptions to the history to enable sync operators/admins to be able to easily view issues with connectors through JIM,
                                                    // rather than an admin having to dig through server logs.
                                                    await taskJim.Activities.FailActivityWithErrorAsync(newServiceTask.Activity, ex);
                                                    Log.Error(ex, "ExecuteAsync: Unhandled exception whilst executing sync run.");
                                                }
                                                finally
                                                {
                                                    // record how long the sync run took, whether it was successful, or not.
                                                    Log.Information($"ExecuteAsync: Completed processing of {newServiceTask.Activity.TargetName} sync run in {newServiceTask.Activity.ExecutionTime}.");
                                                }
                                            }
                                            else
                                            {
                                                Log.Warning($"ExecuteAsync: sync task specifies run profile id {syncServiceTask.ConnectedSystemRunProfileId} but no such profile found on connected system id {syncServiceTask.ConnectedSystemId}.");
                                            }
                                        }
                                        else
                                        {
                                            Log.Warning($"ExecuteAsync: sync task specifies connected system id {syncServiceTask.ConnectedSystemId} but no such connected system found.");
                                        }
                                    }
                                }
                                else if (newServiceTask is ClearConnectedSystemObjectsTask clearConnectedSystemObjectsTask)
                                {
                                    Log.Information("ExecuteAsync: ClearConnectedSystemObjectsTask received for connected system id: " + clearConnectedSystemObjectsTask.ConnectedSystemId);
                                    if (clearConnectedSystemObjectsTask.InitiatedBy == null)
                                    {
                                        Log.Error($"ExecuteAsync: ClearConnectedSystemObjectsTask {clearConnectedSystemObjectsTask.Id} is missing an InitiatedBy value. Cannot continue processing service task.");
                                    }
                                    else
                                    {
                                        // we need a little more information on the connected systmem, so retrieve it
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
                                            await taskJim.Activities.CompleteActivityAsync(newServiceTask.Activity);
                                        }
                                        catch (Exception ex)
                                        {
                                            await taskJim.Activities.FailActivityWithErrorAsync(newServiceTask.Activity, ex);
                                            Log.Error(ex, "ExecuteAsync: Unhandled exception whilst executing clear connected system task.");
                                        }
                                        finally
                                        {
                                            // record how long the sync run took, whether it was successful, or not.
                                            Log.Information($"ExecuteAsync: Completed clearing the connected system ({clearConnectedSystemObjectsTask.ConnectedSystemId}) in {newServiceTask.Activity.ExecutionTime}.");
                                        }
                                    }
                                }
                        
                                // very important: we must mark the task as complete once we're done
                                await taskJim.Tasking.CompleteServiceTaskAsync(newServiceTask);

                                // remove from the current tasks list after locking it for thread safety
                                lock (_currentTasksLock)
                                    CurrentTasks.RemoveAll(q => q.TaskId == newServiceTask.Id);

                            }, cancellationTokenSource.Token);

                            CurrentTasks.Add(new TaskTask(newServiceTask.Id, task, cancellationTokenSource));
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
            var loggingMinimumLevel = Environment.GetEnvironmentVariable("LOGGING_LEVEL");
            var loggingPath = Environment.GetEnvironmentVariable("LOGGING_PATH");

            if (loggingMinimumLevel == null)
                throw new ApplicationException("LOGGING_LEVEL environment variable not found. Cannot continue");
            if (loggingPath == null)
                throw new ApplicationException("LOGGING_PATH environment variable not found. Cannot continue");

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
            loggerConfiguration.WriteTo.File(Path.Combine(loggingPath, "jim.service..log"), rollingInterval: RollingInterval.Day);
            loggerConfiguration.WriteTo.Console();
            Log.Logger = loggerConfiguration.CreateLogger();
        }
        #endregion
    }
}

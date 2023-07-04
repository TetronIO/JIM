using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.LDAP;
using JIM.Models.History;
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
            // this scenario is expected to be for when the worker unexpectedly quits and doesn't complete cancellation.
            foreach (var taskToCancel in await mainLoopJim.Tasking.GetServiceTasksThatNeedCancellingAsync())
                await mainLoopJim.Tasking.DeleteServiceTaskAsync(taskToCancel);

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
                    var serviceTaskIds = CurrentTasks.Select(q => q.TaskId).ToArray();
                    var serviceTasksToCancel = await mainLoopJim.Tasking.GetServiceTasksThatNeedCancellingAsync(serviceTaskIds);
                    foreach (var serviceTaskToCancel in serviceTasksToCancel)
                    {
                        var taskTask = CurrentTasks.SingleOrDefault(q => q.TaskId == serviceTaskToCancel.Id);
                        if (taskTask != null)
                        {
                            Log.Information($"ExecuteAsync: Cancelling task {serviceTaskToCancel.Id}...");
                            taskTask.CancellationTokenSource.Cancel();
                            await mainLoopJim.Tasking.DeleteServiceTaskAsync(serviceTaskToCancel);
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
                                if (newServiceTask is DataGenerationTemplateServiceTask dataGenTemplateServiceTask)
                                {
                                    Log.Information("ExecuteAsync: DataGenerationTemplateServiceTask received for template id: " + dataGenTemplateServiceTask.TemplateId);
                                    await taskJim.DataGeneration.ExecuteTemplateAsync(dataGenTemplateServiceTask.TemplateId, cancellationTokenSource.Token);
                                }
                                else if (newServiceTask is SynchronisationServiceTask syncServiceTask)
                                {
                                    Log.Information("ExecuteAsync: SynchronisationServiceTask received for run profile id: " + syncServiceTask.ConnectedSystemRunProfileId);

                                    var connectedSystem = await taskJim.ConnectedSystems.GetConnectedSystemAsync(syncServiceTask.ConnectedSystemId);
                                    if (connectedSystem != null)
                                    {
                                        // work out what connector we need to use
                                        // todo: run through built-in connectors first, then do a lookup for user-supplied connectors
                                        IConnector connector;
                                        if (connectedSystem.ConnectorDefinition.Name == ConnectorConstants.LdapConnectorName)
                                            connector = new LdapConnector();
                                        else
                                            throw new NotSupportedException($"{connectedSystem.ConnectorDefinition.Name} connector not yet supported for service processing");

                                        // work out what type of run profile we're being asked to run
                                        var runProfile = connectedSystem.RunProfiles?.SingleOrDefault(rp => rp.Id == syncServiceTask.ConnectedSystemRunProfileId);
                                        if (runProfile != null)
                                        {
                                            // create the history item for this run profile execution, then pass it in to the processor for iterative updates for each item processed.
                                            // we copy some run profile information as run profiles can be user-deleted, but we would want to retain core run profile information for audit purposes.
                                            var synchronisationRunHistoryDetail = new SyncRunHistoryDetail
                                            {
                                                RunProfile = runProfile,
                                                RunProfileName = runProfile.Name,
                                                RunType = runProfile.RunType,
                                                ConnectedSystem = connectedSystem,
                                                ConnectedSystemName = connectedSystem.Name                                                
                                            };
                                            await taskJim.History.CreateSyncRunHistoryDetailAsync(synchronisationRunHistoryDetail);

                                            try
                                            {
                                                // hand processing of the sync task to a dedicated task processor to keep the worker abstract of specific tasks
                                                if (runProfile.RunType == ConnectedSystemRunType.FullImport)
                                                {
                                                    var synchronisationImportTaskProcessor = new SynchronisationImportTaskProcessor(taskJim, connector, connectedSystem, runProfile, synchronisationRunHistoryDetail, cancellationTokenSource);
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
                                            }
                                            catch (Exception ex) 
                                            {
                                                // we log unhandled exceptions to the history to enable sync operators/admins to be able to easily view issues with connectors through JIM,
                                                // rather than an admin having to dig through server logs.
                                                synchronisationRunHistoryDetail.ErrorMessage = ex.Message;
                                                synchronisationRunHistoryDetail.ErrorStackTrace = ex.StackTrace;
                                                await taskJim.History.UpdateSyncRunHistoryDetailAsync(synchronisationRunHistoryDetail);
                                                Log.Error(ex, "ExecuteAsync: Unhandled exception whilst executing sync run.");
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

                                // very important: we must delete the task once it's completed so we know it's complete
                                await taskJim.Tasking.DeleteServiceTaskAsync(newServiceTask);

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
                foreach (var currentTask in CurrentTasks)
                {
                    Log.Debug($"ExecuteAsync: Cancelling task {currentTask.TaskId} as worker has been cancelled.");
                    currentTask.CancellationTokenSource.Cancel();
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

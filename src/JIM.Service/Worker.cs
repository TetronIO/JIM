using JIM.Application;
using JIM.PostgresData;
using Serilog;
using JIM.Models.Tasking;

namespace JIM.Service
{
    // **************************************************************************************
    // Junctional Identity Manager - Sync Service
    //
    // Needs to:
    // - Loop until asked to close down
    // - Check the database for manual synchronisation tasks
    // - check the database for synchronisation schedule tasks
    // - check the database for data generation template tasks
    // - create tasks for the jobs, keeping track of cancellation tokens
    // - execute the tasks
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
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            InitialiseLogging();
            Log.Information("Starting JIM.Service");

            // as JIM.Service is the initial JimApplication client, it's responsible for seeing the database is intialised.
            // other JimAppication clients will need to check if the app is ready before completing their initialisation.
            // JimApplication instances are ephemeral and should be disposed as soon as a unit of work is complete (for database tracking reasons).
            var outerJim = new JimApplication(new PostgresDataRepository());
            await outerJim.InitialiseDatabaseAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                // get the oldest ready task
                var task = await outerJim.Tasking.GetNextServiceTaskAsync();
                if (task == null)
                {
                    Log.Debug("ExecuteAsync: No task on queue. Sleeping...");
                    await Task.Delay(4000, stoppingToken);
                }
                else
                {
                    if (task is DataGenerationTemplateServiceTask dataGenerationTemplateServiceTask)
                    {
                        Log.Information("ExecuteAsync: DataGenerationTemplateServiceTask received for template id: " + dataGenerationTemplateServiceTask.TemplateId);
                        await outerJim.DataGeneration.ExecuteTemplateAsync(dataGenerationTemplateServiceTask.TemplateId);
                    }

                    // very importamt: we must delete the task once it's completed so we know it's complete
                    await outerJim.Tasking.DeleteServiceTaskAsync(task);
                }
            }
        }

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
    }
}

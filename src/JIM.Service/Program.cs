// See https://aka.ms/new-console-template for more information

// **************************************************************************************
// Junctional Identity Manager - Sync Service
//
// Needs to:
// - Loop until asked to close down
// - Check the database for manual synchronisation jobs
// - check the database for synchronisation schedule jobs
// - create tasks for the jobs, keeping track of cancellation tokens
// - execute the tasks
//
// The sync tasks will need to perform:
// - Import data from connected systems via connectors
// - Reconcile updates to objects internally, i.e. connector space and metaverse objects
// - Export data to connected systems via connectors
//
// Required environment variables:
// - LOGGING_LEVEL
// - LOGGING_PATH
//
// **************************************************************************************

using Serilog;
InitialiseLogging();
Log.Information("JIM.Service - Hello World!");

try
{
    while (true)
    {
        Log.Information("JIM.Service - Doing some work!");
        Thread.Sleep(2000);
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled exception!");
}
finally
{
    Log.CloseAndFlush();
}

static void InitialiseLogging()
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
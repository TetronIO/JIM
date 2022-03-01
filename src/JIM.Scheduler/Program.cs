// See https://aka.ms/new-console-template for more information

// **************************************************************************************
// Junctional Identity Manager - Scheduler Service
//
// Needs to:
// - Run synchronisation schedules and other jobs on chronological schedules stored in
// the database.
//
// Required environment variables:
// - LOGGING_LEVEL
// - LOGGING_PATH
//
// **************************************************************************************

using Serilog;
InitialiseLogging();
Log.Information("Starting JIM.Scheduler");

try
{
    while (true)
    {
        Log.Information("JIM.Scheduler - Doing some work!");
        Thread.Sleep(4000);
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
    loggerConfiguration.WriteTo.File(Path.Combine(loggingPath, "jim.scheduler..log"), rollingInterval: RollingInterval.Day);
    loggerConfiguration.WriteTo.Console();
    Log.Logger = loggerConfiguration.CreateLogger();
}
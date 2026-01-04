// See https://aka.ms/new-console-template for more information

// **************************************************************************************
// Junctional Identity Manager - Scheduler Service
//
// Needs to:
// - Run synchronisation schedules and other jobs on chronological schedules stored in
// the database.
//
// Required environment variables:
// - JIM_LOG_LEVEL
// - JIM_LOG_PATH
//
// **************************************************************************************

using JIM.Models.Core;
using Serilog;
using Serilog.Formatting.Compact;
using System.Threading.Tasks;
InitialiseLogging();
Log.Information("Starting JIM.Scheduler");

try
{
    while (true)
    {
        Log.Information("JIM.Scheduler - Doing some work!");
        await Task.Delay(4000);
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
        formatter: new CompactJsonFormatter(),
        path: Path.Combine(loggingPath, "jim.scheduler..log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31,  // Keep 31 days of logs for integration test analysis
        fileSizeLimitBytes: 500 * 1024 * 1024,  // 500MB per file max
        rollOnFileSizeLimit: true);
    loggerConfiguration.WriteTo.Console();
    Log.Logger = loggerConfiguration.CreateLogger();
}
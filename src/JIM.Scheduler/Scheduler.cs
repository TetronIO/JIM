using JIM.Application;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using JIM.PostgresData;
using Serilog;
using Serilog.Formatting.Compact;

namespace JIM.Scheduler;

// **************************************************************************************
// Junctional Identity Manager - Scheduler Service
//
// Responsibilities:
// - Poll for schedules that are due to run (based on cron expressions)
// - Create ScheduleExecution records when a schedule is triggered
// - Queue the first step(s) of a schedule as WorkerTask(s)
// - Monitor for completed WorkerTasks that are part of a ScheduleExecution
// - Queue subsequent steps when previous steps complete
// - Handle parallel step grouping (wait for all parallel steps before proceeding)
// - Prevent schedule overlap (don't start if previous execution still running)
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

public class Scheduler : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        InitialiseLogging();

        Log.Information("Starting JIM.Scheduler...");

        // Create credential protection service for encrypting/decrypting secrets
        // This uses the shared key storage to ensure consistency with JIM.Web and JIM.Worker
        var credentialProtection = new CredentialProtectionService(DataProtectionHelper.CreateProvider());

        // Wait for the database to be ready (JIM.Worker handles initial migration)
        Log.Information("Waiting for database to be ready...");
        var databaseReady = false;
        while (!databaseReady && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var checkJim = new JimApplication(new PostgresDataRepository(new JimDbContext()));
                // Simple check - if we can create a JimApplication, the database is accessible
                databaseReady = true;
                Log.Information("Database is ready.");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Database not yet ready, waiting...");
                await Task.Delay(2000, stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Create a fresh JimApplication instance for each polling cycle
                // to avoid EF context caching issues
                using var jim = new JimApplication(new PostgresDataRepository(new JimDbContext()));
                jim.CredentialProtection = credentialProtection;

                // Step 1: Update next run times for cron-based schedules
                await jim.Scheduler.UpdateNextRunTimesAsync();

                // Step 2: Check for and start due schedules
                await ProcessDueSchedulesAsync(jim);

                // Step 3: Safety net - recover stuck executions where the worker completed a task
                // but crashed before TryAdvanceScheduleExecutionAsync could run
                await RecoverStuckExecutionsAsync(jim);

                // Step 4: Crash recovery safety net - detect and recover stale worker tasks
                // that the worker may have abandoned due to a crash or restart
                await RecoverStaleWorkerTasksAsync(jim);

                Log.Debug("Scheduler polling cycle complete.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during scheduler polling cycle");
            }

            // Poll every 30 seconds (configurable in future)
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }

        Log.Information("JIM.Scheduler shutting down...");
    }

    /// <summary>
    /// Checks for schedules that are due to run and starts their execution.
    /// </summary>
    private static async Task ProcessDueSchedulesAsync(JimApplication jim)
    {
        var dueSchedules = await jim.Scheduler.GetDueSchedulesAsync();

        foreach (var schedule in dueSchedules)
        {
            try
            {
                // Check if this schedule already has an active execution (prevent overlap)
                var activeExecutions = await jim.Scheduler.GetActiveExecutionsAsync();
                var hasActiveExecution = activeExecutions.Any(e => e.ScheduleId == schedule.Id);

                if (hasActiveExecution)
                {
                    Log.Warning("ProcessDueSchedulesAsync: Schedule {ScheduleId} ({ScheduleName}) is due but already has an active execution. Skipping.",
                        schedule.Id, schedule.Name);
                    continue;
                }

                Log.Information("ProcessDueSchedulesAsync: Starting execution of due schedule {ScheduleId} ({ScheduleName})",
                    schedule.Id, schedule.Name);

                // Start the schedule execution - initiated by System for cron-triggered schedules
                await jim.Scheduler.StartScheduleExecutionAsync(
                    schedule,
                    Models.Activities.ActivityInitiatorType.System,
                    null,
                    "Scheduler Service");

                // Calculate and set the next run time after starting
                var nextRunTime = jim.Scheduler.CalculateNextRunTime(schedule);
                if (nextRunTime.HasValue)
                {
                    schedule.NextRunTime = nextRunTime.Value;
                    await jim.Repository.Scheduling.UpdateScheduleAsync(schedule);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ProcessDueSchedulesAsync: Failed to start execution for schedule {ScheduleId} ({ScheduleName})",
                    schedule.Id, schedule.Name);
            }
        }
    }

    /// <summary>
    /// Safety net for schedule execution advancement. Normally the worker drives step transitions
    /// via TryAdvanceScheduleExecutionAsync after completing each task. This method catches the
    /// edge case where the worker crashes after deleting a completed task but before the advancement
    /// logic runs. It detects InProgress executions that have no Queued or Processing tasks and
    /// re-runs the advancement logic.
    /// </summary>
    private static async Task RecoverStuckExecutionsAsync(JimApplication jim)
    {
        try
        {
            var activeExecutions = await jim.Scheduler.GetActiveExecutionsAsync();

            foreach (var execution in activeExecutions)
            {
                try
                {
                    if (execution.Status != ScheduleExecutionStatus.InProgress)
                        continue;

                    // Check if there are any active (Queued/Processing) tasks for this execution
                    var allTasks = await jim.Repository.Tasking.GetWorkerTasksByScheduleExecutionAsync(execution.Id);
                    var hasActiveTasks = allTasks.Any(t =>
                        t.Status == WorkerTaskStatus.Queued || t.Status == WorkerTaskStatus.Processing);

                    if (hasActiveTasks)
                        continue; // Normal operation — worker is handling it

                    // No active tasks. Check if there are WaitingForPreviousStep tasks that should have been advanced.
                    var hasWaitingTasks = allTasks.Any(t => t.Status == WorkerTaskStatus.WaitingForPreviousStep);

                    if (hasWaitingTasks)
                    {
                        // Execution has waiting tasks but no active tasks — worker likely crashed
                        // after completing a step but before TryAdvanceScheduleExecutionAsync ran.
                        // Use CheckAndAdvanceExecutionAsync as the recovery mechanism.
                        Log.Warning("RecoverStuckExecutionsAsync: Execution {ExecutionId} for schedule {ScheduleName} has no active tasks but {WaitingCount} waiting tasks. Running safety-net advancement.",
                            execution.Id, execution.ScheduleName, allTasks.Count(t => t.Status == WorkerTaskStatus.WaitingForPreviousStep));

                        await jim.Scheduler.CheckAndAdvanceExecutionAsync(execution);
                    }
                    else if (allTasks.Count == 0)
                    {
                        // No tasks at all — the execution should have been marked complete.
                        // Use CheckAndAdvanceExecutionAsync which handles this case.
                        Log.Warning("RecoverStuckExecutionsAsync: Execution {ExecutionId} for schedule {ScheduleName} has no tasks at all. Running safety-net completion.",
                            execution.Id, execution.ScheduleName);

                        await jim.Scheduler.CheckAndAdvanceExecutionAsync(execution);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "RecoverStuckExecutionsAsync: Error processing execution {ExecutionId}", execution.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RecoverStuckExecutionsAsync: Error during stuck execution recovery");
        }
    }

    /// <summary>
    /// Safety net for crash recovery: detects worker tasks that have been in Processing status
    /// longer than the stale task timeout without a heartbeat update. This handles the case where
    /// the worker crashes and hasn't restarted yet.
    /// </summary>
    private static async Task RecoverStaleWorkerTasksAsync(JimApplication jim)
    {
        try
        {
            var staleTimeout = await jim.ServiceSettings.GetStaleTaskTimeoutAsync();
            var recoveredCount = await jim.Tasking.RecoverStaleWorkerTasksAsync(staleTimeout);
            if (recoveredCount > 0)
            {
                Log.Warning("RecoverStaleWorkerTasksAsync: Recovered {Count} stale worker task(s) abandoned by worker", recoveredCount);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RecoverStaleWorkerTasksAsync: Error during stale task recovery");
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

        // Suppress verbose EF Core SQL query logging (only log warnings/errors)
        loggerConfiguration.MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning);

        loggerConfiguration.Enrich.FromLogContext();
        loggerConfiguration.WriteTo.File(
            formatter: new RenderedCompactJsonFormatter(),
            path: Path.Combine(loggingPath, "jim.scheduler..log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 31,
            fileSizeLimitBytes: 500 * 1024 * 1024,
            rollOnFileSizeLimit: true);
        loggerConfiguration.WriteTo.Console();
        Log.Logger = loggerConfiguration.CreateLogger();
    }
}

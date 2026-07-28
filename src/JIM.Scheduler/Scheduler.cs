// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using JIM.Utilities;
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
    private readonly IJimApplicationFactory _jimFactory;
    private readonly IDatabaseNotificationListener _notificationListener;
    private readonly AsyncWakeSignal _wakeSignal = new();
    private Task? _listenTask;

    public Scheduler(IJimApplicationFactory jimFactory, IDatabaseNotificationListener notificationListener)
    {
        _jimFactory = jimFactory;
        _notificationListener = notificationListener;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        InitialiseLogging();

        Log.Information("Starting JIM.Scheduler...");

        // Healthcheck heartbeat file path — Docker healthcheck monitors this file's age
        // to determine if the scheduler's main loop is still executing.
        const string healthcheckFile = "/tmp/healthcheck";

        // Wait for the application to be fully ready (JIM.Worker handles initial migration and seeding).
        // We must check IsApplicationReadyAsync() rather than just database connectivity, because the
        // worker needs to complete migrations and seeding before tables like Schedules exist.
        Log.Information("Waiting for application to be ready...");
        while (!stoppingToken.IsCancellationRequested)
        {
            // Touch the healthcheck file during readiness wait so Docker doesn't mark us unhealthy
            try { await File.WriteAllTextAsync(healthcheckFile, DateTime.UtcNow.ToString("O"), stoppingToken); }
            catch { /* Non-critical */ }

            try
            {
                using var checkJim = _jimFactory.Create();
                if (await checkJim.IsApplicationReadyAsync())
                {
                    Log.Information("Application is ready.");
                    break;
                }

                Log.Information("Application is not ready yet (maintenance mode). Waiting...");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Application not yet ready, waiting...");
            }

            await Task.Delay(2000, stoppingToken);
        }

        // Start listening for Worker Task change notifications in the background so the scheduler can
        // react to task completion in under a second rather than waiting for the next polling cycle.
        // The listener reconnects with backoff on failure and must never take down the scheduler;
        // the 30-second polling cycle below remains the fallback for anything missed.
        _listenTask = ListenForWorkerTaskChangesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Touch the healthcheck file each polling cycle so Docker knows the main loop is alive
            try { await File.WriteAllTextAsync(healthcheckFile, DateTime.UtcNow.ToString("O"), stoppingToken); }
            catch { /* Non-critical */ }

            try
            {
                // Create a fresh JimApplication instance for each polling cycle
                // to avoid EF context caching issues
                using var jim = _jimFactory.Create();

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

            // Wait for the next cycle: woken early by a Worker Task change notification, or after
            // 30 seconds as the polling fallback (notifications are fire-and-forget hints; polling
            // remains the safety net for anything missed while disconnected)
            var wokenByNotification = await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            if (wokenByNotification)
            {
                Log.Debug("Scheduler woken by Worker Task change notification; running next cycle after settling delay.");

                // Short settling delay: the worker's own synchronous schedule advancement runs just
                // after the task delete commits, so a brief pause usually lets it complete first.
                // The polling cycle is idempotent either way.
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            }
        }

        Log.Information("JIM.Scheduler shutting down...");
    }

    /// <summary>
    /// Runs the database notification listen loop for Worker Task changes until shutdown. The listener
    /// reconnects with backoff internally; this wrapper exists to observe the task's completion so a
    /// listener failure is logged rather than left as an unobserved exception, and never crashes the
    /// scheduler (the polling fallback keeps schedules advancing).
    /// </summary>
    private async Task ListenForWorkerTaskChangesAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _notificationListener.ListenAsync(
                [Constants.NotificationChannels.WorkerTaskChange],
                HandleNotificationAsync,
                stoppingToken);

            Log.Information("ListenForWorkerTaskChangesAsync: Worker Task change notification listener stopped.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "ListenForWorkerTaskChangesAsync: Notification listener failed. Continuing with polling fallback only.");
        }
    }

    /// <summary>
    /// Handles a database notification received on the Worker Task change channel. A Delete operation
    /// for a task that belongs to a Schedule Execution means a schedule step just completed, so the
    /// polling loop is woken to advance the execution promptly. Unparseable payloads are ignored;
    /// notification handling must never take down the listen loop.
    /// </summary>
    private Task HandleNotificationAsync(string channelName, string payload, CancellationToken cancellationToken)
    {
        if (!WorkerTaskChangeNotification.TryParse(payload, out var notification))
        {
            Log.Debug("HandleNotificationAsync: Ignoring unparseable notification payload on channel {ChannelName}.",
                LogSanitiser.Sanitise(channelName));
            return Task.CompletedTask;
        }

        if (notification!.Operation == WorkerTaskChangeOperation.Delete && notification.ScheduleExecutionId.HasValue)
        {
            Log.Debug("HandleNotificationAsync: Worker Task {TaskId} for Schedule Execution {ScheduleExecutionId} reached terminal state. Waking scheduler.",
                notification.TaskId, notification.ScheduleExecutionId.Value);

            _wakeSignal.Signal();
        }

        return Task.CompletedTask;
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
                    await jim.Scheduler.UpdateScheduleRunTimesAsync(schedule);
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
                    var allTasks = await jim.Tasking.GetWorkerTasksByScheduleExecutionAsync(execution.Id);
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
            retainedFileCountLimit: 100,
            fileSizeLimitBytes: 50 * 1024 * 1024,  // 50MB per file — keeps files manageable for analysis
            rollOnFileSizeLimit: true);
        loggerConfiguration.WriteTo.Console();
        Log.Logger = loggerConfiguration.CreateLogger();
    }
}

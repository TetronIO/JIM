// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Operations;
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

    /// <summary>
    /// How long the main loop waits between polling cycles when no Worker Task change notification arrives.
    /// </summary>
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    public Scheduler(IJimApplicationFactory jimFactory, IDatabaseNotificationListener notificationListener)
    {
        _jimFactory = jimFactory;
        _notificationListener = notificationListener;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        InitialiseLogging();

        Log.Information("Starting JIM.Scheduler...");

        // The same liveness as the health-check heartbeat file, written to the database for administrators: the
        // Operations page reads it to show whether the Scheduler is up, since when, and which version. Written
        // wherever the file is touched.
        var heartbeat = ServiceHeartbeatWriter.ForThisProcess(JimService.Scheduler);

        // Wait for the database server first, keeping the health-check heartbeat fresh meanwhile. Until it answers,
        // every readiness check below would only fail, so there is nothing to gain from starting them sooner.
        using (var waitJim = _jimFactory.Create())
            await waitJim.WaitForDatabaseAsync(JimApplication.DefaultDatabaseWaitBudget,
                _ => HealthcheckFile.TouchAsync(), stoppingToken);

        // Wait for the application to be fully ready (JIM.Worker handles initial migration and seeding).
        // We must check IsApplicationReadyAsync() rather than just database connectivity, because the
        // worker needs to complete migrations and seeding before tables like Schedules exist.
        Log.Information("Waiting for application to be ready...");
        while (!stoppingToken.IsCancellationRequested)
        {
            // Touch the health-check heartbeat file while waiting, so the container is not judged unhealthy
            await HealthcheckFile.TouchAsync();

            try
            {
                using var checkJim = _jimFactory.Create();

                // Reported while waiting too, so an administrator can tell "the Scheduler is up but the Worker has
                // not finished migrating" from "the Scheduler is down". The writer swallows the write failing
                // because the table does not exist yet, which it will not until the Worker has migrated.
                await heartbeat.WriteAsync(checkJim, null, null, "Waiting for the application to be ready", stoppingToken);

                if (await checkJim.IsApplicationReadyAsync())
                {
                    Log.Information("Application is ready.");
                    break;
                }

                Log.Information("Application is not ready yet (maintenance mode). Waiting...");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database answered the wait above, so this is the brief window before JIM.Worker has created
                // the schema, or the database blipping mid-check: keep waiting either way.
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
            // Touch the health-check heartbeat file each polling cycle, so the runtime knows the loop is alive
            await HealthcheckFile.TouchAsync();

            try
            {
                // Create a fresh JimApplication instance for each polling cycle
                // to avoid EF context caching issues
                using var jim = _jimFactory.Create();

                await heartbeat.WriteAsync(jim, null, null, null, stoppingToken);

                // Step 1: Check for and start due schedules. This runs BEFORE the next-run-time bootstrap
                // below, and must keep doing so: both read NextRunTime, and attempting to start a schedule is
                // what advances it (whether or not the start succeeds). Bootstrapping first is how every
                // cron-triggered schedule came to be swallowed on the cycle it became due (the bootstrap query
                // has since been narrowed to rows with no next run time at all, so the two can no longer claim
                // the same schedule; the order is kept because "start the work, then fill in what is missing"
                // is the honest reading).
                await jim.Scheduler.StartDueSchedulesAsync();

                // Step 2: Give a next run time to any cron-based schedule that has none yet: newly created,
                // newly enabled, or newly switched from a manual trigger.
                await jim.Scheduler.UpdateNextRunTimesAsync();

                // Step 3: Safety net - recover stuck executions where the worker completed a task
                // but crashed before TryAdvanceScheduleExecutionAsync could run, and fail any execution left
                // Queued part-way through starting (#1768)
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
            var wokenByNotification = await WaitForNextCycleAsync(heartbeat, stoppingToken);
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
    /// Waits out the polling interval (or a wake-up notification, whichever is first) in heartbeat-sized slices,
    /// writing the Scheduler's heartbeat between them. The cycle itself takes well under a second, so without this
    /// the heartbeat would move once per 30-second cycle and a perfectly healthy Scheduler would read as Degraded
    /// (heartbeat overdue: older than three intervals) for most of every minute. Returns true when woken by a
    /// notification.
    /// </summary>
    private async Task<bool> WaitForNextCycleAsync(ServiceHeartbeatWriter heartbeat, CancellationToken stoppingToken)
    {
        var deadline = DateTime.UtcNow + PollingInterval;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return false;

            var slice = remaining < ServiceHeartbeatWriter.Interval ? remaining : ServiceHeartbeatWriter.Interval;
            if (await _wakeSignal.WaitAsync(slice, stoppingToken))
                return true;

            // A fresh instance per write, as the cycle above uses; the writer itself is throttled and swallows
            // database failures, so this can neither hammer the database nor end the loop.
            using var jim = _jimFactory.Create();
            await heartbeat.WriteAsync(jim, null, null, null, stoppingToken);
        }
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
    /// Safety net for Schedule Executions: concludes an InProgress execution the Worker lost track of (it completed a
    /// task but stopped before advancing), and fails one left Queued part-way through starting for longer than any start
    /// takes (#1768). The logic lives in SchedulerServer.RecoverStuckExecutionsAsync, where it is unit tested; this only
    /// keeps a failure from ending the polling cycle.
    /// </summary>
    private static async Task RecoverStuckExecutionsAsync(JimApplication jim)
    {
        try
        {
            await jim.Scheduler.RecoverStuckExecutionsAsync();
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

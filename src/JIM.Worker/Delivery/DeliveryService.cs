// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Operations;
using Serilog;

namespace JIM.Worker.Delivery;

/// <summary>
/// A delivery service hosted in the Worker process: a queue delivered on its own clock by a
/// <see cref="DeliveryScheduler"/>, woken by the queue's database notification, by the earliest scheduled retry,
/// and by a thirty-second safety poll, and reporting its own heartbeat. The Password Delivery Service (#1635) and
/// the Exchange Delivery Service (#2021) are the two; each supplies its name, its notification channel, its
/// heartbeat service and its work, and this class supplies the rest.
/// <para>
/// Hosted as a <see cref="BackgroundService"/> of its own (plan D3 of #1635) rather than as a Worker Task: a task
/// waits its turn behind whatever Run Profile is executing. A fault here must never stop the synchronisation loop
/// or another delivery service. The loop's iterations and its lanes each catch their own faults, and this class
/// wraps the whole loop in a catch-all with a restart delay, so nothing escapes to the host. The host keeps its
/// default of stopping on a faulted background service deliberately: the synchronisation loop has no catch-all of
/// its own, and a Worker that failed to start must restart its container rather than idle with only this service
/// alive.
/// </para>
/// </summary>
internal abstract class DeliveryService : BackgroundService
{
    /// <summary>
    /// How long to wait before starting the loop again after it escaped its own boundaries. Long enough not to
    /// spin on a persistent fault, short enough that nobody waiting on a delivery is kept long.
    /// </summary>
    internal static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    private readonly IDatabaseNotificationListener _notificationListener;

    protected DeliveryService(IJimApplicationFactory jimFactory, IDatabaseNotificationListener notificationListener)
    {
        JimFactory = jimFactory;
        _notificationListener = notificationListener;
    }

    protected IJimApplicationFactory JimFactory { get; }

    /// <summary>The service in an administrator's words, for its log lines ("Password Delivery Service").</summary>
    protected abstract string ServiceName { get; }

    /// <summary>The scheduler's name in its log lines ("Password delivery").</summary>
    protected abstract string SchedulerName { get; }

    /// <summary>The queue's NOTIFY channel; its payload is the Connected System id.</summary>
    protected abstract string NotificationChannel { get; }

    /// <summary>The service its heartbeat is written under.</summary>
    protected abstract JimService HeartbeatService { get; }

    /// <summary>
    /// The queue, the lanes and the heartbeat. The heartbeat writer's instance id doubles as the name every claim is
    /// stamped with.
    /// </summary>
    protected abstract IDeliveryWork CreateWork(ServiceHeartbeatWriter heartbeat);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so the host finishes starting every service before this one does anything; the synchronisation
        // loop has already set up logging by the time it reaches its first await.
        await Task.Yield();

        Log.Information("Starting the {Service}...", ServiceName);

        // The same liveness the Worker's synchronisation loop writes, under a service of its own: a wedged
        // synchronisation loop and a wedged delivery loop need different responses, and the Operations page shows
        // them as different cards.
        var work = CreateWork(ServiceHeartbeatWriter.ForThisProcess(HeartbeatService));

        await WaitUntilApplicationReadyAsync(work, stoppingToken);
        if (stoppingToken.IsCancellationRequested)
            return;

        var scheduler = new DeliveryScheduler(SchedulerName, work);

        // Listen in the background. The listener reconnects with backoff on failure and never takes the loop
        // down; the safety poll inside the scheduler remains the floor for anything missed while disconnected.
        _ = ListenForQueueChangesAsync(scheduler, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await scheduler.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Sanctioned top-level boundary: the loop below this already catches per-iteration and per-lane
                // faults, so anything reaching here is unexpected, and the answer is still to start again rather
                // than to leave the whole queue waiting until somebody restarts the Worker.
                Log.Error(ex, "{Service}: The delivery loop stopped unexpectedly; restarting it in {Delay}.", ServiceName, RestartDelay);
                try
                {
                    await Task.Delay(RestartDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Log.Information("{Service} shutting down...", ServiceName);
    }

    /// <summary>
    /// Waits for the Worker's synchronisation loop to finish migrating and seeding: the queue table, and the
    /// heartbeat table, may not exist yet. The heartbeat is written while waiting (the writer swallows the table
    /// not existing), so an administrator can tell "up but waiting" from "down".
    /// </summary>
    private async Task WaitUntilApplicationReadyAsync(IDeliveryWork work, CancellationToken stoppingToken)
    {
        Log.Information("{Service}: Waiting for the application to be ready...", ServiceName);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Quietly first: the Worker's main loop reports its wait for the database server, and while the
                // server is down each heartbeat and readiness check below would have the data layer log an error.
                using var jim = JimFactory.Create();
                if (await jim.IsDatabaseReachableAsync(stoppingToken))
                {
                    await work.WriteHeartbeatAsync(null, null, "Waiting for the application to be ready", stoppingToken);

                    if (await jim.IsApplicationReadyAsync())
                    {
                        Log.Information("{Service}: Application is ready.", ServiceName);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Expected while the synchronisation loop is still migrating: the tables the readiness check
                // reads may not exist yet. Filtered form so a shutdown mid-wait propagates.
                Log.Debug(ex, "{Service}: Application not yet ready, waiting...", ServiceName);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ListenForQueueChangesAsync(DeliveryScheduler scheduler, CancellationToken stoppingToken)
    {
        try
        {
            await _notificationListener.ListenAsync(
                [NotificationChannel],
                (_, payload, _) =>
                {
                    // The payload is the Connected System id; the scheduler ignores it for a system it is already
                    // delivering to and re-reads the queue for everything else. Anything unparseable wakes it.
                    scheduler.NotifyChanged(int.TryParse(payload, out var connectedSystemId) ? connectedSystemId : null);
                    return Task.CompletedTask;
                },
                stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The listener reconnects internally; only a fault outside its own loop reaches here. The safety poll
            // keeps the service delivering without notifications, a poll late rather than never.
            Log.Error(ex, "{Service}: The queue notification listener stopped; delivery continues on the safety poll alone.", ServiceName);
        }
    }
}

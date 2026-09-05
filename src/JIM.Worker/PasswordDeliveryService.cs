// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Models.Core;
using JIM.Models.Operations;
using JIM.Worker.PasswordDelivery;
using Serilog;

namespace JIM.Worker;

/// <summary>
/// The Password Delivery Service (#1635): delivers the Password Synchronisation queue on its own clock, so a
/// person's reset reaches their accounts in about a second whatever the synchronisation loop is doing.
/// <para>
/// Hosted in the Worker process as a second <see cref="BackgroundService"/> (plan D3) rather than as a Worker
/// Task: a task waits its turn behind whatever Run Profile is executing, and a password change made during a Full
/// Import used to wait for the import to finish. This loop shares nothing with the synchronisation loop but the
/// process; it is woken by the queue's own database notification, by the earliest scheduled retry, and by a
/// thirty-second safety poll, and it reports its own heartbeat under <see cref="JimService.WorkerPasswordDelivery"/>.
/// </para>
/// <para>
/// A fault here must never stop the synchronisation loop. The loop's iterations and its lanes each catch their
/// own faults, and this class wraps the whole loop in a catch-all with a restart delay, so nothing escapes to the
/// host. The host keeps its default of stopping on a faulted background service deliberately: the synchronisation
/// loop has no catch-all of its own, and a Worker that failed to start must restart its container rather than
/// idle with only this service alive.
/// </para>
/// </summary>
public sealed class PasswordDeliveryService : BackgroundService
{
    /// <summary>
    /// How long to wait before starting the loop again after it escaped its own boundaries. Long enough not to
    /// spin on a persistent fault, short enough that a person waiting on a reset is not kept long.
    /// </summary>
    internal static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    private readonly IJimApplicationFactory _jimFactory;
    private readonly IDatabaseNotificationListener _notificationListener;

    public PasswordDeliveryService(IJimApplicationFactory jimFactory, IDatabaseNotificationListener notificationListener)
    {
        _jimFactory = jimFactory;
        _notificationListener = notificationListener;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so the host finishes starting every service before this one does anything; the synchronisation
        // loop has already set up logging by the time it reaches its first await.
        await Task.Yield();

        Log.Information("Starting the Password Delivery Service...");

        // The same liveness the Worker's synchronisation loop writes, under a service of its own: a wedged
        // synchronisation loop and a wedged password loop need different responses, and the Operations page shows
        // them as different cards. The instance id doubles as the name every claim is stamped with.
        var heartbeat = ServiceHeartbeatWriter.ForThisProcess(JimService.WorkerPasswordDelivery);
        var work = new JimPasswordDeliveryWork(_jimFactory, heartbeat);

        await WaitUntilApplicationReadyAsync(work, stoppingToken);
        if (stoppingToken.IsCancellationRequested)
            return;

        var scheduler = new PasswordDeliveryScheduler(work);

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
                // than to leave every queued password waiting until somebody restarts the Worker.
                Log.Error(ex, "PasswordDeliveryService: The delivery loop stopped unexpectedly; restarting it in {Delay}.", RestartDelay);
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

        Log.Information("Password Delivery Service shutting down...");
    }

    /// <summary>
    /// Waits for the Worker's synchronisation loop to finish migrating and seeding: the queue table, and the
    /// heartbeat table, may not exist yet. The heartbeat is written while waiting (the writer swallows the table
    /// not existing), so an administrator can tell "up but waiting" from "down".
    /// </summary>
    private async Task WaitUntilApplicationReadyAsync(JimPasswordDeliveryWork work, CancellationToken stoppingToken)
    {
        Log.Information("PasswordDeliveryService: Waiting for the application to be ready...");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await work.WriteHeartbeatAsync(null, null, "Waiting for the application to be ready", stoppingToken);

                using var jim = _jimFactory.Create();
                if (await jim.IsApplicationReadyAsync())
                {
                    Log.Information("PasswordDeliveryService: Application is ready.");
                    return;
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
                Log.Debug(ex, "PasswordDeliveryService: Application not yet ready, waiting...");
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

    private async Task ListenForQueueChangesAsync(PasswordDeliveryScheduler scheduler, CancellationToken stoppingToken)
    {
        try
        {
            await _notificationListener.ListenAsync(
                [Constants.NotificationChannels.PasswordChange],
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
            Log.Error(ex, "PasswordDeliveryService: The queue notification listener stopped; delivery continues on the safety poll alone.");
        }
    }
}

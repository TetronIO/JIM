// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Globalization;
using JIM.Models.Transactional.DTOs;
using JIM.Utilities;
using Serilog;

namespace JIM.Worker.PasswordDelivery;

/// <summary>
/// The Password Delivery Service's loop (#1635), separated from its host so the wake and dispatch rules can be
/// tested with a fake clock and a fake queue.
/// <para>
/// One iteration is <see cref="DispatchAsync"/> followed by <see cref="WaitAsync"/>. Dispatch reads what is due,
/// starts a lane for every Connected System with work that does not already have one running (bounded across
/// systems, never more than one per system), writes the heartbeat, and says how long to wait: until the earliest
/// scheduled retry, or the safety poll, whichever is sooner. The wait ends early on a wake: a queue notification,
/// or a lane finishing. Lanes run on their own tasks; a lane that throws is logged and forgotten, and the loop
/// carries on, because one directory's fault must never delay another's passwords.
/// </para>
/// </summary>
internal sealed class PasswordDeliveryScheduler
{
    /// <summary>
    /// How long the loop sleeps with nothing scheduled and no notification. Notifications are hints delivered on
    /// a connection that can drop, so a poll remains the floor under everything, as it does for the Scheduler.
    /// </summary>
    public static readonly TimeSpan SafetyPoll = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many Connected Systems are delivered to at once. Lanes are bounded so a burst across many systems does
    /// not open many directory connections together; sequential within a system, because one system's changes
    /// share one password channel.
    /// </summary>
    public const int MaximumParallelLanes = 4;

    private readonly IPasswordDeliveryWork _work;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _safetyPoll;
    private readonly TimeSpan _heartbeatSlice;
    private readonly AsyncWakeSignal _wake = new();
    private readonly SemaphoreSlim _laneSlots;
    private readonly Lock _lanesLock = new();
    private readonly Dictionary<int, PasswordDeliveryLane> _lanesInFlight = [];
    private readonly Dictionary<int, DateTime> _heldOffUntil = [];
    private readonly List<Task> _laneTasks = [];
    private PasswordQueueDeliveryOutlook _lastOutlook = new();

    /// <param name="work">The queue, the lanes and the heartbeat.</param>
    /// <param name="utcNow">The clock; tests pin it.</param>
    /// <param name="safetyPoll">The longest the loop sleeps; null takes <see cref="SafetyPoll"/>.</param>
    /// <param name="maximumParallelLanes">How many lanes may run at once; null takes <see cref="MaximumParallelLanes"/>.</param>
    /// <param name="heartbeatSlice">How often the wait writes the heartbeat; null takes the heartbeat interval.</param>
    public PasswordDeliveryScheduler(
        IPasswordDeliveryWork work,
        Func<DateTime>? utcNow = null,
        TimeSpan? safetyPoll = null,
        int? maximumParallelLanes = null,
        TimeSpan? heartbeatSlice = null)
    {
        _work = work;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _safetyPoll = safetyPoll ?? SafetyPoll;
        _heartbeatSlice = heartbeatSlice ?? JIM.Application.Services.ServiceHeartbeatWriter.Interval;
        var slots = maximumParallelLanes ?? MaximumParallelLanes;
        _laneSlots = new SemaphoreSlim(slots, slots);
    }

    /// <summary>
    /// The lanes currently running, for the heartbeat and for tests.
    /// </summary>
    public IReadOnlyList<PasswordDeliveryLane> LanesInFlight
    {
        get
        {
            lock (_lanesLock)
                return _lanesInFlight.Values.OrderBy(l => l.StartedAt).ThenBy(l => l.ConnectedSystemId).ToList();
        }
    }

    /// <summary>
    /// Wakes the loop: a queue notification, or anything else that means the queue should be looked at now.
    /// Signals coalesce, so a burst of notifications costs one dispatch.
    /// </summary>
    public void Wake() => _wake.Signal();

    /// <summary>
    /// A queue notification for one Connected System. Ignored while that system has a lane running: the lane's
    /// own writes raise a notification per row, and the lane re-checks its system's queue when it finishes in any
    /// case, so waking for them would only have the loop confirm the lane it already knows about. Lifts any
    /// hold-off on the system, because a notification from outside a lane is somebody acting on it (a new change,
    /// a retry from the queue page) and deserves an attempt now. A notification whose system is unknown (an
    /// unparseable payload) wakes the loop regardless.
    /// </summary>
    public void NotifyChanged(int? connectedSystemId)
    {
        if (connectedSystemId.HasValue)
        {
            lock (_lanesLock)
            {
                if (_lanesInFlight.ContainsKey(connectedSystemId.Value))
                    return;

                _heldOffUntil.Remove(connectedSystemId.Value);
            }
        }

        Wake();
    }

    /// <summary>
    /// The Connected Systems currently held off, with when each is due again, for tests.
    /// </summary>
    public IReadOnlyDictionary<int, DateTime> HeldOff
    {
        get
        {
            lock (_lanesLock)
                return new Dictionary<int, DateTime>(_heldOffUntil);
        }
    }

    /// <summary>
    /// Runs the loop until cancelled. Each iteration's faults are logged and followed by a safety-poll wait, so a
    /// database outage costs missed iterations rather than the service.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan waitFor;
            try
            {
                waitFor = await DispatchAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Resilience boundary (filtered form): whatever the queue reads did, the loop must come round
                // again. The next iteration re-reads everything, so nothing is lost by dropping this one.
                Log.Error(ex, "PasswordDeliveryScheduler: An iteration failed; the service will look again after the safety poll.");
                waitFor = _safetyPoll;
            }

            try
            {
                var woken = await WaitAsync(waitFor, cancellationToken);
                if (woken)
                {
                    // A short settle so a burst of notifications (a bulk reset across many systems) becomes one
                    // dispatch rather than one per row. Two hundred and fifty milliseconds is invisible to a
                    // person waiting on a reset and coalesces everything one transaction commits.
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        await DrainLanesAsync();
    }

    /// <summary>
    /// One dispatch: reads the outlook and the systems with work due, starts a lane for each system not already
    /// being delivered to (as slots allow), writes the heartbeat, and returns how long to wait before the next
    /// dispatch: until the earliest scheduled retry, or the safety poll, whichever is sooner.
    /// </summary>
    public async Task<TimeSpan> DispatchAsync(CancellationToken cancellationToken)
    {
        var now = _utcNow();

        _lastOutlook = await _work.GetOutlookAsync(now, cancellationToken);
        var dueSystems = await _work.GetConnectedSystemIdsWithWorkDueAsync(now, cancellationToken);

        var started = 0;
        var deferred = 0;
        DateTime? earliestHoldOffExpiry = null;
        foreach (var connectedSystemId in dueSystems)
        {
            switch (TryStartLane(connectedSystemId, now, cancellationToken, out var heldOffUntil))
            {
                case LaneStart.Started:
                    started++;
                    break;
                case LaneStart.Deferred:
                    deferred++;
                    break;
                case LaneStart.HeldOff:
                    if (earliestHoldOffExpiry == null || heldOffUntil < earliestHoldOffExpiry)
                        earliestHoldOffExpiry = heldOffUntil;
                    break;
            }
        }

        if (started > 0 || deferred > 0)
            Log.Debug("PasswordDeliveryScheduler: {Due} Connected System(s) with work due; {Started} lane(s) started, {Deferred} waiting for a slot, {InFlight} in flight.",
                dueSystems.Count, started, deferred, LanesInFlight.Count);

        await WriteHeartbeatAsync(cancellationToken);

        // Until the earliest retry, or the earliest hold-off ending, where either is sooner than the poll. A
        // deferred system needs no timer of its own: the lane whose slot it is waiting for wakes the loop when it
        // finishes.
        var waitFor = _safetyPoll;
        if (_lastOutlook.NextAttemptAt is { } nextAttemptAt)
            waitFor = Sooner(waitFor, nextAttemptAt - now);
        if (earliestHoldOffExpiry is { } holdOffExpiry)
            waitFor = Sooner(waitFor, holdOffExpiry - now);

        return waitFor;
    }

    private static TimeSpan Sooner(TimeSpan current, TimeSpan candidate)
    {
        if (candidate >= current)
            return current;
        return candidate < TimeSpan.Zero ? TimeSpan.Zero : candidate;
    }

    /// <summary>
    /// Waits out <paramref name="duration"/> in heartbeat-sized slices, writing the heartbeat between them, or
    /// returns early on a wake. Returns true when woken. Without the slicing the heartbeat would move once per
    /// thirty-second poll and a perfectly healthy service would read as Stale for most of every minute.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        // Measured on real elapsed time rather than the injectable clock: the clock answers "what is due now",
        // which tests pin, whereas a wait is a length of wall time and must end whether or not the clock moves.
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var remaining = duration - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;

            var slice = remaining < _heartbeatSlice ? remaining : _heartbeatSlice;
            if (await _wake.WaitAsync(slice, cancellationToken))
                return true;

            await WriteHeartbeatAsync(cancellationToken);
        }
    }

    private enum LaneStart { Started, AlreadyRunning, Deferred, HeldOff }

    private LaneStart TryStartLane(int connectedSystemId, DateTime now, CancellationToken cancellationToken, out DateTime heldOffUntil)
    {
        heldOffUntil = default;
        PasswordDeliveryLane lane;
        lock (_lanesLock)
        {
            // One lane per system at a time: a system's changes share one password channel, and two lanes would
            // race each other for the same rows (harmlessly, thanks to the claim, but pointlessly).
            if (_lanesInFlight.ContainsKey(connectedSystemId))
                return LaneStart.AlreadyRunning;

            // A system whose last lane could not deliver at all waits out its hold-off: its rows are due again
            // the moment that lane released them, and without this the same failing lane would run back to back
            // for as long as the directory stayed down.
            if (_heldOffUntil.TryGetValue(connectedSystemId, out heldOffUntil) && heldOffUntil > now)
                return LaneStart.HeldOff;
            _heldOffUntil.Remove(connectedSystemId);

            // A slot is taken without waiting: a dispatch must never block behind a slow directory. The system
            // is picked up again on the wake the finishing lane raises.
            if (!_laneSlots.Wait(0, CancellationToken.None))
                return LaneStart.Deferred;

            lane = new PasswordDeliveryLane(connectedSystemId, now);
            _lanesInFlight[connectedSystemId] = lane;
        }

        var laneTask = Task.Run(() => RunLaneAsync(lane, cancellationToken), CancellationToken.None);
        lock (_lanesLock)
        {
            _laneTasks.RemoveAll(t => t.IsCompleted);
            _laneTasks.Add(laneTask);
        }

        return LaneStart.Started;
    }

    private async Task RunLaneAsync(PasswordDeliveryLane lane, CancellationToken cancellationToken)
    {
        var holdOff = false;
        try
        {
            holdOff = await _work.RunLaneAsync(lane, cancellationToken) == PasswordDeliveryLaneOutcome.CouldNotDeliver;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Information("PasswordDeliveryScheduler: The lane for {ConnectedSystem} was cancelled; its unattempted claims were released.",
                LogSanitiser.Sanitise(lane.Describe()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Resilience boundary (filtered form): the lane's own outcomes are already on the rows and their
            // Activities; what reaches here is a fault of the lane as a whole, and it must not take the loop or
            // the other lanes with it. The rows it held come back after the claim lease, at the latest. Held off
            // for the same reason a lane that could not deliver is: a fault that repeats must not spin.
            Log.Error(ex, "PasswordDeliveryScheduler: The lane for {ConnectedSystem} failed. Its claimed changes are released when the claim lease runs out, and the service carries on.",
                LogSanitiser.Sanitise(lane.Describe()));
            holdOff = true;
        }
        finally
        {
            lock (_lanesLock)
            {
                _lanesInFlight.Remove(lane.ConnectedSystemId);
                if (holdOff)
                    _heldOffUntil[lane.ConnectedSystemId] = _utcNow() + _safetyPoll;
            }

            _laneSlots.Release();

            // The loop re-reads the queue: the lane may have left work behind (the pass bound, or rows queued
            // while it ran), and a system deferred for want of a slot can have this one.
            Wake();
        }
    }

    private async Task WriteHeartbeatAsync(CancellationToken cancellationToken)
    {
        var (currentWork, startedAt) = DescribeCurrentWork(LanesInFlight);
        await _work.WriteHeartbeatAsync(currentWork, startedAt, DescribeOutlook(_lastOutlook), cancellationToken);
    }

    /// <summary>
    /// The lanes in flight as one CurrentWork string ("Delivering to Corporate Directory, HR System") and the
    /// earliest start among them; null and null when idle.
    /// </summary>
    internal static (string? CurrentWork, DateTime? StartedAt) DescribeCurrentWork(IReadOnlyList<PasswordDeliveryLane> lanes)
    {
        if (lanes.Count == 0)
            return (null, null);

        return ($"Delivering to {string.Join(", ", lanes.Select(l => l.Describe()))}", lanes.Min(l => l.StartedAt));
    }

    /// <summary>
    /// The outlook as the heartbeat's detail ("3 due, 1 retrying, next attempt 09:19 UTC"), or null when the
    /// queue has nothing ahead. Absolute rather than relative time, because the detail is read for up to a poll
    /// after it was written.
    /// </summary>
    internal static string? DescribeOutlook(PasswordQueueDeliveryOutlook outlook)
    {
        if (outlook.DueCount == 0 && outlook.RetryingCount == 0 && outlook.NextAttemptAt == null)
            return null;

        var parts = new List<string>
        {
            $"{outlook.DueCount} due",
            $"{outlook.RetryingCount} retrying"
        };

        if (outlook.NextAttemptAt is { } nextAttemptAt)
            parts.Add($"next attempt {nextAttemptAt.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC");

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Waits for the lanes still running at shutdown. Each has the stopping token and persists what it achieved
    /// in its own finally; this only keeps the host from tearing the process down under them.
    /// </summary>
    private async Task DrainLanesAsync()
    {
        Task[] running;
        lock (_lanesLock)
            running = _laneTasks.Where(t => !t.IsCompleted).ToArray();

        if (running.Length == 0)
            return;

        Log.Information("PasswordDeliveryScheduler: Waiting for {Count} lane(s) to stop...", running.Length);
        try
        {
            await Task.WhenAll(running);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Every lane task already logs its own fault; nothing is left to report here.
            Log.Debug(ex, "PasswordDeliveryScheduler: A lane faulted while stopping.");
        }
    }
}

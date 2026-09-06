// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JIM.Models.Transactional.DTOs;
using JIM.Worker.PasswordDelivery;
using NUnit.Framework;

namespace JIM.Worker.Tests.PasswordDelivery;

/// <summary>
/// The Password Delivery Service's loop (#1635): when it wakes, which lanes it starts, and what it reports while
/// they run. Exercised through <see cref="PasswordDeliveryScheduler"/> with a fake queue and a pinned clock, so
/// every rule is asserted without a database or a directory.
/// </summary>
[TestFixture]
public class PasswordDeliverySchedulerTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 9, 15, 0, DateTimeKind.Utc);
    private static readonly TimeSpan SafetyPoll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    private FakeWork _work = null!;

    [SetUp]
    public void SetUp()
    {
        _work = new FakeWork();
    }

    private PasswordDeliveryScheduler NewScheduler(int maximumParallelLanes = 4, TimeSpan? heartbeatSlice = null) =>
        new(_work, () => Now, SafetyPoll, maximumParallelLanes, heartbeatSlice ?? TimeSpan.FromMilliseconds(20));

    #region wake rules

    [Test]
    public async Task DispatchAsync_NothingScheduled_WaitsForTheSafetyPollAsync()
    {
        var scheduler = NewScheduler();

        var waitFor = await scheduler.DispatchAsync(CancellationToken.None);

        Assert.That(waitFor, Is.EqualTo(SafetyPoll));
    }

    [Test]
    public async Task DispatchAsync_RetryScheduledBeforeThePoll_WaitsUntilItAsync()
    {
        // The case nothing else catches: a change that failed once comes due minutes later with nothing else
        // happening in the system. The loop sets its own alarm for it.
        _work.Outlook.NextAttemptAt = Now.AddSeconds(7);
        var scheduler = NewScheduler();

        var waitFor = await scheduler.DispatchAsync(CancellationToken.None);

        Assert.That(waitFor, Is.EqualTo(TimeSpan.FromSeconds(7)));
    }

    [Test]
    public async Task DispatchAsync_RetryScheduledAfterThePoll_WaitsForThePollAsync()
    {
        _work.Outlook.NextAttemptAt = Now.AddMinutes(5);
        var scheduler = NewScheduler();

        var waitFor = await scheduler.DispatchAsync(CancellationToken.None);

        Assert.That(waitFor, Is.EqualTo(SafetyPoll));
    }

    [Test]
    public async Task DispatchAsync_RetryAlreadyDue_DoesNotWaitAsync()
    {
        _work.Outlook.NextAttemptAt = Now.AddSeconds(-1);
        var scheduler = NewScheduler();

        var waitFor = await scheduler.DispatchAsync(CancellationToken.None);

        Assert.That(waitFor, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public async Task WaitAsync_Woken_ReturnsTrueAtOnceAsync()
    {
        var scheduler = NewScheduler();

        scheduler.Wake();
        var woken = await scheduler.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).WaitAsync(Soon);

        Assert.That(woken, Is.True);
    }

    [Test]
    public async Task WaitAsync_NotWoken_ReturnsFalseAfterTheDurationWritingHeartbeatsBetweenSlicesAsync()
    {
        // Without slicing the heartbeat would move once per poll and a healthy service would read as Stale for
        // most of every minute.
        var scheduler = NewScheduler(heartbeatSlice: TimeSpan.FromMilliseconds(10));

        var woken = await scheduler.WaitAsync(TimeSpan.FromMilliseconds(60), CancellationToken.None).WaitAsync(Soon);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(woken, Is.False);
            Assert.That(_work.Heartbeats, Has.Count.GreaterThanOrEqualTo(2), "One heartbeat per slice, not one per wait.");
        }
    }

    [Test]
    public async Task NotifyChanged_ForASystemWithALaneRunning_DoesNotWakeAsync()
    {
        // The lane's own writes raise a notification per row, and it re-checks its queue when it finishes; waking
        // for them would only have the loop confirm the lane it already knows about.
        _work.DueSystems = [3];
        var scheduler = NewScheduler();
        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3);

        scheduler.NotifyChanged(3);
        var wokenForRunningLane = await scheduler.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None).WaitAsync(Soon);

        scheduler.NotifyChanged(4);
        var wokenForOtherSystem = await scheduler.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).WaitAsync(Soon);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wokenForRunningLane, Is.False);
            Assert.That(wokenForOtherSystem, Is.True);
        }

        _work.ReleaseLane(3);
    }

    [Test]
    public async Task NotifyChanged_WithNoSystemNamed_WakesAsync()
    {
        var scheduler = NewScheduler();

        scheduler.NotifyChanged(null);
        var woken = await scheduler.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None).WaitAsync(Soon);

        Assert.That(woken, Is.True);
    }

    [Test]
    public async Task LaneFinishing_WakesTheLoopAsync()
    {
        // The lane may have left work behind (the pass bound, rows queued while it ran), and a system deferred
        // for want of a slot can have this one.
        _work.DueSystems = [3];
        var scheduler = NewScheduler();
        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3);

        var waiting = scheduler.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        _work.ReleaseLane(3);
        var woken = await waiting.WaitAsync(Soon);

        Assert.That(woken, Is.True);
    }

    #endregion

    #region lane scheduling

    [Test]
    public async Task DispatchAsync_StartsOneLanePerSystemWithWorkDueAsync()
    {
        _work.DueSystems = [3, 4];
        var scheduler = NewScheduler();

        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3);
        await _work.WaitForLaneToStartAsync(4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_work.LanesStarted, Is.EquivalentTo(new[] { 3, 4 }));
            Assert.That(scheduler.LanesInFlight.Select(l => l.ConnectedSystemId), Is.EquivalentTo(new[] { 3, 4 }));
        }

        _work.ReleaseLane(3);
        _work.ReleaseLane(4);
    }

    [Test]
    public async Task DispatchAsync_SystemAlreadyBeingDeliveredTo_DoesNotStartASecondLaneAsync()
    {
        // One lane per system at a time: a system's changes share one password channel.
        _work.DueSystems = [3];
        var scheduler = NewScheduler();
        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3);

        await scheduler.DispatchAsync(CancellationToken.None);

        Assert.That(_work.LanesStarted, Is.EqualTo(new[] { 3 }));

        _work.ReleaseLane(3);
    }

    [Test]
    public async Task DispatchAsync_BoundsLanesAcrossSystemsAndTakesTheNextWhenASlotFreesAsync()
    {
        _work.DueSystems = [1, 2, 3];
        var scheduler = NewScheduler(maximumParallelLanes: 2);

        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(1);
        await _work.WaitForLaneToStartAsync(2);

        Assert.That(_work.LanesStarted, Is.EquivalentTo(new[] { 1, 2 }), "The third system waits for a slot.");

        var waiting = scheduler.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        _work.ReleaseLane(1);
        Assert.That(await waiting.WaitAsync(Soon), Is.True, "The finishing lane wakes the loop for the deferred system.");

        // The finished lane drained its system, so the next dispatch finds the other two due.
        _work.DueSystems = [2, 3];
        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3);

        Assert.That(_work.LanesStarted, Is.EquivalentTo(new[] { 1, 2, 3 }));

        _work.ReleaseLane(2);
        _work.ReleaseLane(3);
    }

    [Test]
    public async Task DispatchAsync_SystemWhoseLaneFinished_CanBeDispatchedAgainAsync()
    {
        _work.DueSystems = [3];
        var scheduler = NewScheduler();
        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3);
        var waiting = scheduler.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        _work.ReleaseLane(3);
        await waiting.WaitAsync(Soon);

        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3, occurrence: 2);

        Assert.That(_work.LanesStarted, Is.EqualTo(new[] { 3, 3 }));

        _work.ReleaseLane(3);
    }

    [Test]
    public async Task LaneThatThrows_IsForgottenAndTheLoopCarriesOnAsync()
    {
        // One directory's fault must never delay another's passwords, and must never take the loop down.
        _work.DueSystems = [3, 4];
        _work.ThrowFor.Add(3);
        var scheduler = NewScheduler();

        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(4);
        var waiting = scheduler.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.That(await waiting.WaitAsync(Soon), Is.True, "The faulted lane still wakes the loop as it leaves.");

        await WaitUntilAsync(() => scheduler.HeldOff.ContainsKey(3));
        // Somebody acts on the system (a retry from the queue page); the hold-off lifts and it is tried again.
        scheduler.NotifyChanged(3);
        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3, occurrence: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_work.LanesStarted.Count(id => id == 3), Is.EqualTo(2), "The faulted system is tried again, and the fault did not stop the loop.");
            Assert.That(scheduler.LanesInFlight.Select(l => l.ConnectedSystemId), Does.Contain(4), "The other lane was untouched.");
        }

        _work.ReleaseLane(4);
    }

    [Test]
    public async Task DispatchAsync_WhenTheQueueReadFails_TheExceptionReachesRunAsyncsBoundaryAsync()
    {
        // DispatchAsync itself throws; RunAsync is what catches and waits the safety poll. Pinned so a future
        // change cannot quietly swallow the fault inside dispatch, where it would hide from the log.
        _work.FailOutlookWith = new InvalidOperationException("database gone");
        var scheduler = NewScheduler();

        Assert.That(() => scheduler.DispatchAsync(CancellationToken.None), Throws.InvalidOperationException);
        await Task.CompletedTask;
    }

    [Test]
    public async Task RunAsync_IterationThatFails_CarriesOnUntilCancelledAsync()
    {
        _work.FailOutlookWith = new InvalidOperationException("database gone");
        var scheduler = new PasswordDeliveryScheduler(_work, () => DateTime.UtcNow, TimeSpan.FromMilliseconds(30), 4, TimeSpan.FromMilliseconds(10));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await scheduler.RunAsync(cancellation.Token);

        Assert.That(_work.OutlookReads, Is.GreaterThanOrEqualTo(2), "The loop came round again after the failed iteration.");
    }

    [Test]
    public async Task LaneThatCouldNotDeliver_HoldsItsSystemOffUntilTheSafetyPollAsync()
    {
        // The lane gave its claims back unattempted, so the same rows are due again the moment it finished.
        // Without the hold-off the same failing lane would run back to back for as long as the directory stayed
        // down, bounded only by the connection timeout.
        _work.DueSystems = [3];
        _work.CouldNotDeliverFor.Add(3);
        var clock = Now;
        var scheduler = new PasswordDeliveryScheduler(_work, () => clock, SafetyPoll, 4, TimeSpan.FromMilliseconds(20));
        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3);
        var waiting = scheduler.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        _work.ReleaseLane(3);
        await waiting.WaitAsync(Soon);

        clock = Now.AddSeconds(10);
        var waitFor = await scheduler.DispatchAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_work.LanesStarted, Is.EqualTo(new[] { 3 }), "No second lane while the hold-off stands.");
            Assert.That(scheduler.HeldOff.Keys, Is.EqualTo(new[] { 3 }));
            Assert.That(waitFor, Is.EqualTo(SafetyPoll - TimeSpan.FromSeconds(10)), "The loop wakes when the hold-off ends.");
        }

        clock = Now + SafetyPoll;
        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3, occurrence: 2);

        Assert.That(_work.LanesStarted, Is.EqualTo(new[] { 3, 3 }), "Tried again once the hold-off has run out.");

        _work.ReleaseLane(3);
    }

    [Test]
    public async Task NotifyChanged_ForAHeldOffSystem_LiftsTheHoldOffAsync()
    {
        // A notification from outside a lane is somebody acting on the system (a new change, a retry from the
        // queue page) and deserves an attempt now rather than at the end of the hold-off.
        _work.DueSystems = [3];
        _work.CouldNotDeliverFor.Add(3);
        var scheduler = NewScheduler();
        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3);
        var waiting = scheduler.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        _work.ReleaseLane(3);
        await waiting.WaitAsync(Soon);
        await WaitUntilAsync(() => scheduler.HeldOff.ContainsKey(3));

        scheduler.NotifyChanged(3);
        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3, occurrence: 2);

        Assert.That(_work.LanesStarted, Is.EqualTo(new[] { 3, 3 }));

        _work.ReleaseLane(3);
    }

    [Test]
    public async Task LaneThatThrows_HoldsItsSystemOffAsync()
    {
        _work.DueSystems = [3];
        _work.ThrowFor.Add(3);
        var scheduler = NewScheduler();
        await scheduler.DispatchAsync(CancellationToken.None);
        var waiting = scheduler.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await waiting.WaitAsync(Soon);
        await WaitUntilAsync(() => scheduler.HeldOff.ContainsKey(3));

        await scheduler.DispatchAsync(CancellationToken.None);

        Assert.That(_work.LanesStarted, Is.EqualTo(new[] { 3 }), "A fault that repeats must not spin.");
    }

    #endregion

    #region heartbeat

    [Test]
    public async Task DispatchAsync_WritesTheHeartbeatEveryIterationAsync()
    {
        var scheduler = NewScheduler();

        await scheduler.DispatchAsync(CancellationToken.None);
        await scheduler.DispatchAsync(CancellationToken.None);

        Assert.That(_work.Heartbeats, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Heartbeat_WhileIdle_HasNoCurrentWorkAndNoDetailAsync()
    {
        var scheduler = NewScheduler();

        await scheduler.DispatchAsync(CancellationToken.None);

        var heartbeat = _work.Heartbeats.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(heartbeat.CurrentWork, Is.Null);
            Assert.That(heartbeat.CurrentWorkStartedAt, Is.Null);
            Assert.That(heartbeat.Detail, Is.Null);
        }
    }

    [Test]
    public async Task Heartbeat_WhileLanesRun_NamesTheSystemsBeingDeliveredToAsync()
    {
        _work.DueSystems = [3, 4];
        var scheduler = NewScheduler();
        await scheduler.DispatchAsync(CancellationToken.None);
        await _work.WaitForLaneToStartAsync(3);
        await _work.WaitForLaneToStartAsync(4);

        // The lanes name their systems once started; a wait slice writes the heartbeat that sees the names.
        await scheduler.WaitAsync(TimeSpan.FromMilliseconds(30), CancellationToken.None).WaitAsync(Soon);

        var heartbeat = _work.Heartbeats.Last();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(heartbeat.CurrentWork, Is.EqualTo("Delivering to System 3, System 4"));
            Assert.That(heartbeat.CurrentWorkStartedAt, Is.EqualTo(Now));
        }

        _work.ReleaseLane(3);
        _work.ReleaseLane(4);
    }

    [Test]
    public async Task Heartbeat_CarriesTheOutlookAsDetailAsync()
    {
        _work.Outlook = new PasswordQueueDeliveryOutlook { DueCount = 3, RetryingCount = 1, NextAttemptAt = new DateTime(2026, 9, 5, 9, 19, 0, DateTimeKind.Utc) };
        var scheduler = NewScheduler();

        await scheduler.DispatchAsync(CancellationToken.None);

        Assert.That(_work.Heartbeats.Single().Detail, Is.EqualTo("3 due, 1 retrying, next attempt 09:19 UTC"));
    }

    [Test]
    public void DescribeOutlook_NothingAhead_IsNull()
    {
        Assert.That(PasswordDeliveryScheduler.DescribeOutlook(new PasswordQueueDeliveryOutlook()), Is.Null);
    }

    [Test]
    public void DescribeOutlook_DueOnly_OmitsTheNextAttempt()
    {
        Assert.That(PasswordDeliveryScheduler.DescribeOutlook(new PasswordQueueDeliveryOutlook { DueCount = 2 }), Is.EqualTo("2 due, 0 retrying"));
    }

    [Test]
    public void DescribeCurrentWork_NoLanes_IsIdle()
    {
        Assert.That(PasswordDeliveryScheduler.DescribeCurrentWork([]), Is.EqualTo(((string?)null, (DateTime?)null)));
    }

    [Test]
    public void DescribeCurrentWork_UnnamedLane_FallsBackToTheSystemId()
    {
        var lane = new PasswordDeliveryLane(7, Now);

        Assert.That(PasswordDeliveryScheduler.DescribeCurrentWork([lane]).CurrentWork, Is.EqualTo("Delivering to Connected System 7"));
    }

    #endregion

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Soon;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition was not met in time.");
            await Task.Delay(5);
        }
    }

    /// <summary>
    /// A queue and a set of lanes the tests control: lanes block until released, so the tests can observe the
    /// loop with lanes in flight, and can be told to throw.
    /// </summary>
    private sealed class FakeWork : IPasswordDeliveryWork
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _gates = new();
        private readonly ConcurrentDictionary<(int, int), TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<int, int> _startCounts = new();

        public PasswordQueueDeliveryOutlook Outlook { get; set; } = new();
        public List<int> DueSystems { get; set; } = [];
        public HashSet<int> ThrowFor { get; } = [];
        public HashSet<int> CouldNotDeliverFor { get; } = [];
        public Exception? FailOutlookWith { get; set; }
        public int OutlookReads;
        public ConcurrentQueue<int> LanesStartedQueue { get; } = new();
        public List<int> LanesStarted => LanesStartedQueue.ToList();
        public List<(string? CurrentWork, DateTime? CurrentWorkStartedAt, string? Detail)> Heartbeats { get; } = [];

        public Task<PasswordQueueDeliveryOutlook> GetOutlookAsync(DateTime asOf, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref OutlookReads);
            if (FailOutlookWith != null)
                throw FailOutlookWith;
            return Task.FromResult(Outlook);
        }

        public Task<IReadOnlyList<int>> GetConnectedSystemIdsWithWorkDueAsync(DateTime asOf, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>(DueSystems.ToList());

        public async Task<PasswordDeliveryLaneOutcome> RunLaneAsync(PasswordDeliveryLane lane, CancellationToken cancellationToken)
        {
            lane.ConnectedSystemName = $"System {lane.ConnectedSystemId}";
            LanesStartedQueue.Enqueue(lane.ConnectedSystemId);
            var occurrence = _startCounts.AddOrUpdate(lane.ConnectedSystemId, 1, (_, n) => n + 1);
            StartedSignal(lane.ConnectedSystemId, occurrence).TrySetResult();

            if (ThrowFor.Contains(lane.ConnectedSystemId))
                throw new InvalidOperationException($"Lane {lane.ConnectedSystemId} failed.");

            await _gates.GetOrAdd(lane.ConnectedSystemId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

            return CouldNotDeliverFor.Contains(lane.ConnectedSystemId)
                ? PasswordDeliveryLaneOutcome.CouldNotDeliver
                : PasswordDeliveryLaneOutcome.Completed;
        }

        public Task WriteHeartbeatAsync(string? currentWork, DateTime? currentWorkStartedAt, string? detail, CancellationToken cancellationToken)
        {
            lock (Heartbeats)
                Heartbeats.Add((currentWork, currentWorkStartedAt, detail));
            return Task.CompletedTask;
        }

        public Task WaitForLaneToStartAsync(int connectedSystemId, int occurrence = 1) =>
            StartedSignal(connectedSystemId, occurrence).Task.WaitAsync(Soon);

        /// <summary>
        /// Lets a blocked lane finish, and re-arms its gate so a later lane for the same system blocks again.
        /// </summary>
        public void ReleaseLane(int connectedSystemId)
        {
            if (_gates.TryRemove(connectedSystemId, out var gate))
                gate.TrySetResult();
        }

        private TaskCompletionSource StartedSignal(int connectedSystemId, int occurrence) =>
            _started.GetOrAdd((connectedSystemId, occurrence), _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// A clock a test moves by hand. Its timers fire only when <see cref="Advance"/> passes their due time, on the test's own
/// thread, so what a timer does has happened by the time <see cref="Advance"/> returns (as long as what it does completes
/// without waiting on anything, which the in-memory repository's writes do).
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public ManualTimeProvider() : this(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>How many timers are due to fire, for tests asserting nothing is left scheduled.</summary>
    public int ScheduledTimerCount
    {
        get
        {
            lock (_timers)
                return _timers.Count(t => t.DueAt.HasValue);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_timers)
            _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves the clock on, firing every timer that falls due on the way, in the order they fall due.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_timers)
                next = _timers.Where(t => t.DueAt <= target).OrderBy(t => t.DueAt).FirstOrDefault();

            if (next == null)
                break;

            _now = next.DueAt!.Value;
            next.Fire();
        }

        _now = target;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            return true;
        }

        public void Fire()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : DueAt + _period;
            callback(state);
        }

        public void Dispose()
        {
            DueAt = null;
            lock (owner._timers)
                owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

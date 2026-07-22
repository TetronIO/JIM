// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Worker.Processors;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// Pins the sliding-window throughput and ETA behaviour of <see cref="ThroughputTracker"/> (#1005).
/// The displayed rate must reflect recent progress, not a whole-run average that misleads in both
/// directions on heavy-tailed runs, and the ETA must be suppressed ("finishing up") when the
/// counter has stopped advancing instead of fabricating a seconds-remaining figure.
/// </summary>
[TestFixture]
public class ThroughputTrackerTests
{
    private DateTime _now;

    private ThroughputTracker CreateTracker()
    {
        _now = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
        return new ThroughputTracker(() => _now);
    }

    private void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    [Test]
    public void FormatThroughput_SteadyRate_ReportsRateAndRemaining()
    {
        var tracker = CreateTracker();

        Advance(10);
        var suffix = tracker.FormatThroughput(processed: 1000, total: 10000);

        Assert.That(suffix, Does.Contain("100 obj/s"));
        Assert.That(suffix, Does.Contain("remaining"));
    }

    /// <summary>
    /// A fast phase followed by a slow phase: the run-average would report a high rate and a short
    /// ETA while actual progress is slow. The displayed rate must reflect the recent window.
    /// </summary>
    [Test]
    public void FormatThroughput_RecentSlowdown_ReportsWindowedRateNotRunAverage()
    {
        var tracker = CreateTracker();

        // Fast phase: 100,000 objects in the first 100 seconds (1,000/s run average so far).
        for (var i = 1; i <= 10; i++)
        {
            Advance(10);
            tracker.FormatThroughput(processed: i * 10000, total: 105000);
        }

        // Slow phase: 10 objects/second for the next 300 seconds.
        var processed = 100000;
        string suffix = string.Empty;
        for (var i = 0; i < 30; i++)
        {
            Advance(10);
            processed += 100;
            suffix = tracker.FormatThroughput(processed, total: 105000);
        }

        // Run average would be ~257 obj/s with a ~8 sec ETA; the window must show ~10 obj/s.
        Assert.That(suffix, Does.Contain("10 obj/s"),
            "The displayed rate must be computed over the recent window, not the whole run");
        Assert.That(suffix, Does.Not.Contain("257"));
    }

    /// <summary>
    /// The counter stops advancing (heavyweight final batches, silent bookkeeping): the display
    /// must stop fabricating a seconds-remaining figure and qualify instead.
    /// </summary>
    [Test]
    public void FormatThroughput_CounterStalled_ShowsFinishingUpInsteadOfEta()
    {
        var tracker = CreateTracker();

        for (var i = 1; i <= 10; i++)
        {
            Advance(10);
            tracker.FormatThroughput(processed: i * 10000, total: 105000);
        }

        // The counter now sits at 100,000 of 105,000 while the tail runs silently.
        string suffix = string.Empty;
        for (var i = 0; i < 20; i++)
        {
            Advance(20);
            suffix = tracker.FormatThroughput(processed: 100000, total: 105000);
        }

        Assert.That(suffix, Does.Contain("finishing up"),
            "A stalled counter must not fabricate a seconds-remaining estimate");
        Assert.That(suffix, Does.Not.Contain("remaining"));
    }

    /// <summary>
    /// Early in a run the window is too thin to be meaningful; the run average is the best
    /// available signal (parity with the previous behaviour).
    /// </summary>
    [Test]
    public void FormatThroughput_EarlyInRun_FallsBackToRunAverage()
    {
        var tracker = CreateTracker();

        Advance(3);
        var suffix = tracker.FormatThroughput(processed: 300, total: 10000);

        Assert.That(suffix, Does.Contain("100 obj/s"));
    }

    [Test]
    public void FormatThroughput_UnderTwoSecondsElapsed_ReturnsEmpty()
    {
        var tracker = CreateTracker();

        Advance(1);
        Assert.That(tracker.FormatThroughput(processed: 100, total: 1000), Is.Empty);
    }

    /// <summary>
    /// A counter reset (a phase restarting its count) must start the window afresh rather than
    /// producing a negative windowed rate.
    /// </summary>
    [Test]
    public void FormatThroughput_CounterReset_StartsWindowAfresh()
    {
        var tracker = CreateTracker();

        Advance(30);
        tracker.FormatThroughput(processed: 5000, total: 10000);

        // Phase restart: counter drops, then advances steadily at 50/s.
        string suffix = string.Empty;
        for (var i = 1; i <= 6; i++)
        {
            Advance(10);
            suffix = tracker.FormatThroughput(processed: i * 500, total: 10000);
        }

        Assert.That(suffix, Does.Contain("50 obj/s"));
        Assert.That(suffix, Does.Not.Contain("-"));
    }

    [Test]
    public void FormatCompletion_ReportsRunAverage()
    {
        var tracker = CreateTracker();

        Advance(100);
        var suffix = tracker.FormatCompletion(processed: 10000);

        Assert.That(suffix, Does.Contain("avg 100 obj/s"));
        Assert.That(suffix, Does.Contain("1 min 40 sec"));
    }
}

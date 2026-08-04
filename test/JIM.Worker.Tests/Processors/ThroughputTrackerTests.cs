// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Worker.Processors;

namespace JIM.Worker.Tests.Processors;

/// <summary>
/// Pins the completion summary a finished run leaves on its Activity.
/// </summary>
/// <remarks>
/// This fixture used to pin live throughput and ETA behaviour too (#1005: a sliding window rather
/// than a run average, and "finishing up" in place of a fabricated estimate once the counter
/// stopped advancing). That measurement moved out of the worker: the portal and the progress API
/// derive both from the Activity's own counters, so the worker's figures were consumed only as
/// prose inside the progress message and the two estimators disagreed on screen. The live
/// equivalents of those cases are covered by ActivityEtaTrackerTests and RunProgressMetricsTests.
/// </remarks>
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
    public void FormatCompletion_ReportsRunAverage()
    {
        var tracker = CreateTracker();

        Advance(100);
        var suffix = tracker.FormatCompletion(processed: 10000);

        Assert.Multiple(() =>
        {
            Assert.That(suffix, Does.Contain("avg 100 obj/s"));
            Assert.That(suffix, Does.Contain("1 min 40 sec"));
        });
    }

    [Test]
    public void FormatCompletion_LongRun_ReportsElapsedInHours()
    {
        var tracker = CreateTracker();

        Advance(3720);
        var suffix = tracker.FormatCompletion(processed: 372000);

        Assert.That(suffix, Does.Contain("1 hr 2 min"));
    }

    /// <summary>
    /// A run that finished before the clock could measure it has no meaningful average, and an
    /// operation that processed nothing has nothing to average; both say nothing rather than
    /// dividing by a near-zero elapsed time.
    /// </summary>
    [Test]
    public void FormatCompletion_NothingWorthAveraging_SaysNothing()
    {
        var tracker = CreateTracker();

        Assert.Multiple(() =>
        {
            Assert.That(tracker.FormatCompletion(processed: 10000), Is.Empty, "under a second elapsed");

            Advance(100);
            Assert.That(tracker.FormatCompletion(processed: 0), Is.Empty, "nothing processed");
        });
    }
}

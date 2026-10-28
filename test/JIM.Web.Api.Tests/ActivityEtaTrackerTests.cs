// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Web.Services;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Verifies the Activity ETA tracker (#202): a windowed objects-per-second rate computed from
/// successive progress samples, with counter-reset detection (run phases reuse the progress
/// counters) and bounded per-Activity state.
/// </summary>
[TestFixture]
public class ActivityEtaTrackerTests
{
    private ManualTimeProvider _clock = null!;
    private ActivityEtaTracker _tracker = null!;
    private Guid _activityId;

    [SetUp]
    public void SetUp()
    {
        _clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 24, 10, 0, 0, TimeSpan.Zero));
        _tracker = new ActivityEtaTracker(_clock);
        _activityId = Guid.NewGuid();
    }

    /// <summary>
    /// Minimal controllable clock; avoids taking a dependency on a time-testing package.
    /// </summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _utcNow = start;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan by) => _utcNow += by;
    }

    [Test]
    public void RecordSample_FirstSample_ReturnsNoEstimate()
    {
        var estimate = _tracker.RecordSample(_activityId, objectsProcessed: 100, objectsToProcess: 1000);

        Assert.That(estimate.ObjectsPerSecond, Is.Null);
        Assert.That(estimate.EstimatedSecondsRemaining, Is.Null);
    }

    [Test]
    public void RecordSample_TwoSamplesWithProgress_ReturnsRateAndEta()
    {
        _tracker.RecordSample(_activityId, 0, 1000);
        _clock.Advance(TimeSpan.FromSeconds(10));

        var estimate = _tracker.RecordSample(_activityId, 100, 1000);

        Assert.That(estimate.ObjectsPerSecond, Is.EqualTo(10d).Within(0.001));
        Assert.That(estimate.EstimatedSecondsRemaining, Is.EqualTo(90d).Within(0.001));
    }

    [Test]
    public void RecordSample_NoProgressBetweenSamples_ReturnsZeroRateAndNoEta()
    {
        _tracker.RecordSample(_activityId, 100, 1000);
        _clock.Advance(TimeSpan.FromSeconds(10));

        var estimate = _tracker.RecordSample(_activityId, 100, 1000);

        Assert.That(estimate.ObjectsPerSecond, Is.EqualTo(0d).Within(0.001));
        Assert.That(estimate.EstimatedSecondsRemaining, Is.Null);
    }

    [Test]
    public void RecordSample_ProcessedCountDecreased_StartsANewWindow()
    {
        _tracker.RecordSample(_activityId, 900, 1000);
        _clock.Advance(TimeSpan.FromSeconds(5));
        _tracker.RecordSample(_activityId, 950, 1000);
        _clock.Advance(TimeSpan.FromSeconds(5));

        // A new run phase reset the counters (for example importing -> saving changes).
        var estimate = _tracker.RecordSample(_activityId, 10, 200);

        Assert.That(estimate.ObjectsPerSecond, Is.Null);
        Assert.That(estimate.EstimatedSecondsRemaining, Is.Null);
    }

    [Test]
    public void RecordSample_TotalChanged_StartsANewWindow()
    {
        _tracker.RecordSample(_activityId, 100, 1000);
        _clock.Advance(TimeSpan.FromSeconds(5));

        var estimate = _tracker.RecordSample(_activityId, 150, 2000);

        Assert.That(estimate.ObjectsPerSecond, Is.Null);
        Assert.That(estimate.EstimatedSecondsRemaining, Is.Null);
    }

    [Test]
    public void RecordSample_IndeterminateTotal_ReturnsRateWithoutEta()
    {
        _tracker.RecordSample(_activityId, 0, 0);
        _clock.Advance(TimeSpan.FromSeconds(10));

        var estimate = _tracker.RecordSample(_activityId, 50, 0);

        Assert.That(estimate.ObjectsPerSecond, Is.EqualTo(5d).Within(0.001));
        Assert.That(estimate.EstimatedSecondsRemaining, Is.Null);
    }

    [Test]
    public void RecordSample_SamplesOlderThanWindowButNoneNewer_StillProducesAnEstimate()
    {
        _tracker.RecordSample(_activityId, 0, 3000);
        _clock.Advance(TimeSpan.FromSeconds(150));

        // The only prior sample is older than the rate window; it must be retained rather than
        // trimmed away, otherwise slow phases would never produce an estimate.
        var estimate = _tracker.RecordSample(_activityId, 1500, 3000);

        Assert.That(estimate.ObjectsPerSecond, Is.EqualTo(10d).Within(0.001));
        Assert.That(estimate.EstimatedSecondsRemaining, Is.EqualTo(150d).Within(0.001));
    }

    [Test]
    public void RecordSample_ManySamples_RateReflectsTheRecentWindowOnly()
    {
        // Slow start: 1 object/second for 60 seconds.
        _tracker.RecordSample(_activityId, 0, 10000);
        _clock.Advance(TimeSpan.FromSeconds(60));
        _tracker.RecordSample(_activityId, 60, 10000);

        // Then fast: 100 objects/second for 180 seconds, sampled every 30 seconds. By the end,
        // every slow-phase sample is older than the two-minute rate window.
        for (var i = 1; i <= 6; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(30));
            _tracker.RecordSample(_activityId, 60 + i * 3000, 100000);
        }

        _clock.Advance(TimeSpan.FromSeconds(30));
        var estimate = _tracker.RecordSample(_activityId, 60 + 7 * 3000, 100000);

        Assert.That(estimate.ObjectsPerSecond, Is.EqualTo(100d).Within(1d));
    }

    [Test]
    public void RecordSample_CounterStillLongerThanTheWindowAfterProgress_ReportsASlowingRateRatherThanZero()
    {
        // The worker saves its count once per page, and a page of large groups can take longer
        // than the rate window. Every sample inside the window then carried the same count, so
        // the rate read as zero and a run still working looked like one tidying up. The window
        // has to reach back to the last point the counter moved.
        _tracker.RecordSample(_activityId, 0, 10000);
        _clock.Advance(TimeSpan.FromSeconds(30));
        _tracker.RecordSample(_activityId, 500, 10000);

        // Polled every 10 seconds for three minutes with no movement.
        for (var i = 0; i < 18; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            _tracker.RecordSample(_activityId, 500, 10000);
        }

        var estimate = _tracker.RecordSample(_activityId, 500, 10000);

        using (Assert.EnterMultipleScope())
        {
            // 500 objects over the 210 seconds since the counter last stood still before moving.
            Assert.That(estimate.ObjectsPerSecond, Is.EqualTo(500d / 210d).Within(0.001));
            Assert.That(estimate.EstimatedSecondsRemaining, Is.EqualTo(9500d / (500d / 210d)).Within(0.1));
        }
    }

    [Test]
    public void RecordSample_CounterStalledAfterSteadyProgress_KeepsTheWindowOfProgressBeforeTheStall()
    {
        // 500 objects every 15 seconds for two minutes, then a page that holds the counter for
        // three. Measured from the last couple of movements alone, the rate collapsed to a few
        // objects a second and the time remaining leapt from minutes to over an hour, when the
        // run was minutes from the end (seen live on a 790,000-object Full Synchronisation).
        _tracker.RecordSample(_activityId, 0, 20000);
        for (var i = 1; i <= 8; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(15));
            _tracker.RecordSample(_activityId, i * 500, 20000);
        }

        for (var i = 0; i < 18; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            _tracker.RecordSample(_activityId, 4000, 20000);
        }

        var estimate = _tracker.RecordSample(_activityId, 4000, 20000);

        // 4,000 objects over the 120 seconds of progress plus the 180-second stall.
        Assert.That(estimate.ObjectsPerSecond, Is.EqualTo(4000d / 300d).Within(0.001));
    }

    [Test]
    public void RecordSample_CounterStalledAfterProgress_RateKeepsFallingTheLongerItStalls()
    {
        _tracker.RecordSample(_activityId, 0, 10000);
        _clock.Advance(TimeSpan.FromSeconds(30));
        _tracker.RecordSample(_activityId, 500, 10000);

        _clock.Advance(TimeSpan.FromMinutes(3));
        var earlier = _tracker.RecordSample(_activityId, 500, 10000);
        _clock.Advance(TimeSpan.FromMinutes(3));
        var later = _tracker.RecordSample(_activityId, 500, 10000);

        Assert.That(later.ObjectsPerSecond, Is.GreaterThan(0d).And.LessThan(earlier.ObjectsPerSecond!.Value));
    }

    [Test]
    public void RecordSample_FrequentPollsDuringALongStall_DoNotPushTheLastMovementOutOfTheWindow()
    {
        // The Activity page polls every couple of seconds; a stall of several minutes is far more
        // polls than the per-Activity sample cap.
        _tracker.RecordSample(_activityId, 0, 10000);
        _clock.Advance(TimeSpan.FromSeconds(30));
        _tracker.RecordSample(_activityId, 500, 10000);

        for (var i = 0; i < 200; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            _tracker.RecordSample(_activityId, 500, 10000);
        }

        var estimate = _tracker.RecordSample(_activityId, 500, 10000);

        Assert.That(estimate.ObjectsPerSecond, Is.GreaterThan(0d));
    }

    [Test]
    public void Remove_ClearsTheActivityState()
    {
        _tracker.RecordSample(_activityId, 0, 1000);
        _clock.Advance(TimeSpan.FromSeconds(10));
        _tracker.Remove(_activityId);

        var estimate = _tracker.RecordSample(_activityId, 100, 1000);

        Assert.That(estimate.ObjectsPerSecond, Is.Null);
        Assert.That(estimate.EstimatedSecondsRemaining, Is.Null);
    }

    [Test]
    public void RecordSample_MoreActivitiesThanTheCap_EvictsTheLeastRecentlyTouched()
    {
        var cappedTracker = new ActivityEtaTracker(_clock, maxTrackedActivities: 3);
        var evicted = Guid.NewGuid();
        var kept = Guid.NewGuid();

        cappedTracker.RecordSample(evicted, 0, 1000);
        _clock.Advance(TimeSpan.FromSeconds(1));
        cappedTracker.RecordSample(kept, 0, 1000);
        _clock.Advance(TimeSpan.FromSeconds(1));
        cappedTracker.RecordSample(Guid.NewGuid(), 0, 1000);
        _clock.Advance(TimeSpan.FromSeconds(1));
        cappedTracker.RecordSample(Guid.NewGuid(), 0, 1000);
        _clock.Advance(TimeSpan.FromSeconds(1));

        // The kept Activity (not the least recently touched) retained its history.
        var keptEstimate = cappedTracker.RecordSample(kept, 40, 1000);
        Assert.That(keptEstimate.ObjectsPerSecond, Is.Not.Null);

        // The evicted Activity lost its history, so its next sample starts a fresh window.
        var estimate = cappedTracker.RecordSample(evicted, 500, 1000);
        Assert.That(estimate.ObjectsPerSecond, Is.Null);
    }
}

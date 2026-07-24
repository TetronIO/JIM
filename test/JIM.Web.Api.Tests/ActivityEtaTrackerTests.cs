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

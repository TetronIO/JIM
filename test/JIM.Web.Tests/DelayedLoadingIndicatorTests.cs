// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Pins when a list's loading indicator shows. The two failures it guards against pull in opposite directions: a
/// spinner that flashes for a load the reader would never have noticed, and a list that sits silent through a
/// five-second search. The threshold is driven by hand here, so no test waits on a real clock.
/// </summary>
[TestFixture]
public class DelayedLoadingIndicatorTests
{
    private ManualThreshold _threshold = null!;
    private DelayedLoadingIndicator _indicator = null!;
    private List<bool> _changes = null!;

    [SetUp]
    public void SetUp()
    {
        _threshold = new ManualThreshold();
        _indicator = new DelayedLoadingIndicator(_threshold.WaitAsync);
        _changes = [];
        _indicator.VisibilityChanged += () => _changes.Add(_indicator.IsVisible);
    }

    [TearDown]
    public void TearDown() => _indicator.Dispose();

    [Test]
    public async Task TrackAsync_LoadFinishesBeforeThreshold_NeverShowsAsync()
    {
        var result = await _indicator.TrackAsync(() => Task.FromResult(42));

        // The threshold passing after the load has finished must not show anything either.
        _threshold.Pass();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(42));
            Assert.That(_indicator.IsVisible, Is.False);
            Assert.That(_changes, Is.Empty);
        }
    }

    [Test]
    public async Task TrackAsync_LoadOutlastsThreshold_ShowsUntilLoadFinishesAsync()
    {
        var load = new TaskCompletionSource<int>();
        var tracked = _indicator.TrackAsync(() => load.Task);

        Assert.That(_indicator.IsVisible, Is.False, "Nothing shows before the threshold passes.");

        _threshold.Pass();
        Assert.That(_indicator.IsVisible, Is.True, "A load still running at the threshold shows the indicator.");

        load.SetResult(7);
        await tracked;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_indicator.IsVisible, Is.False);
            Assert.That(_changes, Is.EqualTo(new[] { true, false }));
        }
    }

    [Test]
    public async Task TrackAsync_OverlappingLoads_StaysVisibleUntilTheLastFinishesAsync()
    {
        var first = new TaskCompletionSource<int>();
        var second = new TaskCompletionSource<int>();
        var trackedFirst = _indicator.TrackAsync(() => first.Task);
        var trackedSecond = _indicator.TrackAsync(() => second.Task);

        Assert.That(_threshold.WaitCount, Is.EqualTo(1), "Overlapping loads share one busy period and one threshold.");

        _threshold.Pass();
        first.SetResult(1);
        await trackedFirst;
        Assert.That(_indicator.IsVisible, Is.True, "Another load is still running.");

        second.SetResult(2);
        await trackedSecond;
        Assert.That(_indicator.IsVisible, Is.False);
    }

    [Test]
    public void TrackAsync_LoadCancelled_HidesAndRethrows()
    {
        var load = new TaskCompletionSource<int>();
        var tracked = _indicator.TrackAsync(() => load.Task);
        _threshold.Pass();

        load.SetCanceled();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(async () => await tracked, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(_indicator.IsVisible, Is.False);
        }
    }

    [Test]
    public async Task TrackAsync_LoadAfterAnEarlierBusyPeriod_WaitsForItsOwnThresholdAsync()
    {
        var earlier = new TaskCompletionSource<int>();
        var trackedEarlier = _indicator.TrackAsync(() => earlier.Task);
        _threshold.Pass();
        earlier.SetResult(1);
        await trackedEarlier;

        var later = new TaskCompletionSource<int>();
        var trackedLater = _indicator.TrackAsync(() => later.Task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_threshold.WaitCount, Is.EqualTo(2));
            Assert.That(_indicator.IsVisible, Is.False, "A new busy period starts from nothing shown.");
        }

        later.SetResult(2);
        await trackedLater;
    }

    [Test]
    public async Task TrackAsync_ThresholdOfEndedBusyPeriodPassesLate_DoesNotShowAsync()
    {
        // The first busy period ends before its threshold; the threshold then passes during the second, which must
        // keep waiting for its own rather than show early on the strength of the stale one.
        var trackedFirst = _indicator.TrackAsync(() => Task.FromResult(1));
        await trackedFirst;
        var staleWait = _threshold.Latest;

        var second = new TaskCompletionSource<int>();
        var trackedSecond = _indicator.TrackAsync(() => second.Task);
        staleWait.TrySetResult();

        Assert.That(_indicator.IsVisible, Is.False);

        second.SetResult(2);
        await trackedSecond;
    }

    /// <summary>A threshold the test passes by hand. Each busy period's wait is its own task.</summary>
    private sealed class ManualThreshold
    {
        private readonly List<TaskCompletionSource> _waits = [];

        public int WaitCount => _waits.Count;

        public TaskCompletionSource Latest => _waits[^1];

        public Task WaitAsync(CancellationToken token)
        {
            var wait = new TaskCompletionSource();
            token.Register(() => wait.TrySetCanceled(token));
            _waits.Add(wait);
            return wait.Task;
        }

        public void Pass() => Latest.TrySetResult();
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// How long a Full Synchronisation preview is expected to take (#1530), stated before a large one starts. It is never
/// faster than the reference rate: a routine Full Synchronisation skips objects unchanged since the last one, so its
/// own speed overstates how fast a full evaluation runs, and the preview is asked for exactly when configuration has
/// changed and nothing will be skipped. A system measured slower than the reference is estimated at its own speed.
/// </summary>
[TestFixture]
public class FullSynchronisationDurationEstimateTests
{
    private static readonly double ReferenceRate = FullSynchronisationDurationEstimate.ReferenceObjectsPerSecond;

    [Test]
    public void For_NoPreviousRun_UsesTheReferenceRate()
    {
        var estimate = FullSynchronisationDurationEstimate.For(objects: 100_000, lastRunObjects: null, lastRunTime: null);

        Assert.That(estimate, Is.EqualTo(TimeSpan.FromSeconds(100_000 / ReferenceRate)));
    }

    [Test]
    public void For_APreviousRunSlowerThanTheReference_UsesItsRate()
    {
        // 1,000 objects in 1,000 seconds: one a second, well below the reference.
        var estimate = FullSynchronisationDurationEstimate.For(objects: 5_000, lastRunObjects: 1_000, lastRunTime: TimeSpan.FromSeconds(1_000));

        Assert.That(estimate, Is.EqualTo(TimeSpan.FromSeconds(5_000)));
    }

    [Test]
    public void For_APreviousRunFasterThanTheReference_StillUsesTheReferenceRate()
    {
        // A routine run that skipped most of its objects as unchanged: far faster than any full evaluation.
        var estimate = FullSynchronisationDurationEstimate.For(objects: 100_000, lastRunObjects: 100_000, lastRunTime: TimeSpan.FromSeconds(20));

        Assert.That(estimate, Is.EqualTo(TimeSpan.FromSeconds(100_000 / ReferenceRate)),
            "a run that skipped unchanged objects says nothing about how fast every object can be evaluated");
    }

    [TestCase(0, 60, TestName = "For_APreviousRunOfNoObjects_UsesTheReferenceRate")]
    [TestCase(1_000, 0, TestName = "For_APreviousRunWithNoRecordedTime_UsesTheReferenceRate")]
    public void For_APreviousRunWithNothingToMeasure_UsesTheReferenceRate(int lastRunObjects, int lastRunSeconds)
    {
        var estimate = FullSynchronisationDurationEstimate.For(objects: 10_000, lastRunObjects, TimeSpan.FromSeconds(lastRunSeconds));

        Assert.That(estimate, Is.EqualTo(TimeSpan.FromSeconds(10_000 / ReferenceRate)));
    }
}

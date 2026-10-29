// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Preview;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// About how long processing every object of an Object Type takes, at the speed of the Connected System's last Full
/// Synchronisation (#2615). Unlike the preview estimate it applies no reference rate: the system's own last run is the only
/// evidence, and with no run there is no estimate.
/// </summary>
[TestFixture]
public class ConfigurationChangeProcessingEstimateTests
{
    [Test]
    public void For_LastRunKnown_ScalesByTheSpeedOfThatRun()
    {
        // 100,000 objects in 50 minutes is 2,000 a minute: 30,000 take 15.
        var estimate = ConfigurationChangeProcessingEstimate.For(30_000, 100_000, TimeSpan.FromMinutes(50));

        Assert.That(estimate, Is.EqualTo(TimeSpan.FromMinutes(15)));
    }

    [Test]
    public void For_TypeAsLargeAsTheWholePopulation_TakesAsLongAsTheLastRun()
    {
        Assert.That(ConfigurationChangeProcessingEstimate.For(100_000, 100_000, TimeSpan.FromMinutes(50)), Is.EqualTo(TimeSpan.FromMinutes(50)));
    }

    [Test]
    public void For_LastRunFasterThanAnyReferenceRate_IsNotRaisedToOne()
    {
        // 1,000,000 objects in 10 seconds is 100,000 a second, far above the preview estimate's 50 a second cap; this
        // estimate keeps the system's own speed, because the measurement is the only evidence.
        var estimate = ConfigurationChangeProcessingEstimate.For(500_000, 1_000_000, TimeSpan.FromSeconds(10));

        Assert.That(estimate, Is.EqualTo(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void For_NoObjectsToProcess_IsZero()
    {
        Assert.That(ConfigurationChangeProcessingEstimate.For(0, 100_000, TimeSpan.FromMinutes(50)), Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void For_NoLastRun_HasNoEstimate()
    {
        Assert.That(ConfigurationChangeProcessingEstimate.For(30_000, null, null), Is.Null, "there is no speed to estimate from");
    }

    [Test]
    public void For_LastRunRecordedNoTime_HasNoEstimate()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConfigurationChangeProcessingEstimate.For(30_000, 100_000, null), Is.Null);
            Assert.That(ConfigurationChangeProcessingEstimate.For(30_000, 100_000, TimeSpan.Zero), Is.Null, "a run that took no time says nothing about speed");
        }
    }

    [Test]
    public void For_LastRunWentThroughNoObjects_HasNoEstimate()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConfigurationChangeProcessingEstimate.For(30_000, 0, TimeSpan.FromMinutes(5)), Is.Null);
            Assert.That(ConfigurationChangeProcessingEstimate.For(30_000, null, TimeSpan.FromMinutes(5)), Is.Null);
        }
    }

    [Test]
    public void For_NoObjectsAndNoLastRun_HasNoEstimate()
    {
        // Nothing to say about a duration without a speed, even when there is nothing to process.
        Assert.That(ConfigurationChangeProcessingEstimate.For(0, null, null), Is.Null);
    }
}

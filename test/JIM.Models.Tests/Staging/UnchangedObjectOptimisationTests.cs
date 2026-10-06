// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Models.Tests.Staging;

/// <summary>
/// The unchanged-object optimisation of a Full Synchronisation: which objects it skips, and when it is on at all. The
/// run's loader and the Full Synchronisation preview (#1530) both answer from these, so a preview never proposes a
/// change for an object the run will not process.
/// </summary>
[TestFixture]
public class UnchangedObjectOptimisationTests
{
    private static readonly DateTime Watermark = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private static ConnectedSystemObject JoinedObject(DateTime created, DateTime? lastUpdated = null) => new()
    {
        Status = ConnectedSystemObjectStatus.Normal,
        MetaverseObjectId = Guid.NewGuid(),
        Created = created,
        LastUpdated = lastUpdated
    };

    [Test]
    public void IsUnchangedSince_JoinedObjectLastUpdatedBeforeTheWatermark_ReturnsTrue()
    {
        Assert.That(JoinedObject(Watermark.AddDays(-2), Watermark.AddDays(-1)).IsUnchangedSince(Watermark), Is.True);
    }

    [Test]
    public void IsUnchangedSince_NeverUpdatedObjectCreatedBeforeTheWatermark_ReturnsTrue()
    {
        Assert.That(JoinedObject(Watermark.AddDays(-1)).IsUnchangedSince(Watermark), Is.True);
    }

    [Test]
    public void IsUnchangedSince_ObjectUpdatedAfterTheWatermark_ReturnsFalse()
    {
        Assert.That(JoinedObject(Watermark.AddDays(-2), Watermark.AddMinutes(1)).IsUnchangedSince(Watermark), Is.False);
    }

    [Test]
    public void IsUnchangedSince_NeverUpdatedObjectCreatedAfterTheWatermark_ReturnsFalse()
    {
        Assert.That(JoinedObject(Watermark.AddMinutes(1)).IsUnchangedSince(Watermark), Is.False);
    }

    [Test]
    public void IsUnchangedSince_ObjectNotJoined_ReturnsFalse()
    {
        var cso = JoinedObject(Watermark.AddDays(-1));
        cso.MetaverseObjectId = null;

        Assert.That(cso.IsUnchangedSince(Watermark), Is.False, "an object not yet joined may join or project");
    }

    [TestCase(ConnectedSystemObjectStatus.PendingProvisioning)]
    [TestCase(ConnectedSystemObjectStatus.Obsolete)]
    public void IsUnchangedSince_ObjectNotNormal_ReturnsFalse(ConnectedSystemObjectStatus status)
    {
        var cso = JoinedObject(Watermark.AddDays(-1));
        cso.Status = status;

        Assert.That(cso.IsUnchangedSince(Watermark), Is.False);
    }

    [Test]
    public void IsUnchangedSince_ObjectFlaggedForScopeReview_ReturnsFalse()
    {
        var cso = JoinedObject(Watermark.AddDays(-1));
        cso.ScopeReviewPending = true;

        Assert.That(cso.IsUnchangedSince(Watermark), Is.False, "a relative date may have moved it in or out of scope");
    }

    [Test]
    public void IsUnchangedSince_ObjectMarkedForDerivedInputChange_ReturnsFalse()
    {
        var cso = JoinedObject(Watermark.AddDays(-1));
        cso.DerivedInputChangePending = true;

        Assert.That(cso.IsUnchangedSince(Watermark), Is.False, "a derived flow's input changed in another system");
    }

    [Test]
    public void GetUnchangedObjectWatermark_ConfigurationUnchangedSinceItWasLastFullyApplied_ReturnsTheLastSynchronisation()
    {
        var system = new ConnectedSystem { LastSyncCompletedAt = Watermark, ConfigurationLastFullyAppliedAt = Watermark.AddMinutes(-5) };

        Assert.That(system.GetUnchangedObjectWatermark(latestConfigurationChange: Watermark.AddDays(-1)), Is.EqualTo(Watermark));
    }

    [Test]
    public void GetUnchangedObjectWatermark_NoConfigurationChangeRecorded_ReturnsTheLastSynchronisation()
    {
        var system = new ConnectedSystem { LastSyncCompletedAt = Watermark, ConfigurationLastFullyAppliedAt = Watermark.AddMinutes(-5) };

        Assert.That(system.GetUnchangedObjectWatermark(latestConfigurationChange: null), Is.EqualTo(Watermark));
    }

    [Test]
    public void GetUnchangedObjectWatermark_ConfigurationChangedSinceItWasLastFullyApplied_ReturnsNull()
    {
        var system = new ConnectedSystem { LastSyncCompletedAt = Watermark, ConfigurationLastFullyAppliedAt = Watermark.AddMinutes(-5) };

        Assert.That(system.GetUnchangedObjectWatermark(latestConfigurationChange: Watermark.AddMinutes(-1)), Is.Null,
            "a configuration change must reach every object");
    }

    [Test]
    public void GetUnchangedObjectWatermark_ConfigurationNeverFullyApplied_ReturnsNull()
    {
        var system = new ConnectedSystem { LastSyncCompletedAt = Watermark };

        Assert.That(system.GetUnchangedObjectWatermark(latestConfigurationChange: Watermark.AddDays(-1)), Is.Null);
    }

    [Test]
    public void GetUnchangedObjectWatermark_NeverSynchronised_ReturnsNull()
    {
        Assert.That(new ConnectedSystem().GetUnchangedObjectWatermark(latestConfigurationChange: null), Is.Null);
    }
}

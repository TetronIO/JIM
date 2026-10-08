// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Activities;
using JIM.Models.Preview;
using NUnit.Framework;

namespace JIM.Models.Tests.Preview;

/// <summary>
/// What overtook a preview, said in a way an administrator can act on (#2022): which run, on which Connected System, or
/// which configuration changed. The sentence is snapshotted onto the Activity of a change that cites an out-of-date
/// preview, so it has to name things itself rather than point at an Activity that retention will remove.
/// </summary>
[TestFixture]
public class PreviewOvertakingActivityTests
{
    private static readonly DateTime When = new(2026, 10, 8, 10, 42, 0, DateTimeKind.Utc);

    [Test]
    public void Describe_ARun_NamesTheRunProfileAndItsConnectedSystem()
    {
        var run = Overtaking(ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute, "Delta Import", "HR Import");

        var description = run.Describe();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(description, Does.Contain("Delta Import"));
            Assert.That(description, Does.Contain("HR Import"));
        }
    }

    [Test]
    public void Describe_AConfigurationChange_NamesWhatChanged()
    {
        var change = Overtaking(ActivityTargetType.SynchronisationRule, ActivityTargetOperationType.Update, "HR Users", null);

        Assert.That(change.Describe(), Does.Contain("HR Users"));
    }

    [Test]
    public void Describe_DifferentOperationsOnOneObject_AreToldApart()
    {
        // A cleared Connector Space and a deleted Connected System both move data; the administrator needs to know which.
        var cleared = Overtaking(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Clear, "Old HR", null);
        var deleted = Overtaking(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Delete, "Old HR", null);

        Assert.That(cleared.Describe(), Is.Not.EqualTo(deleted.Describe()));
    }

    // Every kind of Activity the staleness query counts, so a kind added there without a sentence here is caught.
    [TestCase(ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute)]
    [TestCase(ActivityTargetType.MetaverseObjectHousekeeping, ActivityTargetOperationType.Execute)]
    [TestCase(ActivityTargetType.TemporalScopeReconciliation, ActivityTargetOperationType.Execute)]
    [TestCase(ActivityTargetType.DataGeneration, ActivityTargetOperationType.Execute)]
    [TestCase(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Clear)]
    [TestCase(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.SchemaRefreshRemoval)]
    [TestCase(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Deprovision)]
    [TestCase(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Delete)]
    [TestCase(ActivityTargetType.SynchronisationRule, ActivityTargetOperationType.RecallAttributeValues)]
    [TestCase(ActivityTargetType.SynchronisationRule, ActivityTargetOperationType.Create)]
    [TestCase(ActivityTargetType.ObjectMatchingRule, ActivityTargetOperationType.Delete)]
    public void Describe_AnyCountedKindWithoutAName_StillSaysSomething(ActivityTargetType targetType, ActivityTargetOperationType operation)
    {
        var description = Overtaking(targetType, operation, null, null).Describe();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(description, Is.Not.Empty);
            Assert.That(description, Does.Not.Contain("''"), "a missing name must not leave empty quotes behind");
        }
    }

    [Test]
    public void Staleness_NothingOvertookIt_IsCurrentAndSaysNothing()
    {
        var staleness = new ConfigurationChangePreviewStaleness(null, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(staleness.IsStale, Is.False);
            Assert.That(staleness.OvertakenAt, Is.Null);
            Assert.That(staleness.Describe(), Is.Null);
        }
    }

    [Test]
    public void Staleness_DataAndConfigurationOvertookIt_NamesBothAndIsDatedByTheLater()
    {
        var run = Overtaking(ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute, "Delta Import", "HR Import");
        var edit = Overtaking(ActivityTargetType.SynchronisationRule, ActivityTargetOperationType.Update, "HR Users", null, When.AddMinutes(5));

        var staleness = new ConfigurationChangePreviewStaleness(run, edit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(staleness.IsStale, Is.True);
            Assert.That(staleness.DataChangedAt, Is.EqualTo(When));
            Assert.That(staleness.ConfigurationChangedAt, Is.EqualTo(When.AddMinutes(5)));
            Assert.That(staleness.OvertakenAt, Is.EqualTo(When.AddMinutes(5)));
            Assert.That(staleness.Describe(), Does.Contain("Delta Import").And.Contain("HR Users"));
        }
    }

    private static PreviewOvertakingActivity Overtaking(ActivityTargetType targetType, ActivityTargetOperationType operation,
        string? targetName, string? targetContext, DateTime? created = null) =>
        new(Guid.NewGuid(), created ?? When, targetType, operation, targetName, targetContext);
}

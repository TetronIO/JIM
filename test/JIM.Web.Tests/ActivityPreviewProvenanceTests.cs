// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Activities;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What an Activity says about the preview behind it (#134). Any change carrying a preview says it was informed by one;
/// a Connected System deletion says so either way, because whether the administrator looked first is the question its
/// audit trail is read to answer.
/// </summary>
[TestFixture]
public class ActivityPreviewProvenanceTests
{
    [TestCase(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Deprovision)]
    [TestCase(ActivityTargetType.MetaverseObjectType, ActivityTargetOperationType.Update)]
    public void For_AChangeCarryingAPreview_IsInformedByIt(ActivityTargetType targetType, ActivityTargetOperationType operation)
    {
        var activity = new Activity { TargetType = targetType, TargetOperationType = operation, PreviewActivityId = Guid.CreateVersion7() };

        Assert.That(ActivityPreviewProvenance.For(activity), Is.EqualTo(ActivityPreviewProvenanceKind.InformedByPreview));
    }

    [TestCase(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Deprovision)]
    [TestCase(ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute)]
    public void For_AChangeCarryingAPreviewThatWasOutOfDate_SaysSo(ActivityTargetType targetType, ActivityTargetOperationType operation)
    {
        // The change was made on a preview something had since overtaken (#2022); saying only that it was informed by
        // the preview would overstate what the administrator was shown.
        var activity = new Activity
        {
            TargetType = targetType,
            TargetOperationType = operation,
            PreviewActivityId = Guid.CreateVersion7(),
            PreviewOvertakenAt = DateTime.UtcNow,
            PreviewOvertakenBy = "Run Profile 'Delta Import' ran on Connected System 'HR Import'"
        };

        Assert.That(ActivityPreviewProvenance.For(activity), Is.EqualTo(ActivityPreviewProvenanceKind.InformedByOutOfDatePreview));
    }

    [TestCase(ActivityTargetOperationType.Delete)]
    [TestCase(ActivityTargetOperationType.Deprovision)]
    public void For_AConnectedSystemDeletionWithoutOne_WentAheadWithoutAPreview(ActivityTargetOperationType operation)
    {
        var activity = new Activity { TargetType = ActivityTargetType.ConnectedSystem, TargetOperationType = operation };

        Assert.That(ActivityPreviewProvenance.For(activity), Is.EqualTo(ActivityPreviewProvenanceKind.WentAheadWithoutAPreview));
    }

    [TestCase(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Update)]
    [TestCase(ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute)]
    [TestCase(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Preview)]
    public void For_AnythingElseWithoutOne_SaysNothing(ActivityTargetType targetType, ActivityTargetOperationType operation)
    {
        // Most changes have no preview surface at all; saying each went ahead without one would be noise.
        var activity = new Activity { TargetType = targetType, TargetOperationType = operation };

        Assert.That(ActivityPreviewProvenance.For(activity), Is.EqualTo(ActivityPreviewProvenanceKind.NotApplicable));
    }
}

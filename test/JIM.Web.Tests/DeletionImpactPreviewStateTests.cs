// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Text.Json;
using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Web.Models;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What the delete dialog says about the system's deletion impact preview (#134), and above all which preview, if any,
/// the deletion records as having informed it. Only a finished preview that nothing has since overtaken may be
/// recorded: an audit trail saying the administrator was shown the consequences, when what they were shown no longer
/// held, is worse than one saying they went ahead without looking.
/// </summary>
[TestFixture]
public class DeletionImpactPreviewStateTests
{
    private static readonly DateTime Started = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

    [Test]
    public void From_NoPreview_IsNotPreviewedAndRecordsNone()
    {
        var state = DeletionImpactPreviewState.From(null, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Status, Is.EqualTo(DeletionImpactPreviewStatus.NotPreviewed));
            Assert.That(state.InformingPreviewActivityId, Is.Null);
        }
    }

    [Test]
    public void From_StillEvaluating_IsRunningWithItsProgressAndRecordsNone()
    {
        var preview = Preview(ActivityStatus.InProgress, ConfigurationChangePreviewStageStatus.InProgress);
        preview.Activity.ObjectsToProcess = 400;
        preview.Activity.ObjectsProcessed = 100;

        var state = DeletionImpactPreviewState.From(preview, Current());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Status, Is.EqualTo(DeletionImpactPreviewStatus.Running));
            Assert.That(state.PercentComplete, Is.EqualTo(25));
            Assert.That(state.InformingPreviewActivityId, Is.Null, "an unfinished preview has told the administrator nothing yet");
        }
    }

    [Test]
    public void From_StillEvaluatingWithNoTotalYet_IsRunningWithNoPercentage()
    {
        var preview = Preview(ActivityStatus.InProgress, ConfigurationChangePreviewStageStatus.InProgress);

        var state = DeletionImpactPreviewState.From(preview, Current());

        Assert.That(state.PercentComplete, Is.Null, "0% would claim a measurement nobody has made");
    }

    [Test]
    public void From_FinishedAndNothingSince_IsCurrentAndRecordsIt()
    {
        var preview = Preview(ActivityStatus.Complete, ConfigurationChangePreviewStageStatus.Complete,
            counts: [new PreviewImpactCount(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, 312)]);

        var state = DeletionImpactPreviewState.From(preview, Current());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Status, Is.EqualTo(DeletionImpactPreviewStatus.Current));
            Assert.That(state.InformingPreviewActivityId, Is.EqualTo(preview.ActivityId));
            Assert.That(state.Started, Is.EqualTo(Started));
            Assert.That(state.Lines, Is.EqualTo(new[] { new PreviewVerdictLine(Severity.Error, "312 objects would become eligible for deletion.") }));
        }
    }

    [Test]
    public void From_FinishedButOvertaken_IsStaleAndRecordsNone()
    {
        var preview = Preview(ActivityStatus.Complete, ConfigurationChangePreviewStageStatus.Complete);
        var staleness = new ConfigurationChangePreviewStaleness(null, Started.AddMinutes(2));

        var state = DeletionImpactPreviewState.From(preview, staleness);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Status, Is.EqualTo(DeletionImpactPreviewStatus.Stale));
            Assert.That(state.Staleness, Is.EqualTo(staleness), "the host says which kind of change overtook it");
            Assert.That(state.InformingPreviewActivityId, Is.Null);
        }
    }

    [TestCase(ActivityStatus.FailedWithError, ConfigurationChangePreviewStageStatus.Failed, TestName = "From_Failed_DidNotFinishAndRecordsNone")]
    [TestCase(ActivityStatus.Cancelled, ConfigurationChangePreviewStageStatus.Cancelled, TestName = "From_Cancelled_DidNotFinishAndRecordsNone")]
    public void From_EndedWithoutAnAnswer_DidNotFinishAndRecordsNone(ActivityStatus activityStatus, ConfigurationChangePreviewStageStatus stageStatus)
    {
        var preview = Preview(activityStatus, stageStatus);

        var state = DeletionImpactPreviewState.From(preview, Current());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Status, Is.EqualTo(DeletionImpactPreviewStatus.DidNotFinish));
            Assert.That(state.InformingPreviewActivityId, Is.Null);
        }
    }

    [Test]
    public void From_BlockedAtValidation_DidNotFinishAndRecordsNone()
    {
        // A blocked preview evaluated nothing, so it says nothing about what the deletion would do.
        var preview = Preview(ActivityStatus.Complete, ConfigurationChangePreviewStageStatus.NotApplicable);
        preview.ValidationStatus = ConfigurationChangePreviewStageStatus.Complete;
        preview.ValidationFindings = JsonSerializer.Serialize(new List<PreviewValidationFinding>
        {
            new(PreviewValidationSeverity.Blocking, "This Connected System is already being deleted.")
        });

        var state = DeletionImpactPreviewState.From(preview, Current());

        Assert.That(state.Status, Is.EqualTo(DeletionImpactPreviewStatus.DidNotFinish));
    }

    private static ConfigurationChangePreviewStaleness Current() => new(null, null);

    private static ConfigurationChangePreview Preview(ActivityStatus activityStatus, ConfigurationChangePreviewStageStatus stageStatus,
        List<PreviewImpactCount>? counts = null)
    {
        var activityId = Guid.CreateVersion7();
        return new ConfigurationChangePreview
        {
            ActivityId = activityId,
            Activity = new Activity { Id = activityId, Created = Started, Status = activityStatus, TargetType = ActivityTargetType.ConnectedSystem },
            Surface = ConfigurationChangePreviewSurface.ConnectedSystemDeletion,
            ValidationStatus = stageStatus == ConfigurationChangePreviewStageStatus.InProgress ? ConfigurationChangePreviewStageStatus.Complete : stageStatus,
            ImpactCountsStatus = stageStatus,
            SummaryStatus = stageStatus,
            DeltasStatus = stageStatus,
            ImpactCounts = counts == null ? null : JsonSerializer.Serialize(counts)
        };
    }
}

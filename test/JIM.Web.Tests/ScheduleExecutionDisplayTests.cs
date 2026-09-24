// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Schedules list's last-run outcome chip. A failed run and a run that continued past a failure (#1787) both name
/// the step or steps involved, so the administrator does not have to open the Schedule Execution to find out.
/// </summary>
[TestFixture]
public class ScheduleExecutionDisplayTests
{
    private static ScheduleHeader Header(ScheduleExecutionStatus? status, int[]? failedStepIndices = null, int? currentStepIndex = null, int? totalSteps = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Nightly HR sync",
        LastExecutionStatus = status,
        LastExecutionFailedStepIndices = failedStepIndices ?? [],
        LastExecutionCurrentStepIndex = currentStepIndex,
        LastExecutionTotalSteps = totalSteps
    };

    [Test]
    public void LastOutcomeLabel_NeverRun_IsEmpty()
    {
        Assert.That(ScheduleExecutionDisplay.LastOutcomeLabel(Header(null)), Is.Empty);
    }

    [TestCase(ScheduleExecutionStatus.Complete, "Complete")]
    [TestCase(ScheduleExecutionStatus.InProgress, "In Progress")]
    [TestCase(ScheduleExecutionStatus.Cancelled, "Cancelled")]
    public void LastOutcomeLabel_OtherStatuses_AreTheStatusName(ScheduleExecutionStatus status, string expected)
    {
        Assert.That(ScheduleExecutionDisplay.LastOutcomeLabel(Header(status)), Is.EqualTo(expected));
    }

    [Test]
    public void LastOutcomeLabel_Failed_NamesTheStepItStoppedOn()
    {
        Assert.That(ScheduleExecutionDisplay.LastOutcomeLabel(Header(ScheduleExecutionStatus.Failed, currentStepIndex: 2, totalSteps: 4)),
            Is.EqualTo("Failed on step 3"));
    }

    [Test]
    public void LastOutcomeLabel_FailedPastItsLastStep_IsClampedToTheTotal()
    {
        Assert.That(ScheduleExecutionDisplay.LastOutcomeLabel(Header(ScheduleExecutionStatus.Failed, currentStepIndex: 4, totalSteps: 4)),
            Is.EqualTo("Failed on step 4"));
    }

    [Test]
    public void LastOutcomeLabel_FailedWithNoPosition_IsPlainFailed()
    {
        Assert.That(ScheduleExecutionDisplay.LastOutcomeLabel(Header(ScheduleExecutionStatus.Failed)), Is.EqualTo("Failed"));
    }

    [TestCase(new[] { 2 }, "Complete With Error on step 3")]
    [TestCase(new[] { 1, 2 }, "Complete With Error on steps 2 and 3")]
    [TestCase(new[] { 0, 1, 3 }, "Complete With Error on steps 1, 2 and 4")]
    public void LastOutcomeLabel_CompleteWithError_NamesTheFailedStepsOneBased(int[] failedStepIndices, string expected)
    {
        Assert.That(ScheduleExecutionDisplay.LastOutcomeLabel(Header(ScheduleExecutionStatus.CompleteWithError, failedStepIndices)),
            Is.EqualTo(expected));
    }

    [Test]
    public void LastOutcomeLabel_CompleteWithErrorAndParallelFailuresAtOneIndex_NamesTheStepOnce()
    {
        // Parallel steps share a step index, so two failures in one parallel group are one step number to a reader.
        Assert.That(ScheduleExecutionDisplay.LastOutcomeLabel(Header(ScheduleExecutionStatus.CompleteWithError, [1, 1, 3])),
            Is.EqualTo("Complete With Error on steps 2 and 4"));
    }

    [Test]
    public void LastOutcomeLabel_CompleteWithErrorAndNoFailedSteps_IsPlainCompleteWithError()
    {
        Assert.That(ScheduleExecutionDisplay.LastOutcomeLabel(Header(ScheduleExecutionStatus.CompleteWithError)),
            Is.EqualTo("Complete With Error"));
    }

    [Test]
    public void CompleteWithErrorMessage_RecordedMessage_IsUsedAsIs()
    {
        const string recorded = "The Schedule finished, but step 3, Active Directory - Export, failed. It is set to let the Schedule continue.";

        Assert.That(ScheduleExecutionDisplay.CompleteWithErrorMessage(recorded), Is.EqualTo(recorded));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void CompleteWithErrorMessage_NothingRecorded_FallsBackToAGeneralExplanation(string? recorded)
    {
        Assert.That(ScheduleExecutionDisplay.CompleteWithErrorMessage(recorded), Is.EqualTo(
            "The Schedule finished, but one or more steps failed. They are set to let the Schedule continue; the step statuses below show which."));
    }
}

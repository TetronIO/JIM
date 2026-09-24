// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Scheduling;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST write rule for a step's failure behaviour (#1787). <c>onFailure</c> is the step's own setting and wins when
/// supplied. <c>continueOnFailure</c> is kept for existing clients; it reads as the EFFECTIVE behaviour, so on write it
/// must not pin an existing step that a get-modify-put client merely echoed back: the "round-trip pinning" correction in
/// the plan. Only a value that differs from what the step does today changes it.
/// </summary>
[TestFixture]
public class ScheduleStepFailureWriteRuleTests
{
    private static Schedule ScheduleThat(ScheduleFailureBehaviour onStepFailure) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Nightly HR sync",
        OnStepFailure = onStepFailure
    };

    private static ScheduleStep StepThat(ScheduleStepFailureBehaviour onFailure) => new()
    {
        Id = Guid.NewGuid(),
        OnFailure = onFailure
    };

    // -----------------------------------------------------------------------------------------------------------------
    // New steps
    // -----------------------------------------------------------------------------------------------------------------

    [TestCase(true, ScheduleStepFailureBehaviour.Continue)]
    [TestCase(false, ScheduleStepFailureBehaviour.FollowSchedule)]
    [TestCase(null, ScheduleStepFailureBehaviour.FollowSchedule)]
    public void Resolve_NewStepWithoutOnFailure_MapsContinueOnFailure(bool? continueOnFailure, ScheduleStepFailureBehaviour expected)
    {
        // false has always meant "the default", so it follows the Schedule rather than pinning the step to Stop.
        var result = ScheduleStepFailureWriteRule.Resolve(null, continueOnFailure, existingStep: null, storedSchedule: null);

        Assert.That(result, Is.EqualTo(expected));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Existing steps, round-tripped unchanged
    // -----------------------------------------------------------------------------------------------------------------

    [TestCase(ScheduleFailureBehaviour.Continue, ScheduleStepFailureBehaviour.FollowSchedule, true)]
    [TestCase(ScheduleFailureBehaviour.Stop, ScheduleStepFailureBehaviour.FollowSchedule, false)]
    [TestCase(ScheduleFailureBehaviour.Continue, ScheduleStepFailureBehaviour.Continue, true)]
    [TestCase(ScheduleFailureBehaviour.Stop, ScheduleStepFailureBehaviour.Continue, true)]
    [TestCase(ScheduleFailureBehaviour.Continue, ScheduleStepFailureBehaviour.Stop, false)]
    [TestCase(ScheduleFailureBehaviour.Stop, ScheduleStepFailureBehaviour.Stop, false)]
    public void Resolve_ExistingStepEchoingItsEffectiveBehaviour_KeepsItsSetting(
        ScheduleFailureBehaviour scheduleSetting,
        ScheduleStepFailureBehaviour stepSetting,
        bool echoedContinueOnFailure)
    {
        // The case that matters: a step following a continuing Schedule reads continueOnFailure: true; sending that back
        // must leave it following the Schedule, not pin it to Continue.
        var step = StepThat(stepSetting);

        var result = ScheduleStepFailureWriteRule.Resolve(null, echoedContinueOnFailure, step, ScheduleThat(scheduleSetting));

        Assert.That(result, Is.EqualTo(stepSetting));
    }

    [TestCase(ScheduleFailureBehaviour.Continue, ScheduleStepFailureBehaviour.FollowSchedule)]
    [TestCase(ScheduleFailureBehaviour.Stop, ScheduleStepFailureBehaviour.Continue)]
    [TestCase(ScheduleFailureBehaviour.Continue, ScheduleStepFailureBehaviour.Stop)]
    public void Resolve_ExistingStepWithNeitherField_KeepsItsSetting(ScheduleFailureBehaviour scheduleSetting, ScheduleStepFailureBehaviour stepSetting)
    {
        var result = ScheduleStepFailureWriteRule.Resolve(null, null, StepThat(stepSetting), ScheduleThat(scheduleSetting));

        Assert.That(result, Is.EqualTo(stepSetting));
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Existing steps, flipped
    // -----------------------------------------------------------------------------------------------------------------

    [TestCase(ScheduleFailureBehaviour.Stop, ScheduleStepFailureBehaviour.FollowSchedule, true, ScheduleStepFailureBehaviour.Continue)]
    [TestCase(ScheduleFailureBehaviour.Continue, ScheduleStepFailureBehaviour.FollowSchedule, false, ScheduleStepFailureBehaviour.Stop)]
    [TestCase(ScheduleFailureBehaviour.Continue, ScheduleStepFailureBehaviour.Continue, false, ScheduleStepFailureBehaviour.Stop)]
    [TestCase(ScheduleFailureBehaviour.Stop, ScheduleStepFailureBehaviour.Stop, true, ScheduleStepFailureBehaviour.Continue)]
    public void Resolve_ExistingStepAskedForTheOppositeOfWhatItDoes_SetsItOnTheStep(
        ScheduleFailureBehaviour scheduleSetting,
        ScheduleStepFailureBehaviour stepSetting,
        bool requestedContinueOnFailure,
        ScheduleStepFailureBehaviour expected)
    {
        // The client asked for the opposite of what the step does now, so false means Stop here, not "the default":
        // mapping it to FollowSchedule would leave a step under a continuing Schedule still continuing.
        var result = ScheduleStepFailureWriteRule.Resolve(null, requestedContinueOnFailure, StepThat(stepSetting), ScheduleThat(scheduleSetting));

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void Resolve_ExistingStepFollowingWithNoStoredSchedule_JudgesEffectiveBehaviourAsStop()
    {
        // With no Schedule to follow the resolver fails safe to Stop, so an echoed false is unchanged and true flips.
        var step = StepThat(ScheduleStepFailureBehaviour.FollowSchedule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScheduleStepFailureWriteRule.Resolve(null, false, step, null), Is.EqualTo(ScheduleStepFailureBehaviour.FollowSchedule));
            Assert.That(ScheduleStepFailureWriteRule.Resolve(null, true, step, null), Is.EqualTo(ScheduleStepFailureBehaviour.Continue));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // onFailure wins
    // -----------------------------------------------------------------------------------------------------------------

    [TestCase(ScheduleStepFailureBehaviour.FollowSchedule, true)]
    [TestCase(ScheduleStepFailureBehaviour.Stop, true)]
    [TestCase(ScheduleStepFailureBehaviour.Continue, false)]
    [TestCase(ScheduleStepFailureBehaviour.FollowSchedule, null)]
    public void Resolve_OnFailureSupplied_WinsOverContinueOnFailureForNewAndExistingSteps(ScheduleStepFailureBehaviour onFailure, bool? continueOnFailure)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScheduleStepFailureWriteRule.Resolve(onFailure, continueOnFailure, null, null), Is.EqualTo(onFailure));
            Assert.That(
                ScheduleStepFailureWriteRule.Resolve(onFailure, continueOnFailure, StepThat(ScheduleStepFailureBehaviour.Stop), ScheduleThat(ScheduleFailureBehaviour.Continue)),
                Is.EqualTo(onFailure));
        }
    }

    [Test]
    public void Resolve_FromRequest_ReadsBothFieldsOffTheRequest()
    {
        var request = new ScheduleStepRequest { ContinueOnFailure = true };
        var existing = StepThat(ScheduleStepFailureBehaviour.FollowSchedule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScheduleStepFailureWriteRule.Resolve(request, existing, ScheduleThat(ScheduleFailureBehaviour.Continue)),
                Is.EqualTo(ScheduleStepFailureBehaviour.FollowSchedule), "echoed effective value");
            request.OnFailure = ScheduleStepFailureBehaviour.Stop;
            Assert.That(ScheduleStepFailureWriteRule.Resolve(request, existing, ScheduleThat(ScheduleFailureBehaviour.Continue)),
                Is.EqualTo(ScheduleStepFailureBehaviour.Stop), "onFailure wins");
        }
    }
}

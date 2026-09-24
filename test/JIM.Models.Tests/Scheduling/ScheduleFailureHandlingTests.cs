// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using NUnit.Framework;

namespace JIM.Models.Tests.Scheduling;

/// <summary>
/// Tests <see cref="ScheduleFailureHandling"/>, the one place a step's effective failure behaviour is resolved (#1787):
/// the step's own setting when it has one, otherwise the Schedule's.
/// </summary>
[TestFixture]
public class ScheduleFailureHandlingTests
{
    [TestCase(ScheduleStepFailureBehaviour.FollowSchedule, ScheduleFailureBehaviour.Stop, false)]
    [TestCase(ScheduleStepFailureBehaviour.FollowSchedule, ScheduleFailureBehaviour.Continue, true)]
    [TestCase(ScheduleStepFailureBehaviour.Stop, ScheduleFailureBehaviour.Stop, false)]
    [TestCase(ScheduleStepFailureBehaviour.Stop, ScheduleFailureBehaviour.Continue, false)]
    [TestCase(ScheduleStepFailureBehaviour.Continue, ScheduleFailureBehaviour.Stop, true)]
    [TestCase(ScheduleStepFailureBehaviour.Continue, ScheduleFailureBehaviour.Continue, true)]
    public void ContinuesOnFailure_StepAndScheduleSetting_ResolvesTheEffectiveBehaviour(
        ScheduleStepFailureBehaviour stepSetting, ScheduleFailureBehaviour scheduleSetting, bool expected)
    {
        var schedule = new Schedule { OnStepFailure = scheduleSetting };
        var step = new ScheduleStep { OnFailure = stepSetting, Schedule = schedule };

        Assert.That(ScheduleFailureHandling.ContinuesOnFailure(step, schedule), Is.EqualTo(expected));
    }

    [Test]
    public void ContinuesOnFailure_FollowsScheduleWithNoSchedule_StopsFailingSafe()
    {
        var step = new ScheduleStep { OnFailure = ScheduleStepFailureBehaviour.FollowSchedule };

        Assert.That(ScheduleFailureHandling.ContinuesOnFailure(step, null), Is.False,
            "with no Schedule to follow, the step must fail safe and stop");
    }

    [TestCase(ScheduleStepFailureBehaviour.Continue, true)]
    [TestCase(ScheduleStepFailureBehaviour.Stop, false)]
    public void ContinuesOnFailure_OwnSettingWithNoSchedule_UsesTheStepSetting(ScheduleStepFailureBehaviour stepSetting, bool expected)
    {
        var step = new ScheduleStep { OnFailure = stepSetting };

        Assert.That(ScheduleFailureHandling.ContinuesOnFailure(step, null), Is.EqualTo(expected));
    }

    [Test]
    public void ContinuesOnFailure_ScheduleArgumentGiven_ReadsItRatherThanTheStepNavigation()
    {
        // Callers pass the Schedule they hold; a step's navigation may be unloaded, or stale.
        var step = new ScheduleStep
        {
            OnFailure = ScheduleStepFailureBehaviour.FollowSchedule,
            Schedule = new Schedule { OnStepFailure = ScheduleFailureBehaviour.Stop }
        };

        Assert.That(ScheduleFailureHandling.ContinuesOnFailure(step, new Schedule { OnStepFailure = ScheduleFailureBehaviour.Continue }), Is.True);
    }

    [TestCase(ScheduleStepFailureBehaviour.FollowSchedule, ScheduleFailureBehaviourSource.Schedule)]
    [TestCase(ScheduleStepFailureBehaviour.Stop, ScheduleFailureBehaviourSource.Step)]
    [TestCase(ScheduleStepFailureBehaviour.Continue, ScheduleFailureBehaviourSource.Step)]
    public void Source_StepSetting_SaysWhereTheBehaviourComesFrom(ScheduleStepFailureBehaviour stepSetting, ScheduleFailureBehaviourSource expected)
    {
        Assert.That(ScheduleFailureHandling.Source(new ScheduleStep { OnFailure = stepSetting }), Is.EqualTo(expected));
    }
}

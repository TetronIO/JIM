// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.Web.Models;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The words the Schedule editor uses for failure behaviour (#1787). The step list, the step editor's select and the
/// Schedule's own select all describe the same two settings, so they are worded in one place and pinned here.
/// </summary>
[TestFixture]
public class ScheduleFailureBehaviourDisplayTests
{
    [TestCase(ScheduleFailureBehaviour.Stop, "Stop the Schedule")]
    [TestCase(ScheduleFailureBehaviour.Continue, "Continue the Schedule")]
    public void ScheduleOption_EachBehaviour_IsWordedAsAnInstruction(ScheduleFailureBehaviour behaviour, string expected)
    {
        Assert.That(ScheduleFailureBehaviourDisplay.ScheduleOption(behaviour), Is.EqualTo(expected));
    }

    [TestCase(ScheduleStepFailureBehaviour.FollowSchedule, ScheduleFailureBehaviour.Stop, "Follow the Schedule (currently: stop)")]
    [TestCase(ScheduleStepFailureBehaviour.FollowSchedule, ScheduleFailureBehaviour.Continue, "Follow the Schedule (currently: continue)")]
    [TestCase(ScheduleStepFailureBehaviour.Stop, ScheduleFailureBehaviour.Continue, "Stop the Schedule")]
    [TestCase(ScheduleStepFailureBehaviour.Continue, ScheduleFailureBehaviour.Stop, "Continue the Schedule")]
    public void StepOption_FollowStatesTheSchedulesCurrentValue(
        ScheduleStepFailureBehaviour option, ScheduleFailureBehaviour scheduleBehaviour, string expected)
    {
        Assert.That(ScheduleFailureBehaviourDisplay.StepOption(option, scheduleBehaviour), Is.EqualTo(expected));
    }

    [TestCase(true, "Continues on failure")]
    [TestCase(false, "Stops on failure")]
    public void Effective_DescribesWhatTheStepDoes(bool continues, string expected)
    {
        Assert.That(ScheduleFailureBehaviourDisplay.Effective(continues), Is.EqualTo(expected));
    }

    [Test]
    public void EffectiveColour_ContinuingIsHighlighted_StoppingIsTheQuietDefault()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScheduleFailureBehaviourDisplay.EffectiveColour(true), Is.EqualTo(Color.Warning));
            Assert.That(ScheduleFailureBehaviourDisplay.EffectiveColour(false), Is.EqualTo(Color.Default));
        }
    }

    [TestCase(ScheduleFailureBehaviourSource.Schedule, "From the Schedule")]
    [TestCase(ScheduleFailureBehaviourSource.Step, "Set on this step")]
    public void Source_NamesWhereTheBehaviourComesFrom(ScheduleFailureBehaviourSource source, string expected)
    {
        Assert.That(ScheduleFailureBehaviourDisplay.Source(source), Is.EqualTo(expected));
    }

    [TestCase(ScheduleStepFailureBehaviour.FollowSchedule, ScheduleFailureBehaviour.Continue, "Continues on failure", "From the Schedule")]
    [TestCase(ScheduleStepFailureBehaviour.FollowSchedule, ScheduleFailureBehaviour.Stop, "Stops on failure", "From the Schedule")]
    [TestCase(ScheduleStepFailureBehaviour.Stop, ScheduleFailureBehaviour.Continue, "Stops on failure", "Set on this step")]
    [TestCase(ScheduleStepFailureBehaviour.Continue, ScheduleFailureBehaviour.Stop, "Continues on failure", "Set on this step")]
    public void ForStep_ResolvesThroughTheSharedResolver(
        ScheduleStepFailureBehaviour stepSetting, ScheduleFailureBehaviour scheduleSetting, string expectedEffective, string expectedSource)
    {
        var schedule = new Schedule { Name = "Nightly HR sync", OnStepFailure = scheduleSetting };
        var step = new ScheduleStep { OnFailure = stepSetting };

        var (effective, _, source) = ScheduleFailureBehaviourDisplay.ForStep(step, schedule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(effective, Is.EqualTo(expectedEffective));
            Assert.That(source, Is.EqualTo(expectedSource));
        }
    }

    [TestCase(ScheduleFailureBehaviourSource.Schedule, "This step follows the Schedule, which is set to continue when a step fails.")]
    [TestCase(ScheduleFailureBehaviourSource.Step, "This step is set to let the Schedule continue when it fails.")]
    public void ContinuedAfterFailure_ExplainsWhyTheExecutionCarriedOn(ScheduleFailureBehaviourSource source, string expected)
    {
        Assert.That(ScheduleFailureBehaviourDisplay.ContinuedAfterFailure(source), Is.EqualTo(expected));
    }
}

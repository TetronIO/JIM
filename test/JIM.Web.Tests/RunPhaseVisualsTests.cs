// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// What a step looks like for a given outcome (#1162). These rules used to live inside
/// <see cref="RunPhaseStepper"/> as private methods; three rails now draw the same steps (the
/// Activity page's stepper, the queue's per-task rail, and the Schedule Execution group header),
/// and a run whose step reads green on one and grey on another is worse than one that draws none.
/// </summary>
/// <remarks>
/// Pinned as a plain unit test rather than through a rendered component because the rules are the
/// shared thing: a component test would prove only that one of the three consumers is correct.
/// </remarks>
[TestFixture]
public class RunPhaseVisualsTests
{
    private static readonly ActivityPhaseStatus[] EveryStatus = Enum.GetValues<ActivityPhaseStatus>();

    private static ActivityPhase Phase(ActivityPhaseStatus status, string key = RunPhaseKeys.ImportSave) => new()
    {
        Id = Guid.NewGuid(),
        ActivityId = Guid.NewGuid(),
        Key = key,
        Name = "Saving changes",
        Order = 0,
        Status = status
    };

    #region Status modifier

    [Test]
    public void StatusModifier_EveryStatus_HasAModifierOfItsOwn()
    {
        // The modifier is what a stylesheet hangs a step's appearance on, so two statuses sharing
        // one would make them indistinguishable on every rail at once.
        var modifiers = EveryStatus.Select(RunPhaseVisuals.StatusModifier).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(modifiers.Any(string.IsNullOrWhiteSpace), Is.False, "Every status needs a modifier a stylesheet can select on");
            Assert.That(modifiers.Distinct().Count(), Is.EqualTo(EveryStatus.Length), "Two statuses sharing a modifier would be indistinguishable on every rail at once");
        }
    }

    [Test]
    public void StatusModifier_NotReachedYet_IsPending()
    {
        Assert.That(RunPhaseVisuals.StatusModifier(ActivityPhaseStatus.Pending), Is.EqualTo("pending"));
    }

    #endregion

    #region Icon

    [Test]
    public void StatusIcon_NoPhaseAtAll_IsTheUnreachedMarker()
    {
        // A rail can be asked to draw a step it has no record of, which is not the same as a step
        // that has not run: it must still draw something rather than fall over.
        Assert.That(RunPhaseVisuals.StatusIcon(null), Is.EqualTo(Icons.Material.Filled.RadioButtonUnchecked));
    }

    [Test]
    public void StatusIcon_StepThatHasFinished_ShowsTheOutcomeRatherThanTheWork()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RunPhaseVisuals.StatusIcon(Phase(ActivityPhaseStatus.Completed)), Is.EqualTo(Icons.Material.Filled.Check));
            Assert.That(RunPhaseVisuals.StatusIcon(Phase(ActivityPhaseStatus.Skipped)), Is.EqualTo(Icons.Material.Filled.Remove));
            Assert.That(RunPhaseVisuals.StatusIcon(Phase(ActivityPhaseStatus.Failed)), Is.EqualTo(Icons.Material.Filled.PriorityHigh));
        }
    }

    [Test]
    public void StatusIcon_StepStillToFinish_ShowsWhatTheStepIsFor()
    {
        // Before there is an outcome to report, the icon's job is to make the rail scannable by
        // shape, so it names the work rather than the state.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RunPhaseVisuals.StatusIcon(Phase(ActivityPhaseStatus.Active)),
                Is.EqualTo(RunPhaseIcons.ForPhase(RunPhaseKeys.ImportSave)));
            Assert.That(RunPhaseVisuals.StatusIcon(Phase(ActivityPhaseStatus.Pending)),
                Is.EqualTo(RunPhaseIcons.ForPhase(RunPhaseKeys.ImportSave)));
        }
    }

    #endregion

    #region Fill

    [Test]
    public void HasRun_TerminalOutcomes_AreAllPastTense()
    {
        // Completed, skipped and failed all mean "the run is past this step". Treating skipped as
        // not-yet-run would leave a permanent gap in every rail that draws a Delta Import.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RunPhaseVisuals.HasRun(ActivityPhaseStatus.Completed), Is.True);
            Assert.That(RunPhaseVisuals.HasRun(ActivityPhaseStatus.Skipped), Is.True);
            Assert.That(RunPhaseVisuals.HasRun(ActivityPhaseStatus.Failed), Is.True);
            Assert.That(RunPhaseVisuals.HasRun(ActivityPhaseStatus.Active), Is.False);
            Assert.That(RunPhaseVisuals.HasRun(ActivityPhaseStatus.Pending), Is.False);
        }
    }

    [Test]
    public void FillPercent_StepThatRan_IsFullWhateverTheOutcome()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RunPhaseVisuals.FillPercent(Phase(ActivityPhaseStatus.Completed), null), Is.EqualTo(100d));
            Assert.That(RunPhaseVisuals.FillPercent(Phase(ActivityPhaseStatus.Skipped), null), Is.EqualTo(100d));
            Assert.That(RunPhaseVisuals.FillPercent(Phase(ActivityPhaseStatus.Failed), null), Is.EqualTo(100d));
        }
    }

    [Test]
    public void FillPercent_RunningStep_IsThatStepsOwnProgress()
    {
        Assert.That(RunPhaseVisuals.FillPercent(Phase(ActivityPhaseStatus.Active), 0.5d), Is.EqualTo(50d));
    }

    [Test]
    public void FillPercent_RunningStepWithNothingToCount_StaysEmptyRatherThanGuessing()
    {
        Assert.That(RunPhaseVisuals.FillPercent(Phase(ActivityPhaseStatus.Active), null), Is.EqualTo(0d));
    }

    [Test]
    public void FillPercent_ProgressOutsideItsRange_IsClamped()
    {
        // The count and the total are reported separately and briefly disagree at a step boundary,
        // which is enough to produce a ratio above one and a fill running off the end of its track.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RunPhaseVisuals.FillPercent(Phase(ActivityPhaseStatus.Active), 1.4d), Is.EqualTo(100d));
            Assert.That(RunPhaseVisuals.FillPercent(Phase(ActivityPhaseStatus.Active), -0.2d), Is.EqualTo(0d));
        }
    }

    [Test]
    public void FillPercent_StepAheadOfTheRun_IsEmpty()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RunPhaseVisuals.FillPercent(Phase(ActivityPhaseStatus.Pending), 0.5d), Is.EqualTo(0d));
            Assert.That(RunPhaseVisuals.FillPercent(null, 0.5d), Is.EqualTo(0d));
        }
    }

    #endregion

    #region Outcome tooltip

    [Test]
    public void OutcomeTooltip_UnusualOutcomes_ExplainThemselves()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RunPhaseVisuals.OutcomeTooltip(Phase(ActivityPhaseStatus.Skipped)), Is.EqualTo("Not needed for this run"));
            Assert.That(RunPhaseVisuals.OutcomeTooltip(Phase(ActivityPhaseStatus.Failed)), Is.EqualTo("The run failed at this step"));
        }
    }

    [Test]
    public void OutcomeTooltip_OrdinaryOutcomes_SayNothing()
    {
        // A tooltip on every step would train the reader to ignore all of them, including the two
        // that carry something worth reading.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RunPhaseVisuals.OutcomeTooltip(Phase(ActivityPhaseStatus.Completed)), Is.Empty);
            Assert.That(RunPhaseVisuals.OutcomeTooltip(Phase(ActivityPhaseStatus.Active)), Is.Empty);
            Assert.That(RunPhaseVisuals.OutcomeTooltip(Phase(ActivityPhaseStatus.Pending)), Is.Empty);
            Assert.That(RunPhaseVisuals.OutcomeTooltip(null), Is.Empty);
        }
    }

    #endregion
}

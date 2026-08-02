// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Activities;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The horizontal phase stepper (#454): the run read left to right as a stepped progress bar. What
/// is worth pinning is the part a screenshot cannot prove on its own: that each leg's fill reflects
/// the step it leaves, so the rail reads as one bar across the whole run rather than a bar that
/// restarts at every step.
/// </summary>
[TestFixture]
public class RunPhaseStepperTests : JimComponentTestContext
{
    private static readonly DateTime Started = new(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);

    private static ActivityPhase Phase(
        string key,
        string name,
        int order,
        ActivityPhaseStatus status,
        string? parentKey = null,
        DateTime? started = null,
        DateTime? ended = null) => new()
    {
        Id = Guid.NewGuid(),
        ActivityId = Guid.NewGuid(),
        Key = key,
        Name = name,
        Order = order,
        Status = status,
        ParentKey = parentKey,
        Started = started,
        Ended = ended
    };

    private static List<ActivityPhase> ImportInProgress() =>
    [
        Phase(RunPhaseKeys.ImportConnect, "Connecting to Connected System", 0, ActivityPhaseStatus.Skipped),
        Phase(RunPhaseKeys.ImportFetch, "Importing objects", 1, ActivityPhaseStatus.Active, started: Started),
        Phase(ActivityPhase.QualifyConnectorKey("read"), "Reading the file", 2, ActivityPhaseStatus.Active,
            parentKey: RunPhaseKeys.ImportFetch, started: Started),
        Phase(RunPhaseKeys.ImportSave, "Saving changes", 3, ActivityPhaseStatus.Pending)
    ];

    private static IEnumerable<string> ConnectorWidths(IRenderedComponent<RunPhaseStepper> cut) =>
        cut.FindAll(".jim-phase-h-connector-fill").Select(e => e.GetAttribute("style") ?? string.Empty);

    /// <summary>
    /// Tooltip text is rendered by MudBlazor's popover provider rather than into the component's own
    /// markup, so it is read from the tooltip components' parameters.
    /// </summary>
    private static IEnumerable<string> TooltipTexts(IRenderedComponent<RunPhaseStepper> cut) =>
        cut.FindComponents<MudTooltip>().Select(c => c.Instance.Text ?? string.Empty);

    [Test]
    public void RunPhaseStepper_NoPhases_RendersNothing()
    {
        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, new List<ActivityPhase>()));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void RunPhaseStepper_WithPhases_ShowsEveryTopLevelStep()
    {
        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, ImportInProgress()));

        Assert.That(cut.Markup, Does.Contain("Connecting to Connected System"));
        Assert.That(cut.Markup, Does.Contain("Importing objects"));
        Assert.That(cut.Markup, Does.Contain("Saving changes"));
    }

    [Test]
    public void RunPhaseStepper_LegLeavingTheRunningStep_FillsToThatStepsOwnProgress()
    {
        var cut = Render<RunPhaseStepper>(p => p
            .Add(c => c.Phases, ImportInProgress())
            .Add(c => c.StepProgress, 0.5d));

        // Legs, in order: after the skipped step (done), after the running step (half).
        var widths = ConnectorWidths(cut).ToList();
        Assert.That(widths, Has.Count.EqualTo(2), "A leg sits between each pair of steps, so three steps have two legs");
        Assert.That(widths[0], Does.Contain("100"));
        Assert.That(widths[1], Does.Contain("50"));
    }

    [Test]
    public void RunPhaseStepper_LegLeavingAStepThatRan_IsFullWhateverTheOutcome()
    {
        // Completed, skipped and failed all mean "the run is past this step", so the leg is full;
        // otherwise a skipped step would leave a permanent gap in the rail.
        var phases = new List<ActivityPhase>
        {
            Phase(RunPhaseKeys.ImportConnect, "Connecting to Connected System", 0, ActivityPhaseStatus.Completed, started: Started, ended: Started.AddSeconds(5)),
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 1, ActivityPhaseStatus.Skipped),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 2, ActivityPhaseStatus.Failed, started: Started, ended: Started.AddMinutes(1)),
            Phase(RunPhaseKeys.ImportRecordResults, "Recording results", 3, ActivityPhaseStatus.Pending)
        };

        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, phases));

        Assert.That(ConnectorWidths(cut).Take(3).All(w => w.Contains("100")), Is.True);
    }

    [Test]
    public void RunPhaseStepper_LegAheadOfTheRun_IsEmpty()
    {
        var cut = Render<RunPhaseStepper>(p => p
            .Add(c => c.Phases, ImportInProgress())
            .Add(c => c.StepProgress, 0.5d));

        var widths = ConnectorWidths(cut).ToList();
        Assert.That(widths[^1], Does.Not.Contain("100"));
    }

    [Test]
    public void RunPhaseStepper_RunningStepWithNothingToCount_LeavesItsLegEmptyRatherThanGuessing()
    {
        var cut = Render<RunPhaseStepper>(p => p
            .Add(c => c.Phases, ImportInProgress())
            .Add(c => c.StepProgress, null));

        var widths = ConnectorWidths(cut).ToList();
        Assert.That(widths[1], Does.Contain("0"));
    }

    [Test]
    public void RunPhaseStepper_RunningStep_IsMarkedActiveSoTheEyeLandsOnIt()
    {
        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, ImportInProgress()));

        Assert.That(cut.FindAll(".jim-phase-h-label--active"), Has.Count.EqualTo(1));
        Assert.That(cut.FindAll(".jim-phase-h-connector-fill--active"), Has.Count.EqualTo(1));
    }

    [Test]
    public void RunPhaseStepper_SkippedStep_ExplainsItselfWithoutACaptionUnderEveryStep()
    {
        // The rail stays clean: the marker's muted state and its tooltip carry the explanation,
        // rather than a caption sitting under every step that did not run normally.
        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, ImportInProgress()));

        Assert.That(cut.FindAll(".jim-phase-h-label--skipped"), Has.Count.EqualTo(1));
        Assert.That(TooltipTexts(cut), Does.Contain("Not needed for this run"));
    }

    [Test]
    public void RunPhaseStepper_FailedStep_SaysWhereTheRunFailed()
    {
        var phases = new List<ActivityPhase>
        {
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Completed, started: Started, ended: Started.AddMinutes(1)),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 1, ActivityPhaseStatus.Failed, started: Started.AddMinutes(1), ended: Started.AddMinutes(2))
        };

        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, phases));

        Assert.That(cut.FindAll(".jim-phase-h-label--failed"), Has.Count.EqualTo(1));
        Assert.That(TooltipTexts(cut), Does.Contain("The run failed at this step"));
    }

    [Test]
    public void RunPhaseStepper_CompletedStep_ShowsHowLongItTook()
    {
        var phases = new List<ActivityPhase>
        {
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Completed, started: Started, ended: Started.AddMinutes(4)),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 1, ActivityPhaseStatus.Active, started: Started.AddMinutes(4))
        };

        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, phases));

        Assert.That(cut.Markup, Does.Contain("4 min"));
    }

    [Test]
    public void RunPhaseStepper_ConnectorSteps_AppearBeneathTheRailWhileTheirStepRuns()
    {
        // A horizontal rail has no room for a Connector's own steps, so they sit under it, and only
        // while the step that called the Connector is the one running.
        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, ImportInProgress()));

        Assert.That(cut.FindAll(".jim-phase-substep"), Has.Count.EqualTo(1));
        Assert.That(cut.Markup, Does.Contain("Reading the file"));
    }

    [Test]
    public void RunPhaseStepper_WithAMessage_ShowsItBeneathTheRail()
    {
        var cut = Render<RunPhaseStepper>(p => p
            .Add(c => c.Phases, ImportInProgress())
            .Add(c => c.Message, "Parsed 50,000 rows..."));

        Assert.That(cut.Find(".jim-phase-h-detail").InnerHtml, Does.Contain("Parsed 50,000 rows..."));
    }

    [Test]
    public void RunPhaseStepper_FinishedRun_ShowsNoLiveDetail()
    {
        var phases = new List<ActivityPhase>
        {
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Completed, started: Started, ended: Started.AddMinutes(1)),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 1, ActivityPhaseStatus.Completed, started: Started.AddMinutes(1), ended: Started.AddMinutes(2))
        };

        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, phases));

        Assert.That(cut.FindAll(".jim-phase-h-detail"), Is.Empty);
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Activities;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Components.Tests;

/// <summary>
/// The Activity detail page's phase stepper (#454). The behaviour worth pinning is what an
/// administrator can tell at a glance: which step is running, which are done and how long they
/// took, which are still to come, and that a step the run skipped does not read as a failure.
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
        Phase(RunPhaseKeys.ImportConnect, "Connecting to Connected System", 0, ActivityPhaseStatus.Completed,
            started: Started, ended: Started.AddSeconds(12)),
        Phase(RunPhaseKeys.ImportFetch, "Importing objects", 1, ActivityPhaseStatus.Active, started: Started.AddSeconds(12)),
        Phase(ActivityPhase.QualifyConnectorKey("read"), "Reading the file", 2, ActivityPhaseStatus.Active,
            parentKey: RunPhaseKeys.ImportFetch, started: Started.AddSeconds(13)),
        Phase(RunPhaseKeys.ImportDeletions, "Processing deletions", 3, ActivityPhaseStatus.Skipped),
        Phase(RunPhaseKeys.ImportSave, "Saving changes", 4, ActivityPhaseStatus.Pending)
    ];

    [Test]
    public void RunPhaseStepper_NoPhases_RendersNothing()
    {
        // Activities that are not Run Profile executions, and runs that predate phase recording.
        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, new List<ActivityPhase>()));

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void RunPhaseStepper_WithPhases_ShowsEveryTopLevelStepIncludingThoseStillToCome()
    {
        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, ImportInProgress()));

        Assert.That(cut.Markup, Does.Contain("Connecting to Connected System"));
        Assert.That(cut.Markup, Does.Contain("Importing objects"));
        Assert.That(cut.Markup, Does.Contain("Processing deletions"));
        Assert.That(cut.Markup, Does.Contain("Saving changes"),
            "Showing what is still to come is the reason the stepper exists; a progress bar cannot say it");
    }

    [Test]
    public void RunPhaseStepper_CompletedStep_ShowsHowLongItTook()
    {
        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, ImportInProgress()));

        Assert.That(cut.Markup, Does.Contain("12 sec"));
    }

    [Test]
    public void RunPhaseStepper_RunningStep_ShowsNoDurationYet()
    {
        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases,
            new List<ActivityPhase> { Phase(RunPhaseKeys.ImportSave, "Saving changes", 0, ActivityPhaseStatus.Active, started: Started) }));

        Assert.That(cut.FindAll(".jim-phase-step-duration"), Is.Empty);
    }

    [Test]
    public void RunPhaseStepper_SkippedStep_SaysItWasNotNeededRatherThanReadingAsAFailure()
    {
        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, ImportInProgress()));

        Assert.That(cut.Markup, Does.Contain("not needed"));
        Assert.That(cut.FindAll(".jim-phase-step--skipped"), Has.Count.EqualTo(1));
    }

    [Test]
    public void RunPhaseStepper_FailedStep_IsMarkedAsWhereTheRunFailed()
    {
        var phases = new List<ActivityPhase>
        {
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Completed, started: Started, ended: Started.AddMinutes(1)),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 1, ActivityPhaseStatus.Failed, started: Started.AddMinutes(1), ended: Started.AddMinutes(2))
        };

        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, phases));

        Assert.That(cut.Markup, Does.Contain("failed here"));
        Assert.That(cut.FindAll(".jim-phase-step--failed"), Has.Count.EqualTo(1));
    }

    [Test]
    public void RunPhaseStepper_ConnectorSteps_NestInsideTheStepThatCalledTheConnector()
    {
        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, ImportInProgress()));

        Assert.That(cut.FindAll(".jim-phase-substep"), Has.Count.EqualTo(1));
        Assert.That(cut.Markup, Does.Contain("Reading the file"));
        Assert.That(cut.FindAll(".jim-phase-step"), Has.Count.EqualTo(4),
            "A Connector's steps are detail inside the step that called it, so the top-level step count is the Connector's business only");
    }

    [Test]
    public void RunPhaseStepper_ConnectorStepsOfAStepNotRunning_AreNotShown()
    {
        // The Connector's detail belongs to the step in flight; showing every Connector step for
        // every phase would bury the one thing the administrator is watching.
        var phases = new List<ActivityPhase>
        {
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Completed, started: Started, ended: Started.AddMinutes(1)),
            Phase(ActivityPhase.QualifyConnectorKey("read"), "Reading the file", 1, ActivityPhaseStatus.Completed,
                parentKey: RunPhaseKeys.ImportFetch, started: Started, ended: Started.AddMinutes(1)),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 2, ActivityPhaseStatus.Active, started: Started.AddMinutes(1))
        };

        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, phases));

        Assert.That(cut.FindAll(".jim-phase-substep"), Is.Empty);
    }

    [Test]
    public void RunPhaseStepper_WithAMessage_ShowsItUnderTheStepItDescribes()
    {
        var cut = Render<RunPhaseStepper>(p => p
            .Add(c => c.Phases, ImportInProgress())
            .Add(c => c.Message, "Parsed 50,000 rows..."));

        var activeStep = cut.Find(".jim-phase-step--active");
        Assert.That(activeStep.InnerHtml, Does.Contain("Parsed 50,000 rows..."));
    }

    [Test]
    public void RunPhaseStepper_WithNoMessage_ShowsNoMessageLine()
    {
        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, ImportInProgress()));

        Assert.That(cut.FindAll(".jim-phase-step-message"), Is.Empty);
    }

    [Test]
    public void RunPhaseStepper_Phases_RenderInRunOrderWhateverOrderTheyArrive()
    {
        var phases = ImportInProgress();
        phases.Reverse();

        var cut = Render<RunPhaseStepper>(p => p.Add(c => c.Phases, phases));

        var names = cut.FindAll(".jim-phase-step-name").Select(e => e.TextContent).ToList();
        Assert.That(names, Is.EqualTo(new[]
        {
            "Connecting to Connected System",
            "Importing objects",
            "Processing deletions",
            "Saving changes"
        }));
    }
}

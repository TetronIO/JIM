// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Activities;
using JIM.Models.Tasking;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Progress cell of a row in the Operations queue (#1162). It used to show a bar and a count
/// with no way to tell which of a run's steps that count measured, nor how much of the run was
/// left. It now shows the run's shape above the running step's own bar.
/// </summary>
/// <remarks>
/// The two geometries carry different denominators on purpose: the rail is the run, the bar is the
/// step. That is only safe because the caption underneath names the step, which is why the caption
/// is pinned here rather than treated as decoration.
/// </remarks>
[TestFixture]
public class WorkerTaskProgressTests : JimComponentTestContext
{
    private const string RailSelector = "[data-testid='jim-queue-steps']";
    private const string SegmentSelector = "[data-testid='jim-queue-step']";
    private const string CaptionSelector = ".jim-queue-progress-caption";

    /// <summary>
    /// The caption's text, read from the element rather than from the whole cell's markup: NUnit
    /// clips a long actual value out of a failure message, and a rendered MudBlazor progress bar is
    /// long enough to push the caption past the cut.
    /// </summary>
    private static string Caption(IRenderedComponent<WorkerTaskProgress> cut) =>
        cut.Find(CaptionSelector).TextContent;

    /// <summary>
    /// An import partway through saving: one step skipped, one Connector step recorded inside the
    /// step that fetched, and two still to come. Five of the six recorded phases are steps of the
    /// run, so "Saving changes" is step 3 of 5 rather than step 4 of 6.
    /// </summary>
    private static RunPhaseSummary ImportSaving() => RunPhaseSummary.From(
    [
        Phase(RunPhaseKeys.ImportConnect, "Connecting to Connected System", 0, ActivityPhaseStatus.Skipped),
        Phase(RunPhaseKeys.ImportFetch, "Importing objects", 1, ActivityPhaseStatus.Completed),
        Phase(ActivityPhase.QualifyConnectorKey("read"), "Reading the file", 2, ActivityPhaseStatus.Completed, RunPhaseKeys.ImportFetch),
        Phase(RunPhaseKeys.ImportSave, "Saving changes", 3, ActivityPhaseStatus.Active),
        Phase(RunPhaseKeys.ImportReconcile, "Reconciling Pending Exports", 4, ActivityPhaseStatus.Pending),
        Phase(RunPhaseKeys.ImportRecordResults, "Recording results", 5, ActivityPhaseStatus.Pending)
    ])!;

    private static ActivityPhase Phase(string key, string name, int order, ActivityPhaseStatus status, string? parentKey = null) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Name = name,
        Order = order,
        Status = status,
        ParentKey = parentKey
    };

    private IRenderedComponent<WorkerTaskProgress> RenderCell(
        RunPhaseSummary? steps,
        int? objectsToProcess = 40000,
        int? objectsProcessed = 12480,
        string? progressMessage = null,
        WorkerTaskStatus status = WorkerTaskStatus.Processing) =>
        Render<WorkerTaskProgress>(p => p
            .Add(c => c.Status, status)
            .Add(c => c.Steps, steps)
            .Add(c => c.ObjectsToProcess, objectsToProcess)
            .Add(c => c.ObjectsProcessed, objectsProcessed)
            .Add(c => c.ProgressMessage, progressMessage));

    #region the rail

    [Test]
    public void WorkerTaskProgress_RunProfileExecution_DrawsOneSegmentPerStepOfTheRun()
    {
        // Six phases were recorded, one of them a Connector's. Five are the run's own steps, and
        // drawing the Connector's would make the same Run Profile read as a different length
        // depending on which Connected System it ran against.
        var cut = RenderCell(ImportSaving());

        Assert.That(cut.FindAll(SegmentSelector), Has.Count.EqualTo(5));
    }

    [Test]
    public void WorkerTaskProgress_RunProfileExecution_MarksEachSegmentWithItsOwnOutcome()
    {
        // The rail's whole job is that a skipped step reads differently from one not reached yet.
        var cut = RenderCell(ImportSaving());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll($"{SegmentSelector}.jim-queue-step--skipped"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll($"{SegmentSelector}.jim-queue-step--completed"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll($"{SegmentSelector}.jim-queue-step--active"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll($"{SegmentSelector}.jim-queue-step--pending"), Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void WorkerTaskProgress_FailedStep_IsVisibleWithoutOpeningTheActivity()
    {
        var steps = RunPhaseSummary.From(
        [
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Completed),
            Phase(RunPhaseKeys.ImportDeletions, "Processing deletions", 1, ActivityPhaseStatus.Failed),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 2, ActivityPhaseStatus.Pending)
        ]);

        var cut = RenderCell(steps);

        Assert.That(cut.FindAll($"{SegmentSelector}.jim-queue-step--failed"), Has.Count.EqualTo(1));
    }

    #endregion

    #region degrading to today's bar

    [Test]
    public void WorkerTaskProgress_TaskThatIsNotARunProfileExecution_DrawsNoRail()
    {
        // Clearing Connected System Objects, example data generation and factory reset record no
        // steps. They must render as they always have rather than as an empty rail.
        var cut = RenderCell(steps: null, progressMessage: "Deleting Connected System Objects");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(RailSelector), Is.Empty);
            Assert.That(Caption(cut), Does.Contain("Deleting Connected System Objects"));
        }
    }

    [Test]
    public void WorkerTaskProgress_TaskWithNoStepsAndNoCount_KeepsItsMessageAndAnIndeterminateBar()
    {
        var cut = RenderCell(steps: null, objectsToProcess: null, objectsProcessed: null,
            progressMessage: "Deleting Connected System Objects");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(RailSelector), Is.Empty);
            Assert.That(Caption(cut), Does.Contain("Deleting Connected System Objects"));
        }
    }

    [Test]
    public void WorkerTaskProgress_QueuedTask_StillJustSaysItIsWaiting()
    {
        var cut = RenderCell(ImportSaving(), status: WorkerTaskStatus.Queued);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(RailSelector), Is.Empty, "Nothing has run yet, so there is no shape to report");
            Assert.That(cut.Markup, Does.Contain("Waiting"));
        }
    }

    #endregion

    #region the caption

    [Test]
    public void WorkerTaskProgress_RunProfileExecution_NamesTheStepTheCountIsMeasuring()
    {
        // Without this the rail and the bar are two denominators with nothing to tell them apart.
        var cut = RenderCell(ImportSaving());

        Assert.That(Caption(cut), Does.Contain("Step 3 of 5: Saving changes"));
    }

    [Test]
    public void WorkerTaskProgress_RunProfileExecution_KeepsTheCountAlongsideTheStepName()
    {
        var cut = RenderCell(ImportSaving());

        Assert.That(Caption(cut), Does.Contain("12,480 / 40,000"));
    }

    [Test]
    public void WorkerTaskProgress_RunProfileExecution_LeavesTheNarrationToTheActivity()
    {
        // The worker's message narrates what is happening inside the step, which this cell has no
        // room for: appended, it produced "Step 3 of 5: Saving changes - 12,480 / 40,000 - Importing
        // users", which is both too long for the column and self-contradictory to skim. A message
        // different from the step name is the case that matters; one that merely repeats it would
        // have passed a weaker check either way.
        var cut = RenderCell(ImportSaving(), progressMessage: "Importing users");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Caption(cut), Does.Not.Contain("Importing users"));
            Assert.That(Caption(cut), Is.EqualTo("Step 3 of 5: Saving changes - 12,480 / 40,000"));
        }
    }

    [Test]
    public void WorkerTaskProgress_RunBetweenSteps_ReportsTheRunWithoutNamingAStep()
    {
        // A run momentarily has nothing running as it moves between steps. The rail still has a
        // shape worth showing; claiming a step is running would be a lie.
        var steps = RunPhaseSummary.From(
        [
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Completed),
            Phase(RunPhaseKeys.ImportSave, "Saving changes", 1, ActivityPhaseStatus.Pending)
        ]);

        var cut = RenderCell(steps);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(SegmentSelector), Has.Count.EqualTo(2));
            Assert.That(Caption(cut), Does.Not.Contain("Step "));
        }
    }

    [Test]
    public void WorkerTaskProgress_RunProfileExecutionWithNothingToCount_NamesTheStepAnyway()
    {
        // A paged import does not know its total. The step name is the whole of what the cell can
        // say, and is worth more than the bar it replaces.
        var cut = RenderCell(ImportSaving(), objectsToProcess: null, objectsProcessed: null);

        Assert.That(Caption(cut), Does.Contain("Step 3 of 5: Saving changes"));
    }

    #endregion
}

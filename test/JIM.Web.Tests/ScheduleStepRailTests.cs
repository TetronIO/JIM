// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using Bunit;
using JIM.Models.Scheduling;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Schedule Execution rail in an Operations queue group header (#1162). The header used to offer
/// only arithmetic ("6 tasks across 6 steps, 1 processing, 3 queued, 2 waiting"), which has to be
/// added up before it says how far the Schedule has to go.
/// </summary>
[TestFixture]
public class ScheduleStepRailTests : JimComponentTestContext
{
    private const string RailSelector = "[data-testid='jim-schedule-rail']";
    private const string StepSelector = "[data-testid='jim-schedule-step']";
    private const string LabelSelector = "[data-testid='jim-schedule-step-label']";

    private static ScheduleStepProgress Step(int index, string name, ScheduleExecutionStepStatus status, params ScheduleExecutionStepStatus[] taskStatuses) => new()
    {
        StepIndex = index,
        Name = name,
        Status = status,
        TaskStatuses = taskStatuses.Length > 0 ? taskStatuses : [status]
    };

    private static ScheduleExecutionProgress Progress(int? currentStepNumber, params ScheduleStepProgress[] steps) => new()
    {
        CurrentStepNumber = currentStepNumber,
        Steps = steps
    };

    private IRenderedComponent<ScheduleStepRail> RenderRail(ScheduleExecutionProgress? progress) =>
        Render<ScheduleStepRail>(p => p.Add(c => c.Progress, progress));

    [Test]
    public void ScheduleStepRail_ScheduleMidwayThrough_DrawsAMarkerPerStepCarryingItsOwnOutcome()
    {
        var cut = RenderRail(Progress(3,
            Step(0, "HR Import", ScheduleExecutionStepStatus.Completed),
            Step(1, "HR Sync", ScheduleExecutionStepStatus.Completed),
            Step(2, "AD Import", ScheduleExecutionStepStatus.Processing),
            Step(3, "AD Export", ScheduleExecutionStepStatus.Waiting)));

        var statuses = cut.FindAll(StepSelector).Select(e => e.GetAttribute("data-status")).ToList();

        Assert.That(statuses, Is.EqualTo(new[] { "completed", "completed", "running", "pending" }));
    }

    [Test]
    public void ScheduleStepRail_FailedStep_IsVisibleFromACollapsedGroup()
    {
        // The whole point of putting anything in the group header: a Schedule can be collapsed to one
        // row, and a failure inside it must still read.
        var cut = RenderRail(Progress(null,
            Step(0, "HR Import", ScheduleExecutionStepStatus.Completed),
            Step(1, "AD Import", ScheduleExecutionStepStatus.Failed)));

        Assert.That(cut.FindAll($"{StepSelector}[data-status='failed']"), Has.Count.EqualTo(1));
    }

    [Test]
    public void ScheduleStepRail_ParallelStep_DividesOneMarkerRatherThanGrowingTheHeader()
    {
        // A Schedule can fan out across a dozen Connected Systems. The marker divides; the header does
        // not gain a row per task, which is what ruled out the alternatives.
        var cut = RenderRail(Progress(1,
            Step(0, "3 in parallel", ScheduleExecutionStepStatus.Processing,
                ScheduleExecutionStepStatus.Completed, ScheduleExecutionStepStatus.Processing, ScheduleExecutionStepStatus.Waiting)));

        var marker = cut.Find(StepSelector);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(StepSelector), Has.Count.EqualTo(1));
            Assert.That(marker.GetAttribute("style"), Does.Contain("conic-gradient"));
        }
    }

    [Test]
    public void ScheduleStepRail_ParallelStepWithAFailure_StartsTheFailedWedgeAtTwelveOClock()
    {
        // The rule ScheduleStepReading.OrderWedges exists for, checked where it is actually drawn: the
        // gradient starts at -90deg, so the first stop is the top of the disc.
        var cut = RenderRail(Progress(1,
            Step(0, "2 in parallel", ScheduleExecutionStepStatus.Failed,
                ScheduleExecutionStepStatus.Failed, ScheduleExecutionStepStatus.Processing)));

        var style = cut.Find(StepSelector).GetAttribute("style");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(style, Does.Contain("from -90deg"));
            Assert.That(style, Does.Contain("var(--mud-palette-error) 0deg 180deg"));
        }
    }

    [Test]
    public void ScheduleStepRail_EveryStep_IsNamedBeneathItsMarker()
    {
        // The names are what make the rows underneath make sense, and a collapsed group has no rows.
        var cut = RenderRail(Progress(2,
            Step(0, "HR Import", ScheduleExecutionStepStatus.Completed),
            Step(1, "2 in parallel", ScheduleExecutionStepStatus.Processing,
                ScheduleExecutionStepStatus.Processing, ScheduleExecutionStepStatus.Processing)));

        var labels = cut.FindAll(LabelSelector).Select(e => e.TextContent.Trim()).ToList();

        Assert.That(labels, Is.EqualTo(new[] { "HR Import", "2 in parallel" }));
    }

    [Test]
    public void ScheduleStepRail_NothingToDraw_RendersNothingRatherThanAnEmptyRail()
    {
        // A queue group that is not a Schedule Execution, or one whose shape could not be read, must
        // leave the header as it was rather than showing a rail with no steps on it.
        var cut = RenderRail(null);

        Assert.That(cut.FindAll(RailSelector), Is.Empty);
    }
}

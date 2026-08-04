// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using NUnit.Framework;

namespace JIM.Models.Tests.Scheduling;

/// <summary>
/// A Schedule Execution reduced to the shape a list view can draw (#1162). The Operations queue shows
/// a Schedule's tasks as rows and had nothing at all to say about the Schedule itself, so how far it
/// had to go could only be worked out by counting statuses across the rows.
/// </summary>
[TestFixture]
public class ScheduleStepReadingTests
{
    private static ScheduleStepObservation Task(int stepIndex, string name, WorkerTaskStatus status) =>
        new() { StepIndex = stepIndex, Name = name, TaskStatus = status };

    private static ScheduleStepObservation Ran(int stepIndex, string name, ActivityStatus status) =>
        new() { StepIndex = stepIndex, Name = name, ActivityStatus = status };

    /// <summary>
    /// The reader returns null only where there is nothing to draw, which each of these fixtures rules
    /// out by construction; the one test that exercises it calls the reader directly.
    /// </summary>
    private static ScheduleExecutionProgress Read(int totalSteps, int currentStepIndex, params ScheduleStepObservation[] observations) =>
        ScheduleStepReading.Read(totalSteps, currentStepIndex, observations)!;

    #region what one task's records add up to

    [Test]
    public void StatusOf_TaskFinishedAndDeleted_IsReadFromItsActivity()
    {
        // The only record left once a task completes, and the reason Activities are consulted at all:
        // without them a finished step is indistinguishable from one that never existed.
        Assert.That(ScheduleStepReading.StatusOf(Ran(0, "HR Import", ActivityStatus.Complete)),
            Is.EqualTo(ScheduleStepStatus.Completed));
    }

    [Test]
    public void StatusOf_ActivityThatHasFinished_OutranksATaskStillBeingTidiedAway()
    {
        // A task is deleted just after its Activity reaches a terminal status, so for a moment both
        // exist and disagree. The Activity is the one that has actually concluded.
        var observation = new ScheduleStepObservation
        {
            StepIndex = 0,
            Name = "HR Import",
            TaskStatus = WorkerTaskStatus.Processing,
            ActivityStatus = ActivityStatus.FailedWithError
        };

        Assert.That(ScheduleStepReading.StatusOf(observation), Is.EqualTo(ScheduleStepStatus.Failed));
    }

    [Test]
    public void StatusOf_ActivityStillRunning_DefersToTheTaskWhichSaysMore()
    {
        // "In progress" is true of a queued task's whole lifetime; the task distinguishes waiting from
        // running, which is the distinction the rail draws.
        var observation = new ScheduleStepObservation
        {
            StepIndex = 0,
            Name = "HR Import",
            TaskStatus = WorkerTaskStatus.Queued,
            ActivityStatus = ActivityStatus.InProgress
        };

        Assert.That(ScheduleStepReading.StatusOf(observation), Is.EqualTo(ScheduleStepStatus.Pending));
    }

    [Test]
    public void StatusOf_CompletedWithAnError_ReadsAsFailedNotCompleted()
    {
        // The distinction the rail exists to carry. A step that got to the end having failed objects
        // is not a step an administrator can stop looking at.
        Assert.That(ScheduleStepReading.StatusOf(Ran(0, "HR Import", ActivityStatus.CompleteWithError)),
            Is.EqualTo(ScheduleStepStatus.Failed));
    }

    [Test]
    public void StatusOf_CompletedWithAWarning_ReadsAsCompleted()
    {
        Assert.That(ScheduleStepReading.StatusOf(Ran(0, "HR Import", ActivityStatus.CompleteWithWarning)),
            Is.EqualTo(ScheduleStepStatus.Completed));
    }

    [Test]
    public void StatusOf_TaskWaitingForAnEarlierStep_IsPending()
    {
        Assert.That(ScheduleStepReading.StatusOf(Task(2, "AD Export", WorkerTaskStatus.WaitingForPreviousStep)),
            Is.EqualTo(ScheduleStepStatus.Pending));
    }

    [Test]
    public void StatusOf_TaskBeingCancelled_IsCancelledWhileItStops()
    {
        // It is still running, but reporting it as running would leave an administrator who has just
        // cancelled a Schedule watching for a change that never comes.
        Assert.That(ScheduleStepReading.StatusOf(Task(0, "HR Import", WorkerTaskStatus.CancellationRequested)),
            Is.EqualTo(ScheduleStepStatus.Cancelled));
    }

    #endregion

    #region what a step group's tasks add up to

    [Test]
    public void Aggregate_OneTaskFailedAmongstSuccesses_ReportsTheFailure()
    {
        // A step is not fine because most of it was fine.
        var status = ScheduleStepReading.Aggregate(
            [ScheduleStepStatus.Completed, ScheduleStepStatus.Failed, ScheduleStepStatus.Completed]);

        Assert.That(status, Is.EqualTo(ScheduleStepStatus.Failed));
    }

    [Test]
    public void Aggregate_SomethingStillRunning_OutranksSomethingCancelled()
    {
        var status = ScheduleStepReading.Aggregate([ScheduleStepStatus.Cancelled, ScheduleStepStatus.Running]);

        Assert.That(status, Is.EqualTo(ScheduleStepStatus.Running));
    }

    [Test]
    public void Aggregate_EverythingDone_IsDone()
    {
        var status = ScheduleStepReading.Aggregate([ScheduleStepStatus.Completed, ScheduleStepStatus.Completed]);

        Assert.That(status, Is.EqualTo(ScheduleStepStatus.Completed));
    }

    [Test]
    public void Aggregate_OneTaskDoneAndOneStillQueued_ReadsAsUnderWay()
    {
        // Nothing is running this instant, but the step has started and has not finished. Calling it
        // pending would walk the Schedule backwards on screen as each parallel task lands.
        var status = ScheduleStepReading.Aggregate([ScheduleStepStatus.Completed, ScheduleStepStatus.Pending]);

        Assert.That(status, Is.EqualTo(ScheduleStepStatus.Running));
    }

    [Test]
    public void Aggregate_NothingStarted_IsPending()
    {
        var status = ScheduleStepReading.Aggregate([ScheduleStepStatus.Pending, ScheduleStepStatus.Pending]);

        Assert.That(status, Is.EqualTo(ScheduleStepStatus.Pending));
    }

    #endregion

    #region the wedge order, which is what makes a divided marker work at scale

    // A parallel step is drawn as one marker divided into a wedge per task. The wedges are ordered by
    // status rather than by task, so that a failure always starts at twelve o'clock and always reads.
    // Ordering by task would scatter a single failure anywhere around a 16px disc, and at one task in
    // twelve it would be invisible. This is the rule that lets the marker degrade gracefully from "a
    // wedge per task" into "a proportion" as the fan-out grows, and it is the first thing a later
    // change would simplify away without knowing why it is there.

    [Test]
    public void OrderWedges_TwoTasks_PutsTheFailureFirst()
    {
        var wedges = ScheduleStepReading.OrderWedges([ScheduleStepStatus.Completed, ScheduleStepStatus.Failed]);

        Assert.That(wedges, Is.EqualTo(new[] { ScheduleStepStatus.Failed, ScheduleStepStatus.Completed }));
    }

    [Test]
    public void OrderWedges_ThreeTasks_RunsFailedThenCompletedThenRunningThenPending()
    {
        var wedges = ScheduleStepReading.OrderWedges(
            [ScheduleStepStatus.Pending, ScheduleStepStatus.Running, ScheduleStepStatus.Completed]);

        Assert.That(wedges, Is.EqualTo(new[]
        {
            ScheduleStepStatus.Completed, ScheduleStepStatus.Running, ScheduleStepStatus.Pending
        }));
    }

    [Test]
    public void OrderWedges_SixTasks_KeepsTheLoneFailureAtTwelveOClock()
    {
        var wedges = ScheduleStepReading.OrderWedges(
        [
            ScheduleStepStatus.Completed, ScheduleStepStatus.Completed, ScheduleStepStatus.Running,
            ScheduleStepStatus.Failed, ScheduleStepStatus.Completed, ScheduleStepStatus.Pending
        ]);

        Assert.That(wedges[0], Is.EqualTo(ScheduleStepStatus.Failed));
    }

    [Test]
    public void OrderWedges_TwelveTasks_KeepsTheLoneFailureAtTwelveOClock()
    {
        // The fan-out that ruled out every treatment whose height grows with the task count. One
        // failure in twelve is a thirty-degree wedge, which reads only because it starts at the top.
        var statuses = Enumerable.Repeat(ScheduleStepStatus.Completed, 7)
            .Append(ScheduleStepStatus.Running)
            .Concat(Enumerable.Repeat(ScheduleStepStatus.Pending, 3))
            .Append(ScheduleStepStatus.Failed)
            .ToList();

        var wedges = ScheduleStepReading.OrderWedges(statuses);

        Assert.Multiple(() =>
        {
            Assert.That(wedges[0], Is.EqualTo(ScheduleStepStatus.Failed));
            Assert.That(wedges, Has.Count.EqualTo(12), "Every task keeps its wedge; ordering is not grouping");
        });
    }

    [Test]
    public void OrderWedges_CancelledTasks_SitWithTheOtherOutcomesNeedingAttention()
    {
        var wedges = ScheduleStepReading.OrderWedges(
        [
            ScheduleStepStatus.Pending, ScheduleStepStatus.Completed,
            ScheduleStepStatus.Cancelled, ScheduleStepStatus.Failed
        ]);

        Assert.That(wedges, Is.EqualTo(new[]
        {
            ScheduleStepStatus.Failed, ScheduleStepStatus.Cancelled,
            ScheduleStepStatus.Completed, ScheduleStepStatus.Pending
        }));
    }

    #endregion

    #region the Schedule as a whole

    [Test]
    public void Read_ScheduleMidwayThrough_DrawsEveryStepIncludingThoseAlreadyFinished()
    {
        // The finished steps' tasks have been deleted, so only their Activities remain. A rail built
        // from the queue alone would have shrunk to three segments and read "step 1 of 3".
        var progress = Read(totalSteps: 5, currentStepIndex: 2,
        [
            Ran(0, "HR Import", ActivityStatus.Complete),
            Ran(1, "HR Sync", ActivityStatus.Complete),
            Task(2, "AD Import", WorkerTaskStatus.Processing),
            Task(3, "AD Sync", WorkerTaskStatus.WaitingForPreviousStep),
            Task(4, "AD Export", WorkerTaskStatus.WaitingForPreviousStep)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(progress.TotalSteps, Is.EqualTo(5));
            Assert.That(progress.CurrentStepNumber, Is.EqualTo(3));
            Assert.That(progress.Steps.Select(s => s.Status), Is.EqualTo(new[]
            {
                ScheduleStepStatus.Completed, ScheduleStepStatus.Completed, ScheduleStepStatus.Running,
                ScheduleStepStatus.Pending, ScheduleStepStatus.Pending
            }));
        });
    }

    [Test]
    public void Read_StepRunningSeveralTasks_IsNamedByHowManyRatherThanByOneOfThem()
    {
        var progress = Read(totalSteps: 2, currentStepIndex: 0,
        [
            Task(0, "AD Import", WorkerTaskStatus.Processing),
            Task(0, "Cloud Import", WorkerTaskStatus.Processing),
            Task(1, "AD Sync", WorkerTaskStatus.WaitingForPreviousStep)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Steps[0].Name, Is.EqualTo("2 in parallel"));
            Assert.That(progress.Steps[0].IsParallel, Is.True);
            Assert.That(progress.Steps[1].Name, Is.EqualTo("AD Sync"));
            Assert.That(progress.Steps[1].IsParallel, Is.False);
        });
    }

    [Test]
    public void Read_ParallelStepWhereOneTaskFailed_ShowsBothOutcomesAndLeadsWithTheFailure()
    {
        // The case the whole treatment was chosen for: one of two concurrent imports has failed while
        // the other is still going, seen from a group header with the rows collapsed underneath it.
        var progress = Read(totalSteps: 1, currentStepIndex: 0,
        [
            Ran(0, "AD Import", ActivityStatus.FailedWithError),
            Task(0, "Cloud Import", WorkerTaskStatus.Processing)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Steps[0].Status, Is.EqualTo(ScheduleStepStatus.Failed));
            Assert.That(progress.Steps[0].TaskStatuses, Is.EqualTo(new[]
            {
                ScheduleStepStatus.Failed, ScheduleStepStatus.Running
            }));
        });
    }

    [Test]
    public void Read_StepThatLeftNoRecordAtAll_StillTakesItsPlaceInTheSchedule()
    {
        // A step type that queues no task and records no Activity is passed straight through. It is
        // still a step of the Schedule, and dropping it would shorten the rail as the run proceeded.
        var progress = Read(totalSteps: 3, currentStepIndex: 2,
        [
            Ran(0, "HR Import", ActivityStatus.Complete),
            Task(2, "AD Sync", WorkerTaskStatus.Processing)
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(progress.Steps, Has.Count.EqualTo(3));
            Assert.That(progress.Steps[1].Status, Is.EqualTo(ScheduleStepStatus.Completed),
                "The Schedule has moved past it, so it is behind us whatever it left behind");
            Assert.That(progress.Steps[1].Name, Is.EqualTo("Step 2"));
        });
    }

    [Test]
    public void Read_ScheduleBetweenSteps_StillSaysWhichStepItHasReached()
    {
        // A Schedule momentarily has nothing running as it moves between steps, but its position is
        // recorded rather than inferred, so it does not stop knowing where it is.
        var progress = Read(totalSteps: 2, currentStepIndex: 1,
            Ran(0, "HR Import", ActivityStatus.Complete),
            Task(1, "AD Sync", WorkerTaskStatus.Queued));

        Assert.Multiple(() =>
        {
            Assert.That(progress.CurrentStepNumber, Is.EqualTo(2));
            Assert.That(progress.Steps[1].Status, Is.EqualTo(ScheduleStepStatus.Pending),
                "Knowing which step is next is not the same as claiming it has started");
        });
    }

    [Test]
    public void Read_StepHoldingAFailureBesideARunningTask_StillSaysWhichStepItIs()
    {
        // The step aggregates to failed, which is right, but the Schedule is still on it. Deriving the
        // position from step statuses instead lost it precisely here, and the group header fell back
        // to counting tasks at the moment the step number mattered most.
        var progress = Read(totalSteps: 5, currentStepIndex: 1,
            Ran(0, "HR Import", ActivityStatus.Complete),
            Ran(1, "Legacy CRM Import", ActivityStatus.FailedWithError),
            Task(1, "AD Import", WorkerTaskStatus.Processing));

        Assert.Multiple(() =>
        {
            Assert.That(progress.Steps[1].Status, Is.EqualTo(ScheduleStepStatus.Failed));
            Assert.That(progress.CurrentStepNumber, Is.EqualTo(2));
        });
    }

    [Test]
    public void Read_MoreStepsObservedThanTheExecutionRecorded_DrawsThemAllAnyway()
    {
        // The recorded total is a snapshot and the observations are what is actually there. Trusting
        // the smaller number would hide a task from a view whose job is to account for every one.
        var progress = Read(totalSteps: 2, currentStepIndex: 2,
        [
            Ran(0, "HR Import", ActivityStatus.Complete),
            Ran(1, "HR Sync", ActivityStatus.Complete),
            Task(2, "AD Import", WorkerTaskStatus.Processing)
        ]);

        Assert.That(progress.TotalSteps, Is.EqualTo(3));
    }

    [Test]
    public void Read_NothingToReport_IsNothingRatherThanAnEmptyRail()
    {
        Assert.That(ScheduleStepReading.Read(totalSteps: 0, currentStepIndex: 0, []), Is.Null);
    }

    #endregion
}

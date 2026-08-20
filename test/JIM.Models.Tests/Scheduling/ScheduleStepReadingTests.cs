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
/// <remarks>
/// The step statuses are the ones the Schedule Execution detail page and the REST read use, whose
/// display strings are a published contract. That is deliberate: the group view derives from the same
/// rule as the row view rather than deriving its own, so the two cannot disagree about a step that is
/// finishing at the moment they are each asked.
/// </remarks>
[TestFixture]
public class ScheduleStepReadingTests
{
    private const ScheduleExecutionStatus Running = ScheduleExecutionStatus.InProgress;

    private static ScheduleStepObservation Task(int stepIndex, string name, WorkerTaskStatus status) =>
        new() { StepIndex = stepIndex, Name = name, TaskStatus = status };

    private static ScheduleStepObservation Ran(int stepIndex, string name, ActivityStatus status) =>
        new() { StepIndex = stepIndex, Name = name, ActivityStatus = status };

    /// <summary>
    /// The reader returns null only where there is nothing to draw, which each of these fixtures rules
    /// out by construction; the one test that exercises it calls the reader directly.
    /// </summary>
    private static ScheduleExecutionProgress Read(int totalSteps, int currentStepIndex, params ScheduleStepObservation[] observations) =>
        ScheduleStepReading.Read(totalSteps, currentStepIndex, Running, observations)!;

    private static ScheduleExecutionStepStatus StatusOf(ScheduleStepObservation observation) =>
        ScheduleStepReading.StatusOf(observation.TaskStatus, observation.ActivityStatus, observation.StepIndex, 0, Running);

    #region what one task's records add up to

    [Test]
    public void StatusOf_TaskFinishedAndDeleted_IsReadFromItsActivity()
    {
        // The only record left once a task completes, and the reason Activities are consulted at all:
        // without them a finished step is indistinguishable from one that never existed.
        Assert.That(StatusOf(Ran(0, "HR Import", ActivityStatus.Complete)),
            Is.EqualTo(ScheduleExecutionStepStatus.Completed));
    }

    [Test]
    public void StatusOf_CompletedWithAnError_IsNotTheSameAsCompleted()
    {
        // The distinction the rail exists to carry. A step that got to the end having failed objects
        // is not a step an administrator can stop looking at.
        Assert.That(StatusOf(Ran(0, "HR Import", ActivityStatus.CompleteWithError)),
            Is.EqualTo(ScheduleExecutionStepStatus.CompletedWithError));
    }

    [Test]
    public void StatusOf_TaskWaitingForAnEarlierStep_IsWaiting()
    {
        Assert.That(StatusOf(Task(2, "AD Export", WorkerTaskStatus.WaitingForPreviousStep)),
            Is.EqualTo(ScheduleExecutionStepStatus.Waiting));
    }

    [Test]
    public void StatusOf_TaskBeingCancelled_SaysSoWhileItStops()
    {
        // It is still running, but reporting it as running would leave an administrator who has just
        // cancelled a Schedule watching for a change that never comes.
        Assert.That(StatusOf(Task(0, "HR Import", WorkerTaskStatus.CancellationRequested)),
            Is.EqualTo(ScheduleExecutionStepStatus.Cancelling));
    }

    [Test]
    public void StatusOf_NoRecordOfItsOwn_IsInferredFromWhereTheExecutionGot()
    {
        // A step type that queues no task and writes no Activity is passed straight through, so its
        // place in the Schedule is all there is to go on.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScheduleStepReading.StatusOf(null, null, 0, 2, Running),
                Is.EqualTo(ScheduleExecutionStepStatus.Completed), "Behind the execution's position");
            Assert.That(ScheduleStepReading.StatusOf(null, null, 3, 2, Running),
                Is.EqualTo(ScheduleExecutionStepStatus.Pending), "Ahead of it");
        }
    }

    #endregion

    #region what a step group's tasks add up to

    [Test]
    public void Aggregate_OneTaskFailedAmongstSuccesses_ReportsTheFailure()
    {
        // A step is not fine because most of it was fine.
        var status = ScheduleStepReading.Aggregate(
        [
            ScheduleExecutionStepStatus.Completed,
            ScheduleExecutionStepStatus.Failed,
            ScheduleExecutionStepStatus.Completed
        ]);

        Assert.That(status, Is.EqualTo(ScheduleExecutionStepStatus.Failed));
    }

    [Test]
    public void Aggregate_SomethingStillRunning_OutranksSomethingCancelled()
    {
        var status = ScheduleStepReading.Aggregate(
            [ScheduleExecutionStepStatus.Cancelled, ScheduleExecutionStepStatus.Processing]);

        Assert.That(status, Is.EqualTo(ScheduleExecutionStepStatus.Processing));
    }

    [Test]
    public void Aggregate_EverythingDoneButOneWithAWarning_CarriesTheWarning()
    {
        var status = ScheduleStepReading.Aggregate(
            [ScheduleExecutionStepStatus.Completed, ScheduleExecutionStepStatus.CompletedWithWarning]);

        Assert.That(status, Is.EqualTo(ScheduleExecutionStepStatus.CompletedWithWarning));
    }

    [Test]
    public void Aggregate_EverythingDone_IsDone()
    {
        var status = ScheduleStepReading.Aggregate(
            [ScheduleExecutionStepStatus.Completed, ScheduleExecutionStepStatus.Completed]);

        Assert.That(status, Is.EqualTo(ScheduleExecutionStepStatus.Completed));
    }

    [Test]
    public void Aggregate_OneTaskDoneAndOneStillQueued_ReadsAsUnderWay()
    {
        // Nothing is running this instant, but the step has started and has not finished. Calling it
        // pending would walk the Schedule backwards on screen as each parallel task lands.
        var status = ScheduleStepReading.Aggregate(
            [ScheduleExecutionStepStatus.Completed, ScheduleExecutionStepStatus.Waiting]);

        Assert.That(status, Is.EqualTo(ScheduleExecutionStepStatus.Processing));
    }

    [Test]
    public void Aggregate_NothingStarted_IsNotStarted()
    {
        var status = ScheduleStepReading.Aggregate(
            [ScheduleExecutionStepStatus.Waiting, ScheduleExecutionStepStatus.Waiting]);

        Assert.That(status, Is.EqualTo(ScheduleExecutionStepStatus.Waiting));
    }

    #endregion

    #region the wedge order, which is what makes a divided marker work at scale

    // A parallel step is drawn as one marker divided into a wedge per task. The wedges are ordered by
    // outcome rather than by task, so that a failure always starts at twelve o'clock and always reads.
    // Ordering by task would scatter a single failure anywhere around a 16px disc, and at one task in
    // twelve it would be invisible. This is the rule that lets the marker degrade gracefully from "a
    // wedge per task" into "a proportion" as the fan-out grows, and it is the first thing a later
    // change would simplify away without knowing why it is there.

    [Test]
    public void OrderWedges_TwoTasks_PutsTheFailureFirst()
    {
        var wedges = ScheduleStepReading.OrderWedges(
            [ScheduleExecutionStepStatus.Completed, ScheduleExecutionStepStatus.Failed]);

        Assert.That(wedges, Is.EqualTo(new[]
        {
            ScheduleExecutionStepStatus.Failed, ScheduleExecutionStepStatus.Completed
        }));
    }

    [Test]
    public void OrderWedges_ThreeTasks_RunsWhatFinishedThenWhatIsRunningThenWhatHasNotStarted()
    {
        var wedges = ScheduleStepReading.OrderWedges(
        [
            ScheduleExecutionStepStatus.Waiting,
            ScheduleExecutionStepStatus.Processing,
            ScheduleExecutionStepStatus.Completed
        ]);

        Assert.That(wedges, Is.EqualTo(new[]
        {
            ScheduleExecutionStepStatus.Completed,
            ScheduleExecutionStepStatus.Processing,
            ScheduleExecutionStepStatus.Waiting
        }));
    }

    [Test]
    public void OrderWedges_SixTasks_KeepsTheLoneFailureAtTwelveOClock()
    {
        var wedges = ScheduleStepReading.OrderWedges(
        [
            ScheduleExecutionStepStatus.Completed, ScheduleExecutionStepStatus.Completed,
            ScheduleExecutionStepStatus.Processing, ScheduleExecutionStepStatus.Failed,
            ScheduleExecutionStepStatus.Completed, ScheduleExecutionStepStatus.Waiting
        ]);

        Assert.That(wedges[0], Is.EqualTo(ScheduleExecutionStepStatus.Failed));
    }

    [Test]
    public void OrderWedges_TwelveTasks_KeepsTheLoneFailureAtTwelveOClock()
    {
        // The fan-out that ruled out every treatment whose height grows with the task count. One
        // failure in twelve is a thirty-degree wedge, which reads only because it starts at the top.
        var statuses = Enumerable.Repeat(ScheduleExecutionStepStatus.Completed, 7)
            .Append(ScheduleExecutionStepStatus.Processing)
            .Concat(Enumerable.Repeat(ScheduleExecutionStepStatus.Waiting, 3))
            .Append(ScheduleExecutionStepStatus.Failed)
            .ToList();

        var wedges = ScheduleStepReading.OrderWedges(statuses);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wedges[0], Is.EqualTo(ScheduleExecutionStepStatus.Failed));
            Assert.That(wedges, Has.Count.EqualTo(12), "Every task keeps its wedge; ordering is not grouping");
        }
    }

    [Test]
    public void OrderWedges_AStepThatFinishedWithErrors_SitsWithTheOutrightFailures()
    {
        // Both need attention, and both must lead. A completed-with-errors step buried behind the
        // successes is the case a divided marker exists to surface.
        var wedges = ScheduleStepReading.OrderWedges(
        [
            ScheduleExecutionStepStatus.Waiting,
            ScheduleExecutionStepStatus.Completed,
            ScheduleExecutionStepStatus.CompletedWithError
        ]);

        Assert.That(wedges[0], Is.EqualTo(ScheduleExecutionStepStatus.CompletedWithError));
    }

    #endregion

    #region the Schedule as a whole

    [Test]
    public void Read_ScheduleMidwayThrough_DrawsEveryStepIncludingThoseAlreadyFinished()
    {
        // The finished steps' tasks have been deleted, so only their Activities remain. A rail built
        // from the queue alone would have shrunk to three segments and read "step 1 of 3".
        var progress = Read(totalSteps: 5, currentStepIndex: 2,
            Ran(0, "HR Import", ActivityStatus.Complete),
            Ran(1, "HR Sync", ActivityStatus.Complete),
            Task(2, "AD Import", WorkerTaskStatus.Processing),
            Task(3, "AD Sync", WorkerTaskStatus.WaitingForPreviousStep),
            Task(4, "AD Export", WorkerTaskStatus.WaitingForPreviousStep));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.TotalSteps, Is.EqualTo(5));
            Assert.That(progress.CurrentStepNumber, Is.EqualTo(3));
            Assert.That(progress.Steps.Select(s => s.Status), Is.EqualTo(new[]
            {
                ScheduleExecutionStepStatus.Completed, ScheduleExecutionStepStatus.Completed,
                ScheduleExecutionStepStatus.Processing, ScheduleExecutionStepStatus.Waiting,
                ScheduleExecutionStepStatus.Waiting
            }));
        }
    }

    [Test]
    public void Read_StepRunningSeveralTasks_IsNamedByHowManyRatherThanByOneOfThem()
    {
        var progress = Read(totalSteps: 2, currentStepIndex: 0,
            Task(0, "AD Import", WorkerTaskStatus.Processing),
            Task(0, "Cloud Import", WorkerTaskStatus.Processing),
            Task(1, "AD Sync", WorkerTaskStatus.WaitingForPreviousStep));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.Steps[0].Name, Is.EqualTo("2 in parallel"));
            Assert.That(progress.Steps[0].IsParallel, Is.True);
            Assert.That(progress.Steps[1].Name, Is.EqualTo("AD Sync"));
            Assert.That(progress.Steps[1].IsParallel, Is.False);
        }
    }

    [Test]
    public void Read_ParallelStepWhereOneTaskFailed_ShowsBothOutcomesAndLeadsWithTheFailure()
    {
        // The case the whole treatment was chosen for: one of two concurrent imports has failed while
        // the other is still going, seen from a group header with the rows collapsed underneath it.
        var progress = Read(totalSteps: 1, currentStepIndex: 0,
            Ran(0, "AD Import", ActivityStatus.FailedWithError),
            Task(0, "Cloud Import", WorkerTaskStatus.Processing));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.Steps[0].Status, Is.EqualTo(ScheduleExecutionStepStatus.Failed));
            Assert.That(progress.Steps[0].TaskStatuses, Is.EqualTo(new[]
            {
                ScheduleExecutionStepStatus.Failed, ScheduleExecutionStepStatus.Processing
            }));
        }
    }

    [Test]
    public void Read_StepThatLeftNoRecordAtAll_StillTakesItsPlaceInTheSchedule()
    {
        // A step type that queues no task and records no Activity is passed straight through. It is
        // still a step of the Schedule, and dropping it would shorten the rail as the run proceeded.
        var progress = Read(totalSteps: 3, currentStepIndex: 2,
            Ran(0, "HR Import", ActivityStatus.Complete),
            Task(2, "AD Sync", WorkerTaskStatus.Processing));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.Steps, Has.Count.EqualTo(3));
            Assert.That(progress.Steps[1].Status, Is.EqualTo(ScheduleExecutionStepStatus.Completed),
                "The Schedule has moved past it, so it is behind us whatever it left behind");
            Assert.That(progress.Steps[1].Name, Is.EqualTo("Step 2"));
        }
    }

    [Test]
    public void Read_ScheduleBetweenSteps_StillSaysWhichStepItHasReached()
    {
        // A Schedule momentarily has nothing running as it moves between steps, but its position is
        // recorded rather than inferred, so it does not stop knowing where it is.
        var progress = Read(totalSteps: 2, currentStepIndex: 1,
            Ran(0, "HR Import", ActivityStatus.Complete),
            Task(1, "AD Sync", WorkerTaskStatus.Queued));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.CurrentStepNumber, Is.EqualTo(2));
            Assert.That(progress.Steps[1].Status, Is.EqualTo(ScheduleExecutionStepStatus.Queued),
                "Knowing which step is next is not the same as claiming it has started");
        }
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.Steps[1].Status, Is.EqualTo(ScheduleExecutionStepStatus.Failed));
            Assert.That(progress.CurrentStepNumber, Is.EqualTo(2));
        }
    }

    [Test]
    public void Read_MoreStepsObservedThanTheExecutionRecorded_DrawsThemAllAnyway()
    {
        // The recorded total is a snapshot and the observations are what is actually there. Trusting
        // the smaller number would hide a task from a view whose job is to account for every one.
        var progress = Read(totalSteps: 2, currentStepIndex: 2,
            Ran(0, "HR Import", ActivityStatus.Complete),
            Ran(1, "HR Sync", ActivityStatus.Complete),
            Task(2, "AD Import", WorkerTaskStatus.Processing));

        Assert.That(progress.TotalSteps, Is.EqualTo(3));
    }

    [Test]
    public void Read_ObservationCarryingAnAlreadyDerivedStatus_UsesItRatherThanDerivingAgain()
    {
        // The Schedule Execution detail read has already worked out each step's status before it asks
        // for the group view, so the group view derives from the row view rather than from the records
        // a second time; the two accounts of one execution cannot then disagree.
        var progress = ScheduleStepReading.Read(1, 0, Running,
        [
            new ScheduleStepObservation
            {
                StepIndex = 0, Name = "HR Import", Status = ScheduleExecutionStepStatus.CompletedWithWarning
            }
        ])!;

        Assert.That(progress.Steps[0].Status, Is.EqualTo(ScheduleExecutionStepStatus.CompletedWithWarning));
    }

    [Test]
    public void Read_NothingToReport_IsNothingRatherThanAnEmptyRail()
    {
        Assert.That(ScheduleStepReading.Read(0, 0, Running, []), Is.Null);
    }

    #endregion
}

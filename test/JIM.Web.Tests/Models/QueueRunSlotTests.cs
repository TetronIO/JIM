// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Models;

/// <summary>
/// The Activity tab's Queued section (#2549) numbers queued work in the order the worker will start it: the worker
/// takes queued tasks by Schedule step and then by age, and starts every task of a parallel Schedule step together.
/// These tests pin that model, since "Next" on screen is a promise about what the worker does.
/// </summary>
[TestFixture]
public class QueueRunSlotTests
{
    private static readonly DateTime BaseTime = new(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc);

    private static WorkerTaskHeader Task(string name, int minutesAfterBase,
        WorkerTaskStatus status = WorkerTaskStatus.Queued,
        Guid? execution = null, int? step = null,
        WorkerTaskExecutionMode mode = WorkerTaskExecutionMode.Sequential) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Type = "Synchronisation",
        Status = status,
        Timestamp = BaseTime.AddMinutes(minutesAfterBase),
        ScheduleExecutionId = execution,
        ScheduleStepIndex = step,
        ExecutionMode = mode
    };

    [Test]
    public void Plan_TasksOutsideSchedules_RunOldestFirstOneAtATime()
    {
        var newer = Task("Newer", 5);
        var older = Task("Older", 1);

        var slots = QueueRunSlot.Plan([newer, older]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots.Select(s => s.Tasks.Single().Name), Is.EqualTo(new[] { "Older", "Newer" }));
            Assert.That(slots.Select(s => s.Position), Is.EqualTo(new[] { 1, 2 }));
        }
    }

    [Test]
    public void Plan_ParallelScheduleStep_IsOneSlotHoldingEveryTaskOfTheStep()
    {
        var execution = Guid.NewGuid();
        var a = Task("A", 0, execution: execution, step: 2, mode: WorkerTaskExecutionMode.Parallel);
        var b = Task("B", 0, execution: execution, step: 2, mode: WorkerTaskExecutionMode.Parallel);

        var slot = QueueRunSlot.Plan([a, b]).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slot.Tasks, Is.EquivalentTo(new[] { a, b }));
            Assert.That(slot.IsParallel, Is.True);
        }
    }

    [Test]
    public void Plan_ScheduleStepsRunBeforeTasksOutsideSchedules_EvenWhenQueuedLater()
    {
        // The worker sorts by Schedule step first and puts tasks without one last, so a Schedule's next step
        // overtakes a manually queued run that has been waiting longer.
        var execution = Guid.NewGuid();
        var manual = Task("Manual", 0);
        var stepThree = Task("Step 3", 10, WorkerTaskStatus.WaitingForPreviousStep, execution, 2);

        var slots = QueueRunSlot.Plan([manual, stepThree]);

        Assert.That(slots.Select(s => s.Tasks.Single().Name), Is.EqualTo(new[] { "Step 3", "Manual" }));
    }

    [Test]
    public void Plan_SequentialStepsOfOneSchedule_RunInStepOrder()
    {
        var execution = Guid.NewGuid();
        var slots = QueueRunSlot.Plan(
        [
            Task("Step 3", 0, WorkerTaskStatus.WaitingForPreviousStep, execution, 2),
            Task("Step 2", 0, WorkerTaskStatus.Queued, execution, 1)
        ]);

        Assert.That(slots.Select(s => s.Tasks.Single().Name), Is.EqualTo(new[] { "Step 2", "Step 3" }));
    }

    [Test]
    public void Plan_ProcessingTasks_AreNotPartOfThePlan()
    {
        var running = Task("Running", 0, WorkerTaskStatus.Processing);
        var cancelling = Task("Cancelling", 0, WorkerTaskStatus.CancellationRequested);
        var queued = Task("Queued", 1);

        var slots = QueueRunSlot.Plan([running, cancelling, queued]);

        Assert.That(slots.SelectMany(s => s.Tasks), Is.EqualTo(new[] { queued }));
    }

    [Test]
    public void Plan_NothingQueued_IsEmpty()
    {
        Assert.That(QueueRunSlot.Plan([]), Is.Empty);
    }
}

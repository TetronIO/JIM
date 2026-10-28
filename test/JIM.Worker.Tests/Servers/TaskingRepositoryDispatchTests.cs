// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Tasking;
using JIM.PostgresData;
using MockQueryable.Moq;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Which queued Worker Tasks the worker starts together. A batch is either one task, or the tasks of one parallel
/// Schedule step; nothing else may join it, because the tasks in a batch run at the same time.
/// </summary>
/// <remarks>
/// The fault pinned here: a task that must run alone, queued behind a parallel Schedule step, was added to that step's
/// batch. A hand-queued Full Import then ran alongside the same Connected System's Full Import from the Schedule. The
/// portal's queue (<c>QueueRunSlot</c>) already describes batches this way, so the worker now does what it shows.
/// </remarks>
[TestFixture]
public class TaskingRepositoryDispatchTests
{
    private static readonly DateTime Start = new(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc);

    private static SynchronisationWorkerTask Task(
        int minutesAfterStart,
        WorkerTaskExecutionMode mode = WorkerTaskExecutionMode.Sequential,
        Guid? scheduleExecutionId = null,
        int? stepIndex = null,
        WorkerTaskStatus status = WorkerTaskStatus.Queued) => new(1, 1)
    {
        Id = Guid.NewGuid(),
        Timestamp = Start.AddMinutes(minutesAfterStart),
        ExecutionMode = mode,
        ScheduleExecutionId = scheduleExecutionId,
        ScheduleStepIndex = stepIndex,
        Status = status,
        InitiatedByType = ActivityInitiatorType.System,
        Activity = new Activity
        {
            Id = Guid.NewGuid(),
            InitiatedByType = ActivityInitiatorType.System,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile
        }
    };

    private static SynchronisationWorkerTask ParallelStepTask(Guid scheduleExecutionId, int stepIndex, int minutesAfterStart) =>
        Task(minutesAfterStart, WorkerTaskExecutionMode.Parallel, scheduleExecutionId, stepIndex);

    private static JimApplication BuildApplication(List<WorkerTask> workerTasks)
    {
        // Moq's proxy runs JimDbContext's parameterless constructor, which builds a connection string from these.
        TestUtilities.SetEnvironmentVariables();

        var mockDbContext = new Mock<JimDbContext>();
        mockDbContext.Setup(db => db.WorkerTasks).Returns(workerTasks.BuildMockDbSet().Object);
        return new JimApplication(new PostgresDataRepository(mockDbContext.Object));
    }

    private static async Task<List<Guid>> DispatchAsync(params WorkerTask[] queue)
    {
        using var jim = BuildApplication([.. queue]);
        return (await jim.Tasking.GetNextWorkerTasksToProcessAsync()).Select(t => t.Id).ToList();
    }

    [Test]
    public async Task GetNextWorkerTasksToProcessAsync_EmptyQueue_DispatchesNothingAsync()
    {
        Assert.That(await DispatchAsync(), Is.Empty);
    }

    [Test]
    public async Task GetNextWorkerTasksToProcessAsync_ParallelStepThenSequentialTask_DispatchesOnlyTheStepAsync()
    {
        // The reported fault: the hand-queued run joined the step and ran alongside it.
        var execution = Guid.NewGuid();
        var first = ParallelStepTask(execution, 0, 0);
        var second = ParallelStepTask(execution, 0, 0);
        var handQueued = Task(1);

        var dispatched = await DispatchAsync(first, second, handQueued);

        Assert.That(dispatched, Is.EquivalentTo(new[] { first.Id, second.Id }));
    }

    [Test]
    public async Task GetNextWorkerTasksToProcessAsync_SequentialTaskFirst_DispatchesItAloneAsync()
    {
        var execution = Guid.NewGuid();
        var sequential = Task(0, scheduleExecutionId: execution, stepIndex: 0);
        var laterParallel = ParallelStepTask(Guid.NewGuid(), 1, 1);

        var dispatched = await DispatchAsync(sequential, laterParallel);

        Assert.That(dispatched, Is.EqualTo(new[] { sequential.Id }));
    }

    [Test]
    public async Task GetNextWorkerTasksToProcessAsync_TwoSchedulesParallelStepsQueued_DispatchesOnlyTheFirstStepAsync()
    {
        // Two Schedules at the same step index sort together; their steps are separate batches all the same.
        var executionA = Guid.NewGuid();
        var executionB = Guid.NewGuid();
        var a1 = ParallelStepTask(executionA, 0, 0);
        var a2 = ParallelStepTask(executionA, 0, 0);
        var b1 = ParallelStepTask(executionB, 0, 1);
        var b2 = ParallelStepTask(executionB, 0, 1);

        var dispatched = await DispatchAsync(a1, a2, b1, b2);

        Assert.That(dispatched, Is.EquivalentTo(new[] { a1.Id, a2.Id }));
    }

    [Test]
    public async Task GetNextWorkerTasksToProcessAsync_StepTasksInterleavedWithAnotherScheduleByAge_DispatchesTheWholeStepAsync()
    {
        // A step's tasks are a batch however their ages interleave with another Schedule's step at the same index.
        var executionA = Guid.NewGuid();
        var executionB = Guid.NewGuid();
        var a1 = ParallelStepTask(executionA, 0, 0);
        var b1 = ParallelStepTask(executionB, 0, 1);
        var a2 = ParallelStepTask(executionA, 0, 2);

        var dispatched = await DispatchAsync(a1, b1, a2);

        Assert.That(dispatched, Is.EquivalentTo(new[] { a1.Id, a2.Id }));
    }

    [Test]
    public async Task GetNextWorkerTasksToProcessAsync_ParallelTaskOutsideASchedule_DispatchesItAloneAsync()
    {
        // Without a Schedule step there is nothing that says which tasks belong together.
        var loose = Task(0, WorkerTaskExecutionMode.Parallel);
        var another = Task(1, WorkerTaskExecutionMode.Parallel);

        var dispatched = await DispatchAsync(loose, another);

        Assert.That(dispatched, Is.EqualTo(new[] { loose.Id }));
    }

    [Test]
    public async Task GetNextWorkerTasksToProcessAsync_StepTaskNotYetQueued_IsLeftBehindAsync()
    {
        var execution = Guid.NewGuid();
        var queued = ParallelStepTask(execution, 0, 0);
        var waiting = Task(0, WorkerTaskExecutionMode.Parallel, execution, 1, WorkerTaskStatus.WaitingForPreviousStep);

        var dispatched = await DispatchAsync(queued, waiting);

        Assert.That(dispatched, Is.EqualTo(new[] { queued.Id }));
    }

    [Test]
    public async Task GetNextWorkerTasksToProcessAsync_DispatchedTasks_AreMarkedProcessingAndTheRestLeftQueuedAsync()
    {
        var execution = Guid.NewGuid();
        var step = ParallelStepTask(execution, 0, 0);
        var handQueued = Task(1);

        await DispatchAsync(step, handQueued);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(step.Status, Is.EqualTo(WorkerTaskStatus.Processing));
            Assert.That(handQueued.Status, Is.EqualTo(WorkerTaskStatus.Queued), "it starts once the step has finished");
        }
    }
}

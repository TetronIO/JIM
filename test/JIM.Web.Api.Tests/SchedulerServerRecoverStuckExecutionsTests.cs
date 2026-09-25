// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Scheduler's safety net over active Schedule Executions (#1768). An execution still starting (Queued)
/// is never advanced or completed by it; only one left Queued for longer than any start takes is treated as abandoned,
/// its waiting steps cancelled and the execution failed. InProgress executions with nothing running are concluded as
/// before.
/// </summary>
[TestFixture]
public class SchedulerServerRecoverStuckExecutionsTests
{
    private const string AbandonedStartMessage =
        "The Schedule did not finish starting, most likely because a JIM service stopped part-way through. No steps ran.";

    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;
    private List<ScheduleExecution> _activeExecutions = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);

        _application = new JimApplication(_mockRepository.Object);
        _mockSchedulingRepository.EmulateConditionalTransitions();

        _activeExecutions = new List<ScheduleExecution>();
        // No step of these executions failed, unless a test says otherwise; a run that reaches its end is Complete.
        _mockActivityRepository.Setup(r => r.GetFailedScheduleExecutionActivitiesAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<Activity>());
        _mockSchedulingRepository.Setup(r => r.GetActiveScheduleExecutionsAsync()).ReturnsAsync(() => _activeExecutions);
        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(It.IsAny<Guid>(), It.IsAny<string>())).ReturnsAsync(0);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task RecoverStuckExecutionsAsync_QueuedExecutionWithNoTasksYet_IsLeftAloneAsync()
    {
        // Caught between being created and its first step being queued: no tasks at all. The safety net used to find
        // exactly this shape (as InProgress) and mark it Complete, reporting a run that never happened as a success.
        var execution = AddExecution(ScheduleExecutionStatus.Queued, queuedAgo: TimeSpan.FromSeconds(2));
        SetUpTasks(execution.Id);

        await _application.Scheduler.RecoverStuckExecutionsAsync();

        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Queued));
        VerifyNothingDoneTo(execution);
    }

    [Test]
    public async Task RecoverStuckExecutionsAsync_QueuedExecutionWithWaitingSteps_IsNeverReleasedAsync()
    {
        // Part-way through starting: some steps queued as waiting. Releasing them here would race the start, which
        // may yet find a step it cannot queue.
        var execution = AddExecution(ScheduleExecutionStatus.Queued, queuedAgo: TimeSpan.FromMinutes(1));
        SetUpTasks(execution.Id, WaitingTask(execution.Id, 0), WaitingTask(execution.Id, 1));

        await _application.Scheduler.RecoverStuckExecutionsAsync();

        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Queued));
        VerifyNothingDoneTo(execution);
    }

    [Test]
    public async Task RecoverStuckExecutionsAsync_QueuedExecutionOlderThanTheThreshold_FailsItAndCancelsItsWaitingStepsAsync()
    {
        var execution = AddExecution(ScheduleExecutionStatus.Queued, queuedAgo: SchedulerServer.StaleStartThreshold + TimeSpan.FromMinutes(1));
        SetUpTasks(execution.Id, WaitingTask(execution.Id, 0));

        await _application.Scheduler.RecoverStuckExecutionsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
            Assert.That(execution.ErrorMessage, Is.EqualTo(AbandonedStartMessage));
            Assert.That(execution.CompletedAt, Is.Not.Null);
        }
        _mockTaskingRepository.Verify(
            r => r.DeleteWaitingTasksForExecutionAsync(execution.Id, ScheduleStepNotRunReasons.ScheduleCouldNotStart), Times.Once);
    }

    [Test]
    public async Task RecoverStuckExecutionsAsync_AbandonedStartWhoseStepsCannotBeRemoved_StaysQueuedForTheNextCycleAsync()
    {
        var execution = AddExecution(ScheduleExecutionStatus.Queued, queuedAgo: SchedulerServer.StaleStartThreshold + TimeSpan.FromMinutes(1));
        SetUpTasks(execution.Id, WaitingTask(execution.Id, 0));
        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(execution.Id, It.IsAny<string>()))
            .ThrowsAsync(new TimeoutException("Simulated database failure"));

        Assert.That(() => _application.Scheduler.RecoverStuckExecutionsAsync(), Throws.Nothing,
            "one execution's failure must not stop the safety net");

        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Queued),
            "failing it now would strand its waiting step; it is retried on the next cycle instead");
    }

    [Test]
    public async Task RecoverStuckExecutionsAsync_OneExecutionFails_OthersAreStillRecoveredAsync()
    {
        var broken = AddExecution(ScheduleExecutionStatus.Queued, queuedAgo: SchedulerServer.StaleStartThreshold + TimeSpan.FromMinutes(1));
        var healthy = AddExecution(ScheduleExecutionStatus.Queued, queuedAgo: SchedulerServer.StaleStartThreshold + TimeSpan.FromMinutes(2));
        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(broken.Id, It.IsAny<string>()))
            .ThrowsAsync(new TimeoutException("Simulated database failure"));

        await _application.Scheduler.RecoverStuckExecutionsAsync();

        Assert.That(healthy.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
    }

    [Test]
    public async Task RecoverStuckExecutionsAsync_InProgressExecutionWithActiveTasks_IsLeftAloneAsync()
    {
        var execution = AddExecution(ScheduleExecutionStatus.InProgress, queuedAgo: TimeSpan.FromHours(1));
        SetUpTasks(execution.Id,
            new SynchronisationWorkerTask { Id = Guid.NewGuid(), ScheduleExecutionId = execution.Id, ScheduleStepIndex = 0, Status = WorkerTaskStatus.Processing },
            WaitingTask(execution.Id, 1));

        await _application.Scheduler.RecoverStuckExecutionsAsync();

        _mockSchedulingRepository.Verify(r => r.GetScheduleExecutionWithScheduleAsync(execution.Id), Times.Never,
            "the Worker is handling it");
    }

    [Test]
    public async Task RecoverStuckExecutionsAsync_InProgressExecutionWithOnlyWaitingSteps_AdvancesItAsync()
    {
        // The Worker finished step 1 but stopped before it could advance the execution.
        var step = new ScheduleStep { Id = Guid.NewGuid(), StepIndex = 0, StepType = ScheduleStepType.RunProfile };
        var execution = AddExecution(ScheduleExecutionStatus.InProgress, queuedAgo: TimeSpan.FromHours(1), step,
            new ScheduleStep { Id = Guid.NewGuid(), StepIndex = 1, StepType = ScheduleStepType.RunProfile });
        SetUpTasks(execution.Id, WaitingTask(execution.Id, 1));
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionStepAsync(execution.Id, 0)).ReturnsAsync(new List<WorkerTask>());
        _mockActivityRepository.Setup(r => r.GetActivitiesByScheduleExecutionStepAsync(execution.Id, 0))
            .ReturnsAsync(new List<Activity> { new() { Status = ActivityStatus.Complete, ScheduleStepIndex = 0, ScheduleStepId = step.Id } });
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(1);

        await _application.Scheduler.RecoverStuckExecutionsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.InProgress));
            Assert.That(execution.CurrentStepIndex, Is.EqualTo(1));
        }
    }

    #region Helpers

    private ScheduleExecution AddExecution(ScheduleExecutionStatus status, TimeSpan queuedAgo, params ScheduleStep[] steps)
    {
        var schedule = new Schedule { Id = Guid.NewGuid(), Name = "Nightly Sync", Steps = steps.ToList() };
        var execution = new ScheduleExecution
        {
            Id = Guid.NewGuid(),
            ScheduleId = schedule.Id,
            Schedule = schedule,
            ScheduleName = schedule.Name,
            Status = status,
            QueuedAt = DateTime.UtcNow - queuedAgo
        };
        _activeExecutions.Add(execution);
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(execution.Id)).ReturnsAsync(execution);
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(execution.Id)).ReturnsAsync(execution);
        SetUpTasks(execution.Id);
        return execution;
    }

    private void SetUpTasks(Guid executionId, params WorkerTask[] tasks)
    {
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(executionId)).ReturnsAsync(tasks.ToList());
    }

    private static WorkerTask WaitingTask(Guid executionId, int stepIndex) => new SynchronisationWorkerTask
    {
        Id = Guid.NewGuid(),
        ScheduleExecutionId = executionId,
        ScheduleStepIndex = stepIndex,
        Status = WorkerTaskStatus.WaitingForPreviousStep
    };

    private void VerifyNothingDoneTo(ScheduleExecution execution)
    {
        _mockTaskingRepository.Verify(r => r.DeleteWaitingTasksForExecutionAsync(execution.Id, It.IsAny<string>()), Times.Never);
        _mockSchedulingRepository.Verify(r => r.TryAdvanceScheduleExecutionAsync(execution, It.IsAny<int>()), Times.Never);
        _mockSchedulingRepository.Verify(r => r.TryStartScheduleExecutionAsync(execution, It.IsAny<int>()), Times.Never);
        _mockSchedulingRepository.Verify(r => r.TryFinishScheduleExecutionAsync(execution,
            It.IsAny<IReadOnlyCollection<ScheduleExecutionStatus>>(), It.IsAny<ScheduleExecutionStatus>(), It.IsAny<string?>()), Times.Never);
        _mockSchedulingRepository.Verify(r => r.UpdateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()), Times.Never);
    }

    #endregion
}

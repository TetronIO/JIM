using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for TryAdvanceScheduleExecutionAsync — the worker-driven step advancement
/// that runs after CompleteWorkerTaskAsync deletes a schedule-linked task.
/// </summary>
[TestFixture]
public class TaskingServerStepAdvancementTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();

        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);

        _application = new JimApplication(_mockRepository.Object);

        // Default: DeleteWorkerTaskAsync succeeds
        _mockTaskingRepository.Setup(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Returns(Task.CompletedTask);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task CompleteWorkerTask_NonScheduleTask_DoesNotCallAdvancementAsync()
    {
        // Arrange: A standalone task (no schedule execution)
        var task = CreateWorkerTask(scheduleExecutionId: null, stepIndex: null);

        // Act
        await _application.Tasking.CompleteWorkerTaskAsync(task);

        // Assert: No schedule advancement queries should be made
        _mockTaskingRepository.Verify(
            r => r.GetWorkerTaskCountByExecutionStepAsync(It.IsAny<Guid>(), It.IsAny<int>()),
            Times.Never);
    }

    [Test]
    public async Task CompleteWorkerTask_NotLastTaskInStep_DoesNotAdvanceAsync()
    {
        // Arrange: Task is part of a schedule, but there are still remaining tasks at this step
        var executionId = Guid.NewGuid();
        var task = CreateWorkerTask(scheduleExecutionId: executionId, stepIndex: 0);

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskCountByExecutionStepAsync(executionId, 0))
            .ReturnsAsync(1); // One task still remaining

        // Act
        await _application.Tasking.CompleteWorkerTaskAsync(task);

        // Assert: No advancement — still tasks remaining
        _mockTaskingRepository.Verify(
            r => r.TransitionStepToQueuedAsync(It.IsAny<Guid>(), It.IsAny<int>()),
            Times.Never);
        _mockSchedulingRepository.Verify(
            r => r.UpdateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()),
            Times.Never);
    }

    [Test]
    public async Task CompleteWorkerTask_LastTaskInStep_AdvancesToNextStepAsync()
    {
        // Arrange: Last task at step 0, next waiting step is 1
        var executionId = Guid.NewGuid();
        var task = CreateWorkerTask(scheduleExecutionId: executionId, stepIndex: 0);
        var execution = new ScheduleExecution { Id = executionId, Status = ScheduleExecutionStatus.InProgress, CurrentStepIndex = 0 };

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskCountByExecutionStepAsync(executionId, 0))
            .ReturnsAsync(0); // No tasks remaining at step 0
        _mockActivityRepository.Setup(r => r.GetActivitiesByScheduleExecutionStepAsync(executionId, 0))
            .ReturnsAsync(new List<Activity>
            {
                new() { Status = ActivityStatus.Complete }
            });
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(executionId))
            .ReturnsAsync(1); // Next step is 1
        _mockTaskingRepository.Setup(r => r.TransitionStepToQueuedAsync(executionId, 1))
            .ReturnsAsync(2); // 2 tasks transitioned
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync(execution);

        // Act
        await _application.Tasking.CompleteWorkerTaskAsync(task);

        // Assert: Transitioned step 1 to Queued and updated execution
        _mockTaskingRepository.Verify(r => r.TransitionStepToQueuedAsync(executionId, 1), Times.Once);
        _mockSchedulingRepository.Verify(
            r => r.UpdateScheduleExecutionAsync(It.Is<ScheduleExecution>(e => e.CurrentStepIndex == 1)),
            Times.Once);
    }

    [Test]
    public async Task CompleteWorkerTask_LastTaskInLastStep_CompletesExecutionAsync()
    {
        // Arrange: Last task at step 2, no more waiting steps
        var executionId = Guid.NewGuid();
        var task = CreateWorkerTask(scheduleExecutionId: executionId, stepIndex: 2);
        var execution = new ScheduleExecution { Id = executionId, Status = ScheduleExecutionStatus.InProgress, CurrentStepIndex = 2 };

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskCountByExecutionStepAsync(executionId, 2))
            .ReturnsAsync(0);
        _mockActivityRepository.Setup(r => r.GetActivitiesByScheduleExecutionStepAsync(executionId, 2))
            .ReturnsAsync(new List<Activity>
            {
                new() { Status = ActivityStatus.Complete }
            });
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(executionId))
            .ReturnsAsync((int?)null); // No more waiting steps
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync(execution);

        // Act
        await _application.Tasking.CompleteWorkerTaskAsync(task);

        // Assert: Execution marked as completed
        _mockSchedulingRepository.Verify(
            r => r.UpdateScheduleExecutionAsync(It.Is<ScheduleExecution>(e =>
                e.Status == ScheduleExecutionStatus.Completed &&
                e.CompletedAt != null)),
            Times.Once);
    }

    [Test]
    public async Task CompleteWorkerTask_StepFailedContinueOnFailureFalse_FailsExecutionAsync()
    {
        // Arrange: Step 0 failed, ContinueOnFailure is false
        var executionId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var task = CreateWorkerTask(scheduleExecutionId: executionId, stepIndex: 0);

        var schedule = new Schedule
        {
            Id = scheduleId,
            Name = "Test Schedule",
            Steps = new List<ScheduleStep>
            {
                new() { StepIndex = 0, Name = "Import", ContinueOnFailure = false }
            }
        };

        var execution = new ScheduleExecution
        {
            Id = executionId,
            ScheduleId = scheduleId,
            Schedule = schedule,
            Status = ScheduleExecutionStatus.InProgress,
            CurrentStepIndex = 0
        };

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskCountByExecutionStepAsync(executionId, 0))
            .ReturnsAsync(0);
        _mockActivityRepository.Setup(r => r.GetActivitiesByScheduleExecutionStepAsync(executionId, 0))
            .ReturnsAsync(new List<Activity>
            {
                new() { Status = ActivityStatus.FailedWithError }
            });
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(executionId))
            .ReturnsAsync(execution);
        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(executionId))
            .ReturnsAsync(3);

        // Act
        await _application.Tasking.CompleteWorkerTaskAsync(task);

        // Assert: Execution failed and waiting tasks cleaned up
        _mockSchedulingRepository.Verify(
            r => r.UpdateScheduleExecutionAsync(It.Is<ScheduleExecution>(e =>
                e.Status == ScheduleExecutionStatus.Failed &&
                e.CompletedAt != null &&
                e.ErrorMessage != null)),
            Times.Once);
        _mockTaskingRepository.Verify(
            r => r.DeleteWaitingTasksForExecutionAsync(executionId),
            Times.Once);
    }

    [Test]
    public async Task CompleteWorkerTask_StepFailedContinueOnFailureTrue_ContinuesToNextStepAsync()
    {
        // Arrange: Step 0 failed, but ContinueOnFailure is true
        var executionId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var task = CreateWorkerTask(scheduleExecutionId: executionId, stepIndex: 0);

        var schedule = new Schedule
        {
            Id = scheduleId,
            Name = "Test Schedule",
            Steps = new List<ScheduleStep>
            {
                new() { StepIndex = 0, Name = "Import", ContinueOnFailure = true }
            }
        };

        var execution = new ScheduleExecution
        {
            Id = executionId,
            ScheduleId = scheduleId,
            Schedule = schedule,
            Status = ScheduleExecutionStatus.InProgress,
            CurrentStepIndex = 0
        };

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskCountByExecutionStepAsync(executionId, 0))
            .ReturnsAsync(0);
        _mockActivityRepository.Setup(r => r.GetActivitiesByScheduleExecutionStepAsync(executionId, 0))
            .ReturnsAsync(new List<Activity>
            {
                new() { Status = ActivityStatus.FailedWithError }
            });
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(executionId))
            .ReturnsAsync(execution);
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(executionId))
            .ReturnsAsync(1);
        _mockTaskingRepository.Setup(r => r.TransitionStepToQueuedAsync(executionId, 1))
            .ReturnsAsync(1);
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync(execution);

        // Act
        await _application.Tasking.CompleteWorkerTaskAsync(task);

        // Assert: Execution continues — step 1 transitioned to Queued
        _mockTaskingRepository.Verify(r => r.TransitionStepToQueuedAsync(executionId, 1), Times.Once);
        _mockSchedulingRepository.Verify(
            r => r.UpdateScheduleExecutionAsync(It.Is<ScheduleExecution>(e =>
                e.Status == ScheduleExecutionStatus.Failed)),
            Times.Never);
    }

    [Test]
    public void CompleteWorkerTask_AdvancementFails_DoesNotThrowAsync()
    {
        // Arrange: The advancement logic throws, but the task completion should not fail
        var executionId = Guid.NewGuid();
        var task = CreateWorkerTask(scheduleExecutionId: executionId, stepIndex: 0);

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskCountByExecutionStepAsync(executionId, 0))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        // Act & Assert: Should not throw
        Assert.DoesNotThrowAsync(() =>
            _application.Tasking.CompleteWorkerTaskAsync(task));
    }

    #region Helper methods

    private static SynchronisationWorkerTask CreateWorkerTask(Guid? scheduleExecutionId, int? stepIndex)
    {
        return new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            ConnectedSystemRunProfileId = 100,
            Status = WorkerTaskStatus.Processing,
            ScheduleExecutionId = scheduleExecutionId,
            ScheduleStepIndex = stepIndex,
            Activity = new Activity
            {
                Id = Guid.NewGuid(),
                Status = ActivityStatus.InProgress
            }
        };
    }

    #endregion
}

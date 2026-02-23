using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests that CancelScheduleExecutionAsync correctly cancels all tasks,
/// cancels their activities, and updates the execution status.
/// </summary>
[TestFixture]
public class SchedulerServerCancelExecutionTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockConnectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _mockServiceSettingsRepository = new Mock<IServiceSettingsRepository>();

        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepository.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepository.Object);

        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task CancelScheduleExecution_InProgressExecution_ReturnsTrueAsync()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = executionId,
            Status = ScheduleExecutionStatus.InProgress
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync(execution);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(executionId))
            .ReturnsAsync(new List<WorkerTask>());

        // Act
        var result = await _application.Scheduler.CancelScheduleExecutionAsync(executionId);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled));
        Assert.That(execution.CompletedAt, Is.Not.Null);
        Assert.That(execution.ErrorMessage, Is.EqualTo("Cancelled by user"));
    }

    [Test]
    public async Task CancelScheduleExecution_QueuedExecution_ReturnsTrueAsync()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = executionId,
            Status = ScheduleExecutionStatus.Queued
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync(execution);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(executionId))
            .ReturnsAsync(new List<WorkerTask>());

        // Act
        var result = await _application.Scheduler.CancelScheduleExecutionAsync(executionId);

        // Assert
        Assert.That(result, Is.True);
        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled));
    }

    [Test]
    public async Task CancelScheduleExecution_CompletedExecution_ReturnsFalseAsync()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = executionId,
            Status = ScheduleExecutionStatus.Completed
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync(execution);

        // Act
        var result = await _application.Scheduler.CancelScheduleExecutionAsync(executionId);

        // Assert
        Assert.That(result, Is.False);
        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Completed));
    }

    [Test]
    public async Task CancelScheduleExecution_NotFound_ReturnsFalseAsync()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync((ScheduleExecution?)null);

        // Act
        var result = await _application.Scheduler.CancelScheduleExecutionAsync(executionId);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CancelScheduleExecution_ProcessingTasks_SignalledForCancellationAsync()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = executionId,
            Status = ScheduleExecutionStatus.InProgress
        };

        var processingTask = new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            ScheduleExecutionId = executionId,
            Activity = new Activity { Id = Guid.NewGuid(), Status = ActivityStatus.InProgress }
        };
        var queuedTask = new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Queued,
            ScheduleExecutionId = executionId
        };
        var waitingTask = new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.WaitingForPreviousStep,
            ScheduleExecutionId = executionId
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync(execution);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(executionId))
            .ReturnsAsync(new List<WorkerTask> { processingTask, queuedTask, waitingTask });

        // Act
        await _application.Scheduler.CancelScheduleExecutionAsync(executionId);

        // Assert: Processing task is signalled for cancellation, NOT deleted immediately
        Assert.That(processingTask.Status, Is.EqualTo(WorkerTaskStatus.CancellationRequested));
        _mockTaskingRepository.Verify(r => r.UpdateWorkerTaskAsync(processingTask), Times.Once);
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(processingTask), Times.Never);

        // Assert: Queued and waiting tasks are deleted immediately
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(queuedTask), Times.Once);
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(waitingTask), Times.Once);
    }

    [Test]
    public async Task CancelScheduleExecution_ProcessingTaskWithActivity_SignalledNotImmediatelyCancelledAsync()
    {
        // Arrange: A processing task should be signalled for cancellation so the worker
        // can gracefully stop it and handle cleanup (including Activity status).
        var executionId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = executionId,
            Status = ScheduleExecutionStatus.InProgress
        };

        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            Status = ActivityStatus.InProgress,
            Executed = DateTime.UtcNow.AddMinutes(-5),
            Created = DateTime.UtcNow.AddMinutes(-6)
        };

        var taskWithActivity = new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            ScheduleExecutionId = executionId,
            Activity = activity
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync(execution);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(executionId))
            .ReturnsAsync(new List<WorkerTask> { taskWithActivity });

        // Act
        await _application.Scheduler.CancelScheduleExecutionAsync(executionId);

        // Assert: Task is signalled for cancellation, not deleted
        Assert.That(taskWithActivity.Status, Is.EqualTo(WorkerTaskStatus.CancellationRequested));
        _mockTaskingRepository.Verify(r => r.UpdateWorkerTaskAsync(taskWithActivity), Times.Once);
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(taskWithActivity), Times.Never);

        // Assert: Activity is NOT cancelled here — the worker will handle it when it
        // detects the CancellationRequested status and triggers the CancellationToken
        Assert.That(activity.Status, Is.EqualTo(ActivityStatus.InProgress));
        _mockActivityRepository.Verify(r => r.UpdateActivityAsync(activity), Times.Never);
    }

    [Test]
    public async Task CancelScheduleExecution_QueuedTaskWithActivity_ActivityCancelledImmediatelyAsync()
    {
        // Arrange: A queued task (not being processed) can be cancelled and cleaned up immediately.
        var executionId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = executionId,
            Status = ScheduleExecutionStatus.InProgress
        };

        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            Status = ActivityStatus.InProgress,
            Executed = DateTime.UtcNow.AddMinutes(-5),
            Created = DateTime.UtcNow.AddMinutes(-6)
        };

        var queuedTaskWithActivity = new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Queued,
            ScheduleExecutionId = executionId,
            Activity = activity
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync(execution);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(executionId))
            .ReturnsAsync(new List<WorkerTask> { queuedTaskWithActivity });

        // Act
        await _application.Scheduler.CancelScheduleExecutionAsync(executionId);

        // Assert: Activity cancelled with timing info
        Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Cancelled));
        Assert.That(activity.ExecutionTime, Is.Not.Null);
        Assert.That(activity.TotalActivityTime, Is.Not.Null);
        _mockActivityRepository.Verify(r => r.UpdateActivityAsync(activity), Times.Once);

        // Assert: Task deleted
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(queuedTaskWithActivity), Times.Once);
    }

    [Test]
    public async Task CancelScheduleExecution_TaskWithoutActivity_DeletedWithoutErrorAsync()
    {
        // Arrange: Queued tasks may not have an Activity yet
        var executionId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = executionId,
            Status = ScheduleExecutionStatus.InProgress
        };

        var taskWithoutActivity = new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Queued,
            ScheduleExecutionId = executionId,
            Activity = null!
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync(execution);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(executionId))
            .ReturnsAsync(new List<WorkerTask> { taskWithoutActivity });

        // Act
        await _application.Scheduler.CancelScheduleExecutionAsync(executionId);

        // Assert: Task deleted, no activity update attempted
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(taskWithoutActivity), Times.Once);
        _mockActivityRepository.Verify(r => r.UpdateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }
}

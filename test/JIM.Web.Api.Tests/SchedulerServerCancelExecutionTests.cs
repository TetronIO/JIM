using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests that CancelScheduleExecutionAsync correctly cancels active tasks,
/// deletes waiting tasks, and updates the execution status.
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
        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(executionId))
            .ReturnsAsync(0);

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
        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(executionId))
            .ReturnsAsync(0);

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
    public async Task CancelScheduleExecution_QueuedTasks_SetToCancellationRequestedAsync()
    {
        // Arrange
        var executionId = Guid.NewGuid();
        var execution = new ScheduleExecution
        {
            Id = executionId,
            Status = ScheduleExecutionStatus.InProgress
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
            .ReturnsAsync(new List<WorkerTask> { queuedTask, waitingTask });
        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(executionId))
            .ReturnsAsync(1);

        // Act
        await _application.Scheduler.CancelScheduleExecutionAsync(executionId);

        // Assert: Queued task set to CancellationRequested
        Assert.That(queuedTask.Status, Is.EqualTo(WorkerTaskStatus.CancellationRequested));

        // Assert: Waiting tasks deleted via repository
        _mockTaskingRepository.Verify(r => r.DeleteWaitingTasksForExecutionAsync(executionId), Times.Once);
    }

    [Test]
    public async Task CancelScheduleExecution_ProcessingTasks_NotCancelledAsync()
    {
        // Arrange: Processing tasks should NOT be cancelled (worker handles them)
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
            ScheduleExecutionId = executionId
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync(execution);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(executionId))
            .ReturnsAsync(new List<WorkerTask> { processingTask });
        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(executionId))
            .ReturnsAsync(0);

        // Act
        await _application.Scheduler.CancelScheduleExecutionAsync(executionId);

        // Assert: Processing task status unchanged (worker manages it)
        Assert.That(processingTask.Status, Is.EqualTo(WorkerTaskStatus.Processing));
    }
}

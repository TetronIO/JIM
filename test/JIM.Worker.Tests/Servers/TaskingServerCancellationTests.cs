using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for RequestWorkerTaskCancellationAsync — the UI-initiated cancellation flow
/// that signals processing tasks for graceful cancellation by the worker, while
/// immediately cancelling tasks that are not actively being processed.
/// </summary>
[TestFixture]
public class TaskingServerCancellationTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);

        _application = new JimApplication(_mockRepository.Object);

        _mockTaskingRepository.Setup(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Returns(Task.CompletedTask);
        _mockTaskingRepository.Setup(r => r.UpdateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Returns(Task.CompletedTask);
        _mockActivityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task RequestCancellation_ProcessingTask_SetsCancellationRequestedStatusAsync()
    {
        // Arrange: A task actively being processed by the worker
        var taskId = Guid.NewGuid();
        var task = new SynchronisationWorkerTask
        {
            Id = taskId,
            Status = WorkerTaskStatus.Processing,
            Activity = new Activity { Id = Guid.NewGuid(), Status = ActivityStatus.InProgress }
        };

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskAsync(taskId))
            .ReturnsAsync(task);

        // Act
        await _application.Tasking.RequestWorkerTaskCancellationAsync(taskId);

        // Assert: Status set to CancellationRequested for worker pickup
        Assert.That(task.Status, Is.EqualTo(WorkerTaskStatus.CancellationRequested));
        _mockTaskingRepository.Verify(r => r.UpdateWorkerTaskAsync(task), Times.Once);

        // Assert: Task NOT deleted — worker will handle cleanup after cancelling
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never);

        // Assert: Activity NOT cancelled yet — worker handles this when it detects cancellation
        Assert.That(task.Activity!.Status, Is.EqualTo(ActivityStatus.InProgress));
    }

    [Test]
    public async Task RequestCancellation_QueuedTask_CancelledImmediatelyAsync()
    {
        // Arrange: A queued task not yet picked up by any worker
        var taskId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            Status = ActivityStatus.InProgress,
            Executed = DateTime.UtcNow.AddMinutes(-1),
            Created = DateTime.UtcNow.AddMinutes(-2)
        };
        var task = new SynchronisationWorkerTask
        {
            Id = taskId,
            Status = WorkerTaskStatus.Queued,
            Activity = activity
        };

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskAsync(taskId))
            .ReturnsAsync(task);

        // Act
        await _application.Tasking.RequestWorkerTaskCancellationAsync(taskId);

        // Assert: Activity cancelled immediately
        Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Cancelled));
        _mockActivityRepository.Verify(r => r.UpdateActivityAsync(activity), Times.Once);

        // Assert: Task deleted immediately
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(task), Times.Once);
    }

    [Test]
    public async Task RequestCancellation_WaitingTask_CancelledImmediatelyAsync()
    {
        // Arrange: A task waiting for a previous schedule step
        var taskId = Guid.NewGuid();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            Status = ActivityStatus.InProgress,
            Executed = DateTime.UtcNow.AddMinutes(-1),
            Created = DateTime.UtcNow.AddMinutes(-2)
        };
        var task = new SynchronisationWorkerTask
        {
            Id = taskId,
            Status = WorkerTaskStatus.WaitingForPreviousStep,
            Activity = activity
        };

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskAsync(taskId))
            .ReturnsAsync(task);

        // Act
        await _application.Tasking.RequestWorkerTaskCancellationAsync(taskId);

        // Assert: Activity cancelled immediately
        Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Cancelled));

        // Assert: Task deleted immediately
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(task), Times.Once);
    }

    [Test]
    public async Task RequestCancellation_NonExistentTask_DoesNotThrowAsync()
    {
        // Arrange: Task does not exist in the database
        var taskId = Guid.NewGuid();
        _mockTaskingRepository.Setup(r => r.GetWorkerTaskAsync(taskId))
            .ReturnsAsync((WorkerTask?)null);

        // Act & Assert: Should not throw
        await _application.Tasking.RequestWorkerTaskCancellationAsync(taskId);

        // Assert: No updates or deletes attempted
        _mockTaskingRepository.Verify(r => r.UpdateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never);
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never);
    }
}

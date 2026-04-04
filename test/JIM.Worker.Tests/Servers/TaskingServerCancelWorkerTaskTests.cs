using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for CancelWorkerTaskAsync — the implementation method that performs the actual
/// cancellation: marks the activity as cancelled and deletes the worker task record.
/// Called by the worker after it has triggered the CancellationToken, or directly for
/// tasks that are not actively being processed.
/// </summary>
[TestFixture]
public class TaskingServerCancelWorkerTaskTests
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
        _mockActivityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task CancelWorkerTask_WithActivity_CancelsActivityAndDeletesTaskAsync()
    {
        // Arrange
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
            Status = WorkerTaskStatus.Processing,
            Activity = activity
        };

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskAsync(taskId))
            .ReturnsAsync(task);

        // Act
        await _application.Tasking.CancelWorkerTaskAsync(taskId);

        // Assert: Activity cancelled
        Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Cancelled));
        _mockActivityRepository.Verify(r => r.UpdateActivityAsync(activity), Times.Once);

        // Assert: Task deleted
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(task), Times.Once);
    }

    [Test]
    public async Task CancelWorkerTask_WithoutActivity_DeletesTaskWithoutErrorAsync()
    {
        // Arrange: Task exists but has no associated activity
        var taskId = Guid.NewGuid();
        var task = new SynchronisationWorkerTask
        {
            Id = taskId,
            Status = WorkerTaskStatus.Processing,
            Activity = null!
        };

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskAsync(taskId))
            .ReturnsAsync(task);

        // Act
        await _application.Tasking.CancelWorkerTaskAsync(taskId);

        // Assert: No activity update attempted
        _mockActivityRepository.Verify(r => r.UpdateActivityAsync(It.IsAny<Activity>()), Times.Never);

        // Assert: Task still deleted
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(task), Times.Once);
    }

    [Test]
    public async Task CancelWorkerTask_NonExistentTask_DoesNotThrowAsync()
    {
        // Arrange: Task does not exist
        var taskId = Guid.NewGuid();
        _mockTaskingRepository.Setup(r => r.GetWorkerTaskAsync(taskId))
            .ReturnsAsync((WorkerTask?)null);

        // Act & Assert: Should not throw
        await _application.Tasking.CancelWorkerTaskAsync(taskId);

        // Assert: No updates or deletes attempted
        _mockActivityRepository.Verify(r => r.UpdateActivityAsync(It.IsAny<Activity>()), Times.Never);
        _mockTaskingRepository.Verify(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never);
    }
}

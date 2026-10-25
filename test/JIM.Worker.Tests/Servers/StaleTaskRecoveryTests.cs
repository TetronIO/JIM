// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Operations;
using JIM.Models.Tasking;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for the crash recovery mechanism that detects and recovers stale worker tasks.
/// Stale tasks are those stuck in Processing status due to a worker crash or restart.
/// </summary>
[TestFixture]
public class StaleTaskRecoveryTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<ISystemRepository> _mockSystemRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockRepository = new Mock<IRepository>();
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _mockSystemRepository = new Mock<ISystemRepository>();
        _mockSystemRepository.Setup(r => r.GetLatestServiceHeartbeatsAsync()).ReturnsAsync([]);
        _mockRepository.Setup(r => r.System).Returns(_mockSystemRepository.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    private static SynchronisationWorkerTask CreateStaleWorkerTask(DateTime? lastHeartbeat = null)
    {
        return new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            LastHeartbeat = lastHeartbeat,
            ConnectedSystemId = 1,
            ConnectedSystemRunProfileId = 1,
            Activity = new Activity
            {
                Id = Guid.NewGuid(),
                TargetName = "Test Run Profile",
                TargetType = ActivityTargetType.ConnectedSystemRunProfile,
                TargetOperationType = ActivityTargetOperationType.Execute,
                Status = ActivityStatus.InProgress,
                Executed = DateTime.UtcNow.AddMinutes(-10)
            }
        };
    }

    [Test]
    public async Task RecoverStaleWorkerTasksAsync_WithStaleTasks_FailsActivitiesAndDeletesTasksAsync()
    {
        // Arrange
        var staleTask1 = CreateStaleWorkerTask(DateTime.UtcNow.AddMinutes(-10));
        var staleTask2 = CreateStaleWorkerTask(null); // No heartbeat (pre-heartbeat task)
        var staleTasks = new List<WorkerTask> { staleTask1, staleTask2 };

        _mockTaskingRepository
            .Setup(r => r.GetStaleProcessingWorkerTasksAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(staleTasks);

        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        _mockTaskingRepository
            .Setup(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Returns(Task.CompletedTask);

        // Act
        var recoveredCount = await _application.Tasking.RecoverStaleWorkerTasksAsync(TimeSpan.FromMinutes(5));

        // Assert
        Assert.That(recoveredCount, Is.EqualTo(2));

        // Verify activities were failed
        _mockActivityRepository.Verify(
            r => r.UpdateActivityAsync(It.Is<Activity>(a =>
                a.Status == ActivityStatus.FailedWithError &&
                a.ErrorMessage != null &&
                a.ErrorMessage.Contains("worker crash or restart"))),
            Times.Exactly(2));

        // Verify worker tasks were deleted
        _mockTaskingRepository.Verify(
            r => r.DeleteWorkerTaskAsync(It.Is<WorkerTask>(t => t.Id == staleTask1.Id)),
            Times.Once);
        _mockTaskingRepository.Verify(
            r => r.DeleteWorkerTaskAsync(It.Is<WorkerTask>(t => t.Id == staleTask2.Id)),
            Times.Once);
    }

    [Test]
    public async Task RecoverStaleWorkerTasksAsync_WithNoStaleTasks_ReturnsZeroAsync()
    {
        // Arrange
        _mockTaskingRepository
            .Setup(r => r.GetStaleProcessingWorkerTasksAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new List<WorkerTask>());

        // Act
        var recoveredCount = await _application.Tasking.RecoverStaleWorkerTasksAsync(TimeSpan.FromMinutes(5));

        // Assert
        Assert.That(recoveredCount, Is.EqualTo(0));

        // Verify no activities were updated and no tasks were deleted
        _mockActivityRepository.Verify(
            r => r.UpdateActivityAsync(It.IsAny<Activity>()),
            Times.Never);
        _mockTaskingRepository.Verify(
            r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()),
            Times.Never);
    }

    [Test]
    public async Task RecoverStaleWorkerTasksAsync_WithAlreadyFailedActivity_StillDeletesTaskAsync()
    {
        // Arrange - activity already failed (e.g., by SafeFailActivityAsync before crash)
        var staleTask = CreateStaleWorkerTask(DateTime.UtcNow.AddMinutes(-10));
        staleTask.Activity.Status = ActivityStatus.FailedWithError;
        staleTask.Activity.ErrorMessage = "Previous error";

        _mockTaskingRepository
            .Setup(r => r.GetStaleProcessingWorkerTasksAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new List<WorkerTask> { staleTask });

        _mockTaskingRepository
            .Setup(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Returns(Task.CompletedTask);

        // Act
        var recoveredCount = await _application.Tasking.RecoverStaleWorkerTasksAsync(TimeSpan.FromMinutes(5));

        // Assert
        Assert.That(recoveredCount, Is.EqualTo(1));

        // Activity should NOT be updated again since it's already failed
        _mockActivityRepository.Verify(
            r => r.UpdateActivityAsync(It.IsAny<Activity>()),
            Times.Never);

        // But the worker task should still be deleted to free the queue
        _mockTaskingRepository.Verify(
            r => r.DeleteWorkerTaskAsync(It.Is<WorkerTask>(t => t.Id == staleTask.Id)),
            Times.Once);
    }

    [Test]
    public async Task RecoverStaleWorkerTasksAsync_WhenActivityUpdateFails_StillDeletesTaskAsync()
    {
        // Arrange
        var staleTask = CreateStaleWorkerTask(DateTime.UtcNow.AddMinutes(-10));

        _mockTaskingRepository
            .Setup(r => r.GetStaleProcessingWorkerTasksAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new List<WorkerTask> { staleTask });

        // Simulate activity update failure (e.g., DB issue)
        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        _mockTaskingRepository
            .Setup(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Returns(Task.CompletedTask);

        // Act
        var recoveredCount = await _application.Tasking.RecoverStaleWorkerTasksAsync(TimeSpan.FromMinutes(5));

        // Assert - should still recover the task even if activity update failed
        Assert.That(recoveredCount, Is.EqualTo(1));

        // Worker task should still be deleted despite activity update failure
        _mockTaskingRepository.Verify(
            r => r.DeleteWorkerTaskAsync(It.Is<WorkerTask>(t => t.Id == staleTask.Id)),
            Times.Once);
    }

    [Test]
    public async Task RecoverStaleWorkerTasksAsync_ErrorMessageContainsCrashRecoveryContextAsync()
    {
        // Arrange
        var staleTask = CreateStaleWorkerTask(DateTime.UtcNow.AddMinutes(-10));

        _mockTaskingRepository
            .Setup(r => r.GetStaleProcessingWorkerTasksAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new List<WorkerTask> { staleTask });

        Activity? capturedActivity = null;
        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => capturedActivity = a)
            .Returns(Task.CompletedTask);

        _mockTaskingRepository
            .Setup(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Returns(Task.CompletedTask);

        // Act
        await _application.Tasking.RecoverStaleWorkerTasksAsync(TimeSpan.FromMinutes(5));

        // Assert
        Assert.That(capturedActivity, Is.Not.Null);
        Assert.That(capturedActivity!.Status, Is.EqualTo(ActivityStatus.FailedWithError));
        Assert.That(capturedActivity.ErrorMessage, Does.Contain("worker crash or restart"));
        Assert.That(capturedActivity.ErrorMessage, Does.Contain("does not indicate a data integrity issue"));
        Assert.That(capturedActivity.ExecutionTime, Is.Not.Null);
        Assert.That(capturedActivity.TotalActivityTime, Is.Not.Null);
    }

    /// <summary>Recovers one stale task with the given Worker heartbeat on record, and returns its failed Activity.</summary>
    private async Task<Activity> RecoverOneWithWorkerHeartbeatAsync(ServiceHeartbeat? workerHeartbeat)
    {
        var staleTask = CreateStaleWorkerTask(DateTime.UtcNow.AddMinutes(-1));
        _mockTaskingRepository.Setup(r => r.GetStaleProcessingWorkerTasksAsync(It.IsAny<TimeSpan>())).ReturnsAsync([staleTask]);
        _mockTaskingRepository.Setup(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>())).Returns(Task.CompletedTask);
        _mockActivityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockSystemRepository.Setup(r => r.GetLatestServiceHeartbeatsAsync()).ReturnsAsync(workerHeartbeat == null ? [] : [workerHeartbeat]);

        await _application.Tasking.RecoverStaleWorkerTasksAsync(TimeSpan.Zero);

        return staleTask.Activity;
    }

    private static ServiceHeartbeat WorkerHeartbeat(long totalBytes, long availableBytes, long processBytes) => new()
    {
        Service = JimService.WorkerSync,
        InstanceId = "jim-worker-1a2b3c4d",
        HostName = "jim-worker",
        Version = "0.15.0",
        StartedAt = DateTime.UtcNow.AddHours(-4),
        LastSeenAt = DateTime.UtcNow.AddMinutes(-1),
        MemoryTotalBytes = totalBytes,
        MemoryAvailableBytes = availableBytes,
        ProcessMemoryBytes = processBytes
    };

    [Test]
    public async Task RecoverStaleWorkerTasksAsync_WorkerLastReportedMemoryLow_SaysItMostLikelyRanOutOfMemoryAsync()
    {
        var activity = await RecoverOneWithWorkerHeartbeatAsync(WorkerHeartbeat(62_000_000_000, 3_000_000_000, 45_000_000_000));

        Assert.That(activity.ErrorMessage, Does.Contain("most likely ran out of memory")
            .And.Contain("59 GB of 62 GB in use, 45 GB of it by the Worker")
            .And.Contain("give JIM more memory").IgnoreCase);
    }

    [Test]
    public async Task RecoverStaleWorkerTasksAsync_WorkerLastReportedMemoryFine_SaysNothingAboutMemoryAsync()
    {
        var activity = await RecoverOneWithWorkerHeartbeatAsync(WorkerHeartbeat(62_000_000_000, 40_000_000_000, 10_000_000_000));

        Assert.That(activity.ErrorMessage, Does.Not.Contain("memory"));
    }

    [Test]
    public async Task RecoverStaleWorkerTasksAsync_NoWorkerHeartbeatOnRecord_SaysNothingAboutMemoryAsync()
    {
        var activity = await RecoverOneWithWorkerHeartbeatAsync(null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(activity.ErrorMessage, Does.Not.Contain("memory"));
        }
    }

    [Test]
    public async Task RecoverStaleWorkerTasksAsync_WithZeroThreshold_RecoverAllProcessingTasksAsync()
    {
        // Arrange - simulates worker startup where ALL processing tasks should be recovered
        var recentTask = CreateStaleWorkerTask(DateTime.UtcNow.AddSeconds(-1)); // Very recent heartbeat
        var oldTask = CreateStaleWorkerTask(DateTime.UtcNow.AddMinutes(-30));
        var noHeartbeatTask = CreateStaleWorkerTask(null);

        _mockTaskingRepository
            .Setup(r => r.GetStaleProcessingWorkerTasksAsync(TimeSpan.Zero))
            .ReturnsAsync(new List<WorkerTask> { recentTask, oldTask, noHeartbeatTask });

        _mockActivityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        _mockTaskingRepository
            .Setup(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Returns(Task.CompletedTask);

        // Act
        var recoveredCount = await _application.Tasking.RecoverStaleWorkerTasksAsync(TimeSpan.Zero);

        // Assert
        Assert.That(recoveredCount, Is.EqualTo(3));

        _mockTaskingRepository.Verify(
            r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()),
            Times.Exactly(3));
    }

    [Test]
    public async Task UpdateWorkerTaskHeartbeatsAsync_CallsRepositoryAsync()
    {
        // Arrange
        var taskIds = new[] { Guid.NewGuid(), Guid.NewGuid() };

        _mockTaskingRepository
            .Setup(r => r.UpdateWorkerTaskHeartbeatsAsync(taskIds))
            .Returns(Task.CompletedTask);

        // Act
        await _application.Tasking.UpdateWorkerTaskHeartbeatsAsync(taskIds);

        // Assert
        _mockTaskingRepository.Verify(
            r => r.UpdateWorkerTaskHeartbeatsAsync(taskIds),
            Times.Once);
    }
}

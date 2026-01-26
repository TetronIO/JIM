using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

[TestFixture]
public class ConnectedSystemDeletionTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private Mock<IMetaverseRepository> _mockMvRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private Mock<ITaskingRepository> _mockTaskingRepo = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _initiatedBy = null!;

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockMvRepo = new Mock<IMetaverseRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockTaskingRepo = new Mock<ITaskingRepository>();

        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(_mockMvRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepo.Object);

        // Setup activity repository to handle activity creation
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);

        // Setup tasking repository
        _mockTaskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Returns(Task.CompletedTask);

        // Default setup for metaverse repository
        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<MetaverseObject>());
        _mockMvRepo.Setup(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(0);

        _jim = new JimApplication(_mockRepository.Object);
        _initiatedBy = TestUtilities.GetInitiatedBy();
    }

    #region GetDeletionPreviewAsync Tests

    [Test]
    public async Task GetDeletionPreviewAsync_WithValidId_ReturnsPreviewAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(500);
        _mockCsRepo.Setup(r => r.GetSyncRuleCountAsync(1)).ReturnsAsync(3);
        _mockCsRepo.Setup(r => r.GetRunProfileCountAsync(1)).ReturnsAsync(2);
        _mockCsRepo.Setup(r => r.GetPartitionCountAsync(1)).ReturnsAsync(1);
        _mockCsRepo.Setup(r => r.GetContainerCountAsync(1)).ReturnsAsync(5);
        _mockCsRepo.Setup(r => r.GetPendingExportsCountAsync(1)).ReturnsAsync(10);
        _mockCsRepo.Setup(r => r.GetActivityCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.GetJoinedMvoCountAsync(1)).ReturnsAsync(450);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);

        // Act
        var result = await _jim.ConnectedSystems.GetDeletionPreviewAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ConnectedSystemId, Is.EqualTo(1));
        Assert.That(result.ConnectedSystemName, Is.EqualTo("Test System"));
        Assert.That(result.ConnectedSystemObjectCount, Is.EqualTo(500));
        Assert.That(result.SyncRuleCount, Is.EqualTo(3));
        Assert.That(result.RunProfileCount, Is.EqualTo(2));
        Assert.That(result.PartitionCount, Is.EqualTo(1));
        Assert.That(result.ContainerCount, Is.EqualTo(5));
        Assert.That(result.PendingExportCount, Is.EqualTo(10));
        Assert.That(result.ActivityCount, Is.EqualTo(100));
        Assert.That(result.JoinedMvoCount, Is.EqualTo(450));
        Assert.That(result.HasRunningSyncOperation, Is.False);
        Assert.That(result.WillRunAsBackgroundJob, Is.False); // 500 < 1000 threshold
    }

    [Test]
    public async Task GetDeletionPreviewAsync_WithNonExistentId_ReturnsNullAsync()
    {
        // Arrange
        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(999)).ReturnsAsync((ConnectedSystem?)null);

        // Act
        var result = await _jim.ConnectedSystems.GetDeletionPreviewAsync(999);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetDeletionPreviewAsync_WithRunningSyncTask_SetsWarningAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Test System" };
        var runningTask = new SynchronisationWorkerTask { Id = Guid.NewGuid() };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.GetSyncRuleCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetRunProfileCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPartitionCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetContainerCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPendingExportsCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetActivityCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetJoinedMvoCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync(runningTask);

        // Act
        var result = await _jim.ConnectedSystems.GetDeletionPreviewAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.HasRunningSyncOperation, Is.True);
        Assert.That(result.Warnings, Has.Some.Contains("synchronisation"));
    }

    [Test]
    public async Task GetDeletionPreviewAsync_WithLargeCsoCount_SetsBackgroundJobFlagAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Large System" };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(5000); // > 1000 threshold
        _mockCsRepo.Setup(r => r.GetSyncRuleCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetRunProfileCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPartitionCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetContainerCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPendingExportsCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetActivityCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetJoinedMvoCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);

        // Act
        var result = await _jim.ConnectedSystems.GetDeletionPreviewAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.WillRunAsBackgroundJob, Is.True);
    }

    [Test]
    public async Task GetDeletionPreviewAsync_WithSyncRules_AddsWarningAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Test System" };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.GetSyncRuleCountAsync(1)).ReturnsAsync(5);
        _mockCsRepo.Setup(r => r.GetRunProfileCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPartitionCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetContainerCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPendingExportsCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetActivityCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetJoinedMvoCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);

        // Act
        var result = await _jim.ConnectedSystems.GetDeletionPreviewAsync(1);

        // Assert
        Assert.That(result!.Warnings, Has.Some.Contains("sync rule"));
    }

    [Test]
    public async Task GetDeletionPreviewAsync_WithJoinedMvos_AddsWarningAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Test System" };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.GetSyncRuleCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetRunProfileCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPartitionCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetContainerCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPendingExportsCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetActivityCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetJoinedMvoCountAsync(1)).ReturnsAsync(50);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);

        // Act
        var result = await _jim.ConnectedSystems.GetDeletionPreviewAsync(1);

        // Assert
        Assert.That(result!.Warnings, Has.Some.Contains("Metaverse Object"));
    }

    [Test]
    public async Task GetDeletionPreviewAsync_WithPendingExports_AddsWarningAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Test System" };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.GetSyncRuleCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetRunProfileCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPartitionCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetContainerCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPendingExportsCountAsync(1)).ReturnsAsync(25);
        _mockCsRepo.Setup(r => r.GetActivityCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetJoinedMvoCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);

        // Act
        var result = await _jim.ConnectedSystems.GetDeletionPreviewAsync(1);

        // Assert
        Assert.That(result!.Warnings, Has.Some.Contains("pending export"));
    }

    [Test]
    public async Task GetDeletionPreviewAsync_WithDeletingStatus_AddsWarningAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Deleting
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.GetSyncRuleCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetRunProfileCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPartitionCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetContainerCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPendingExportsCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetActivityCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetJoinedMvoCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);

        // Act
        var result = await _jim.ConnectedSystems.GetDeletionPreviewAsync(1);

        // Assert
        Assert.That(result!.Warnings, Has.Some.Contains("already being deleted"));
    }

    #endregion

    #region DeleteAsync Tests

    [Test]
    public async Task DeleteAsync_WithNonExistentId_ReturnsFailedResultAsync()
    {
        // Arrange
        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(999)).ReturnsAsync((ConnectedSystem?)null);

        // Act
        var result = await _jim.ConnectedSystems.DeleteAsync(999, _initiatedBy);

        // Assert
        Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.Failed));
        Assert.That(result.ErrorMessage, Does.Contain("not found"));
    }

    [Test]
    public async Task DeleteAsync_WhenAlreadyDeleting_ReturnsFailedResultAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Deleting
        };
        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);

        // Act
        var result = await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.Failed));
        Assert.That(result.ErrorMessage, Does.Contain("already being deleted"));
    }

    [Test]
    public async Task DeleteAsync_SetsStatusToDeletingAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };
        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        _mockCsRepo.Verify(r => r.UpdateConnectedSystemAsync(
            It.Is<ConnectedSystem>(cs => cs.Status == ConnectedSystemStatus.Deleting)), Times.AtLeastOnce);
    }

    [Test]
    public async Task DeleteAsync_WithRunningSyncTask_QueuesDeletionAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };
        var runningTask = new SynchronisationWorkerTask { Id = Guid.NewGuid() };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync(runningTask);

        // Act
        var result = await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.QueuedAfterSync));
        Assert.That(result.WorkerTaskId, Is.Not.Null);
        _mockTaskingRepo.Verify(r => r.CreateWorkerTaskAsync(
            It.Is<DeleteConnectedSystemWorkerTask>(t => t.ConnectedSystemId == 1)), Times.Once);
    }

    [Test]
    public async Task DeleteAsync_WithLargeCsoCount_QueuesDeletionAsBackgroundJobAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Large System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(5000); // > 1000 threshold

        // Act
        var result = await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.QueuedAsBackgroundJob));
        Assert.That(result.WorkerTaskId, Is.Not.Null);
        _mockTaskingRepo.Verify(r => r.CreateWorkerTaskAsync(
            It.Is<DeleteConnectedSystemWorkerTask>(t => t.ConnectedSystemId == 1)), Times.Once);
    }

    [Test]
    public async Task DeleteAsync_WithSmallCsoCount_ExecutesSynchronouslyAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Small System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100); // < 1000 threshold
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.CompletedImmediately));
        Assert.That(result.ActivityId, Is.Not.Null);
        _mockCsRepo.Verify(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>()), Times.Once);
    }

    [Test]
    public async Task DeleteAsync_WithSynchronousDeletion_CreatesActivityAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        _mockActivityRepo.Verify(r => r.CreateActivityAsync(It.Is<Activity>(a =>
            a.TargetType == ActivityTargetType.ConnectedSystem &&
            a.TargetOperationType == ActivityTargetOperationType.Delete &&
            a.TargetName == "Test System")), Times.Once);
    }

    [Test]
    public async Task DeleteAsync_WhenDeletionFails_ResetsStatusAndReturnsFailedAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).ThrowsAsync(new Exception("Database error"));

        // Act
        var result = await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.Failed));
        Assert.That(result.ErrorMessage, Does.Contain("Database error"));

        // Verify status was reset to Active
        _mockCsRepo.Verify(r => r.UpdateConnectedSystemAsync(
            It.Is<ConnectedSystem>(cs => cs.Status == ConnectedSystemStatus.Active)), Times.AtLeastOnce);
    }

    [Test]
    public async Task DeleteAsync_WhenDeletionFails_FailsActivityAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).ThrowsAsync(new Exception("Test error"));

        // Act
        await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        _mockActivityRepo.Verify(r => r.UpdateActivityAsync(It.Is<Activity>(a =>
            a.Status == ActivityStatus.FailedWithError &&
            a.ErrorMessage != null &&
            a.ErrorMessage.Contains("Test error"))), Times.Once);
    }

    [Test]
    public async Task DeleteAsync_WithNestedExceptions_CapturesFullErrorMessageAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };

        var innerException = new InvalidOperationException("Inner error");
        var outerException = new Exception("Outer error", innerException);

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).ThrowsAsync(outerException);

        // Act
        var result = await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        Assert.That(result.ErrorMessage, Does.Contain("Outer error"));
        Assert.That(result.ErrorMessage, Does.Contain("Inner error"));
    }

    [Test]
    public async Task DeleteAsync_AtExactThreshold_ExecutesSynchronouslyAsync()
    {
        // Arrange - exactly 1000 CSOs is at the threshold boundary
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Boundary System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(1000); // exactly at threshold
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert - at threshold should still be synchronous (> 1000 triggers async)
        Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.CompletedImmediately));
    }

    [Test]
    public async Task DeleteAsync_JustAboveThreshold_QueuesAsBackgroundJobAsync()
    {
        // Arrange - 1001 CSOs is just above the threshold
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Large System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(1001); // just above threshold

        // Act
        var result = await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.QueuedAsBackgroundJob));
    }

    [Test]
    public async Task DeleteAsync_WithZeroCsos_ExecutesSynchronouslyAsync()
    {
        // Arrange - empty connected system
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Empty System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        var result = await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.CompletedImmediately));
    }

    #endregion

    #region ExecuteDeletionAsync Tests

    [Test]
    public async Task ExecuteDeletionAsync_CallsRepositoryDeletionAsync()
    {
        // Arrange
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.ExecuteDeletionAsync(1);

        // Assert
        _mockCsRepo.Verify(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>()), Times.Once);
    }

    [Test]
    public async Task ExecuteDeletionAsync_WithEvaluateMvoDeletionRulesTrue_MarksOrphanedMvosAsync()
    {
        // Arrange
        var orphanedMvo1 = new MetaverseObject { Id = Guid.NewGuid() };
        var orphanedMvo2 = new MetaverseObject { Id = Guid.NewGuid() };
        var orphanedMvos = new List<MetaverseObject> { orphanedMvo1, orphanedMvo2 };

        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1))
            .ReturnsAsync(orphanedMvos);
        _mockMvRepo.Setup(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(2);
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.ExecuteDeletionAsync(1, evaluateMvoDeletionRules: true);

        // Assert
        _mockMvRepo.Verify(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1), Times.Once);
        _mockMvRepo.Verify(r => r.MarkMvosAsDisconnectedAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.Count() == 2 &&
                ids.Contains(orphanedMvo1.Id) && ids.Contains(orphanedMvo2.Id))), Times.Once);
        _mockCsRepo.Verify(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>()), Times.Once);
    }

    [Test]
    public async Task ExecuteDeletionAsync_WithEvaluateMvoDeletionRulesFalse_DoesNotMarkOrphanedMvosAsync()
    {
        // Arrange
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.ExecuteDeletionAsync(1, evaluateMvoDeletionRules: false);

        // Assert
        _mockMvRepo.Verify(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(It.IsAny<int>()), Times.Never);
        _mockMvRepo.Verify(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>()), Times.Never);
        _mockCsRepo.Verify(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>()), Times.Once);
    }

    [Test]
    public async Task ExecuteDeletionAsync_WithNoOrphanedMvos_SkipsMarkingAsync()
    {
        // Arrange
        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1))
            .ReturnsAsync(new List<MetaverseObject>());
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.ExecuteDeletionAsync(1, evaluateMvoDeletionRules: true);

        // Assert
        _mockMvRepo.Verify(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1), Times.Once);
        _mockMvRepo.Verify(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>()), Times.Never);
        _mockCsRepo.Verify(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>()), Times.Once);
    }

    [Test]
    public async Task ExecuteDeletionAsync_DefaultsToEvaluateMvoDeletionRulesTrueAsync()
    {
        // Arrange
        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1))
            .ReturnsAsync(new List<MetaverseObject>());
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act - call without the parameter to test default behaviour
        await _jim.ConnectedSystems.ExecuteDeletionAsync(1);

        // Assert - should call orphan detection by default
        _mockMvRepo.Verify(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1), Times.Once);
        _mockCsRepo.Verify(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>()), Times.Once);
    }

    #endregion

    #region MarkOrphanedMvosForDeletionAsync Tests

    [Test]
    public async Task MarkOrphanedMvosForDeletionAsync_WithOrphanedMvos_ReturnsCountAsync()
    {
        // Arrange
        var orphanedMvo1 = new MetaverseObject { Id = Guid.NewGuid() };
        var orphanedMvo2 = new MetaverseObject { Id = Guid.NewGuid() };
        var orphanedMvos = new List<MetaverseObject> { orphanedMvo1, orphanedMvo2 };

        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1))
            .ReturnsAsync(orphanedMvos);
        _mockMvRepo.Setup(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(2);

        // Act
        var result = await _jim.Metaverse.MarkOrphanedMvosForDeletionAsync(1);

        // Assert
        Assert.That(result, Is.EqualTo(2));
    }

    [Test]
    public async Task MarkOrphanedMvosForDeletionAsync_WithNoOrphanedMvos_ReturnsZeroAsync()
    {
        // Arrange
        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1))
            .ReturnsAsync(new List<MetaverseObject>());

        // Act
        var result = await _jim.Metaverse.MarkOrphanedMvosForDeletionAsync(1);

        // Assert
        Assert.That(result, Is.EqualTo(0));
        _mockMvRepo.Verify(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>()), Times.Never);
    }

    #endregion

    #region DeleteAsync Orphan Marking Tests

    [Test]
    public async Task DeleteAsync_WithSmallCsoCount_MarksOrphanedMvosAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };
        var orphanedMvo = new MetaverseObject { Id = Guid.NewGuid() };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);
        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1))
            .ReturnsAsync(new List<MetaverseObject> { orphanedMvo });
        _mockMvRepo.Setup(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(1);

        // Act
        var result = await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.CompletedImmediately));
        _mockMvRepo.Verify(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1), Times.Once);
        _mockMvRepo.Verify(r => r.MarkMvosAsDisconnectedAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.Contains(orphanedMvo.Id))), Times.Once);
    }

    [Test]
    public async Task DeleteAsync_WithLargeCsoCount_TaskIncludesEvaluateMvoDeletionRulesTrueAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Large System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(5000);

        // Act
        await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert - the task should have EvaluateMvoDeletionRules = true
        _mockTaskingRepo.Verify(r => r.CreateWorkerTaskAsync(
            It.Is<DeleteConnectedSystemWorkerTask>(t =>
                t.ConnectedSystemId == 1 &&
                t.EvaluateMvoDeletionRules == true)), Times.Once);
    }

    [Test]
    public async Task DeleteAsync_WithRunningSyncTask_TaskIncludesEvaluateMvoDeletionRulesTrueAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };
        var runningTask = new SynchronisationWorkerTask { Id = Guid.NewGuid() };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync(runningTask);

        // Act
        await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert - the task should have EvaluateMvoDeletionRules = true
        _mockTaskingRepo.Verify(r => r.CreateWorkerTaskAsync(
            It.Is<DeleteConnectedSystemWorkerTask>(t =>
                t.ConnectedSystemId == 1 &&
                t.EvaluateMvoDeletionRules == true)), Times.Once);
    }

    #endregion

    #region ClearConnectedSystemObjectsAsync Tests

    [Test]
    public async Task ClearConnectedSystemObjectsAsync_WithActiveStatus_ClearsObjectsAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(1);

        // Assert
        _mockCsRepo.Verify(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, true), Times.Once);
    }

    [Test]
    public void ClearConnectedSystemObjectsAsync_WithDeletingStatus_ThrowsException()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Deleting
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(1)).ReturnsAsync(connectedSystem);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(1));

        Assert.That(ex!.Message, Does.Contain("being deleted"));
    }

    [Test]
    public void ClearConnectedSystemObjectsAsync_WithNonExistentSystem_ThrowsException()
    {
        // Arrange
        _mockCsRepo.Setup(r => r.GetConnectedSystemAsync(999)).ReturnsAsync((ConnectedSystem?)null);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(999));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    #endregion
}

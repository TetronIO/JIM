// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Sync;
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
        _mockMvRepo.Setup(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(0);

        // Default setup for Connected System name resolution (#119 policy snapshots)
        _mockCsRepo.Setup(r => r.GetConnectedSystemNamesAsync())
            .ReturnsAsync(new Dictionary<int, string>());

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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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
        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(999, It.IsAny<bool>())).ReturnsAsync((ConnectedSystem?)null);

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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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
        Assert.That(result!.Warnings, Has.Some.Contains("Synchronisation Rule"));
    }

    [Test]
    public async Task GetDeletionPreviewAsync_WithJoinedMvos_AddsWarningAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Test System" };

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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
        Assert.That(result!.Warnings, Has.Some.Contains("Pending Export"));
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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
        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(999, It.IsAny<bool>())).ReturnsAsync((ConnectedSystem?)null);

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
        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);

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
        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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
        // Arrange - empty Connected System
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Empty System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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
        await _jim.ConnectedSystems.ExecuteDeletionAsync(1, new Activity());

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
        _mockMvRepo.Setup(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(2);
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.ExecuteDeletionAsync(1, new Activity(), evaluateMvoDeletionRules: true);

        // Assert
        _mockMvRepo.Verify(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1), Times.Once);
        _mockMvRepo.Verify(r => r.MarkMvosAsDisconnectedAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.Count() == 2 &&
                ids.Contains(orphanedMvo1.Id) && ids.Contains(orphanedMvo2.Id)),
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
        _mockCsRepo.Verify(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>()), Times.Once);
    }

    [Test]
    public async Task ExecuteDeletionAsync_WithEvaluateMvoDeletionRulesFalse_DoesNotMarkOrphanedMvosAsync()
    {
        // Arrange
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        await _jim.ConnectedSystems.ExecuteDeletionAsync(1, new Activity(), evaluateMvoDeletionRules: false);

        // Assert
        _mockMvRepo.Verify(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(It.IsAny<int>()), Times.Never);
        _mockMvRepo.Verify(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
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
        await _jim.ConnectedSystems.ExecuteDeletionAsync(1, new Activity(), evaluateMvoDeletionRules: true);

        // Assert
        _mockMvRepo.Verify(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1), Times.Once);
        _mockMvRepo.Verify(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
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
        await _jim.ConnectedSystems.ExecuteDeletionAsync(1, new Activity());

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
        _mockMvRepo.Setup(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
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
        _mockMvRepo.Verify(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    #endregion

    #region Trigger Recording and Policy Snapshot Tests (#119)

    [Test]
    public async Task MarkOrphanedMvosForDeletionAsync_SpecificMode_RecordsTriggeringSystemAndPolicySnapshotAsync()
    {
        // Arrange - Specific mode object type with the deleted system (1) and another source (2) listed;
        // the MVO retains a joined CSO in the other listed source.
        var objectType = CreateAuthoritativeSourceType(10, AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect, new List<int> { 1, 2 }, TimeSpan.FromDays(7));
        var mvo = CreateProjectedMvoWithCsos(objectType, 1, 2);

        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1))
            .ReturnsAsync(new List<MetaverseObject> { mvo });
        _mockCsRepo.Setup(r => r.GetConnectedSystemNamesAsync())
            .ReturnsAsync(new Dictionary<int, string> { { 1, "HR System" }, { 2, "AD System" } });

        var capturedCalls = CaptureMarkMvosAsDisconnectedCalls();

        // Act
        var markedCount = await _jim.Metaverse.MarkOrphanedMvosForDeletionAsync(1);

        // Assert
        Assert.That(markedCount, Is.EqualTo(1));
        Assert.That(capturedCalls, Has.Count.EqualTo(1));

        var call = capturedCalls[0];
        Assert.That(call.MvoIds, Is.EquivalentTo(new[] { mvo.Id }));
        Assert.That(call.TriggeredBySystemId, Is.EqualTo(1));
        Assert.That(call.TriggeredBySystemName, Is.EqualTo("HR System"));

        var snapshot = MvoDeletionPolicySnapshot.FromJson(call.PolicySnapshotJson);
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.DeletionRule, Is.EqualTo(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected));
        Assert.That(snapshot.TriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect));
        Assert.That(snapshot.SelectedSourceSystemIds, Is.EqualTo(new List<int> { 1, 2 }));
        Assert.That(snapshot.SelectedSourceSystemNames, Is.EqualTo(new List<string> { "HR System", "AD System" }));
        Assert.That(snapshot.GracePeriod, Is.EqualTo(TimeSpan.FromDays(7)));
        Assert.That(snapshot.TriggeringSystemId, Is.EqualTo(1));
        Assert.That(snapshot.TriggeringSystemName, Is.EqualTo("HR System"));
        Assert.That(snapshot.RemainingConnectedSourceSystemIds, Is.EqualTo(new List<int> { 2 }));
        Assert.That(snapshot.RemainingConnectedSourceSystemNames, Is.EqualTo(new List<string> { "AD System" }));
    }

    [Test]
    public async Task MarkOrphanedMvosForDeletionAsync_AllMode_SnapshotRecordsNoRemainingSourcesAsync()
    {
        // Arrange - All mode: a marked MVO by definition has no remaining listed source connections
        // (only an unlisted target remains joined).
        var objectType = CreateAuthoritativeSourceType(11, AuthoritativeSourceTriggerMode.AllSourcesDisconnect, new List<int> { 1, 2 }, null);
        var mvo = CreateProjectedMvoWithCsos(objectType, 1, 3);

        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1))
            .ReturnsAsync(new List<MetaverseObject> { mvo });
        _mockCsRepo.Setup(r => r.GetConnectedSystemNamesAsync())
            .ReturnsAsync(new Dictionary<int, string> { { 1, "HR System" }, { 2, "AD System" }, { 3, "Target App" } });

        var capturedCalls = CaptureMarkMvosAsDisconnectedCalls();

        // Act
        var markedCount = await _jim.Metaverse.MarkOrphanedMvosForDeletionAsync(1);

        // Assert
        Assert.That(markedCount, Is.EqualTo(1));
        Assert.That(capturedCalls, Has.Count.EqualTo(1));

        var snapshot = MvoDeletionPolicySnapshot.FromJson(capturedCalls[0].PolicySnapshotJson);
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.TriggerMode, Is.EqualTo(AuthoritativeSourceTriggerMode.AllSourcesDisconnect));
        Assert.That(snapshot.RemainingConnectedSourceSystemIds, Is.Empty);
        Assert.That(snapshot.RemainingConnectedSourceSystemNames, Is.Empty);
    }

    [Test]
    public async Task MarkOrphanedMvosForDeletionAsync_WithMultipleObjectTypes_BuildsOneSnapshotPerObjectTypeAsync()
    {
        // Arrange - two object types are affected by the same system deletion; each group of MVOs gets
        // its own decision-time snapshot because the policy facts differ per object type.
        var authoritativeType = CreateAuthoritativeSourceType(12, AuthoritativeSourceTriggerMode.AllSourcesDisconnect, new List<int> { 1 }, TimeSpan.FromDays(30));
        var lastConnectorType = new MetaverseObjectType
        {
            Id = 13,
            Name = "Robot",
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(1)
        };

        var authoritativeMvo = CreateProjectedMvoWithCsos(authoritativeType, 1);
        var lastConnectorMvo = CreateProjectedMvoWithCsos(lastConnectorType, 1);

        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1))
            .ReturnsAsync(new List<MetaverseObject> { authoritativeMvo, lastConnectorMvo });
        _mockCsRepo.Setup(r => r.GetConnectedSystemNamesAsync())
            .ReturnsAsync(new Dictionary<int, string> { { 1, "HR System" } });

        var capturedCalls = CaptureMarkMvosAsDisconnectedCalls();

        // Act
        var markedCount = await _jim.Metaverse.MarkOrphanedMvosForDeletionAsync(1);

        // Assert - one marking call (and so one snapshot) per object type, all totalling correctly
        Assert.That(markedCount, Is.EqualTo(2));
        Assert.That(capturedCalls, Has.Count.EqualTo(2));

        var authoritativeCall = capturedCalls.Single(c => c.MvoIds.Contains(authoritativeMvo.Id));
        var lastConnectorCall = capturedCalls.Single(c => c.MvoIds.Contains(lastConnectorMvo.Id));

        var authoritativeSnapshot = MvoDeletionPolicySnapshot.FromJson(authoritativeCall.PolicySnapshotJson);
        Assert.That(authoritativeSnapshot, Is.Not.Null);
        Assert.That(authoritativeSnapshot!.DeletionRule, Is.EqualTo(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected));
        Assert.That(authoritativeSnapshot.GracePeriod, Is.EqualTo(TimeSpan.FromDays(30)));

        var lastConnectorSnapshot = MvoDeletionPolicySnapshot.FromJson(lastConnectorCall.PolicySnapshotJson);
        Assert.That(lastConnectorSnapshot, Is.Not.Null);
        Assert.That(lastConnectorSnapshot!.DeletionRule, Is.EqualTo(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected));
        Assert.That(lastConnectorSnapshot.GracePeriod, Is.EqualTo(TimeSpan.FromDays(1)));
    }

    [Test]
    public async Task MarkOrphanedMvosForDeletionAsync_WithUnknownSystemName_UsesFallbackNameAsync()
    {
        // Arrange - name resolution has no entry for the deleted system; a stable fallback keeps the
        // trigger fields populated rather than failing the deletion.
        var objectType = CreateAuthoritativeSourceType(14, AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect, new List<int> { 1 }, null);
        var mvo = CreateProjectedMvoWithCsos(objectType, 1);

        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1))
            .ReturnsAsync(new List<MetaverseObject> { mvo });
        _mockCsRepo.Setup(r => r.GetConnectedSystemNamesAsync())
            .ReturnsAsync(new Dictionary<int, string>());

        var capturedCalls = CaptureMarkMvosAsDisconnectedCalls();

        // Act
        await _jim.Metaverse.MarkOrphanedMvosForDeletionAsync(1);

        // Assert
        Assert.That(capturedCalls, Has.Count.EqualTo(1));
        Assert.That(capturedCalls[0].TriggeredBySystemName, Is.EqualTo("Connected System 1"));
    }

    #endregion

    #region Deletion Preview Mode-Aware Count Tests (#119)

    [Test]
    public async Task GetDeletionPreviewAsync_PopulatesMvosMarkedForDeletionCountFromSharedPredicateAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Test System", Status = ConnectedSystemStatus.Active };
        SetupPreviewCountMocks(connectedSystem);
        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionCountAsync(1)).ReturnsAsync(3);

        // Act
        var result = await _jim.ConnectedSystems.GetDeletionPreviewAsync(1);

        // Assert - the preview count comes from the same mode-aware predicate execution marks with
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.MvosWithDeletionRuleCount, Is.EqualTo(3));
        Assert.That(result.Warnings, Has.Some.Contains("marked for deletion"));
        _mockMvRepo.Verify(r => r.GetMvosOrphanedByConnectedSystemDeletionCountAsync(1), Times.Once);
    }

    [Test]
    public async Task GetDeletionPreviewAsync_WithNoMvosToMark_AddsNoDeletionWarningAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Test System", Status = ConnectedSystemStatus.Active };
        SetupPreviewCountMocks(connectedSystem);
        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionCountAsync(1)).ReturnsAsync(0);

        // Act
        var result = await _jim.ConnectedSystems.GetDeletionPreviewAsync(1);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.MvosWithDeletionRuleCount, Is.EqualTo(0));
        Assert.That(result.Warnings, Has.None.Contains("marked for deletion"));
    }

    [Test]
    public async Task GetDeletionPreviewAsync_CountAgreesWithExecutionMarkingAsync()
    {
        // Arrange - back the preview count and the execution list with the same data set, mirroring the
        // shared repository predicate, and prove the surfaced count equals what execution marks.
        var objectType = CreateAuthoritativeSourceType(15, AuthoritativeSourceTriggerMode.AllSourcesDisconnect, new List<int> { 1 }, TimeSpan.FromDays(30));
        var orphanedMvos = new List<MetaverseObject>
        {
            CreateProjectedMvoWithCsos(objectType, 1),
            CreateProjectedMvoWithCsos(objectType, 1)
        };

        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Test System", Status = ConnectedSystemStatus.Active };
        SetupPreviewCountMocks(connectedSystem);
        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionCountAsync(1)).ReturnsAsync(orphanedMvos.Count);
        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1)).ReturnsAsync(orphanedMvos);
        _mockMvRepo.Setup(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((IEnumerable<Guid> ids, int _, string _, string? _) => ids.Count());

        // Act
        var preview = await _jim.ConnectedSystems.GetDeletionPreviewAsync(1);
        var markedCount = await _jim.Metaverse.MarkOrphanedMvosForDeletionAsync(1);

        // Assert
        Assert.That(preview, Is.Not.Null);
        Assert.That(preview!.MvosWithDeletionRuleCount, Is.EqualTo(markedCount));
    }

    #endregion

    #region #119 Test Helpers

    private sealed record MarkMvosCall(List<Guid> MvoIds, int TriggeredBySystemId, string TriggeredBySystemName, string? PolicySnapshotJson);

    /// <summary>
    /// Replaces the MarkMvosAsDisconnectedAsync setup with one that captures every call's arguments and
    /// returns the number of MVOs passed, so tests can assert on grouping and snapshot content.
    /// </summary>
    private List<MarkMvosCall> CaptureMarkMvosAsDisconnectedCalls()
    {
        var capturedCalls = new List<MarkMvosCall>();
        _mockMvRepo.Setup(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((IEnumerable<Guid> ids, int systemId, string systemName, string? snapshotJson) =>
            {
                var idList = ids.ToList();
                capturedCalls.Add(new MarkMvosCall(idList, systemId, systemName, snapshotJson));
                return idList.Count;
            });
        return capturedCalls;
    }

    private static MetaverseObjectType CreateAuthoritativeSourceType(int id, AuthoritativeSourceTriggerMode triggerMode, List<int> triggerSystemIds, TimeSpan? gracePeriod)
    {
        return new MetaverseObjectType
        {
            Id = id,
            Name = $"Person{id}",
            DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            DeletionTriggerMode = triggerMode,
            DeletionTriggerConnectedSystemIds = triggerSystemIds,
            DeletionGracePeriod = gracePeriod
        };
    }

    private static MetaverseObject CreateProjectedMvoWithCsos(MetaverseObjectType type, params int[] connectedSystemIds)
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Origin = MetaverseObjectOrigin.Projected,
            Type = type,
            ConnectedSystemObjects = new List<ConnectedSystemObject>()
        };

        foreach (var connectedSystemId in connectedSystemIds)
        {
            mvo.ConnectedSystemObjects.Add(new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = connectedSystemId,
                MetaverseObject = mvo,
                MetaverseObjectId = mvo.Id
            });
        }

        return mvo;
    }

    /// <summary>
    /// Sets up the standard Connected System repository count mocks the deletion preview reads.
    /// </summary>
    private void SetupPreviewCountMocks(ConnectedSystem connectedSystem)
    {
        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(connectedSystem.Id, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(connectedSystem.Id)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.GetSyncRuleCountAsync(connectedSystem.Id)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetRunProfileCountAsync(connectedSystem.Id)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPartitionCountAsync(connectedSystem.Id)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetContainerCountAsync(connectedSystem.Id)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetPendingExportsCountAsync(connectedSystem.Id)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetActivityCountAsync(connectedSystem.Id)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetJoinedMvoCountAsync(connectedSystem.Id)).ReturnsAsync(0);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(connectedSystem.Id)).ReturnsAsync((SynchronisationWorkerTask?)null);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.UpdateConnectedSystemAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _mockCsRepo.Setup(r => r.GetRunningSyncTaskAsync(1)).ReturnsAsync((SynchronisationWorkerTask?)null);
        _mockCsRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(1)).ReturnsAsync(100);
        _mockCsRepo.Setup(r => r.DeleteConnectedSystemAsync(1, It.IsAny<bool>())).Returns(Task.CompletedTask);
        _mockMvRepo.Setup(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1))
            .ReturnsAsync(new List<MetaverseObject> { orphanedMvo });
        _mockMvRepo.Setup(r => r.MarkMvosAsDisconnectedAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(1);

        // Act
        var result = await _jim.ConnectedSystems.DeleteAsync(1, _initiatedBy);

        // Assert
        Assert.That(result.Outcome, Is.EqualTo(DeletionOutcome.CompletedImmediately));
        _mockMvRepo.Verify(r => r.GetMvosOrphanedByConnectedSystemDeletionAsync(1), Times.Once);
        _mockMvRepo.Verify(r => r.MarkMvosAsDisconnectedAsync(
            It.Is<IEnumerable<Guid>>(ids => ids.Contains(orphanedMvo.Id)),
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Once);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, It.IsAny<bool>()))
            .ReturnsAsync(new ClearConnectedSystemResult());

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

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(1));

        Assert.That(ex!.Message, Does.Contain("being deleted"));
    }

    [Test]
    public void ClearConnectedSystemObjectsAsync_WithNonExistentSystem_ThrowsException()
    {
        // Arrange
        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(999, It.IsAny<bool>())).ReturnsAsync((ConnectedSystem?)null);

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(999));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task ClearConnectedSystemObjectsAsync_WithDeleteChangeHistoryTrue_ForwardsParameterAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, true))
            .ReturnsAsync(new ClearConnectedSystemResult());

        // Act
        await _jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(1, deleteChangeHistory: true);

        // Assert
        _mockCsRepo.Verify(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, true), Times.Once);
    }

    [Test]
    public async Task ClearConnectedSystemObjectsAsync_WithDeleteChangeHistoryFalse_ForwardsParameterAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, false))
            .ReturnsAsync(new ClearConnectedSystemResult());

        // Act
        await _jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(1, deleteChangeHistory: false);

        // Assert
        _mockCsRepo.Verify(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, false), Times.Once);
    }

    [Test]
    public async Task ClearConnectedSystemObjectsAsync_ReturnsRemovalStatsAsync()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System",
            Status = ConnectedSystemStatus.Active
        };

        var expectedResult = new ClearConnectedSystemResult
        {
            PendingExportsRemoved = 42,
            ConnectedSystemObjectsRemoved = 150
        };

        _mockCsRepo.Setup(r => r.GetConnectedSystemCoreAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _mockCsRepo.Setup(r => r.DeleteAllConnectedSystemObjectsAndDependenciesAsync(1, true))
            .ReturnsAsync(expectedResult);

        // Act
        var result = await _jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(1);

        // Assert
        Assert.That(result.PendingExportsRemoved, Is.EqualTo(42));
        Assert.That(result.ConnectedSystemObjectsRemoved, Is.EqualTo(150));
    }

    #endregion
}

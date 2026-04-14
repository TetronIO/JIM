// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for TaskingServer's partition/container selection validation, invoked from
/// <see cref="JIM.Application.Servers.TaskingServer.CreateWorkerTaskAsync"/> when a
/// <see cref="SynchronisationWorkerTask"/> is submitted.
/// </summary>
[TestFixture]
public class TaskingServerPartitionValidationTests
{
    private const int ConnectedSystemId = 42;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _mockServiceSettingsRepository = new Mock<IServiceSettingsRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepository.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepository.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);

        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task CreateWorkerTaskAsync_WhenConnectedSystemNotFound_ReturnsFailed()
    {
        // Arrange
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, false))
            .ReturnsAsync((ConnectedSystem?)null);

        // Act
        var result = await _application.Tasking.CreateWorkerTaskAsync(BuildSyncTask());

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("Connected System not found").IgnoreCase);
        });
        _mockTaskingRepository.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Never);
    }

    [Test]
    public async Task CreateWorkerTaskAsync_WhenHierarchyNotEnumerated_ReturnsFailedWithDiagnosticMessage()
    {
        // Arrange — connector supports partitions, but none have been enumerated (Partitions == null)
        var connectedSystem = BuildLdapConnectedSystem();
        connectedSystem.Partitions = null;
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, false))
            .ReturnsAsync(connectedSystem);
        _mockServiceSettingsRepository.Setup(r => r.GetSettingAsync(Constants.SettingKeys.PartitionValidationMode))
            .ReturnsAsync((ServiceSetting?)null); // defaults to Error mode

        // Act
        var result = await _application.Tasking.CreateWorkerTaskAsync(BuildSyncTask());

        // Assert — error message must name the system and cite the specific incomplete-configuration reason
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("Resurgam AD"));
            Assert.That(result.ErrorMessage, Does.Contain("hierarchy"));
            Assert.That(result.ErrorMessage, Does.Contain("Partitions & Containers"));
        });
    }

    [Test]
    public async Task CreateWorkerTaskAsync_WhenPartitionsEnumeratedButNoneSelected_ReturnsFailedWithCount()
    {
        // Arrange
        var connectedSystem = BuildLdapConnectedSystem();
        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new() { Name = "DC=resurgam,DC=local", Selected = false },
            new() { Name = "CN=Configuration,DC=resurgam,DC=local", Selected = false }
        };
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, false))
            .ReturnsAsync(connectedSystem);
        _mockServiceSettingsRepository.Setup(r => r.GetSettingAsync(Constants.SettingKeys.PartitionValidationMode))
            .ReturnsAsync((ServiceSetting?)null);

        // Act
        var result = await _application.Tasking.CreateWorkerTaskAsync(BuildSyncTask());

        // Assert — message must include the partition count to aid diagnosis
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("2"));
            Assert.That(result.ErrorMessage, Does.Contain("none").IgnoreCase);
        });
    }

    [Test]
    public async Task CreateWorkerTaskAsync_WhenPartitionSelectedButNoContainerSelected_ReturnsFailedNamingThePartition()
    {
        // Arrange — mirrors the Samba AD scenario where the partition is selected but the nested
        // OU=Users,OU=Corp containers were not selected (root cause suspected behind issue #564).
        var connectedSystem = BuildLdapConnectedSystem();
        var corp = new ConnectedSystemContainer { Name = "OU=Corp", Selected = false };
        var users = new ConnectedSystemContainer { Name = "OU=Users", Selected = false };
        corp.AddChildContainer(users);
        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new()
            {
                Name = "DC=resurgam,DC=local",
                Selected = true,
                Containers = new HashSet<ConnectedSystemContainer> { corp }
            }
        };
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, false))
            .ReturnsAsync(connectedSystem);
        _mockServiceSettingsRepository.Setup(r => r.GetSettingAsync(Constants.SettingKeys.PartitionValidationMode))
            .ReturnsAsync((ServiceSetting?)null);

        // Act
        var result = await _application.Tasking.CreateWorkerTaskAsync(BuildSyncTask());

        // Assert — error must name the selected partition and indicate containers exist but none selected
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("container").IgnoreCase);
            Assert.That(result.ErrorMessage, Does.Contain("DC=resurgam,DC=local"));
            Assert.That(result.ErrorMessage, Does.Contain("2")); // 2 containers in the tree (Corp + Users)
        });
    }

    [Test]
    public async Task CreateWorkerTaskAsync_WhenValidationModeIsWarning_DoesNotBlockButWarningSurfaces()
    {
        // Arrange — partition configuration is incomplete, but validation mode is Warning (permissive)
        var connectedSystem = BuildLdapConnectedSystem();
        connectedSystem.Partitions = null;
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, false))
            .ReturnsAsync(connectedSystem);
        _mockServiceSettingsRepository.Setup(r => r.GetSettingAsync(Constants.SettingKeys.PartitionValidationMode))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.PartitionValidationMode,
                DisplayName = "Partition Validation Mode",
                ValueType = ServiceSettingValueType.Enum,
                Value = PartitionValidationMode.Warning.ToString()
            });
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemRunProfilesAsync(ConnectedSystemId))
            .ReturnsAsync(new List<ConnectedSystemRunProfile>
            {
                new() { Id = 100, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport }
            });

        // Act
        var result = await _application.Tasking.CreateWorkerTaskAsync(BuildSyncTask());

        // Assert — task should be created despite incomplete config, with the diagnostic surfaced as a warning
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.Warnings, Has.Count.GreaterThan(0));
            Assert.That(result.Warnings[0], Does.Contain("hierarchy"));
        });
        _mockTaskingRepository.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Once);
    }

    [Test]
    public async Task CreateWorkerTaskAsync_WhenPartitionSelectionIsComplete_DoesNotBlock()
    {
        // Arrange — fully configured: partition selected, container selected
        var connectedSystem = BuildLdapConnectedSystem();
        var corp = new ConnectedSystemContainer { Name = "OU=Corp", Selected = false };
        var users = new ConnectedSystemContainer { Name = "OU=Users", Selected = true };
        corp.AddChildContainer(users);
        connectedSystem.Partitions = new List<ConnectedSystemPartition>
        {
            new()
            {
                Name = "DC=resurgam,DC=local",
                Selected = true,
                Containers = new HashSet<ConnectedSystemContainer> { corp }
            }
        };
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, false))
            .ReturnsAsync(connectedSystem);
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemRunProfilesAsync(ConnectedSystemId))
            .ReturnsAsync(new List<ConnectedSystemRunProfile>
            {
                new() { Id = 100, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport }
            });

        // Act
        var result = await _application.Tasking.CreateWorkerTaskAsync(BuildSyncTask());

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.Warnings, Is.Empty);
        });
        _mockTaskingRepository.Verify(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()), Times.Once);
        // ServiceSettings should not even be consulted when the configuration is valid
        _mockServiceSettingsRepository.Verify(r => r.GetSettingAsync(It.IsAny<string>()), Times.Never);
    }

    #region Helper methods

    private static SynchronisationWorkerTask BuildSyncTask()
    {
        return new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = ConnectedSystemId,
            ConnectedSystemRunProfileId = 100,
            Status = WorkerTaskStatus.Queued,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedById = Guid.NewGuid(),
            InitiatedByName = "test-user"
        };
    }

    private static ConnectedSystem BuildLdapConnectedSystem()
    {
        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Resurgam AD",
            SettingValues = new List<ConnectedSystemSettingValue>(),
            ConnectorDefinition = new ConnectorDefinition
            {
                Name = "LDAP",
                SupportsPartitions = true,
                SupportsPartitionContainers = true
            }
        };
    }

    #endregion
}

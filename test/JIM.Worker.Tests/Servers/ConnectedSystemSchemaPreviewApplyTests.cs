// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A schema refresh is split into a preview (retrieve from the Connected System and report what changed, touching
/// nothing in JIM) and an apply (persist what the preview retrieved, under an ImportSchema Activity). These tests
/// pin that split: the preview must leave no trace, the apply must persist and audit, and definition changes the
/// merge previously made silently (a data type or plurality restated by the Connector) must be reported.
/// </summary>
[TestFixture]
public class ConnectedSystemSchemaPreviewApplyTests
{
    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private JimApplication _jim = null!;
    private string _csvPath = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repository = new Mock<IRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);

        _activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);

        _jim = new JimApplication(_repository.Object);

        _csvPath = Path.Join(Path.GetTempPath(), $"jim-schema-preview-{Guid.NewGuid():N}.csv");
        File.WriteAllText(_csvPath, "id,displayName\n1,Test User\n");
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        if (File.Exists(_csvPath))
            File.Delete(_csvPath);
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_WithExistingSchema_ReportsRemovalsWithoutPersistingAnythingAsync()
    {
        // The persisted schema knows an attribute the source no longer offers; the preview must report the
        // removal, and must neither persist the merge nor record an Activity: a preview is a read.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "department", Type = AttributeDataType.Text }
                ]
            }
        ];

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.RemovedAttributes["user"], Does.Contain("department"));
            _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>()), Times.Never,
                "A preview must not persist the merged schema.");
            _activityRepository.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never,
                "A preview changes nothing in JIM, so it must not record an Activity.");
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_WhenTheConnectorRestatesADataType_ReportsTheChangeAsync()
    {
        // The merge has always applied a restated data type silently (unless the administrator set the type).
        // The preview must say so: a data type change can invalidate an Attribute Flow mapping.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Reference }
                ]
            }
        ];

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ChangedAttributes, Does.ContainKey("user"));
            var change = result.ChangedAttributes["user"].Single(c => c.AttributeName == "displayName" && c.Aspect == SchemaAttributeChangeAspect.DataType);
            Assert.That(change.OldValue, Is.EqualTo(nameof(AttributeDataType.Reference)));
            Assert.That(change.NewValue, Is.Not.EqualTo(nameof(AttributeDataType.Reference)));
            Assert.That(result.HasChanges, Is.True, "A definition change alone is a change; 'no changes detected' would be false.");
            Assert.That(result.HasRemovalsOrDefinitionChanges, Is.True);
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_WhenTheAdministratorSetTheType_DoesNotReportADataTypeChangeAsync()
    {
        // An administrator-chosen type is never overwritten by a refresh (#1354), so nothing changes and nothing
        // must be reported: ChangedAttributes lists what the merge applied, not what the Connector disagreed with.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Reference, TypeSetByAdministrator = true }
                ]
            }
        ];

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        // Other attributes may legitimately report changes (the File Connector infers "id" as a number); the
        // overridden attribute must not.
        var displayNameChanges = result.ChangedAttributes.TryGetValue("user", out var changes)
            ? changes.Where(c => c.AttributeName == "displayName").ToList()
            : [];
        Assert.That(displayNameChanges, Is.Empty,
            "An administrator-set type is never overwritten by a refresh, so no change is applied and none must be reported.");
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_WhenTheConnectorRestatesPlurality_ReportsTheChangeAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued }
                ]
            }
        ];

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        var change = result.ChangedAttributes["user"].Single(c => c.AttributeName == "displayName" && c.Aspect == SchemaAttributeChangeAspect.Plurality);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.OldValue, Is.EqualTo(nameof(AttributePlurality.MultiValued)));
            Assert.That(change.NewValue, Is.EqualTo(nameof(AttributePlurality.SingleValued)));
        }
    }

    [Test]
    public async Task ApplyConnectedSystemSchemaRefreshAsync_PersistsThePreviewedSchemaUnderAnImportSchemaActivityAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "department", Type = AttributeDataType.Text }
                ]
            }
        ];

        var previewResult = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);
        Activity? createdActivity = null;
        _activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => createdActivity = a)
            .Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.ApplyConnectedSystemSchemaRefreshAsync(connectedSystem, previewResult, NewInitiator());

        using (Assert.EnterMultipleScope())
        {
            _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemSchemaAsync(connectedSystem), Times.Once,
                "Apply is what persists the merged schema.");
            Assert.That(createdActivity, Is.Not.Null, "Apply changes configuration, so it must be audited.");
            Assert.That(createdActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.ImportSchema));
            Assert.That(createdActivity!.Status, Is.EqualTo(ActivityStatus.Complete));
        }
    }

    [Test]
    public async Task ApplyConnectedSystemSchemaRefreshAsync_WithAnApiKey_PersistsAndAuditsIdenticallyAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Text }
                ]
            }
        ];

        var previewResult = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);
        Activity? createdActivity = null;
        _activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => createdActivity = a)
            .Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.ApplyConnectedSystemSchemaRefreshAsync(connectedSystem, previewResult, NewApiKey());

        using (Assert.EnterMultipleScope())
        {
            _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemSchemaAsync(connectedSystem), Times.Once);
            Assert.That(createdActivity, Is.Not.Null);
            Assert.That(createdActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.ImportSchema));
        }
    }

    [Test]
    public async Task ApplyConnectedSystemSchemaRefreshAsync_WhenThePreviewCarriedDiscoveryWarnings_CompletesTheActivityWithThemAsync()
    {
        // The Activity is how discovery warnings reach the REST API and PowerShell; an apply that dropped them
        // would present an import that discovered less than it should have as an unqualified success.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType { Id = 7, Name = "user", Selected = true, Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Text }] }
        ];

        var previewResult = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);
        previewResult.DiscoveryWarnings.Add("The Connector could not discover everything.");
        Activity? createdActivity = null;
        _activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => createdActivity = a)
            .Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.ApplyConnectedSystemSchemaRefreshAsync(connectedSystem, previewResult, NewInitiator());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(createdActivity, Is.Not.Null);
            Assert.That(createdActivity!.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(createdActivity!.WarningMessage, Does.Contain("could not discover everything"));
        }
    }

    private static MetaverseObject NewInitiator() => new()
    {
        Id = Guid.NewGuid(),
        CachedDisplayName = "Test Administrator"
    };

    private static ApiKey NewApiKey() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test API Key"
    };

    private ConnectedSystem CreateFileConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName };
        _jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test File System",
            ConnectorDefinition = connectorDefinition,
            SettingValues = connectorDefinition.Settings.Select(s => new ConnectedSystemSettingValue
            {
                Setting = s,
                StringValue = s.DefaultStringValue
            }).ToList()
        };

        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "File Path").StringValue = _csvPath;
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Object Type").StringValue = "user";
        return connectedSystem;
    }
}

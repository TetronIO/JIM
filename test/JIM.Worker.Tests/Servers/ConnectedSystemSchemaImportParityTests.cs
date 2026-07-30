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
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A schema import must reach the same conclusion whichever surface asked for it. These tests hold the two
/// <c>ImportConnectedSystemSchemaAsync</c> overloads (portal, and REST/PowerShell via an API key) to the same
/// outcome for the same schema, so the only thing the initiator decides is who the Activity is attributed to.
/// </summary>
/// <remarks>
/// The overloads were near-identical copies, and the copies had drifted: only the portal's auto-selected a single
/// newly-discovered object type, so the same import left different configuration behind depending on which surface
/// ran it.
/// </remarks>
[TestFixture]
public class ConnectedSystemSchemaImportParityTests
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

        // One object type with two attributes: the shape that makes the auto-select applicable.
        _csvPath = Path.Combine(Path.GetTempPath(), $"jim-schema-parity-{Guid.NewGuid():N}.csv");
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
    public async Task ImportConnectedSystemSchemaAsync_ForTheSameSchema_BothInitiatorsReachTheSameResultAsync()
    {
        var userInitiated = CreateFileConnectorConnectedSystem();
        var apiKeyInitiated = CreateFileConnectorConnectedSystem();

        var userResult = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(userInitiated, NewInitiator());
        var apiKeyResult = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(apiKeyInitiated, NewApiKey());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(apiKeyResult.Success, Is.EqualTo(userResult.Success));
            Assert.That(apiKeyResult.AddedObjectTypes, Is.EquivalentTo(userResult.AddedObjectTypes));
            Assert.That(apiKeyResult.UpdatedObjectTypes, Is.EquivalentTo(userResult.UpdatedObjectTypes));
            Assert.That(apiKeyResult.RemovedObjectTypes, Is.EquivalentTo(userResult.RemovedObjectTypes));
            Assert.That(apiKeyResult.TotalObjectTypes, Is.EqualTo(userResult.TotalObjectTypes));
            Assert.That(apiKeyResult.TotalAttributes, Is.EqualTo(userResult.TotalAttributes));
            Assert.That(apiKeyResult.AddedAttributes["user"], Is.EquivalentTo(userResult.AddedAttributes["user"]));
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_ForTheSameSchema_BothInitiatorsLeaveTheSameConfigurationAsync()
    {
        var userInitiated = CreateFileConnectorConnectedSystem();
        var apiKeyInitiated = CreateFileConnectorConnectedSystem();

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(userInitiated, NewInitiator());
        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(apiKeyInitiated, NewApiKey());

        var userObjectType = userInitiated.ObjectTypes!.Single();
        var apiKeyObjectType = apiKeyInitiated.ObjectTypes!.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(userObjectType.Selected, Is.True,
                "A single, newly-discovered object type is auto-selected so the admin lands straight on attribute selection.");
            Assert.That(apiKeyObjectType.Selected, Is.EqualTo(userObjectType.Selected),
                "An import through the REST API or PowerShell must leave the same configuration behind as the same import through the portal.");
            Assert.That(apiKeyObjectType.Attributes.Select(a => a.Name), Is.EquivalentTo(userObjectType.Attributes.Select(a => a.Name)));
            Assert.That(apiKeyObjectType.Attributes.Count(a => a.Selected), Is.EqualTo(userObjectType.Attributes.Count(a => a.Selected)));
            Assert.That(apiKeyObjectType.Attributes.Count(a => a.IsExternalId), Is.EqualTo(userObjectType.Attributes.Count(a => a.IsExternalId)));
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_WhenTheObjectTypeAlreadyExisted_DoesNotReselectItAsync()
    {
        // A refresh must never re-select a type the admin previously deselected, on either surface. The guard is
        // "newly added", not "only one", so an object type already known to JIM is left exactly as the admin left it.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType { Id = 7, Name = "user", Selected = false }
        ];

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewApiKey());

        var objectType = connectedSystem.ObjectTypes!.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(objectType.Selected, Is.False);
            Assert.That(objectType.Id, Is.EqualTo(7), "An existing object type keeps its id so Synchronisation Rule mappings survive the refresh.");
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

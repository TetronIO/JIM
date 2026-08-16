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
/// A data type an administrator chose must survive a schema refresh (#1354).
/// </summary>
/// <remarks>
/// <para>
/// The schema merge preserves what an administrator decided (Selected, IsExternalId) and refreshes what the
/// Connector discovered. A data type used to be purely discovered, so it sat on the refreshed side. Now that
/// an administrator can override it, an override left there would be silently undone by the next refresh,
/// which is a routine operation after any change to the source system.
/// </para>
/// <para>
/// Silently is the problem. The mapping validator runs when a mapping is created, not continuously, so a
/// Synchronisation Rule validated against the chosen type would keep running against the reverted one; the
/// Attribute Flow switches on the source type and would then write the value into the wrong column of the
/// Metaverse Object. Refusing the override once an attribute holds values, while letting a refresh change the
/// type of that same attribute, is the same permission granted through a different door.
/// </para>
/// <para>
/// Exercised through the File Connector because it has declared
/// <c>SupportsUserSelectedAttributeTypes</c> since long before the SQL Connector did, so this was reachable
/// well before Oracle numeric inference made it likely.
/// </para>
/// <para>
/// The overridden type is deliberately the one discovery would <b>not</b> choose. The File Connector samples
/// values rather than defaulting to Text, so it reads a column holding "4200" as a Number; an administrator
/// who knows the employee numbers carry leading zeros, or must never be compared numerically, records it as
/// Text. Picking an override discovery agrees with would let these tests pass whether or not the guard
/// exists.
/// </para>
/// </remarks>
[TestFixture]
public class ConnectedSystemSchemaTypeOverrideDurabilityTests
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

        _csvPath = Path.Join(Path.GetTempPath(), $"jim-schema-type-override-{Guid.NewGuid():N}.csv");
        File.WriteAllText(_csvPath, "id,employeeNumber\n1,4200\n");
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        if (File.Exists(_csvPath))
            File.Delete(_csvPath);
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_AnAdministratorChosenType_SurvivesTheRefreshAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes = [ExistingUserObjectType(typeSetByAdministrator: true)];

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewApiKey());

        var employeeNumber = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "employeeNumber");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(employeeNumber.Type, Is.EqualTo(AttributeDataType.Text),
                "Discovery samples the values and reads '4200' as a Number. The administrator knows better, and the refresh must not overrule them.");
            Assert.That(employeeNumber.TypeSetByAdministrator, Is.True,
                "The override outlives the refresh that would otherwise have undone it, so it survives the next one too.");
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_AnInferredType_IsStillRefreshedAsync()
    {
        // The other half of the guard. Without it, a stale inferred type would be pinned forever and a
        // Connector improving its own inference could never reach an existing Connected System.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes = [ExistingUserObjectType(typeSetByAdministrator: false)];

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewApiKey());

        var employeeNumber = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "employeeNumber");
        Assert.That(employeeNumber.Type, Is.EqualTo(AttributeDataType.Number),
            "Nobody chose Text, so the Connector's own reading of the column stands and the refresh applies it.");
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_AnAdministratorChosenType_StillRefreshesEverythingElseAsync()
    {
        // The flag pins the type alone. Writability, plurality and the description are the Connector's to
        // state, and pinning them too would make an override a way to freeze an attribute in the past.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        var objectType = ExistingUserObjectType(typeSetByAdministrator: true);
        objectType.Attributes.Single(a => a.Name == "employeeNumber").Description = "Stale description from an earlier refresh.";
        connectedSystem.ObjectTypes = [objectType];

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewApiKey());

        var employeeNumber = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "employeeNumber");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(employeeNumber.Type, Is.EqualTo(AttributeDataType.Text));
            Assert.That(employeeNumber.Description, Is.Not.EqualTo("Stale description from an earlier refresh."),
                "Everything the Connector does state is still refreshed.");
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_AnAttributeTheAdministratorNeverTouched_KeepsItsSelectionAsync()
    {
        // A regression guard on what the merge already got right, so the new branch cannot quietly cost it.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes = [ExistingUserObjectType(typeSetByAdministrator: false)];

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewApiKey());

        var employeeNumber = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "employeeNumber");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(employeeNumber.Selected, Is.True, "Selection is an administrator's decision and has always survived a refresh.");
            Assert.That(employeeNumber.Id, Is.EqualTo(31), "The attribute keeps its id, so Synchronisation Rule mappings survive.");
        }
    }

    /// <summary>
    /// A Connected System already carrying the two columns the CSV holds, with <c>employeeNumber</c> either
    /// recorded as Text by an administrator, or left at the Number discovery reads from its values.
    /// </summary>
    private static ConnectedSystemObjectType ExistingUserObjectType(bool typeSetByAdministrator) => new()
    {
        Id = 7,
        Name = "user",
        Selected = true,
        Attributes =
        [
            new ConnectedSystemObjectTypeAttribute
            {
                Id = 30,
                Name = "id",
                Type = AttributeDataType.Text,
                Selected = true,
                IsExternalId = true
            },
            new ConnectedSystemObjectTypeAttribute
            {
                Id = 31,
                Name = "employeeNumber",
                Type = typeSetByAdministrator ? AttributeDataType.Text : AttributeDataType.Number,
                TypeSetByAdministrator = typeSetByAdministrator,
                Selected = true
            }
        ]
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

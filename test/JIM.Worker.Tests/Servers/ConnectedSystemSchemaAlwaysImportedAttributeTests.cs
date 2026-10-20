// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// A Connector can declare an attribute it cannot work without (the SCIM 2.0 Client Connector's <c>meta.version</c>,
/// which conditional export reads its entity tag from, #2404). Schema import selects such an attribute and locks the
/// selection, as it does the External ID; a refresh that stops declaring it releases the lock and leaves the selection
/// to the administrator.
/// </summary>
[TestFixture]
public class ConnectedSystemSchemaAlwaysImportedAttributeTests
{
    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private JimApplication _jim = null!;
    private StubSchemaConnector _connector = null!;
    private ConnectedSystem? _persistedConnectedSystem;

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

        _persistedConnectedSystem = null;
        _connectedSystemRepository.Setup(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>()))
            .Callback<ConnectedSystem>(cs => _persistedConnectedSystem = cs)
            .Returns(Task.CompletedTask);

        _jim = new JimApplication(_repository.Object);

        _connector = new StubSchemaConnector();
        _jim.ConnectedSystems.ConnectorFactory = new StubConnectorFactory(_connector);
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_NewObjectType_SelectsAndLocksAnAlwaysImportedAttributeAsync()
    {
        _connector.SetSchema("User", Identifier(), Mail(), MetaVersion(alwaysImported: true));

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(CreateConnectedSystem(), NewInitiator());

        var objectType = SingleObjectType();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(objectType, "meta.version").Selected, Is.True);
            Assert.That(Attribute(objectType, "meta.version").SelectionLocked, Is.True);
            Assert.That(Attribute(objectType, "mail").Selected, Is.False, "Only what the Connector declares is selected for the administrator.");
            Assert.That(Attribute(objectType, "mail").SelectionLocked, Is.False);
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_NewAttributeAlwaysImported_IsSelectedAndReportedOnlyAsAnAdditionAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        var objectType = ExistingUser(metaVersionSelected: false, metaVersionLocked: false);
        objectType.Attributes.RemoveAll(a => a.Name == "meta.version");
        connectedSystem.ObjectTypes = [objectType];
        _connector.SetSchema("User", Identifier(), Mail(), MetaVersion(alwaysImported: true));

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        var metaVersion = Attribute(connectedSystem.ObjectTypes!.Single(), "meta.version");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metaVersion.Selected, Is.True);
            Assert.That(metaVersion.SelectionLocked, Is.True);
            Assert.That(result.AddedAttributes["User"], Is.EqualTo(new[] { "meta.version" }));
            Assert.That(result.AttributesSelectedByConnector, Is.Empty, "An addition is already reported once.");
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_UnselectedAttributeNowAlwaysImported_SelectsLocksAndReportsItAsync()
    {
        // A Connected System discovered before the Connector declared meta.version, with it left unselected.
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes = [ExistingUser(metaVersionSelected: false, metaVersionLocked: false)];
        _connector.SetSchema("User", Identifier(), Mail(), MetaVersion(alwaysImported: true));

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        var metaVersion = Attribute(connectedSystem.ObjectTypes!.Single(), "meta.version");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metaVersion.Selected, Is.True);
            Assert.That(metaVersion.SelectionLocked, Is.True);
            Assert.That(metaVersion.Id, Is.EqualTo(22), "An existing attribute keeps its row.");
            Assert.That(result.AttributesSelectedByConnector["User"], Is.EqualTo(new[] { "meta.version" }));
            Assert.That(result.HasChanges, Is.True, "Otherwise the refresh is reported as up to date and the selection never applied.");
            Assert.That(result.HasRemovalsOrDefinitionChanges, Is.False, "Selecting an attribute cannot break anything that already works.");
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_AlwaysImportedAttributeAlreadySelectedAndLocked_ReportsNothingAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes = [ExistingUser(metaVersionSelected: true, metaVersionLocked: true)];
        _connector.SetSchema("User", Identifier(), Mail(), MetaVersion(alwaysImported: true));

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AttributesSelectedByConnector, Is.Empty);
            Assert.That(result.HasChanges, Is.False);
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_ConnectorNoLongerDeclaresTheAttribute_ReleasesTheLockAndKeepsTheSelectionAsync()
    {
        // A provider that stops supporting entity tags: JIM no longer needs meta.version, so the administrator decides.
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes = [ExistingUser(metaVersionSelected: true, metaVersionLocked: true)];
        _connector.SetSchema("User", Identifier(), Mail(), MetaVersion(alwaysImported: false));

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewInitiator());

        var objectType = SingleObjectType();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(objectType, "meta.version").SelectionLocked, Is.False);
            Assert.That(Attribute(objectType, "meta.version").Selected, Is.True, "A refresh leaves the administrator's selection alone.");
            Assert.That(Attribute(objectType, "id").SelectionLocked, Is.True, "The External ID stays locked.");
            Assert.That(Attribute(objectType, "id").Selected, Is.True);
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_AlwaysImportedCredentialAttribute_IsStillBlockedAsync()
    {
        // The credential rule is the stronger one: no Connector declaration brings a password into JIM.
        _connector.SetSchema("User", Identifier(), new ConnectorSchemaAttribute("password", AttributeDataType.Text, AttributePlurality.SingleValued)
        {
            AlwaysImported = true
        });

        var result = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(CreateConnectedSystem(), NewInitiator());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SingleObjectType().Attributes.Select(a => a.Name), Does.Not.Contain("password"));
            Assert.That(result.BlockedCredentialAttributes["User"], Is.EqualTo(new[] { "password" }));
            Assert.That(result.AttributesSelectedByConnector, Is.Empty);
        }
    }

    private static ConnectorSchemaAttribute Identifier() =>
        new("id", AttributeDataType.Text, AttributePlurality.SingleValued, writability: AttributeWritability.ReadOnly);

    private static ConnectorSchemaAttribute Mail() =>
        new("mail", AttributeDataType.Text, AttributePlurality.SingleValued);

    private static ConnectorSchemaAttribute MetaVersion(bool alwaysImported) =>
        new("meta.version", AttributeDataType.Text, AttributePlurality.SingleValued, writability: AttributeWritability.ReadOnly)
        {
            AlwaysImported = alwaysImported
        };

    private static ConnectedSystemObjectType ExistingUser(bool metaVersionSelected, bool metaVersionLocked)
    {
        var objectType = new ConnectedSystemObjectType { Id = 10, Name = "User", Selected = true };
        objectType.Attributes =
        [
            new ConnectedSystemObjectTypeAttribute
            {
                Id = 20, Name = "id", Type = AttributeDataType.Text, Writability = AttributeWritability.ReadOnly,
                Selected = true, IsExternalId = true, SelectionLocked = true, ConnectedSystemObjectType = objectType
            },
            new ConnectedSystemObjectTypeAttribute
            {
                Id = 21, Name = "mail", Type = AttributeDataType.Text, Selected = true, ConnectedSystemObjectType = objectType
            },
            new ConnectedSystemObjectTypeAttribute
            {
                Id = 22, Name = "meta.version", Type = AttributeDataType.Text, Writability = AttributeWritability.ReadOnly,
                Selected = metaVersionSelected, SelectionLocked = metaVersionLocked, ConnectedSystemObjectType = objectType
            }
        ];
        return objectType;
    }

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType objectType, string name) =>
        objectType.Attributes.Single(a => a.Name == name);

    private ConnectedSystemObjectType SingleObjectType()
    {
        Assert.That(_persistedConnectedSystem, Is.Not.Null, "The schema import must have persisted the Connected System.");
        Assert.That(_persistedConnectedSystem!.ObjectTypes, Is.Not.Null);
        return _persistedConnectedSystem!.ObjectTypes!.Single();
    }

    private static MetaverseObject NewInitiator() => new()
    {
        Id = Guid.NewGuid(),
        CachedDisplayName = "Test Administrator"
    };

    private static ConnectedSystem CreateConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Stub Schema Connector" };
        var setting = new ConnectorDefinitionSetting { Name = "Dummy Setting", Type = ConnectedSystemSettingType.Text };
        connectorDefinition.Settings.Add(setting);

        return new ConnectedSystem
        {
            Id = 1,
            Name = "Test Stub System",
            ConnectorDefinition = connectorDefinition,
            SettingValues =
            [
                new ConnectedSystemSettingValue { Setting = setting, StringValue = "value" }
            ]
        };
    }

    /// <summary>
    /// A schema-only connector reporting one Object Type with the attributes the test supplies; the first is the
    /// recommended External ID.
    /// </summary>
    private sealed class StubSchemaConnector : IConnector, IConnectorSchema
    {
        public string Name => "Stub Schema Connector";

        public string? Description => null;

        public string? Url => null;

        private ConnectorSchema _schema = new();

        public void SetSchema(string objectTypeName, params ConnectorSchemaAttribute[] attributes)
        {
            var objectType = new ConnectorSchemaObjectType(objectTypeName) { RecommendedExternalIdAttribute = attributes[0] };
            objectType.Attributes.AddRange(attributes);
            _schema = new ConnectorSchema();
            _schema.ObjectTypes.Add(objectType);
        }

        public Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settings, ILogger logger) => Task.FromResult(_schema);
    }

    /// <summary>
    /// Resolves every connector name to the supplied stub, so the server under test exercises its own schema
    /// handling rather than a real connector's.
    /// </summary>
    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }
}

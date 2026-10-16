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
/// A Connector can report an attribute it discovers as operational (an LDAP directory's operational attributes, #2153
/// D8), and schema import persists the flag so the schema screen can hold the attribute back. The flag is
/// connector-owned: a refresh restates it. These tests also hold a refresh of an existing Connected System to
/// reporting newly offered attributes as unselected additions, without disturbing anything already selected.
/// </summary>
[TestFixture]
public class ConnectedSystemSchemaOperationalAttributeTests
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
    public async Task ImportConnectedSystemSchemaAsync_NewObjectType_PersistsTheOperationalFlagAsync()
    {
        _connector.SetSchema("inetOrgPerson", Identifier(), Mail(), PwdFailureTime());

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(CreateConnectedSystem(), NewInitiator());

        var objectType = SingleObjectType();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Attribute(objectType, "pwdFailureTime").IsOperational, Is.True);
            Assert.That(Attribute(objectType, "mail").IsOperational, Is.False);
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_OperationalAttributesNowOffered_ReportsThemAsUnselectedAdditionsAsync()
    {
        // A Connected System discovered before operational attributes were offered: two attributes selected.
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes = [ExistingInetOrgPerson()];
        _connector.SetSchema("inetOrgPerson", Identifier(), Mail(), PwdFailureTime(), CreateTimestamp());

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        var objectType = connectedSystem.ObjectTypes!.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.AddedAttributes["inetOrgPerson"], Is.EquivalentTo(new[] { "pwdFailureTime", "createTimestamp" }));
            Assert.That(result.HasRemovalsOrDefinitionChanges, Is.False, "Additions cannot break anything that already works.");
            Assert.That(Attribute(objectType, "pwdFailureTime").Selected, Is.False);
            Assert.That(Attribute(objectType, "pwdFailureTime").Writability, Is.EqualTo(AttributeWritability.ReadOnly));
            Assert.That(Attribute(objectType, "pwdFailureTime").IsOperational, Is.True);
            Assert.That(Attribute(objectType, "createTimestamp").Selected, Is.False);
            Assert.That(Attribute(objectType, "mail").Selected, Is.True, "A refresh must leave the administrator's selection alone.");
            Assert.That(Attribute(objectType, "mail").Id, Is.EqualTo(21), "An existing attribute keeps its row.");
            Assert.That(Attribute(objectType, "entryUUID").Selected, Is.True);
            Assert.That(Attribute(objectType, "entryUUID").IsExternalId, Is.True);
            Assert.That(objectType.Selected, Is.True);
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_SelectedOperationalAttribute_StaysSelectedAndKeepsItsTagAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        var objectType = ExistingInetOrgPerson();
        objectType.Attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Id = 22, Name = "pwdFailureTime", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.MultiValued,
            Writability = AttributeWritability.ReadOnly, Selected = true, IsOperational = true, ConnectedSystemObjectType = objectType
        });
        connectedSystem.ObjectTypes = [objectType];
        _connector.SetSchema("inetOrgPerson", Identifier(), Mail(), PwdFailureTime());

        var result = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewInitiator());

        var pwdFailureTime = Attribute(SingleObjectType(), "pwdFailureTime");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pwdFailureTime.Selected, Is.True);
            Assert.That(pwdFailureTime.Id, Is.EqualTo(22));
            Assert.That(pwdFailureTime.IsOperational, Is.True);
            Assert.That(result.AddedAttributes, Does.Not.ContainKey("inetOrgPerson"));
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_ConnectorStopsReportingAnAttributeOperational_ClearsTheStaleFlagAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        var objectType = ExistingInetOrgPerson();
        Attribute(objectType, "mail").IsOperational = true;
        connectedSystem.ObjectTypes = [objectType];
        _connector.SetSchema("inetOrgPerson", Identifier(), Mail());

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewInitiator());

        Assert.That(Attribute(SingleObjectType(), "mail").IsOperational, Is.False,
            "The flag is connector-owned, so a refresh restates it rather than keeping one nothing stands behind.");
    }

    private static ConnectorSchemaAttribute Identifier() =>
        new("entryUUID", AttributeDataType.Text, AttributePlurality.SingleValued, writability: AttributeWritability.ReadOnly);

    private static ConnectorSchemaAttribute Mail() =>
        new("mail", AttributeDataType.Text, AttributePlurality.MultiValued, className: "inetOrgPerson");

    private static ConnectorSchemaAttribute PwdFailureTime() =>
        new("pwdFailureTime", AttributeDataType.DateTime, AttributePlurality.MultiValued, writability: AttributeWritability.ReadOnly)
        {
            IsOperational = true
        };

    private static ConnectorSchemaAttribute CreateTimestamp() =>
        new("createTimestamp", AttributeDataType.DateTime, AttributePlurality.SingleValued, writability: AttributeWritability.ReadOnly)
        {
            IsOperational = true
        };

    private static ConnectedSystemObjectType ExistingInetOrgPerson()
    {
        var objectType = new ConnectedSystemObjectType { Id = 10, Name = "inetOrgPerson", Selected = true };
        objectType.Attributes =
        [
            new ConnectedSystemObjectTypeAttribute
            {
                Id = 20, Name = "entryUUID", Type = AttributeDataType.Text, Writability = AttributeWritability.ReadOnly,
                Selected = true, IsExternalId = true, SelectionLocked = true, ConnectedSystemObjectType = objectType
            },
            new ConnectedSystemObjectTypeAttribute
            {
                Id = 21, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued,
                ClassName = "inetOrgPerson", Selected = true, ConnectedSystemObjectType = objectType
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

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
/// Object Type classification (structural, auxiliary, internal, and whatever else a connector reports) is
/// discovered by the connector and persisted against the Connected System Object Type, so the schema screen can
/// group and filter what it shows. These tests hold schema import to the ownership rule that makes a refresh
/// safe: tags are connector-owned, so each refresh REPLACES a type's tags rather than accumulating them, and a
/// connector that reports nothing leaves the type unclassified rather than stale.
/// </summary>
[TestFixture]
public class ConnectedSystemSchemaObjectTypeTagTests
{
    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private JimApplication _jim = null!;
    private StubSchemaConnector _connector = null!;
    private ConnectedSystem? _persistedConnectedSystem;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repository = new Mock<IRepository>();
        var activityRepository = new Mock<IActivityRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _repository.Setup(r => r.Activity).Returns(activityRepository.Object);
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);

        activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

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
    public async Task ImportConnectedSystemSchemaAsync_WhenConnectorClassifiesANewObjectType_PersistsTheClassificationAsync()
    {
        _connector.SetSchema(("inetOrgPerson", [new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural)]));

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(CreateConnectedSystem(), NewInitiator());

        var objectType = SingleObjectType();
        Assert.That(objectType.Tags.Select(t => $"{t.Key}={t.Value}"),
            Is.EquivalentTo(new[] { $"{ObjectTypeTags.Keys.ClassKind}={ObjectTypeTags.Values.ClassKindStructural}" }),
            "A newly discovered object type must carry the classification the connector reported for it.");
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_WhenAnObjectTypesClassificationChanges_ReplacesItRatherThanAccumulatingAsync()
    {
        // The same object type already exists in JIM, classified structural by an earlier import; the directory's
        // schema has since been changed so that it is now auxiliary. Accumulating would leave the type claiming to
        // be both, which no consumer could resolve.
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 10,
                Name = "posixAccount",
                Tags = [new ConnectedSystemObjectTypeTag { Id = 5, Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural }]
            }
        ];
        _connector.SetSchema(("posixAccount", [new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindAuxiliary)]));

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewInitiator());

        var objectType = SingleObjectType();
        Assert.That(objectType.Tags.Select(t => $"{t.Key}={t.Value}"),
            Is.EquivalentTo(new[] { $"{ObjectTypeTags.Keys.ClassKind}={ObjectTypeTags.Values.ClassKindAuxiliary}" }),
            "A refresh must replace a type's connector-owned tags, so a reclassification at the Connected System is reflected rather than accumulated.");
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_WhenTheConnectorStopsClassifyingAnObjectType_ClearsTheStaleClassificationAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 11,
                Name = "groupOfNames",
                Tags = [new ConnectedSystemObjectTypeTag { Id = 6, Key = ObjectTypeTags.Keys.ClassKind, Value = ObjectTypeTags.Values.ClassKindStructural }]
            }
        ];
        _connector.SetSchema(("groupOfNames", []));

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewInitiator());

        Assert.That(SingleObjectType().Tags, Is.Empty,
            "A connector that no longer classifies a type must leave it unclassified, not carrying a classification nothing stands behind.");
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_WhenTheConnectorReportsTheSameTagTwice_PersistsItOnceAsync()
    {
        // The persisted tags are uniquely indexed per (object type, key, value); a connector repeating itself must
        // not turn into a constraint violation at save time.
        _connector.SetSchema(("account",
        [
            new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural),
            new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural)
        ]));

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(CreateConnectedSystem(), NewInitiator());

        Assert.That(SingleObjectType().Tags, Has.Count.EqualTo(1),
            "Duplicate tags from a connector must be collapsed, because the persisted tags are uniquely indexed per object type, key and value.");
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_WhenTheConnectorClassifiesNothing_LeavesObjectTypesUnclassifiedAsync()
    {
        // Every connector that does not implement classification must keep working exactly as before.
        _connector.SetSchema(("User", []));

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(CreateConnectedSystem(), NewInitiator());

        Assert.That(SingleObjectType().Tags, Is.Empty,
            "A connector reporting no classification must leave the object type unclassified, which consumers treat as 'show it, do not group it'.");
    }

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
    /// A schema-only connector whose discovered object types and their classification the test controls.
    /// </summary>
    private sealed class StubSchemaConnector : IConnector, IConnectorSchema
    {
        public string Name => "Stub Schema Connector";

        public string? Description => null;

        public string? Url => null;

        private ConnectorSchema _schema = new();

        public void SetSchema(params (string Name, List<ConnectorSchemaObjectTypeTag> Tags)[] objectTypes)
        {
            _schema = new ConnectorSchema();
            foreach (var (name, tags) in objectTypes)
            {
                var identifier = new ConnectorSchemaAttribute($"{name}Id", AttributeDataType.Text, AttributePlurality.SingleValued);
                var objectType = new ConnectorSchemaObjectType(name) { RecommendedExternalIdAttribute = identifier, Tags = tags };
                objectType.Attributes.Add(identifier);
                _schema.ObjectTypes.Add(objectType);
            }
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

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
using JIM.Models.Staging.DTOs;
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

    // ─── Reporting classification changes on selected Object Types (#2049) ───
    // A selected Object Type the Connected System reclassifies can take entries from, or hand entries to, another
    // selected Object Type at the next import. The refresh must say so before it is applied, rather than replacing
    // the tags silently and leaving the administrator to discover it as Object Type Changed errors.

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_WhenASelectedTypeChangesClassKind_ReportsItAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 10,
                Name = "posixAccount",
                Selected = true,
                Tags = [ClassKindTag(1, ObjectTypeTags.Values.ClassKindStructural)]
            }
        ];
        _connector.SetSchema(("posixAccount", [new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindAuxiliary)]));

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        Assert.That(result.ChangedObjectTypeClassifications, Does.ContainKey("posixAccount"),
            "A selected Object Type the Connected System reclassifies must be reported before the refresh is applied.");
        var change = result.ChangedObjectTypeClassifications["posixAccount"].Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Aspect, Is.EqualTo(SchemaObjectTypeClassificationAspect.ClassKind));
            Assert.That(change.OldValues, Is.EqualTo(new[] { ObjectTypeTags.Values.ClassKindStructural }));
            Assert.That(change.NewValues, Is.EqualTo(new[] { ObjectTypeTags.Values.ClassKindAuxiliary }));
            Assert.That(result.HasChanges, Is.True);
            Assert.That(result.HasRemovalsOrDefinitionChanges, Is.False,
                "Cancelling a classification change keeps things as they are, so it must not drive the destructive decision.");
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_WhenInheritanceNowNamesAnotherSelectedType_ReportsItAsync()
    {
        // inetOrgPerson now inherits from person, and both are selected: an entry carrying both classes resolved to
        // whichever the directory listed first, and now resolves to inetOrgPerson, the more specific of the two.
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType { Id = 10, Name = "inetOrgPerson", Selected = true, Tags = [SuperiorClassTag(1, "top")] },
            new ConnectedSystemObjectType { Id = 11, Name = "person", Selected = true, Tags = [SuperiorClassTag(2, "top")] }
        ];
        _connector.SetSchema(
            ("inetOrgPerson", [SchemaSuperiorClassTag("top"), SchemaSuperiorClassTag("person")]),
            ("person", [SchemaSuperiorClassTag("top")]));

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        Assert.That(result.ChangedObjectTypeClassifications.Keys, Is.EquivalentTo(new[] { "inetOrgPerson" }),
            "Only the Object Type whose inheritance changed is reported; person's is as it was.");
        var change = result.ChangedObjectTypeClassifications["inetOrgPerson"].Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Aspect, Is.EqualTo(SchemaObjectTypeClassificationAspect.Inheritance));
            Assert.That(change.OldValues, Is.EqualTo(new[] { "top" }));
            Assert.That(change.NewValues, Is.EqualTo(new[] { "person", "top" }));
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_WhenInheritanceNoLongerNamesAnotherSelectedType_ReportsItAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType { Id = 10, Name = "inetOrgPerson", Selected = true, Tags = [SuperiorClassTag(1, "top"), SuperiorClassTag(2, "person")] },
            new ConnectedSystemObjectType { Id = 11, Name = "person", Selected = true }
        ];
        _connector.SetSchema(
            ("inetOrgPerson", [SchemaSuperiorClassTag("top")]),
            ("person", []));

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        Assert.That(result.ChangedObjectTypeClassifications, Does.ContainKey("inetOrgPerson"),
            "Losing a selected superior changes resolution as much as gaining one.");
        var change = result.ChangedObjectTypeClassifications["inetOrgPerson"].Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.Aspect, Is.EqualTo(SchemaObjectTypeClassificationAspect.Inheritance));
            Assert.That(change.OldValues, Is.EqualTo(new[] { "person", "top" }));
            Assert.That(change.NewValues, Is.EqualTo(new[] { "top" }));
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_WhenInheritanceNamesOnlyUnselectedClasses_DoesNotReportAsync()
    {
        // The first refresh after JIM began recording inheritance: every type gains its whole chain at once. Only a
        // class that is itself a selected Object Type can move entries, so flagging the rest would be noise on every
        // type of every directory.
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType { Id = 10, Name = "inetOrgPerson", Selected = true },
            new ConnectedSystemObjectType { Id = 11, Name = "person", Selected = false }
        ];
        _connector.SetSchema(
            ("inetOrgPerson", [SchemaSuperiorClassTag("top"), SchemaSuperiorClassTag("person"), SchemaSuperiorClassTag("organizationalPerson")]),
            ("person", [SchemaSuperiorClassTag("top")]));

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ChangedObjectTypeClassifications, Is.Empty);
            Assert.That(result.HasObjectTypeClassificationChanges, Is.False);
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_ForAnUnselectedOrNewType_DoesNotReportAsync()
    {
        // JIM imports no entries of an unselected Object Type, and a new one has none yet, so neither can move
        // anything; selecting one is the Object Type selection preview's business, not the refresh's.
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 10,
                Name = "posixAccount",
                Selected = false,
                Tags = [ClassKindTag(1, ObjectTypeTags.Values.ClassKindStructural)]
            }
        ];
        _connector.SetSchema(
            ("posixAccount", [new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindAuxiliary)]),
            ("device", [new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural)]));

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        Assert.That(result.ChangedObjectTypeClassifications, Is.Empty);
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_WhenAClassKindChangeDoesNotFlipAuxiliary_RecordsItWithoutWarningAsync()
    {
        // Only whether a class is auxiliary decides resolution, so a type gaining its class kind for the first time,
        // or moving between structural and abstract, can move nothing. It is still recorded, so the refresh can be
        // applied rather than discarded as up to date.
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes = [new ConnectedSystemObjectType { Id = 10, Name = "inetOrgPerson", Selected = true }];
        _connector.SetSchema(("inetOrgPerson", [new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural)]));

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasObjectTypeClassificationChanges, Is.False, "Nothing can change which Object Type an entry resolves to");
            Assert.That(result.ObjectTypesWithClassificationUpdates, Is.EqualTo(new[] { "inetOrgPerson" }));
            Assert.That(result.HasChanges, Is.True, "A refresh carrying only this is offered for applying");
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_WhenInheritanceNamesOnlyUnselectedClasses_RecordsItWithoutWarningAsync()
    {
        // Inheritance the selection preview will need once the other class is selected: discarding it as "no
        // changes" would leave that preview unable to warn.
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType { Id = 10, Name = "user", Selected = true },
            new ConnectedSystemObjectType { Id = 11, Name = "computer", Selected = false }
        ];
        _connector.SetSchema(
            ("user", [SchemaSuperiorClassTag("top")]),
            ("computer", [SchemaSuperiorClassTag("user"), SchemaSuperiorClassTag("top")]));

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasObjectTypeClassificationChanges, Is.False);
            Assert.That(result.ObjectTypesWithClassificationUpdates, Is.EquivalentTo(new[] { "user", "computer" }));
            Assert.That(result.HasChanges, Is.True);
            Assert.That(result.HasRemovalsOrDefinitionChanges, Is.False);
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_WhenAClassKindChangeIsReported_DoesNotAlsoListItAsAnUpdateAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType { Id = 10, Name = "posixAccount", Selected = true, Tags = [ClassKindTag(1, ObjectTypeTags.Values.ClassKindStructural)] }
        ];
        _connector.SetSchema(("posixAccount", [new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindAuxiliary)]));

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasObjectTypeClassificationChanges, Is.True);
            Assert.That(result.ObjectTypesWithClassificationUpdates, Is.Empty);
        }
    }

    [Test]
    public async Task PreviewConnectedSystemSchemaRefreshAsync_WhenASelectedTypesClassificationIsUnchanged_DoesNotReportAsync()
    {
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 10,
                Name = "inetOrgPerson",
                Selected = true,
                Tags = [ClassKindTag(1, ObjectTypeTags.Values.ClassKindStructural), SuperiorClassTag(2, "person")]
            },
            new ConnectedSystemObjectType { Id = 11, Name = "person", Selected = true }
        ];
        _connector.SetSchema(
            ("inetOrgPerson", [new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural), SchemaSuperiorClassTag("person")]),
            ("person", []));

        var result = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);

        Assert.That(result.ChangedObjectTypeClassifications, Is.Empty);
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_WithClassificationChanges_CompletesTheActivityWithAWarningAsync()
    {
        // The one-step import (REST import-schema, PowerShell without -Preview) returns the Connected System rather
        // than the refresh result, so the Activity is the only place the change reaches those surfaces.
        Activity? completedActivity = null;
        _activityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => completedActivity = a)
            .Returns(Task.CompletedTask);

        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 10,
                Name = "posixAccount",
                Selected = true,
                Tags = [ClassKindTag(1, ObjectTypeTags.Values.ClassKindStructural)]
            }
        ];
        _connector.SetSchema(("posixAccount", [new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindAuxiliary)]));

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewInitiator());

        Assert.That(completedActivity, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(completedActivity!.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning),
                "An import that reclassified a selected Object Type must not present as an unqualified success.");
            Assert.That(completedActivity!.WarningMessage, Does.Contain("posixAccount"),
                "The warning must name the Object Type whose classification changed.");
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_WithoutClassificationChanges_CompletesTheActivityWithoutAWarningAsync()
    {
        Activity? completedActivity = null;
        _activityRepository
            .Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => completedActivity = a)
            .Returns(Task.CompletedTask);

        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 10,
                Name = "posixAccount",
                Selected = true,
                Tags = [ClassKindTag(1, ObjectTypeTags.Values.ClassKindStructural)]
            }
        ];
        _connector.SetSchema(("posixAccount", [new ConnectorSchemaObjectTypeTag(ObjectTypeTags.Keys.ClassKind, ObjectTypeTags.Values.ClassKindStructural)]));

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewInitiator());

        Assert.That(completedActivity, Is.Not.Null);
        Assert.That(completedActivity!.Status, Is.EqualTo(ActivityStatus.Complete));
    }

    private static ConnectedSystemObjectTypeTag ClassKindTag(int id, string value) =>
        new() { Id = id, Key = ObjectTypeTags.Keys.ClassKind, Value = value };

    private static ConnectedSystemObjectTypeTag SuperiorClassTag(int id, string className) =>
        new() { Id = id, Key = ObjectTypeTags.Keys.SuperiorClass, Value = className };

    private static ConnectorSchemaObjectTypeTag SchemaSuperiorClassTag(string className) =>
        new(ObjectTypeTags.Keys.SuperiorClass, className);

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

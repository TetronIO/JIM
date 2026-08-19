// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using ILogger = Serilog.ILogger;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The Object Type a Reference attribute declares as its target must survive the schema merge (#1285).
/// </summary>
/// <remarks>
/// <para>
/// The SQL Connector's schema document names the Object Type every Reference attribute points at, and the
/// import engine needs that name to resolve references within the right Object Type when two types share an
/// anchor value space (a view over a table being the canonical case). Before #1285 the declaration died inside
/// the connector: <see cref="ConnectorSchemaAttribute"/> had no field for it, so
/// <see cref="ConnectedSystemObjectTypeAttribute"/> never stored it and resolution guessed.
/// </para>
/// <para>
/// The target is connector-stated, so it sits on the refreshed side of the merge (like writability): a refresh
/// restates it, a withdrawal clears it. Wiring uses navigation properties rather than ids because a declared
/// target may be an Object Type created in the same merge, which has no id until it is saved.
/// </para>
/// </remarks>
[TestFixture]
public class ConnectedSystemSchemaReferencedObjectTypeTests
{
    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private JimApplication _jim = null!;
    private StubSchemaConnector _connector = null!;

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

        _connector = new StubSchemaConnector();
        _jim = new JimApplication(_repository.Object, connectorFactory: new StubConnectorFactory(_connector));
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_ADeclaredReferenceTarget_IsWiredOnANewObjectTypeAsync()
    {
        // Person is declared before Department, so the target does not exist yet when Person's attributes are
        // merged. Only a second pass over the completed graph can wire it; this is the forward-reference case
        // the SQL Connector explicitly supports in its own document validation.
        _connector.Schema = SchemaWithDeclaredTarget(referencesObjectTypeName: "Department");
        var connectedSystem = CreateConnectedSystem();

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewApiKey());

        var person = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "Person");
        var department = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "Department");
        var reference = person.Attributes.Single(a => a.Name == "DEPARTMENT_ID");
        Assert.That(reference.ReferencedObjectType, Is.SameAs(department),
            "The declared target must be wired as a navigation to the merged Object Type instance, so EF can assign the foreign key when both are saved together.");
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_ADeclaredReferenceTarget_IsRestatedOntoAnExistingAttributeAsync()
    {
        // A system whose schema predates target declarations: the attribute exists with no target. The refresh
        // restates what the Connector discovered, and the target is connector-stated, so it lands.
        _connector.Schema = SchemaWithDeclaredTarget(referencesObjectTypeName: "Department");
        var connectedSystem = CreateConnectedSystem();
        connectedSystem.ObjectTypes = ExistingObjectTypes();

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewApiKey());

        var person = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "Person");
        var department = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "Department");
        var reference = person.Attributes.Single(a => a.Name == "DEPARTMENT_ID");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reference.Id, Is.EqualTo(31), "The attribute keeps its id, so Synchronisation Rule mappings survive the refresh.");
            Assert.That(reference.ReferencedObjectType, Is.SameAs(department));
            Assert.That(reference.ReferencedObjectTypeId, Is.EqualTo(9),
                "The target already has an id, so the foreign key can be assigned now rather than waiting for EF fix-up.");
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_AWithdrawnReferenceTarget_IsClearedAsync()
    {
        // The other side of "connector-stated": when the Connector stops declaring a target, the refresh must
        // not leave a stale one behind, for the same reason a stale data type may not survive.
        _connector.Schema = SchemaWithDeclaredTarget(referencesObjectTypeName: null);
        var connectedSystem = CreateConnectedSystem();
        var existing = ExistingObjectTypes();
        var existingReference = existing.Single(ot => ot.Name == "Person").Attributes.Single(a => a.Name == "DEPARTMENT_ID");
        existingReference.ReferencedObjectTypeId = 9;
        existingReference.ReferencedObjectType = existing.Single(ot => ot.Name == "Department");
        connectedSystem.ObjectTypes = existing;

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewApiKey());

        var reference = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "Person").Attributes.Single(a => a.Name == "DEPARTMENT_ID");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reference.ReferencedObjectType, Is.Null);
            Assert.That(reference.ReferencedObjectTypeId, Is.Null);
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_ADeclaredTargetTheSchemaDoesNotContain_IsLeftUnsetWithAWarningAsync()
    {
        // The SQL Connector refuses unknown names in its own document validation, so reaching the merge with
        // one means a connector defect. The merge stays defensive: no dangling wiring, and the shortfall is
        // surfaced as a discovery warning rather than swallowed.
        _connector.Schema = SchemaWithDeclaredTarget(referencesObjectTypeName: "Directorate");
        var connectedSystem = CreateConnectedSystem();

        var result = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewApiKey());

        var reference = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "Person").Attributes.Single(a => a.Name == "DEPARTMENT_ID");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reference.ReferencedObjectType, Is.Null);
            Assert.That(reference.ReferencedObjectTypeId, Is.Null);
            Assert.That(result.DiscoveryWarnings, Has.Some.Contains("Directorate"),
                "A declared target the schema does not contain is a connector defect and must be reported, never swallowed.");
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_ADeclaredReferenceTarget_MatchesObjectTypeNamesCaseInsensitivelyAsync()
    {
        // The SQL Connector matches object type names case-insensitively throughout its document handling;
        // the merge must reach the same conclusion about the same document.
        _connector.Schema = SchemaWithDeclaredTarget(referencesObjectTypeName: "dEpArTmEnT");
        var connectedSystem = CreateConnectedSystem();

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewApiKey());

        var person = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "Person");
        var department = connectedSystem.ObjectTypes!.Single(ot => ot.Name == "Department");
        Assert.That(person.Attributes.Single(a => a.Name == "DEPARTMENT_ID").ReferencedObjectType, Is.SameAs(department));
    }

    #region test data

    /// <summary>
    /// A two-type schema in which Person's DEPARTMENT_ID is a Reference attribute, optionally declaring its
    /// target. Person is deliberately declared first so a declared target is always a forward reference.
    /// </summary>
    private static ConnectorSchema SchemaWithDeclaredTarget(string? referencesObjectTypeName)
    {
        var employeeId = new ConnectorSchemaAttribute("EMPLOYEE_ID", AttributeDataType.Number, AttributePlurality.SingleValued);
        var departmentReference = new ConnectorSchemaAttribute("DEPARTMENT_ID", AttributeDataType.Reference, AttributePlurality.SingleValued)
        {
            ReferencesObjectTypeName = referencesObjectTypeName
        };
        var person = new ConnectorSchemaObjectType("Person")
        {
            Attributes = [employeeId, departmentReference],
            RecommendedExternalIdAttribute = employeeId
        };

        var departmentId = new ConnectorSchemaAttribute("DEPT_ID", AttributeDataType.Number, AttributePlurality.SingleValued);
        var department = new ConnectorSchemaObjectType("Department")
        {
            Attributes = [departmentId],
            RecommendedExternalIdAttribute = departmentId
        };

        return new ConnectorSchema { ObjectTypes = [person, department] };
    }

    /// <summary>
    /// The same two Object Types as an existing, persisted schema (ids assigned), with no target recorded on
    /// the Reference attribute unless a test sets one.
    /// </summary>
    private static List<ConnectedSystemObjectType> ExistingObjectTypes() =>
    [
        new ConnectedSystemObjectType
        {
            Id = 7,
            Name = "Person",
            Selected = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 30, Name = "EMPLOYEE_ID", Type = AttributeDataType.Number, Selected = true, IsExternalId = true },
                new ConnectedSystemObjectTypeAttribute { Id = 31, Name = "DEPARTMENT_ID", Type = AttributeDataType.Reference, Selected = true }
            ]
        },
        new ConnectedSystemObjectType
        {
            Id = 9,
            Name = "Department",
            Selected = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 40, Name = "DEPT_ID", Type = AttributeDataType.Number, Selected = true, IsExternalId = true }
            ]
        }
    ];

    private static ConnectedSystem CreateConnectedSystem()
    {
        var setting = new ConnectorDefinitionSetting { Id = 1, Name = "Stub Setting" };
        return new ConnectedSystem
        {
            Id = 1,
            Name = "Test Reference Target System",
            ConnectorDefinition = new ConnectorDefinition { Name = "Stub Schema Connector", Settings = [setting] },
            SettingValues = [new ConnectedSystemSettingValue { Setting = setting, StringValue = "stub" }]
        };
    }

    private static ApiKey NewApiKey() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test API Key"
    };

    private sealed class StubSchemaConnector : IConnector, IConnectorSchema
    {
        public string Name => "Stub Schema Connector";
        public string? Description => null;
        public string? Url => null;

        public ConnectorSchema Schema { get; set; } = new();

        public Task<ConnectorSchema> GetSchemaAsync(List<ConnectedSystemSettingValue> settings, ILogger logger) => Task.FromResult(Schema);
    }

    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }

    #endregion
}

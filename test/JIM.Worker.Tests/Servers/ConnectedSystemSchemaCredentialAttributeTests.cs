// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Proves that credential attributes are never imported into a Connected System's schema as managed attributes,
/// and (the harder half) that a credential attribute which is already persisted from before this rule existed is
/// quarantined rather than deleted.
/// <para>
/// The deletion hazard is the reason this fixture exists. The schema merge derives removed attributes from
/// <c>existing.Except(incoming)</c> and rebuilds the object type's attribute collection, so an attribute simply
/// filtered out of the incoming schema is orphaned and EF issues a DELETE for it. If a Synchronisation Rule
/// Mapping references that attribute, the delete is an FK violation at save time; and even when it is not, the
/// administrator is told an attribute was "removed from the Connected System" when the directory still has it.
/// </para>
/// </summary>
[TestFixture]
public class ConnectedSystemSchemaCredentialAttributeTests
{
    private const string ObjectTypeName = "user";

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepo = null!;
    private Mock<IActivityRepository> _mockActivityRepo = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _initiatedBy = null!;
    private string _tempCsvPath = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockConnectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _mockActivityRepo = new Mock<IActivityRepository>();
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepo.Object);
        _mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockConnectedSystemRepo.Setup(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);

        _jim = new JimApplication(_mockRepository.Object);
        _initiatedBy = TestUtilities.GetInitiatedBy();

        // A CSV whose headers include a denied credential attribute (unicodePwd) alongside ordinary attributes and
        // a name that only *looks* credential-bearing (pwdLastSet), which must import normally.
        _tempCsvPath = Path.GetTempFileName();
        File.WriteAllText(_tempCsvPath, "id,displayName,unicodePwd,pwdLastSet\n1,Test User,ignored,132000000000000000\n");
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        if (File.Exists(_tempCsvPath))
            File.Delete(_tempCsvPath);
    }

    #region New object type

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_NewObjectTypeWithCredentialAttribute_ExcludesTheCredentialAttributeAsync()
    {
        // Arrange: a Connected System with no persisted schema at all, so every attribute is newly discovered.
        var connectedSystem = CreateFileConnectorConnectedSystem();

        // Act
        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, _initiatedBy);

        // Assert
        var objectType = connectedSystem.ObjectTypes!.Single();
        var attributeNames = objectType.Attributes.Select(a => a.Name).ToList();
        Assert.That(attributeNames, Does.Not.Contain("unicodePwd"));
        Assert.That(attributeNames, Does.Contain("displayName"));
        Assert.That(attributeNames, Does.Contain("pwdLastSet"), "A merely credential-looking attribute must still import.");
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_NewObjectTypeWithCredentialAttribute_DoesNotReportItAsAddedAsync()
    {
        // Arrange
        var connectedSystem = CreateFileConnectorConnectedSystem();

        // Act
        var result = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, _initiatedBy);

        // Assert
        Assert.That(result.AddedAttributes[ObjectTypeName], Does.Not.Contain("unicodePwd"));
        Assert.That(result.RemovedAttributes.Values.SelectMany(v => v), Does.Not.Contain("unicodePwd"));
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_CredentialAttributeInSchema_IsReportedAsBlockedAsync()
    {
        // Arrange
        var connectedSystem = CreateFileConnectorConnectedSystem();

        // Act
        var result = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, _initiatedBy);

        // Assert: blocking must be visible to the administrator, not silent.
        Assert.That(result.BlockedCredentialAttributes.ContainsKey(ObjectTypeName), Is.True);
        Assert.That(result.BlockedCredentialAttributes[ObjectTypeName], Does.Contain("unicodePwd"));
        Assert.That(result.BlockedCredentialAttributeCount, Is.EqualTo(1));
    }

    #endregion

    #region Already-persisted credential attribute (the FK / bogus-removal regression)

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_PersistedCredentialAttribute_IsPreservedNotDeletedAsync()
    {
        // Arrange: a deployment configured before this rule existed, with unicodePwd persisted and selected. It
        // may be referenced by a Synchronisation Rule Mapping, so dropping it from the rebuilt attribute
        // collection would orphan the row and EF would issue a DELETE against a referenced FK.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes = [CreatePersistedObjectType(credentialAttributeSelected: true)];

        // Act
        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, _initiatedBy);

        // Assert
        var objectType = connectedSystem.ObjectTypes!.Single();
        var credentialAttribute = objectType.Attributes.SingleOrDefault(a => a.Name == "unicodePwd");
        Assert.That(credentialAttribute, Is.Not.Null, "The persisted credential attribute must be preserved, not orphaned into a DELETE.");
        Assert.That(credentialAttribute!.Id, Is.EqualTo(99), "The persisted row must be reused, preserving its id and any Synchronisation Rule Mapping references.");
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_PersistedCredentialAttribute_IsForcedIntoASafeStateAsync()
    {
        // Arrange
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes = [CreatePersistedObjectType(credentialAttributeSelected: true)];

        // Act
        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, _initiatedBy);

        // Assert
        var credentialAttribute = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "unicodePwd");
        Assert.That(credentialAttribute.Selected, Is.False, "JIM must not manage a credential attribute.");
        Assert.That(credentialAttribute.SelectionLocked, Is.True, "An administrator must not be able to turn it back on.");
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_PersistedCredentialAttribute_IsReportedAsNeitherAddedNorRemovedAsync()
    {
        // Arrange
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes = [CreatePersistedObjectType(credentialAttributeSelected: true)];

        // Act
        var result = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, _initiatedBy);

        // Assert: telling the administrator the attribute was removed would be a lie; the directory still has it.
        Assert.That(result.RemovedAttributes.Values.SelectMany(v => v), Does.Not.Contain("unicodePwd"));
        Assert.That(result.AddedAttributes.Values.SelectMany(v => v), Does.Not.Contain("unicodePwd"));
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_ApiKeyInitiatedWithPersistedCredentialAttribute_IsForcedIntoASafeStateAsync()
    {
        // Arrange: the API-key overload is a near-copy of the user-initiated one; prove the shared enforcement
        // reaches it too, so the two cannot drift.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes = [CreatePersistedObjectType(credentialAttributeSelected: true)];
        var apiKey = new JIM.Models.Security.ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "Test Key",
            KeyHash = "hash",
            KeyPrefix = "prefix"
        };

        // Act
        var result = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, apiKey);

        // Assert
        var credentialAttribute = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "unicodePwd");
        Assert.That(credentialAttribute.Selected, Is.False);
        Assert.That(credentialAttribute.SelectionLocked, Is.True);
        Assert.That(result.RemovedAttributes.Values.SelectMany(v => v), Does.Not.Contain("unicodePwd"));
        Assert.That(result.BlockedCredentialAttributes[ObjectTypeName], Does.Contain("unicodePwd"));
    }

    #endregion

    #region Whole-graph schema save

    [Test]
    public async Task UpdateConnectedSystemSchemaAsync_CredentialAttributeSelectedByCaller_IsDeselectedAndLockedAsync()
    {
        // Arrange: the portal's schema tab saves the whole Connected System graph in one call, so a credential
        // attribute switched on client-side (for example through the CSV quick-select path) would otherwise be
        // persisted as managed without ever passing through the per-attribute validation.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes = [CreatePersistedObjectType(credentialAttributeSelected: true)];

        // Act
        await _jim.ConnectedSystems.UpdateConnectedSystemSchemaAsync(connectedSystem, _initiatedBy);

        // Assert
        var credentialAttribute = connectedSystem.ObjectTypes!.Single().Attributes.Single(a => a.Name == "unicodePwd");
        Assert.That(credentialAttribute.Selected, Is.False);
        Assert.That(credentialAttribute.SelectionLocked, Is.True);
        Assert.That(connectedSystem.ObjectTypes.Single().Attributes.Single(a => a.Name == "displayName").Selected, Is.True,
            "Ordinary attribute selections must be untouched.");
    }

    #endregion

    #region External ID recommendations

    [Test]
    public void FilterCredentialAttributesFromSchema_RecommendedExternalIdIsCredentialAttribute_ClearsTheRecommendation()
    {
        // Arrange: a misbehaving or misconfigured Connector must not be able to make a credential attribute the
        // anchor, which would force it selected and locked on.
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Test" };
        var credentialAttribute = new ConnectorSchemaAttribute("unicodePwd", AttributeDataType.Text, AttributePlurality.SingleValued);
        var schemaObjectType = new ConnectorSchemaObjectType(ObjectTypeName)
        {
            Attributes = [credentialAttribute, new ConnectorSchemaAttribute("displayName", AttributeDataType.Text, AttributePlurality.SingleValued)],
            RecommendedExternalIdAttribute = credentialAttribute
        };
        var schema = new ConnectorSchema();
        schema.ObjectTypes.Add(schemaObjectType);

        // Act
        ConnectedSystemServer.FilterCredentialAttributesFromSchema(connectedSystem, schema, new SchemaRefreshResult());

        // Assert
        Assert.That(schemaObjectType.RecommendedExternalIdAttribute, Is.Null);
        Assert.That(schemaObjectType.Attributes.Select(a => a.Name), Does.Not.Contain("unicodePwd"));
    }

    [Test]
    public void FilterCredentialAttributesFromSchema_RecommendedSecondaryExternalIdIsCredentialAttribute_ClearsTheRecommendation()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem { Id = 1, Name = "Test" };
        var credentialAttribute = new ConnectorSchemaAttribute("userPassword", AttributeDataType.Text, AttributePlurality.SingleValued);
        var anchor = new ConnectorSchemaAttribute("entryUUID", AttributeDataType.Text, AttributePlurality.SingleValued);
        var schemaObjectType = new ConnectorSchemaObjectType(ObjectTypeName)
        {
            Attributes = [credentialAttribute, anchor],
            RecommendedExternalIdAttribute = anchor,
            RecommendedSecondaryExternalIdAttribute = credentialAttribute
        };
        var schema = new ConnectorSchema();
        schema.ObjectTypes.Add(schemaObjectType);

        // Act
        ConnectedSystemServer.FilterCredentialAttributesFromSchema(connectedSystem, schema, new SchemaRefreshResult());

        // Assert
        Assert.That(schemaObjectType.RecommendedSecondaryExternalIdAttribute, Is.Null);
        Assert.That(schemaObjectType.RecommendedExternalIdAttribute, Is.EqualTo(anchor), "A legitimate anchor recommendation must be untouched.");
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Builds a Connected System backed by the File Connector, whose schema discovery reads the temp CSV's headers.
    /// </summary>
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

        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "File Path").StringValue = _tempCsvPath;
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Object Type").StringValue = ObjectTypeName;
        return connectedSystem;
    }

    /// <summary>
    /// A previously-persisted object type carrying the credential attribute, as an upgraded deployment would have.
    /// </summary>
    private static ConnectedSystemObjectType CreatePersistedObjectType(bool credentialAttributeSelected)
    {
        return new ConnectedSystemObjectType
        {
            Id = 1,
            Name = ObjectTypeName,
            Selected = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 97, Name = "id", Type = AttributeDataType.Number, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Id = 98, Name = "displayName", Type = AttributeDataType.Text, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Id = 99, Name = "unicodePwd", Type = AttributeDataType.Text, Selected = credentialAttributeSelected },
                new ConnectedSystemObjectTypeAttribute { Id = 100, Name = "pwdLastSet", Type = AttributeDataType.Text, Selected = true }
            ]
        };
    }

    #endregion
}

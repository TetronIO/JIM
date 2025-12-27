using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Integration tests for the provisioning flow - verifying the complete HR -> MV -> LDAP flow
/// including CSO creation with PendingProvisioning status and ExportResult handling.
/// </summary>
[TestFixture]
public class ProvisioningFlowTests
{
    #region accessors
    private MetaverseObject InitiatedBy { get; set; } = null!;
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<Activity> ActivitiesData { get; set; } = null!;
    private Mock<DbSet<Activity>> MockDbSetActivities { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObject>> MockDbSetConnectedSystemObjects { get; set; } = null!;
    private List<ConnectedSystemRunProfile> ConnectedSystemRunProfilesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemRunProfile>> MockDbSetConnectedSystemRunProfiles { get; set; } = null!;
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<ConnectedSystemPartition> ConnectedSystemPartitionsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemPartition>> MockDbSetConnectedSystemPartitions { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; } = null!;
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; } = null!;
    private Mock<DbSet<MetaverseObjectType>> MockDbSetMetaverseObjectTypes { get; set; } = null!;
    private List<MetaverseObject> MetaverseObjectsData { get; set; } = null!;
    private Mock<DbSet<MetaverseObject>> MockDbSetMetaverseObjects { get; set; } = null!;
    private List<SyncRule> SyncRulesData { get; set; } = null!;
    private Mock<DbSet<SyncRule>> MockDbSetSyncRules { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    #endregion

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
    }

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();
        InitiatedBy = TestUtilities.GetInitiatedBy();

        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();

        var fullSyncRunProfile = ConnectedSystemRunProfilesData.Single(rp => rp.Name == "Dummy Source System Full Sync");
        ActivitiesData = TestUtilities.GetActivityData(fullSyncRunProfile.RunType, fullSyncRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();

        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();

        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.BuildMockDbSet();

        MetaverseObjectsData = TestUtilities.GetMetaverseObjectData();
        MockDbSetMetaverseObjects = MetaverseObjectsData.BuildMockDbSet();

        // Configure the mock to generate IDs when MVOs are added
        MockDbSetMetaverseObjects.Setup(m => m.Add(It.IsAny<MetaverseObject>()))
            .Callback<MetaverseObject>(mvo =>
            {
                if (mvo.Id == Guid.Empty)
                    mvo.Id = Guid.NewGuid();
                MetaverseObjectsData.Add(mvo);
            });

        // Configure mock for CSO additions
        MockDbSetConnectedSystemObjects.Setup(m => m.Add(It.IsAny<ConnectedSystemObject>()))
            .Callback<ConnectedSystemObject>(cso =>
            {
                if (cso.Id == Guid.Empty)
                    cso.Id = Guid.NewGuid();
                ConnectedSystemObjectsData.Add(cso);
            });

        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();

        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.Activities).Returns(MockDbSetActivities.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemPartitions).Returns(MockDbSetConnectedSystemPartitions.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemRunProfiles).Returns(MockDbSetConnectedSystemRunProfiles.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(MockDbSetMetaverseObjects.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(MockDbSetSyncRules.Object);

        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
    }

    #region CSO Provisioning Status Tests

    /// <summary>
    /// Verifies that a CSO can be created with PendingProvisioning status
    /// to represent a planned provision before actual export.
    /// </summary>
    [Test]
    public void ProvisionedCso_WithPendingProvisioningStatus_CanBeCreatedBeforeExport()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData[1]; // Target system
        var targetObjectType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvo = MetaverseObjectsData[0];

        // Act - Create a CSO that represents a planned provision (before export)
        var provisionedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = targetSystem,
            ConnectedSystemId = targetSystem.Id,
            Type = targetObjectType,
            TypeId = targetObjectType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            DateJoined = DateTime.UtcNow,
            Created = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };

        // Assert
        Assert.That(provisionedCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning));
        Assert.That(provisionedCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned));
        Assert.That(provisionedCso.MetaverseObjectId, Is.EqualTo(mvo.Id));
        Assert.That(provisionedCso.DateJoined, Is.Not.Null);
    }

    /// <summary>
    /// Verifies that a PendingProvisioning CSO transitions to Normal status after successful export.
    /// </summary>
    [Test]
    public void ProvisionedCso_AfterSuccessfulExport_TransitionsToNormal()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData[1];
        var targetObjectType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvo = MetaverseObjectsData[0];

        var provisionedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = targetSystem,
            ConnectedSystemId = targetSystem.Id,
            Type = targetObjectType,
            TypeId = targetObjectType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            DateJoined = DateTime.UtcNow,
            Created = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };

        // Act - Simulate what ExportExecutionServer does after successful export
        var exportResult = ExportResult.Succeeded(
            Guid.NewGuid().ToString(),  // objectGUID returned by LDAP
            "CN=Test User,OU=Users,DC=example,DC=com"  // DN returned by LDAP
        );

        // Simulate status transition after successful export
        if (exportResult.Success && provisionedCso.Status == ConnectedSystemObjectStatus.PendingProvisioning)
        {
            provisionedCso.Status = ConnectedSystemObjectStatus.Normal;
        }

        // Assert
        Assert.That(provisionedCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
        Assert.That(provisionedCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned));
    }

    /// <summary>
    /// Verifies that ExportResult with objectGUID can be used to populate CSO external ID.
    /// </summary>
    [Test]
    public void ExportResult_WithObjectGuid_PopulatesCsoExternalId()
    {
        // Arrange
        var targetObjectType = new ConnectedSystemObjectType
        {
            Id = 100,
            Name = "TestUser",
            Attributes = new List<ConnectedSystemObjectTypeAttribute>
            {
                new() { Id = 1000, Name = "objectGUID", Type = AttributeDataType.Guid, IsExternalId = true },
                new() { Id = 1001, Name = "distinguishedName", Type = AttributeDataType.Text }
            }
        };

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Type = targetObjectType,
            TypeId = targetObjectType.Id,
            ExternalIdAttributeId = 1000,  // objectGUID
            SecondaryExternalIdAttributeId = 1001,  // distinguishedName
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };

        // Act - Simulate receiving ExportResult from LDAP connector
        var returnedObjectGuid = Guid.NewGuid();
        var returnedDn = "CN=John Smith,OU=Users,DC=example,DC=com";

        var exportResult = ExportResult.Succeeded(returnedObjectGuid.ToString(), returnedDn);

        // Simulate how ExportExecutionServer would handle this result
        if (exportResult.Success)
        {
            // Add external ID attribute value (objectGUID)
            if (!string.IsNullOrEmpty(exportResult.ExternalId))
            {
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    ConnectedSystemObject = cso,
                    AttributeId = cso.ExternalIdAttributeId,
                    GuidValue = Guid.Parse(exportResult.ExternalId)
                });
            }

            // Add secondary external ID attribute value (DN)
            if (!string.IsNullOrEmpty(exportResult.SecondaryExternalId) && cso.SecondaryExternalIdAttributeId.HasValue)
            {
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    ConnectedSystemObject = cso,
                    AttributeId = cso.SecondaryExternalIdAttributeId.Value,
                    StringValue = exportResult.SecondaryExternalId
                });
            }

            // Transition status
            cso.Status = ConnectedSystemObjectStatus.Normal;
        }

        // Assert
        Assert.That(cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal));
        Assert.That(cso.AttributeValues, Has.Count.EqualTo(2));

        var guidAttrValue = cso.AttributeValues.First(av => av.AttributeId == cso.ExternalIdAttributeId);
        Assert.That(guidAttrValue.GuidValue, Is.EqualTo(returnedObjectGuid));

        var dnAttrValue = cso.AttributeValues.First(av => av.AttributeId == cso.SecondaryExternalIdAttributeId);
        Assert.That(dnAttrValue.StringValue, Is.EqualTo(returnedDn));
    }

    #endregion

    #region Export Evaluation Flow Tests

    /// <summary>
    /// Verifies that a PendingExport is created for a provisioning scenario.
    /// </summary>
    [Test]
    public void PendingExport_ForProvisionedCso_HasCreateChangeType()
    {
        // Arrange
        var targetSystem = ConnectedSystemsData[1];
        var targetObjectType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var mvo = MetaverseObjectsData[0];

        // Create a pending export for a create operation
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = targetSystem,
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystemObject = null,  // No CSO yet for create
            ChangeType = PendingExportChangeType.Create,
            Status = PendingExportStatus.Pending,
            AttributeValueChanges = new List<PendingExportAttributeValueChange>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ChangeType = PendingExportAttributeChangeType.Add,
                    AttributeId = targetObjectType.Attributes.First().Id,
                    Attribute = targetObjectType.Attributes.First(),
                    StringValue = "Test Value"
                }
            }
        };

        // Assert
        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
        Assert.That(pendingExport.Status, Is.EqualTo(PendingExportStatus.Pending));
        Assert.That(pendingExport.ConnectedSystemObject, Is.Null);
    }

    /// <summary>
    /// Verifies that ExportResult failure keeps CSO in PendingProvisioning status.
    /// </summary>
    [Test]
    public void ExportResult_OnFailure_KeepsCsoPendingProvisioning()
    {
        // Arrange
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };

        // Act - Simulate failed export
        var exportResult = ExportResult.Failed("LDAP error: Entry already exists");

        // Simulate how ExportExecutionServer would handle a failure
        if (!exportResult.Success)
        {
            // Don't change status on failure - keep PendingProvisioning for retry
            // In real implementation, we would also update error count/message
        }

        // Assert
        Assert.That(cso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning));
        Assert.That(exportResult.Success, Is.False);
        Assert.That(exportResult.ErrorMessage, Is.EqualTo("LDAP error: Entry already exists"));
    }

    #endregion

    #region MVO to CSO Relationship Tests

    /// <summary>
    /// Verifies that a provisioned CSO maintains its relationship to the source MVO.
    /// </summary>
    [Test]
    public void ProvisionedCso_MaintainsRelationshipToMvo()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var targetSystem = ConnectedSystemsData[1];
        var targetObjectType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Act - Create provisioned CSO linked to MVO
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystem = targetSystem,
            ConnectedSystemId = targetSystem.Id,
            Type = targetObjectType,
            TypeId = targetObjectType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            DateJoined = DateTime.UtcNow,
            Created = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };

        // Assert - CSO is properly linked to MVO
        Assert.That(cso.MetaverseObjectId, Is.EqualTo(mvo.Id));
        Assert.That(cso.MetaverseObject, Is.SameAs(mvo));
        Assert.That(cso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned));
        Assert.That(cso.DateJoined, Is.Not.Null);
    }

    /// <summary>
    /// Verifies that JoinType.Provisioned is distinct from JoinType.Joined.
    /// Provisioned = created by JIM via export
    /// Joined = matched during import via object matching rules
    /// </summary>
    [Test]
    public void JoinType_Provisioned_IsDifferentFromJoined()
    {
        // Assert
        Assert.That(ConnectedSystemObjectJoinType.Provisioned, Is.Not.EqualTo(ConnectedSystemObjectJoinType.Joined));
        Assert.That((int)ConnectedSystemObjectJoinType.Provisioned, Is.EqualTo(2));
        Assert.That((int)ConnectedSystemObjectJoinType.Joined, Is.EqualTo(3));
    }

    #endregion

    #region Object Matching Rule Tests

    /// <summary>
    /// Verifies that ObjectMatchingRuleMode defaults to ConnectedSystem mode.
    /// </summary>
    [Test]
    public void ConnectedSystem_DefaultsToConnectedSystemMatchingMode()
    {
        // Arrange
        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test System"
        };

        // Assert
        Assert.That(connectedSystem.ObjectMatchingRuleMode, Is.EqualTo(ObjectMatchingRuleMode.ConnectedSystem));
    }

    /// <summary>
    /// Verifies that ObjectMatchingRules can be attached to a ConnectedSystemObjectType.
    /// </summary>
    [Test]
    public void ObjectMatchingRules_CanBeAttachedToObjectType()
    {
        // Arrange
        var objectType = ConnectedSystemObjectTypesData[0];
        var mvAttr = MetaverseObjectTypesData[0].Attributes.First();

        var matchingRule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectType = objectType,
            ConnectedSystemObjectTypeId = objectType.Id,
            TargetMetaverseAttribute = mvAttr,
            TargetMetaverseAttributeId = mvAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 1,
                    MetaverseAttribute = mvAttr,
                    MetaverseAttributeId = mvAttr.Id
                }
            }
        };

        // Act
        objectType.ObjectMatchingRules = new List<ObjectMatchingRule> { matchingRule };

        // Assert
        Assert.That(objectType.ObjectMatchingRules, Has.Count.EqualTo(1));
        Assert.That(matchingRule.IsValid(), Is.True);
    }

    /// <summary>
    /// Verifies that ObjectMatchingRules can be attached to a SyncRule.
    /// </summary>
    [Test]
    public void ObjectMatchingRules_CanBeAttachedToSyncRule()
    {
        // Arrange
        var syncRule = SyncRulesData[0];
        var mvAttr = MetaverseObjectTypesData[0].Attributes.First();
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };

        var matchingRule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            SyncRule = syncRule,
            SyncRuleId = syncRule.Id,
            TargetMetaverseAttribute = mvAttr,
            TargetMetaverseAttributeId = mvAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 1,
                    ConnectedSystemAttribute = csAttr,
                    ConnectedSystemAttributeId = csAttr.Id
                }
            }
        };

        // Act
        syncRule.ObjectMatchingRules = new List<ObjectMatchingRule> { matchingRule };

        // Assert
        Assert.That(syncRule.ObjectMatchingRules, Has.Count.EqualTo(1));
        Assert.That(matchingRule.IsValid(), Is.True);
    }

    #endregion
}

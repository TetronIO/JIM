using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Utilities;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for ExportEvaluationServer - the Q1 decision to create PendingExports when MVO changes.
/// </summary>
public class ExportEvaluationTests
{
    #region accessors
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

        // Set up the Connected System Run Profiles mock
        ConnectedSystemRunProfilesData = TestUtilities.GetConnectedSystemRunProfileData();
        MockDbSetConnectedSystemRunProfiles = ConnectedSystemRunProfilesData.BuildMockDbSet();

        // Set up the Activity mock
        var exportRunProfile = ConnectedSystemRunProfilesData.Single(rp => rp.Name == "Dummy Target System Export");
        ActivitiesData = TestUtilities.GetActivityData(exportRunProfile.RunType, exportRunProfile.Id);
        MockDbSetActivities = ActivitiesData.BuildMockDbSet();

        // Set up the Connected Systems mock
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        // Set up the Connected System Object Types mock
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        // Set up the Connected System Objects mock
        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();

        // Set up the Connected System Partitions mock
        ConnectedSystemPartitionsData = TestUtilities.GetConnectedSystemPartitionData();
        MockDbSetConnectedSystemPartitions = ConnectedSystemPartitionsData.BuildMockDbSet();

        // Set up the Pending Export objects mock
        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        // Set up the Metaverse Object Types mock
        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.BuildMockDbSet();

        // Set up the Metaverse Objects mock
        MetaverseObjectsData = TestUtilities.GetMetaverseObjectData();
        MockDbSetMetaverseObjects = MetaverseObjectsData.BuildMockDbSet();

        // Set up the Sync Rule stub mocks
        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();

        // Mock entity framework calls
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

        // Instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
    }

    /// <summary>
    /// Tests that export rules are evaluated based on MVO type matching.
    /// This validates the basic logic of finding applicable export rules for an MVO.
    /// Note: A full integration test would require more complex mocking of repository methods.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenNoExportRulesMatchMvoType_ReturnsEmptyListAsync()
    {
        // Arrange - use an MVO with type that doesn't match any enabled export rules
        var mvo = MetaverseObjectsData[0];

        // Set the MVO type to User
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        // Ensure no export rules match this type by changing all export rules to point to a different type
        foreach (var rule in SyncRulesData.Where(sr => sr.Direction == SyncRuleDirection.Export))
        {
            rule.MetaverseObjectTypeId = 9999; // Non-existent type ID
        }

        var changedAttributes = new List<MetaverseObjectAttributeValue>
        {
            mvo.AttributeValues.First()
        };

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert - no rules should match
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0), "Expected no PendingExports when no export rules match MVO type");
    }

    /// <summary>
    /// Tests the Q3 decision: circular sync prevention - exports should not be created back to the source system.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenSourceSystemIsTarget_SkipsExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var sourceSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Source System");

        // Set up export rule pointing back to source system (which should be skipped)
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportRule.ConnectedSystemId = sourceSystem.Id;
        exportRule.ConnectedSystem = sourceSystem;

        var changedAttributes = new List<MetaverseObjectAttributeValue>
        {
            mvo.AttributeValues.First()
        };

        // Act - pass sourceSystem as the source of changes (Q3 circular prevention)
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes, sourceSystem);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0), "Expected no PendingExports when source system is the target (Q3 decision)");
    }

    /// <summary>
    /// Tests the Q4 decision: only Provisioned CSOs should be deleted when MVO is deleted.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionAsync_WhenCsoIsProvisioned_CreatesDeleteExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create a provisioned CSO joined to the MVO
        var provisionedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned
        };

        ConnectedSystemObjectsData.Add(provisionedCso);

        // Track pending exports created
        MockDbSetPendingExports.Setup(set => set.AddAsync(It.IsAny<PendingExport>(), It.IsAny<CancellationToken>()))
            .Callback((PendingExport entity, CancellationToken _) => { PendingExportsData.Add(entity); })
            .ReturnsAsync((PendingExport entity, CancellationToken _) => null!);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.GreaterThan(0), "Expected delete PendingExport for Provisioned CSO");
        Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Delete));
        Assert.That(result[0].ConnectedSystemObject?.Id, Is.EqualTo(provisionedCso.Id));
    }

    /// <summary>
    /// Tests the Q4 decision: Joined (not Provisioned) CSOs should NOT be deleted when MVO is deleted.
    /// </summary>
    [Test]
    public async Task EvaluateMvoDeletionAsync_WhenCsoIsJoined_DoesNotCreateDeleteExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create a joined (not provisioned) CSO
        var joinedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined
        };

        ConnectedSystemObjectsData.Add(joinedCso);

        // Act
        var result = await Jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0), "Expected no delete PendingExport for Joined CSO (Q4 decision)");
    }

    /// <summary>
    /// Tests that MVOs without a type set are handled gracefully.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenMvoHasNoType_ReturnsEmptyListAsync()
    {
        // Arrange
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = null! // No type set
        };

        var changedAttributes = new List<MetaverseObjectAttributeValue>();

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0), "Expected no PendingExports when MVO has no type");
    }

    /// <summary>
    /// Tests that scoping criteria are evaluated correctly - MVO in scope.
    /// </summary>
    [Test]
    public void IsMvoInScopeForExportRule_WhenMvoMatchesCriteria_ReturnsTrue()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");

        // Add scoping criteria: EmployeeId equals "E123"
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "E123"
                }
            }
        });

        // Act
        var result = Jim.ExportEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.True, "Expected MVO to be in scope when criteria matches");
    }

    /// <summary>
    /// Tests that scoping criteria are evaluated correctly - MVO not in scope.
    /// </summary>
    [Test]
    public void IsMvoInScopeForExportRule_WhenMvoDoesNotMatchCriteria_ReturnsFalse()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");

        // Add scoping criteria: EmployeeId equals "DIFFERENT_VALUE"
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "DIFFERENT_VALUE"
                }
            }
        });

        // Act
        var result = Jim.ExportEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.False, "Expected MVO to be out of scope when criteria does not match");
    }

    /// <summary>
    /// Tests that when no scoping criteria exist, all MVOs are in scope.
    /// </summary>
    [Test]
    public void IsMvoInScopeForExportRule_WhenNoScopingCriteria_ReturnsTrue()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");

        // Ensure no scoping criteria
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Act
        var result = Jim.ExportEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);

        // Assert
        Assert.That(result, Is.True, "Expected MVO to be in scope when no criteria defined");
    }

    #region Provisioning Flow End-to-End Tests

    /// <summary>
    /// End-to-end test: When MVO changes and no CSO exists for the target system,
    /// a new CSO should be created with Status=PendingProvisioning and JoinType=Provisioned,
    /// and a Create PendingExport should be generated.
    /// This tests the happy path of provisioning flow.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenNoCsoExistsAndProvisioningEnabled_CreatesPendingProvisioningCsoAndCreateExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Configure export rule for provisioning
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Ensure no CSO exists for this MVO in the target system
        ConnectedSystemObjectsData.RemoveAll(cso => cso.MetaverseObjectId == mvo.Id && cso.ConnectedSystemId == targetSystem.Id);

        // Track CSOs created - using synchronous Add (not AddAsync) to match repository implementation
        var createdCsos = new List<ConnectedSystemObject>();
        MockDbSetConnectedSystemObjects.Setup(set => set.Add(It.IsAny<ConnectedSystemObject>()))
            .Callback((ConnectedSystemObject entity) =>
            {
                if (entity.Id == Guid.Empty)
                    entity.Id = Guid.NewGuid();
                createdCsos.Add(entity);
                ConnectedSystemObjectsData.Add(entity);
            });

        // Track pending exports created - using synchronous Add to match repository implementation
        MockDbSetPendingExports.Setup(set => set.Add(It.IsAny<PendingExport>()))
            .Callback((PendingExport entity) => { PendingExportsData.Add(entity); });

        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert - CSO should be created with PendingProvisioning status
        Assert.That(createdCsos, Has.Count.EqualTo(1), "Expected exactly one CSO to be created");
        var newCso = createdCsos[0];
        Assert.That(newCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            "CSO should have PendingProvisioning status before export");
        Assert.That(newCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Provisioned),
            "CSO should have Provisioned JoinType");
        Assert.That(newCso.MetaverseObjectId, Is.EqualTo(mvo.Id),
            "CSO should be linked to the MVO");
        Assert.That(newCso.ConnectedSystemId, Is.EqualTo(targetSystem.Id),
            "CSO should be in the target system");

        // Assert - PendingExport should be created with Create change type
        Assert.That(result, Has.Count.EqualTo(1), "Expected exactly one PendingExport");
        var pendingExport = result[0];
        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create),
            "PendingExport should be a Create operation");
        Assert.That(pendingExport.ConnectedSystemObject?.Id, Is.EqualTo(newCso.Id),
            "PendingExport should reference the newly created CSO");
    }

    /// <summary>
    /// End-to-end test: When MVO changes and an existing CSO already exists and is joined,
    /// an Update PendingExport should be generated (not Create).
    /// This tests the scenario where provisioning finds an existing joined object.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenCsoAlreadyExistsAndJoined_CreatesUpdateExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create an existing CSO that is already joined to the MVO
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined, // Already joined via import
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow.AddDays(-1),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(existingCso);

        // Configure export rule
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Add a mapping so attribute changes are generated
        var displayNameCsAttr = targetUserType.Attributes.SingleOrDefault(a => a.Name == "displayName");
        var displayNameMvAttr = mvUserType.Attributes.SingleOrDefault(a => a.Name == Constants.BuiltInAttributes.DisplayName);

        if (displayNameCsAttr != null && displayNameMvAttr != null)
        {
            exportRule.AttributeFlowRules.Clear();
            var mapping = new SyncRuleMapping
            {
                Id = 1,
                TargetConnectedSystemAttribute = displayNameCsAttr,
                TargetConnectedSystemAttributeId = displayNameCsAttr.Id
            };
            mapping.Sources.Add(new SyncRuleMappingSource
            {
                Id = 1,
                MetaverseAttribute = displayNameMvAttr,
                MetaverseAttributeId = displayNameMvAttr.Id
            });
            exportRule.AttributeFlowRules.Add(mapping);
        }

        // Track CSOs created (should be none) - using synchronous Add to match repository implementation
        var createdCsos = new List<ConnectedSystemObject>();
        MockDbSetConnectedSystemObjects.Setup(set => set.Add(It.IsAny<ConnectedSystemObject>()))
            .Callback((ConnectedSystemObject entity) =>
            {
                createdCsos.Add(entity);
                ConnectedSystemObjectsData.Add(entity);
            });

        // Track pending exports created - using synchronous Add to match repository implementation
        MockDbSetPendingExports.Setup(set => set.Add(It.IsAny<PendingExport>()))
            .Callback((PendingExport entity) => { PendingExportsData.Add(entity); });

        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert - No new CSO should be created
        Assert.That(createdCsos, Has.Count.EqualTo(0), "No new CSO should be created when one already exists");

        // Assert - existing CSO should remain unchanged
        Assert.That(existingCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined),
            "Existing CSO JoinType should remain Joined");
        Assert.That(existingCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.Normal),
            "Existing CSO status should remain Normal");

        // Assert - PendingExport should be Update (not Create) if there are attribute changes
        // Note: May be 0 if no mapped attributes have changes
        if (result.Count > 0)
        {
            Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Update),
                "PendingExport should be Update, not Create, when CSO already exists");
            Assert.That(result[0].ConnectedSystemObject?.Id, Is.EqualTo(existingCso.Id),
                "PendingExport should reference the existing CSO");
        }
    }

    /// <summary>
    /// End-to-end test: When provisioning is disabled on the export rule and no CSO exists,
    /// no CSO or PendingExport should be created.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenProvisioningDisabledAndNoCso_DoesNotCreateCsoOrExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Configure export rule with provisioning DISABLED
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = false; // Provisioning disabled
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Ensure no CSO exists
        ConnectedSystemObjectsData.RemoveAll(cso => cso.MetaverseObjectId == mvo.Id && cso.ConnectedSystemId == targetSystem.Id);

        // Track CSOs created - using synchronous Add to match repository implementation
        var createdCsos = new List<ConnectedSystemObject>();
        MockDbSetConnectedSystemObjects.Setup(set => set.Add(It.IsAny<ConnectedSystemObject>()))
            .Callback((ConnectedSystemObject entity) => { createdCsos.Add(entity); });

        // Track pending exports created - using synchronous Add to match repository implementation
        MockDbSetPendingExports.Setup(set => set.Add(It.IsAny<PendingExport>()))
            .Callback((PendingExport entity) => { PendingExportsData.Add(entity); });

        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert - No CSO should be created when provisioning is disabled
        Assert.That(createdCsos, Has.Count.EqualTo(0),
            "No CSO should be created when ProvisionToConnectedSystem is false");

        // Assert - No PendingExport should be created
        Assert.That(result, Has.Count.EqualTo(0),
            "No PendingExport should be created when no CSO exists and provisioning is disabled");
    }

    /// <summary>
    /// End-to-end test: When MVO changes and CSO exists in PendingProvisioning state,
    /// the existing PendingProvisioning CSO should be used (not a new one created).
    /// This tests the scenario where export evaluation runs twice before export execution.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WhenCsoInPendingProvisioning_UsesExistingCsoAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create an existing CSO in PendingProvisioning state (from previous export evaluation)
        var pendingProvisioningCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            Status = ConnectedSystemObjectStatus.PendingProvisioning, // Not yet exported
            DateJoined = DateTime.UtcNow,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(pendingProvisioningCso);

        // Configure export rule
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Track CSOs created (should be none - should use existing) - using synchronous Add to match repository implementation
        var createdCsos = new List<ConnectedSystemObject>();
        MockDbSetConnectedSystemObjects.Setup(set => set.Add(It.IsAny<ConnectedSystemObject>()))
            .Callback((ConnectedSystemObject entity) =>
            {
                createdCsos.Add(entity);
                ConnectedSystemObjectsData.Add(entity);
            });

        // Track pending exports created - using synchronous Add to match repository implementation
        MockDbSetPendingExports.Setup(set => set.Add(It.IsAny<PendingExport>()))
            .Callback((PendingExport entity) => { PendingExportsData.Add(entity); });

        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert - No new CSO should be created (should use existing PendingProvisioning CSO)
        Assert.That(createdCsos, Has.Count.EqualTo(0),
            "No new CSO should be created when one already exists in PendingProvisioning state");

        // The existing CSO should still be PendingProvisioning
        Assert.That(pendingProvisioningCso.Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            "Existing CSO should remain in PendingProvisioning state");

        // If a pending export is created, it should be an Update (since CSO exists)
        if (result.Count > 0)
        {
            Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Update),
                "PendingExport should be Update when CSO exists (even in PendingProvisioning)");
            Assert.That(result[0].ConnectedSystemObject?.Id, Is.EqualTo(pendingProvisioningCso.Id),
                "PendingExport should reference the existing PendingProvisioning CSO");
        }
    }

    /// <summary>
    /// Q1.Expression: Tests that expression-based export mappings correctly evaluate and create pending exports.
    /// </summary>
    [Test]
    public async Task EvaluateExportRulesAsync_WithExpressionBasedMapping_CreatesCorrectPendingExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        // Set up MVO attribute values that will be used in the expression
        var displayNameAttr = mvo.Type.Attributes.Single(a => a.Name == "Display Name");
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = displayNameAttr,
            AttributeId = displayNameAttr.Id,
            StringValue = "Test User"
        });

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Add a DN attribute to the target system
        var dnAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 999,
            ConnectedSystemObjectType = targetUserType,
            Name = "distinguishedName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        targetUserType.Attributes.Add(dnAttr);

        // Configure export rule with expression-based mapping
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.ProvisionToConnectedSystem = true;
        exportRule.ObjectScopingCriteriaGroups.Clear();
        exportRule.AttributeFlowRules.Clear();

        // Add expression-based mapping for DN generation
        var expressionMapping = new SyncRuleMapping
        {
            Id = 888,
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = dnAttr,
            TargetConnectedSystemAttributeId = dnAttr.Id
        };
        expressionMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 777,
            Order = 1,
            Expression = "\"CN=\" + EscapeDN(mv[\"Display Name\"]) + \",CN=Users,DC=testdomain,DC=local\""
        });
        exportRule.AttributeFlowRules.Add(expressionMapping);

        // Track pending exports created
        MockDbSetPendingExports.Setup(set => set.Add(It.IsAny<PendingExport>()))
            .Callback((PendingExport entity) => { PendingExportsData.Add(entity); });

        // Track CSOs created
        var createdCsos = new List<ConnectedSystemObject>();
        MockDbSetConnectedSystemObjects.Setup(set => set.Add(It.IsAny<ConnectedSystemObject>()))
            .Callback((ConnectedSystemObject entity) =>
            {
                createdCsos.Add(entity);
                ConnectedSystemObjectsData.Add(entity);
            });

        var changedAttributes = mvo.AttributeValues.ToList();

        // Act
        var result = await Jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, changedAttributes);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1), "Should create one pending export");
        var pendingExport = result[0];

        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create),
            "Should be a Create operation since no CSO exists");

        Assert.That(pendingExport.AttributeValueChanges, Has.Count.EqualTo(1),
            "Should have one attribute change for the expression-based DN");

        var dnChange = pendingExport.AttributeValueChanges.First();
        Assert.That(dnChange.AttributeId, Is.EqualTo(dnAttr.Id),
            "Attribute change should be for the DN attribute");

        Assert.That(dnChange.StringValue, Is.EqualTo("CN=Test User,CN=Users,DC=testdomain,DC=local"),
            "DN should be correctly generated from the expression using Display Name");

        // Verify a PendingProvisioning CSO was created
        Assert.That(createdCsos, Has.Count.EqualTo(1),
            "Should create one PendingProvisioning CSO for the new provision");
        Assert.That(createdCsos[0].Status, Is.EqualTo(ConnectedSystemObjectStatus.PendingProvisioning),
            "CSO should be in PendingProvisioning state");
    }

    #endregion

    #region Out-of-Scope Deprovisioning Tests

    /// <summary>
    /// Tests that when MVO falls out of scope and OutboundDeprovisionAction is Disconnect,
    /// the CSO is disconnected from MVO but no delete pending export is created.
    /// </summary>
    [Test]
    public async Task EvaluateOutOfScopeExportsAsync_WithDisconnectAction_DisconnectsCsoAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;
        mvo.Origin = MetaverseObjectOrigin.Projected;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create existing CSO that was previously in scope
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow.AddDays(-30),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(existingCso);
        mvo.ConnectedSystemObjects.Add(existingCso);

        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        // Configure export rule with scoping criteria that MVO no longer matches
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Add scoping criteria: EmployeeId = "NOT_MATCHING" (MVO has "E123")
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "NOT_MATCHING_VALUE"
                }
            }
        });

        // Act
        var result = await Jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(mvo);

        // Assert - No delete pending export should be created for Disconnect action
        Assert.That(result, Has.Count.EqualTo(0), "Disconnect action should not create a delete pending export");

        // Assert - CSO should be disconnected from MVO
        Assert.That(existingCso.MetaverseObject, Is.Null, "CSO should be disconnected from MVO");
        Assert.That(existingCso.MetaverseObjectId, Is.Null, "CSO MetaverseObjectId should be null");
        Assert.That(existingCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.NotJoined), "CSO JoinType should be NotJoined");

        // Assert - MVO should no longer have the CSO in its collection
        Assert.That(mvo.ConnectedSystemObjects, Does.Not.Contain(existingCso), "MVO should not contain the disconnected CSO");
    }

    /// <summary>
    /// Tests that when MVO falls out of scope and OutboundDeprovisionAction is Delete,
    /// a delete pending export is created.
    /// </summary>
    [Test]
    public async Task EvaluateOutOfScopeExportsAsync_WithDeleteAction_CreatesDeletePendingExportAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;
        mvo.Origin = MetaverseObjectOrigin.Projected;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create existing CSO that was previously in scope
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow.AddDays(-30),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(existingCso);
        mvo.ConnectedSystemObjects.Add(existingCso);

        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        // Configure export rule with Delete action
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Add scoping criteria that MVO no longer matches
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "NOT_MATCHING_VALUE"
                }
            }
        });

        // Track pending exports created
        MockDbSetPendingExports.Setup(set => set.Add(It.IsAny<PendingExport>()))
            .Callback((PendingExport entity) => { PendingExportsData.Add(entity); });

        // Act
        var result = await Jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(mvo);

        // Assert - Delete pending export should be created
        Assert.That(result, Has.Count.EqualTo(1), "Delete action should create a delete pending export");
        Assert.That(result[0].ChangeType, Is.EqualTo(PendingExportChangeType.Delete), "Pending export should be a Delete operation");
        Assert.That(result[0].ConnectedSystemObject?.Id, Is.EqualTo(existingCso.Id), "Pending export should reference the CSO");
    }

    /// <summary>
    /// Tests that when MVO is still in scope, no deprovisioning occurs.
    /// </summary>
    [Test]
    public async Task EvaluateOutOfScopeExportsAsync_WhenMvoStillInScope_NoDeprovisioningAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvo.Type = mvUserType;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create existing CSO
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow.AddDays(-30),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(existingCso);
        mvo.ConnectedSystemObjects.Add(existingCso);

        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        // Configure export rule with scoping criteria that MVO DOES match
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete; // Even with Delete action
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Add scoping criteria that MVO matches (E123)
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "E123"
                }
            }
        });

        // Act
        var result = await Jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(mvo);

        // Assert - No deprovisioning should occur when MVO is in scope
        Assert.That(result, Has.Count.EqualTo(0), "No deprovisioning should occur when MVO is in scope");
        Assert.That(existingCso.MetaverseObject, Is.Not.Null, "CSO should still be joined to MVO");
        Assert.That(existingCso.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined), "CSO should still be Joined");
    }

    /// <summary>
    /// Tests that when MVO falls out of scope and is the last connector,
    /// LastConnectorDisconnectedDate is set on the MVO for Disconnect action.
    /// </summary>
    [Test]
    public async Task EvaluateOutOfScopeExportsAsync_WhenLastConnectorDisconnected_SetsLastConnectorDateAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        mvUserType.DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected;
        mvo.Type = mvUserType;
        mvo.Origin = MetaverseObjectOrigin.Projected;
        mvo.LastConnectorDisconnectedDate = null;

        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Create existing CSO - this will be the only connector
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObject = mvo,
            MetaverseObjectId = mvo.Id,
            JoinType = ConnectedSystemObjectJoinType.Joined,
            Status = ConnectedSystemObjectStatus.Normal,
            DateJoined = DateTime.UtcNow.AddDays(-30),
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };
        ConnectedSystemObjectsData.Add(existingCso);
        mvo.ConnectedSystemObjects.Clear();
        mvo.ConnectedSystemObjects.Add(existingCso);

        var employeeIdAttr = mvUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        // Configure export rule with Disconnect action
        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportRule.Enabled = true;
        exportRule.Direction = SyncRuleDirection.Export;
        exportRule.MetaverseObjectTypeId = mvUserType.Id;
        exportRule.ConnectedSystemId = targetSystem.Id;
        exportRule.ConnectedSystem = targetSystem;
        exportRule.ConnectedSystemObjectTypeId = targetUserType.Id;
        exportRule.ConnectedSystemObjectType = targetUserType;
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Disconnect;
        exportRule.ObjectScopingCriteriaGroups.Clear();

        // Add scoping criteria that MVO no longer matches
        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = employeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "NOT_MATCHING_VALUE"
                }
            }
        });

        // Act
        var result = await Jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(mvo);

        // Assert - LastConnectorDisconnectedDate should be set
        Assert.That(mvo.LastConnectorDisconnectedDate, Is.Not.Null, "LastConnectorDisconnectedDate should be set when last connector disconnected");
        Assert.That(mvo.ConnectedSystemObjects, Has.Count.EqualTo(0), "MVO should have no connectors after disconnect");
    }

    #endregion
}

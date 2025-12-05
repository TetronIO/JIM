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
}

using JIM.Application;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
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
/// Tests for DriftDetectionService - detecting and remediating unauthorised changes in target systems.
/// </summary>
public class DriftDetectionTests
{
    #region accessors
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObject>> MockDbSetConnectedSystemObjects { get; set; } = null!;
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<MetaverseObject> MetaverseObjectsData { get; set; } = null!;
    private Mock<DbSet<MetaverseObject>> MockDbSetMetaverseObjects { get; set; } = null!;
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; } = null!;
    private Mock<DbSet<MetaverseObjectType>> MockDbSetMetaverseObjectTypes { get; set; } = null!;
    private List<SyncRule> SyncRulesData { get; set; } = null!;
    private Mock<DbSet<SyncRule>> MockDbSetSyncRules { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private ConnectedSystem TargetSystem { get; set; } = null!;
    private ConnectedSystemObjectType TargetUserType { get; set; } = null!;
    private MetaverseObjectType MvoUserType { get; set; } = null!;
    private MetaverseAttribute DisplayNameMvAttr { get; set; } = null!;
    private ConnectedSystemObjectTypeAttribute DisplayNameCsoAttr { get; set; } = null!;
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

        // Set up the Connected Systems mock
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        // Set up the Connected System Object Types mock
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        // Set up the Connected System Objects mock
        ConnectedSystemObjectsData = new List<ConnectedSystemObject>();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();

        // Set up the Metaverse Objects mock
        MetaverseObjectsData = new List<MetaverseObject>();
        MockDbSetMetaverseObjects = MetaverseObjectsData.BuildMockDbSet();

        // Set up the Metaverse Object Types mock
        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.BuildMockDbSet();

        // Set up the Sync Rules mock
        SyncRulesData = new List<SyncRule>();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();

        // Set up the Pending Exports mock
        PendingExportsData = new List<PendingExport>();
        MockDbSetPendingExports = PendingExportsData.BuildMockDbSet();

        // Mock entity framework calls
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(MockDbSetMetaverseObjects.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(MockDbSetSyncRules.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);

        // Instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));

        // Store references to commonly used objects
        TargetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        TargetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        MvoUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        DisplayNameMvAttr = MvoUserType.Attributes.Single(a => a.Name == Constants.BuiltInAttributes.DisplayName);
        DisplayNameCsoAttr = TargetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
    }

    #region Helper Methods

    private ConnectedSystemObject CreateTestCso(MetaverseObject? mvo = null)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            Type = TargetUserType,
            TypeId = TargetUserType.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>(),
            MetaverseObject = mvo,
            MetaverseObjectId = mvo?.Id
        };

        ConnectedSystemObjectsData.Add(cso);
        return cso;
    }

    private MetaverseObject CreateTestMvo()
    {
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = MvoUserType,
            AttributeValues = new List<MetaverseObjectAttributeValue>(),
            ConnectedSystemObjects = new List<ConnectedSystemObject>()
        };

        MetaverseObjectsData.Add(mvo);
        return mvo;
    }

    private SyncRule CreateExportRule(bool enforceState = true)
    {
        var mapping = new SyncRuleMapping
        {
            Id = 1000,
            TargetConnectedSystemAttribute = DisplayNameCsoAttr,
            TargetConnectedSystemAttributeId = DisplayNameCsoAttr.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 10000,
            MetaverseAttribute = DisplayNameMvAttr,
            MetaverseAttributeId = DisplayNameMvAttr.Id
        });

        var exportRule = new SyncRule
        {
            Id = 100,
            Name = "Test Export Rule",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            EnforceState = enforceState,
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObjectTypeId = TargetUserType.Id,
            ConnectedSystemObjectType = TargetUserType,
            MetaverseObjectTypeId = MvoUserType.Id,
            MetaverseObjectType = MvoUserType,
            AttributeFlowRules = new List<SyncRuleMapping> { mapping }
        };

        SyncRulesData.Add(exportRule);
        return exportRule;
    }

    private SyncRule CreateImportRule()
    {
        var mapping = new SyncRuleMapping
        {
            Id = 2000,
            TargetMetaverseAttribute = DisplayNameMvAttr,
            TargetMetaverseAttributeId = DisplayNameMvAttr.Id
        };
        mapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 20000,
            ConnectedSystemAttribute = DisplayNameCsoAttr,
            ConnectedSystemAttributeId = DisplayNameCsoAttr.Id
        });

        var importRule = new SyncRule
        {
            Id = 200,
            Name = "Test Import Rule",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObjectTypeId = TargetUserType.Id,
            ConnectedSystemObjectType = TargetUserType,
            MetaverseObjectTypeId = MvoUserType.Id,
            MetaverseObjectType = MvoUserType,
            AttributeFlowRules = new List<SyncRuleMapping> { mapping }
        };

        SyncRulesData.Add(importRule);
        return importRule;
    }

    #endregion

    #region BuildImportMappingCache Tests

    [Test]
    public void BuildImportMappingCache_WithImportRules_ReturnsCorrectMappings()
    {
        // Arrange
        var importRule = CreateImportRule();

        // Act
        var cache = DriftDetectionService.BuildImportMappingCache(SyncRulesData);

        // Assert
        Assert.That(cache, Is.Not.Null);
        Assert.That(cache.Count, Is.GreaterThan(0));

        var key = (TargetSystem.Id, DisplayNameMvAttr.Id);
        Assert.That(cache.ContainsKey(key), Is.True);
        Assert.That(cache[key].Count, Is.EqualTo(1));
    }

    [Test]
    public void BuildImportMappingCache_WithNoImportRules_ReturnsEmptyCache()
    {
        // Arrange - only add export rules
        CreateExportRule();

        // Act
        var cache = DriftDetectionService.BuildImportMappingCache(SyncRulesData);

        // Assert
        Assert.That(cache, Is.Not.Null);
        Assert.That(cache.Count, Is.EqualTo(0));
    }

    [Test]
    public void BuildImportMappingCache_WithDisabledRules_ExcludesDisabledRules()
    {
        // Arrange
        var importRule = CreateImportRule();
        importRule.Enabled = false;

        // Act
        var cache = DriftDetectionService.BuildImportMappingCache(SyncRulesData);

        // Assert
        Assert.That(cache.Count, Is.EqualTo(0));
    }

    #endregion

    #region HasImportRuleForAttribute Tests

    [Test]
    public void HasImportRuleForAttribute_WhenSystemIsContributor_ReturnsTrue()
    {
        // Arrange
        CreateImportRule();
        var cache = DriftDetectionService.BuildImportMappingCache(SyncRulesData);

        // Act
        var result = Jim.DriftDetection.HasImportRuleForAttribute(
            TargetSystem.Id,
            DisplayNameMvAttr.Id,
            cache);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void HasImportRuleForAttribute_WhenSystemIsNotContributor_ReturnsFalse()
    {
        // Arrange - no import rules for this attribute
        CreateExportRule();
        var cache = DriftDetectionService.BuildImportMappingCache(SyncRulesData);

        // Act
        var result = Jim.DriftDetection.HasImportRuleForAttribute(
            TargetSystem.Id,
            DisplayNameMvAttr.Id,
            cache);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void HasImportRuleForAttribute_WhenCacheIsNull_ReturnsFalse()
    {
        // Act
        var result = Jim.DriftDetection.HasImportRuleForAttribute(
            TargetSystem.Id,
            DisplayNameMvAttr.Id,
            null);

        // Assert
        Assert.That(result, Is.False);
    }

    #endregion

    #region EvaluateDriftAsync Tests

    [Test]
    public async Task EvaluateDriftAsync_WhenNoDrift_ReturnsEmptyResult()
    {
        // Arrange
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = DisplayNameMvAttr,
            AttributeId = DisplayNameMvAttr.Id,
            StringValue = "John Doe"
        });

        var cso = CreateTestCso(mvo);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso,
            Attribute = DisplayNameCsoAttr,
            AttributeId = DisplayNameCsoAttr.Id,
            StringValue = "John Doe" // Same as MVO - no drift
        });
        mvo.ConnectedSystemObjects.Add(cso);

        var exportRule = CreateExportRule(enforceState: true);

        // Act
        var result = await Jim.DriftDetection.EvaluateDriftAsync(
            cso,
            mvo,
            new List<SyncRule> { exportRule },
            null);

        // Assert
        Assert.That(result.HasDrift, Is.False);
        Assert.That(result.DriftedAttributes.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task EvaluateDriftAsync_WhenDriftDetected_ReturnsDriftedAttributes()
    {
        // Arrange
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = DisplayNameMvAttr,
            AttributeId = DisplayNameMvAttr.Id,
            StringValue = "John Doe"
        });

        var cso = CreateTestCso(mvo);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso,
            Attribute = DisplayNameCsoAttr,
            AttributeId = DisplayNameCsoAttr.Id,
            StringValue = "Jane Doe" // Different from MVO - drift!
        });
        mvo.ConnectedSystemObjects.Add(cso);

        var exportRule = CreateExportRule(enforceState: true);

        // Act
        var result = await Jim.DriftDetection.EvaluateDriftAsync(
            cso,
            mvo,
            new List<SyncRule> { exportRule },
            null);

        // Assert
        Assert.That(result.HasDrift, Is.True);
        Assert.That(result.DriftedAttributes.Count, Is.EqualTo(1));
        Assert.That(result.DriftedAttributes[0].ExpectedValue, Is.EqualTo("John Doe"));
        Assert.That(result.DriftedAttributes[0].ActualValue, Is.EqualTo("Jane Doe"));
    }

    [Test]
    public async Task EvaluateDriftAsync_WhenEnforceStateFalse_SkipsRule()
    {
        // Arrange
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = DisplayNameMvAttr,
            AttributeId = DisplayNameMvAttr.Id,
            StringValue = "John Doe"
        });

        var cso = CreateTestCso(mvo);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso,
            Attribute = DisplayNameCsoAttr,
            AttributeId = DisplayNameCsoAttr.Id,
            StringValue = "Jane Doe" // Different - but EnforceState = false
        });
        mvo.ConnectedSystemObjects.Add(cso);

        var exportRule = CreateExportRule(enforceState: false);

        // Act
        var result = await Jim.DriftDetection.EvaluateDriftAsync(
            cso,
            mvo,
            new List<SyncRule> { exportRule },
            null);

        // Assert - no drift because EnforceState is false
        Assert.That(result.HasDrift, Is.False);
    }

    [Test]
    public async Task EvaluateDriftAsync_WhenSystemIsContributor_DoesNotFlagAsDrift()
    {
        // Arrange
        var mvo = CreateTestMvo();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = DisplayNameMvAttr,
            AttributeId = DisplayNameMvAttr.Id,
            StringValue = "John Doe"
        });

        var cso = CreateTestCso(mvo);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso,
            Attribute = DisplayNameCsoAttr,
            AttributeId = DisplayNameCsoAttr.Id,
            StringValue = "Jane Doe" // Different - but system is a contributor
        });
        mvo.ConnectedSystemObjects.Add(cso);

        var exportRule = CreateExportRule(enforceState: true);
        var importRule = CreateImportRule(); // System is also a contributor

        // Build cache with import rule
        var importMappingCache = DriftDetectionService.BuildImportMappingCache(SyncRulesData);

        // Act
        var result = await Jim.DriftDetection.EvaluateDriftAsync(
            cso,
            mvo,
            new List<SyncRule> { exportRule },
            importMappingCache);

        // Assert - no drift because system is a legitimate contributor
        Assert.That(result.HasDrift, Is.False);
    }

    [Test]
    public async Task EvaluateDriftAsync_WhenCsoNotJoined_ReturnsEmptyResult()
    {
        // Arrange
        var cso = CreateTestCso(null); // Not joined to any MVO

        var exportRule = CreateExportRule(enforceState: true);

        // Act
        var result = await Jim.DriftDetection.EvaluateDriftAsync(
            cso,
            null,
            new List<SyncRule> { exportRule },
            null);

        // Assert
        Assert.That(result.HasDrift, Is.False);
    }

    [Test]
    public async Task EvaluateDriftAsync_WhenNoApplicableExportRules_ReturnsEmptyResult()
    {
        // Arrange
        var mvo = CreateTestMvo();
        var cso = CreateTestCso(mvo);
        mvo.ConnectedSystemObjects.Add(cso);

        // Export rule for a different connected system
        var exportRule = CreateExportRule(enforceState: true);
        exportRule.ConnectedSystemId = 999; // Different system

        // Act
        var result = await Jim.DriftDetection.EvaluateDriftAsync(
            cso,
            mvo,
            new List<SyncRule> { exportRule },
            null);

        // Assert
        Assert.That(result.HasDrift, Is.False);
    }

    /// <summary>
    /// Tests that when MVO.Type is null (navigation property not loaded), drift detection
    /// logs a warning and returns an empty result rather than silently failing to find rules.
    /// This test validates the defensive check added in Phase 4 of the fix.
    /// </summary>
    [Test]
    public async Task EvaluateDriftAsync_WhenMvoTypeIsNull_ReturnsEmptyResultAsync()
    {
        // Arrange
        var mvo = CreateTestMvo();
        mvo.Type = null!; // Simulate navigation property not loaded (as happens in delta sync bug)

        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = DisplayNameMvAttr,
            AttributeId = DisplayNameMvAttr.Id,
            StringValue = "John Doe"
        });

        var cso = CreateTestCso(mvo);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso,
            Attribute = DisplayNameCsoAttr,
            AttributeId = DisplayNameCsoAttr.Id,
            StringValue = "Jane Doe" // Different from MVO - would be drift if Type was loaded
        });
        mvo.ConnectedSystemObjects.Add(cso);

        var exportRule = CreateExportRule(enforceState: true);

        // Act
        var result = await Jim.DriftDetection.EvaluateDriftAsync(
            cso,
            mvo,
            new List<SyncRule> { exportRule },
            null);

        // Assert - Should return empty result (not detect drift) when MVO.Type is null
        // This is defensive behaviour - better to not detect drift than to crash or behave unexpectedly
        Assert.That(result.HasDrift, Is.False, "When MVO.Type is null, drift detection should return empty result");
        Assert.That(result.DriftedAttributes.Count, Is.EqualTo(0));
    }

    /// <summary>
    /// Tests that when MVO.Type is properly loaded, the export rule filtering correctly
    /// matches rules by MetaverseObjectTypeId. This validates the fix works when the
    /// repository correctly loads the navigation property.
    /// </summary>
    [Test]
    public async Task EvaluateDriftAsync_WhenMvoTypeIsLoaded_FindsApplicableExportRulesAsync()
    {
        // Arrange
        var mvo = CreateTestMvo();
        // Ensure MVO.Type is loaded (as it should be after the fix)
        Assert.That(mvo.Type, Is.Not.Null, "Test setup: MVO.Type should be set");
        Assert.That(mvo.Type.Id, Is.EqualTo(MvoUserType.Id), "Test setup: MVO.Type.Id should match");

        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = DisplayNameMvAttr,
            AttributeId = DisplayNameMvAttr.Id,
            StringValue = "John Doe"
        });

        var cso = CreateTestCso(mvo);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso,
            Attribute = DisplayNameCsoAttr,
            AttributeId = DisplayNameCsoAttr.Id,
            StringValue = "Jane Doe" // Different from MVO - drift!
        });
        mvo.ConnectedSystemObjects.Add(cso);

        var exportRule = CreateExportRule(enforceState: true);
        // Ensure export rule MetaverseObjectTypeId matches MVO.Type.Id
        Assert.That(exportRule.MetaverseObjectTypeId, Is.EqualTo(mvo.Type.Id),
            "Test setup: Export rule should target the same MVO type");

        // Act
        var result = await Jim.DriftDetection.EvaluateDriftAsync(
            cso,
            mvo,
            new List<SyncRule> { exportRule },
            null);

        // Assert - Should find the applicable export rule and detect drift
        Assert.That(result.HasDrift, Is.True, "Drift detection should find applicable export rules when MVO.Type is loaded");
        Assert.That(result.DriftedAttributes.Count, Is.EqualTo(1));
        Assert.That(result.DriftedAttributes[0].ExpectedValue, Is.EqualTo("John Doe"));
        Assert.That(result.DriftedAttributes[0].ActualValue, Is.EqualTo("Jane Doe"));
    }

    /// <summary>
    /// Tests that when MVO.Type.Id doesn't match the export rule's MetaverseObjectTypeId,
    /// the rule is correctly filtered out and no drift is detected.
    /// This validates the export rule filtering logic.
    /// </summary>
    [Test]
    public async Task EvaluateDriftAsync_WhenMvoTypeIdDoesNotMatch_DoesNotFindRulesAsync()
    {
        // Arrange
        var mvo = CreateTestMvo();
        Assert.That(mvo.Type, Is.Not.Null, "Test setup: MVO.Type should be set");

        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = DisplayNameMvAttr,
            AttributeId = DisplayNameMvAttr.Id,
            StringValue = "John Doe"
        });

        var cso = CreateTestCso(mvo);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso,
            Attribute = DisplayNameCsoAttr,
            AttributeId = DisplayNameCsoAttr.Id,
            StringValue = "Jane Doe" // Different - but rule won't match
        });
        mvo.ConnectedSystemObjects.Add(cso);

        var exportRule = CreateExportRule(enforceState: true);
        // Set export rule to target a DIFFERENT MVO type
        exportRule.MetaverseObjectTypeId = 999;

        // Act
        var result = await Jim.DriftDetection.EvaluateDriftAsync(
            cso,
            mvo,
            new List<SyncRule> { exportRule },
            null);

        // Assert - Should not find any applicable rules
        Assert.That(result.HasDrift, Is.False, "Export rule with different MVO type ID should not match");
        Assert.That(result.DriftedAttributes.Count, Is.EqualTo(0));
    }

    #endregion

    #region Multi-Valued Attribute Drift Tests

    /// <summary>
    /// Tests that drift detection correctly handles multi-valued attributes (like group membership).
    /// When the CSO has extra values not in the MVO, drift should be detected and corrective
    /// exports should remove the extra values.
    /// </summary>
    [Test]
    public async Task EvaluateDriftAsync_WithMultiValuedAttribute_ExtraValueInCso_DetectsDriftAsync()
    {
        // Arrange - Create a multi-valued attribute (like 'member' for groups)
        var memberMvAttr = new MetaverseAttribute
        {
            Id = 5000,
            Name = "Static Members",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued
        };
        MvoUserType.Attributes.Add(memberMvAttr);

        var memberCsoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 5001,
            Name = "member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            ConnectedSystemObjectType = TargetUserType
        };
        TargetUserType.Attributes.Add(memberCsoAttr);

        // Create referenced MVOs (group members)
        var member1Mvo = CreateTestMvo();
        var member2Mvo = CreateTestMvo();
        var member3Mvo = CreateTestMvo(); // Extra member - not expected

        // Create the group MVO with expected members (member1 and member2)
        var groupMvo = CreateTestMvo();
        groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = groupMvo,
            Attribute = memberMvAttr,
            AttributeId = memberMvAttr.Id,
            ReferenceValue = member1Mvo
        });
        groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = groupMvo,
            Attribute = memberMvAttr,
            AttributeId = memberMvAttr.Id,
            ReferenceValue = member2Mvo
        });

        // Create CSOs for the members
        var member1Cso = CreateTestCso(member1Mvo);
        member1Mvo.ConnectedSystemObjects.Add(member1Cso);
        var member2Cso = CreateTestCso(member2Mvo);
        member2Mvo.ConnectedSystemObjects.Add(member2Cso);
        var member3Cso = CreateTestCso(member3Mvo);
        member3Mvo.ConnectedSystemObjects.Add(member3Cso);

        // Create group CSO with actual members (includes extra member3 - drift!)
        var groupCso = CreateTestCso(groupMvo);
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = groupCso,
            Attribute = memberCsoAttr,
            AttributeId = memberCsoAttr.Id,
            ReferenceValue = member1Cso
        });
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = groupCso,
            Attribute = memberCsoAttr,
            AttributeId = memberCsoAttr.Id,
            ReferenceValue = member2Cso
        });
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = groupCso,
            Attribute = memberCsoAttr,
            AttributeId = memberCsoAttr.Id,
            ReferenceValue = member3Cso // Extra member - drift!
        });
        groupMvo.ConnectedSystemObjects.Add(groupCso);

        // Create export rule for the member attribute
        var memberMapping = new SyncRuleMapping
        {
            Id = 3000,
            TargetConnectedSystemAttribute = memberCsoAttr,
            TargetConnectedSystemAttributeId = memberCsoAttr.Id
        };
        memberMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 30000,
            MetaverseAttribute = memberMvAttr,
            MetaverseAttributeId = memberMvAttr.Id
        });

        var exportRule = new SyncRule
        {
            Id = 300,
            Name = "Export Group Members",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            EnforceState = true,
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObjectTypeId = TargetUserType.Id,
            ConnectedSystemObjectType = TargetUserType,
            MetaverseObjectTypeId = MvoUserType.Id,
            MetaverseObjectType = MvoUserType,
            AttributeFlowRules = new List<SyncRuleMapping> { memberMapping }
        };
        SyncRulesData.Add(exportRule);

        // Act
        var result = await Jim.DriftDetection.EvaluateDriftAsync(
            groupCso,
            groupMvo,
            new List<SyncRule> { exportRule },
            null);

        // Assert - Should detect drift due to extra member
        Assert.That(result.HasDrift, Is.True, "Should detect drift when CSO has extra member not in MVO");
        Assert.That(result.DriftedAttributes.Count, Is.EqualTo(1));
        Assert.That(result.DriftedAttributes[0].Attribute.Name, Is.EqualTo("member"));

        // The expected value should be a HashSet with 2 members
        var expectedSet = result.DriftedAttributes[0].ExpectedValue as HashSet<object>;
        Assert.That(expectedSet, Is.Not.Null);
        Assert.That(expectedSet!.Count, Is.EqualTo(2), "Expected 2 members in MVO");

        // The actual value should be a HashSet with 3 members
        var actualSet = result.DriftedAttributes[0].ActualValue as HashSet<object>;
        Assert.That(actualSet, Is.Not.Null);
        Assert.That(actualSet!.Count, Is.EqualTo(3), "Actual has 3 members in CSO (including drift)");
    }

    /// <summary>
    /// Tests that drift detection correctly handles multi-valued attributes when the CSO
    /// is missing values that should be present. This simulates an unauthorised removal.
    /// </summary>
    [Test]
    public async Task EvaluateDriftAsync_WithMultiValuedAttribute_MissingValueInCso_DetectsDriftAsync()
    {
        // Arrange - Create a multi-valued attribute
        var memberMvAttr = new MetaverseAttribute
        {
            Id = 6000,
            Name = "Static Members",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued
        };
        // Replace any existing attribute with same name
        MvoUserType.Attributes.RemoveAll(a => a.Name == "Static Members");
        MvoUserType.Attributes.Add(memberMvAttr);

        var memberCsoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 6001,
            Name = "member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            ConnectedSystemObjectType = TargetUserType
        };
        // Replace any existing attribute with same name
        TargetUserType.Attributes.RemoveAll(a => a.Name == "member");
        TargetUserType.Attributes.Add(memberCsoAttr);

        // Create referenced MVOs (group members)
        var member1Mvo = CreateTestMvo();
        var member2Mvo = CreateTestMvo();

        // Create the group MVO with expected members (member1 and member2)
        var groupMvo = CreateTestMvo();
        groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = groupMvo,
            Attribute = memberMvAttr,
            AttributeId = memberMvAttr.Id,
            ReferenceValue = member1Mvo
        });
        groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = groupMvo,
            Attribute = memberMvAttr,
            AttributeId = memberMvAttr.Id,
            ReferenceValue = member2Mvo
        });

        // Create CSOs for the members
        var member1Cso = CreateTestCso(member1Mvo);
        member1Mvo.ConnectedSystemObjects.Add(member1Cso);
        var member2Cso = CreateTestCso(member2Mvo);
        member2Mvo.ConnectedSystemObjects.Add(member2Cso);

        // Create group CSO with actual members - MISSING member2 (drift!)
        var groupCso = CreateTestCso(groupMvo);
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = groupCso,
            Attribute = memberCsoAttr,
            AttributeId = memberCsoAttr.Id,
            ReferenceValue = member1Cso
        });
        // Note: member2Cso is NOT added - simulates unauthorised removal
        groupMvo.ConnectedSystemObjects.Add(groupCso);

        // Create export rule for the member attribute
        var memberMapping = new SyncRuleMapping
        {
            Id = 4000,
            TargetConnectedSystemAttribute = memberCsoAttr,
            TargetConnectedSystemAttributeId = memberCsoAttr.Id
        };
        memberMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 40000,
            MetaverseAttribute = memberMvAttr,
            MetaverseAttributeId = memberMvAttr.Id
        });

        var exportRule = new SyncRule
        {
            Id = 400,
            Name = "Export Group Members",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            EnforceState = true,
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObjectTypeId = TargetUserType.Id,
            ConnectedSystemObjectType = TargetUserType,
            MetaverseObjectTypeId = MvoUserType.Id,
            MetaverseObjectType = MvoUserType,
            AttributeFlowRules = new List<SyncRuleMapping> { memberMapping }
        };
        SyncRulesData.Add(exportRule);

        // Act
        var result = await Jim.DriftDetection.EvaluateDriftAsync(
            groupCso,
            groupMvo,
            new List<SyncRule> { exportRule },
            null);

        // Assert - Should detect drift due to missing member
        Assert.That(result.HasDrift, Is.True, "Should detect drift when CSO is missing a member from MVO");
        Assert.That(result.DriftedAttributes.Count, Is.EqualTo(1));
        Assert.That(result.DriftedAttributes[0].Attribute.Name, Is.EqualTo("member"));

        // The expected value should be a HashSet with 2 members
        var expectedSet = result.DriftedAttributes[0].ExpectedValue as HashSet<object>;
        Assert.That(expectedSet, Is.Not.Null);
        Assert.That(expectedSet!.Count, Is.EqualTo(2), "Expected 2 members in MVO");

        // The actual value should be a HashSet with only 1 member
        var actualSet = result.DriftedAttributes[0].ActualValue as HashSet<object>;
        Assert.That(actualSet, Is.Not.Null);
        Assert.That(actualSet!.Count, Is.EqualTo(1), "Actual has only 1 member in CSO (missing one due to drift)");
    }

    #endregion
}

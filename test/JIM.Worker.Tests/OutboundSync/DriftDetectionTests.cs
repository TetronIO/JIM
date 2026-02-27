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

    #region EvaluateDrift Tests

    [Test]
    public void EvaluateDrift_WhenNoDrift_ReturnsEmptyResult()
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
        var result = Jim.DriftDetection.EvaluateDrift(
            cso,
            mvo,
            new List<SyncRule> { exportRule },
            null);

        // Assert
        Assert.That(result.HasDrift, Is.False);
        Assert.That(result.DriftedAttributes.Count, Is.EqualTo(0));
    }

    [Test]
    public void EvaluateDrift_WhenDriftDetected_ReturnsDriftedAttributes()
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
        var result = Jim.DriftDetection.EvaluateDrift(
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
    public void EvaluateDrift_WhenEnforceStateFalse_SkipsRule()
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
        var result = Jim.DriftDetection.EvaluateDrift(
            cso,
            mvo,
            new List<SyncRule> { exportRule },
            null);

        // Assert - no drift because EnforceState is false
        Assert.That(result.HasDrift, Is.False);
    }

    [Test]
    public void EvaluateDrift_WhenSystemIsContributor_DoesNotFlagAsDrift()
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
        var result = Jim.DriftDetection.EvaluateDrift(
            cso,
            mvo,
            new List<SyncRule> { exportRule },
            importMappingCache);

        // Assert - no drift because system is a legitimate contributor
        Assert.That(result.HasDrift, Is.False);
    }

    [Test]
    public void EvaluateDrift_WhenCsoNotJoined_ReturnsEmptyResult()
    {
        // Arrange
        var cso = CreateTestCso(null); // Not joined to any MVO

        var exportRule = CreateExportRule(enforceState: true);

        // Act
        var result = Jim.DriftDetection.EvaluateDrift(
            cso,
            null,
            new List<SyncRule> { exportRule },
            null);

        // Assert
        Assert.That(result.HasDrift, Is.False);
    }

    [Test]
    public void EvaluateDrift_WhenNoApplicableExportRules_ReturnsEmptyResult()
    {
        // Arrange
        var mvo = CreateTestMvo();
        var cso = CreateTestCso(mvo);
        mvo.ConnectedSystemObjects.Add(cso);

        // Export rule for a different connected system
        var exportRule = CreateExportRule(enforceState: true);
        exportRule.ConnectedSystemId = 999; // Different system

        // Act
        var result = Jim.DriftDetection.EvaluateDrift(
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
    public void EvaluateDrift_WhenMvoTypeIsNull_ReturnsEmptyResult()
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
        var result = Jim.DriftDetection.EvaluateDrift(
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
    public void EvaluateDrift_WhenMvoTypeIsLoaded_FindsApplicableExportRules()
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
        var result = Jim.DriftDetection.EvaluateDrift(
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
    public void EvaluateDrift_WhenMvoTypeIdDoesNotMatch_DoesNotFindRules()
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
        var result = Jim.DriftDetection.EvaluateDrift(
            cso,
            mvo,
            new List<SyncRule> { exportRule },
            null);

        // Assert - Should not find any applicable rules
        Assert.That(result.HasDrift, Is.False, "Export rule with different MVO type ID should not match");
        Assert.That(result.DriftedAttributes.Count, Is.EqualTo(0));
    }

    #endregion

    #region Cross-System Drift Detection Tests

    /// <summary>
    /// Reproduces the Scenario 8 bug: when a Source system imports group members to
    /// the MVO (member → Static Members) and a Target system exports them (Static Members → member),
    /// drift detection on the Target CSO should NOT treat the Target as a non-contributor
    /// and should NOT create spurious corrective exports.
    ///
    /// The bug: during Target confirming sync, drift detection compares the MVO's Static Members
    /// against the Target CSO's member references. If any Target CSO member references have
    /// null MetaverseObjectId (unresolved during confirming import), the actual set is smaller
    /// than the expected set, causing false drift detection and member removal.
    ///
    /// Root cause: the isContributor check only considers import rules. Since the Target system
    /// has no import rules for Static Members (only the Source does), drift detection proceeds
    /// even though the attribute is managed by JIM's own export pipeline.
    /// </summary>
    [Test]
    public void EvaluateDrift_CrossSystem_TargetExportsSourceImportedAttribute_ShouldNotDetectDriftWhenCsoMembersMatchMvoAsync()
    {
        // Arrange: Two-system topology (Source imports, Target exports)
        var sourceSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Source System");

        // Create multi-valued reference attributes for group membership
        var staticMembersMvAttr = new MetaverseAttribute
        {
            Id = 7000,
            Name = "Static Members",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued
        };
        MvoUserType.Attributes.Add(staticMembersMvAttr);

        var targetMemberCsoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 7001,
            Name = "member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            ConnectedSystemObjectType = TargetUserType
        };
        TargetUserType.Attributes.Add(targetMemberCsoAttr);

        // Create Source import rule: member → Static Members
        // (this is how members flow FROM Source TO Metaverse)
        var sourceImportMapping = new SyncRuleMapping
        {
            Id = 7100,
            TargetMetaverseAttribute = staticMembersMvAttr,
            TargetMetaverseAttributeId = staticMembersMvAttr.Id
        };
        sourceImportMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 71000,
            ConnectedSystemAttribute = ConnectedSystemObjectTypesData
                .Single(t => t.Name == "SOURCE_GROUP").Attributes
                .Single(a => a.Name == "MEMBER"),
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.MEMBER
        });

        var sourceImportRule = new SyncRule
        {
            Id = 710,
            Name = "Source Group Import",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemId = sourceSystem.Id,
            ConnectedSystem = sourceSystem,
            ConnectedSystemObjectTypeId = 2, // SOURCE_GROUP
            MetaverseObjectTypeId = MvoUserType.Id,
            MetaverseObjectType = MvoUserType,
            AttributeFlowRules = new List<SyncRuleMapping> { sourceImportMapping }
        };
        SyncRulesData.Add(sourceImportRule);

        // Create Target export rule: Static Members → member
        // (this is how members flow FROM Metaverse TO Target)
        var targetExportMapping = new SyncRuleMapping
        {
            Id = 7200,
            TargetConnectedSystemAttribute = targetMemberCsoAttr,
            TargetConnectedSystemAttributeId = targetMemberCsoAttr.Id
        };
        targetExportMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 72000,
            MetaverseAttribute = staticMembersMvAttr,
            MetaverseAttributeId = staticMembersMvAttr.Id
        });

        var targetExportRule = new SyncRule
        {
            Id = 720,
            Name = "Target Group Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            EnforceState = true,
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObjectTypeId = TargetUserType.Id,
            ConnectedSystemObjectType = TargetUserType,
            MetaverseObjectTypeId = MvoUserType.Id,
            MetaverseObjectType = MvoUserType,
            AttributeFlowRules = new List<SyncRuleMapping> { targetExportMapping }
        };
        SyncRulesData.Add(targetExportRule);

        // Create 5 member MVOs and their Target CSOs
        var memberMvos = Enumerable.Range(0, 5).Select(_ => CreateTestMvo()).ToList();
        var memberCsos = memberMvos.Select(mvo =>
        {
            var cso = CreateTestCso(mvo);
            mvo.ConnectedSystemObjects.Add(cso);
            return cso;
        }).ToList();

        // Create group MVO with all 5 members in Static Members
        var groupMvo = CreateTestMvo();
        foreach (var memberMvo in memberMvos)
        {
            groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                MetaverseObject = groupMvo,
                Attribute = staticMembersMvAttr,
                AttributeId = staticMembersMvAttr.Id,
                ReferenceValue = memberMvo
            });
        }

        // Create group CSO with all 5 member references (all properly resolved)
        var groupCso = CreateTestCso(groupMvo);
        foreach (var memberCso in memberCsos)
        {
            groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                ConnectedSystemObject = groupCso,
                Attribute = targetMemberCsoAttr,
                AttributeId = targetMemberCsoAttr.Id,
                ReferenceValue = memberCso
            });
        }
        groupMvo.ConnectedSystemObjects.Add(groupCso);

        // Build import mapping cache from ALL sync rules (includes Source import rule)
        var importMappingCache = DriftDetectionService.BuildImportMappingCache(SyncRulesData);

        // Verify cache has Source system's import mapping for Static Members
        Assert.That(importMappingCache.ContainsKey((sourceSystem.Id, staticMembersMvAttr.Id)), Is.True,
            "Source system should have import mapping for Static Members");
        // Verify Target system does NOT have import mapping for Static Members
        Assert.That(importMappingCache.ContainsKey((TargetSystem.Id, staticMembersMvAttr.Id)), Is.False,
            "Target system should NOT have import mapping for Static Members");

        // Act: Run drift detection on the Target group CSO
        var result = Jim.DriftDetection.EvaluateDrift(
            groupCso,
            groupMvo,
            new List<SyncRule> { targetExportRule },
            importMappingCache);

        // Assert: No drift should be detected because all member references match
        Assert.That(result.HasDrift, Is.False,
            "No drift should be detected when Target CSO members exactly match MVO Static Members. " +
            "The Target system exports this attribute (it doesn't import it), so drift detection " +
            "should either skip it or find no differences.");
    }

    /// <summary>
    /// Regression test for dotnet/efcore#33826: verifies that after the repository-level
    /// reference repair, drift detection does not create spurious REMOVE exports when all
    /// CSO member references have MetaverseObjectId properly populated.
    /// Previously this was a bug reproducer where 2 of 5 CSOs had null MetaverseObjectId,
    /// causing drift detection to see an incomplete "actual" set.
    /// </summary>
    [Test]
    public void EvaluateDrift_CrossSystem_RepairedTargetCsoReferences_NoDriftWhenAllResolvedAsync()
    {
        // Arrange: Same two-system topology
        var sourceSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Source System");

        var staticMembersMvAttr = new MetaverseAttribute
        {
            Id = 8000,
            Name = "Static Members",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued
        };
        MvoUserType.Attributes.RemoveAll(a => a.Name == "Static Members");
        MvoUserType.Attributes.Add(staticMembersMvAttr);

        var targetMemberCsoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 8001,
            Name = "member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            ConnectedSystemObjectType = TargetUserType
        };
        TargetUserType.Attributes.RemoveAll(a => a.Name == "member");
        TargetUserType.Attributes.Add(targetMemberCsoAttr);

        // Source import rule
        var sourceImportMapping = new SyncRuleMapping
        {
            Id = 8100,
            TargetMetaverseAttribute = staticMembersMvAttr,
            TargetMetaverseAttributeId = staticMembersMvAttr.Id
        };
        sourceImportMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 81000,
            ConnectedSystemAttribute = ConnectedSystemObjectTypesData
                .Single(t => t.Name == "SOURCE_GROUP").Attributes
                .Single(a => a.Name == "MEMBER"),
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.MEMBER
        });

        var sourceImportRule = new SyncRule
        {
            Id = 810,
            Name = "Source Group Import",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemId = sourceSystem.Id,
            ConnectedSystem = sourceSystem,
            ConnectedSystemObjectTypeId = 2,
            MetaverseObjectTypeId = MvoUserType.Id,
            MetaverseObjectType = MvoUserType,
            AttributeFlowRules = new List<SyncRuleMapping> { sourceImportMapping }
        };
        SyncRulesData.Add(sourceImportRule);

        // Target export rule
        var targetExportMapping = new SyncRuleMapping
        {
            Id = 8200,
            TargetConnectedSystemAttribute = targetMemberCsoAttr,
            TargetConnectedSystemAttributeId = targetMemberCsoAttr.Id
        };
        targetExportMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 82000,
            MetaverseAttribute = staticMembersMvAttr,
            MetaverseAttributeId = staticMembersMvAttr.Id
        });

        var targetExportRule = new SyncRule
        {
            Id = 820,
            Name = "Target Group Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            EnforceState = true,
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObjectTypeId = TargetUserType.Id,
            ConnectedSystemObjectType = TargetUserType,
            MetaverseObjectTypeId = MvoUserType.Id,
            MetaverseObjectType = MvoUserType,
            AttributeFlowRules = new List<SyncRuleMapping> { targetExportMapping }
        };
        SyncRulesData.Add(targetExportRule);

        // Create 5 member MVOs
        var memberMvos = Enumerable.Range(0, 5).Select(_ => CreateTestMvo()).ToList();

        // Create Target user CSOs: all 5 properly joined with MetaverseObjectId populated
        var memberCsos = new List<ConnectedSystemObject>();
        for (var i = 0; i < 5; i++)
        {
            var cso = CreateTestCso(memberMvos[i]);
            memberMvos[i].ConnectedSystemObjects.Add(cso);
            memberCsos.Add(cso);
        }

        // Create group MVO with all 5 members
        var groupMvo = CreateTestMvo();
        foreach (var memberMvo in memberMvos)
        {
            groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                MetaverseObject = groupMvo,
                Attribute = staticMembersMvAttr,
                AttributeId = staticMembersMvAttr.Id,
                ReferenceValue = memberMvo
            });
        }

        // Create group CSO with all 5 member references
        var groupCso = CreateTestCso(groupMvo);
        foreach (var memberCso in memberCsos)
        {
            groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                ConnectedSystemObject = groupCso,
                Attribute = targetMemberCsoAttr,
                AttributeId = targetMemberCsoAttr.Id,
                ReferenceValue = memberCso
            });
        }
        groupMvo.ConnectedSystemObjects.Add(groupCso);

        // Build import mapping cache (includes Source import rule only)
        var importMappingCache = DriftDetectionService.BuildImportMappingCache(SyncRulesData);

        // Act
        var result = Jim.DriftDetection.EvaluateDrift(
            groupCso,
            groupMvo,
            new List<SyncRule> { targetExportRule },
            importMappingCache);

        // Assert: After the repository repair ensures all MetaverseObjectIds are populated,
        // drift detection should find no differences between actual and expected member sets.
        Assert.That(result.HasDrift, Is.False,
            "No drift should be detected when all CSO member references have MetaverseObjectId " +
            "properly populated (post-repair state). Previously this test demonstrated a bug where " +
            "null MetaverseObjectId caused spurious REMOVE exports.");
        Assert.That(result.CorrectiveExports, Has.Count.EqualTo(0),
            "No corrective exports should be created when all references are properly resolved.");
    }

    /// <summary>
    /// Documents the known behaviour when ReferenceValue is completely null on CSO attribute
    /// values (i.e., the repository repair did not run or failed). Drift detection will see
    /// an incomplete "actual" set and create spurious REMOVE exports. This is the raw EF Core
    /// AsSplitQuery bug behaviour (dotnet/efcore#33826) before the repair.
    /// </summary>
    [Test]
    public void EvaluateDrift_CrossSystem_NullReferenceValueNavigation_CreatesSpuriousRemovalsAsync()
    {
        // Arrange: Same two-system topology as the repaired test above
        var sourceSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Source System");

        var staticMembersMvAttr = new MetaverseAttribute
        {
            Id = 9000,
            Name = "Static Members",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued
        };
        MvoUserType.Attributes.RemoveAll(a => a.Name == "Static Members");
        MvoUserType.Attributes.Add(staticMembersMvAttr);

        var targetMemberCsoAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 9001,
            Name = "member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            ConnectedSystemObjectType = TargetUserType
        };
        TargetUserType.Attributes.RemoveAll(a => a.Name == "member");
        TargetUserType.Attributes.Add(targetMemberCsoAttr);

        var sourceImportMapping = new SyncRuleMapping
        {
            Id = 9100,
            TargetMetaverseAttribute = staticMembersMvAttr,
            TargetMetaverseAttributeId = staticMembersMvAttr.Id
        };
        sourceImportMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 91000,
            ConnectedSystemAttribute = ConnectedSystemObjectTypesData
                .Single(t => t.Name == "SOURCE_GROUP").Attributes
                .Single(a => a.Name == "MEMBER"),
            ConnectedSystemAttributeId = (int)MockSourceSystemAttributeNames.MEMBER
        });
        var sourceImportRule = new SyncRule
        {
            Id = 910,
            Name = "Source Group Import",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemId = sourceSystem.Id,
            ConnectedSystem = sourceSystem,
            ConnectedSystemObjectTypeId = 2,
            MetaverseObjectTypeId = MvoUserType.Id,
            MetaverseObjectType = MvoUserType,
            AttributeFlowRules = new List<SyncRuleMapping> { sourceImportMapping }
        };
        SyncRulesData.Add(sourceImportRule);

        var targetExportMapping = new SyncRuleMapping
        {
            Id = 9200,
            TargetConnectedSystemAttribute = targetMemberCsoAttr,
            TargetConnectedSystemAttributeId = targetMemberCsoAttr.Id
        };
        targetExportMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 92000,
            MetaverseAttribute = staticMembersMvAttr,
            MetaverseAttributeId = staticMembersMvAttr.Id
        });
        var targetExportRule = new SyncRule
        {
            Id = 920,
            Name = "Target Group Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            EnforceState = true,
            ConnectedSystemId = TargetSystem.Id,
            ConnectedSystem = TargetSystem,
            ConnectedSystemObjectTypeId = TargetUserType.Id,
            ConnectedSystemObjectType = TargetUserType,
            MetaverseObjectTypeId = MvoUserType.Id,
            MetaverseObjectType = MvoUserType,
            AttributeFlowRules = new List<SyncRuleMapping> { targetExportMapping }
        };
        SyncRulesData.Add(targetExportRule);

        // Create 5 member MVOs
        var memberMvos = Enumerable.Range(0, 5).Select(_ => CreateTestMvo()).ToList();

        // Create Target user CSOs: 3 properly joined, 2 with null ReferenceValue
        // (simulating AsSplitQuery failing to materialise the navigation)
        var memberCsos = new List<ConnectedSystemObject>();
        for (var i = 0; i < 5; i++)
        {
            if (i < 3)
            {
                var cso = CreateTestCso(memberMvos[i]);
                memberMvos[i].ConnectedSystemObjects.Add(cso);
                memberCsos.Add(cso);
            }
            else
            {
                // ReferenceValue will be null on the attribute value pointing to this CSO.
                // This simulates the AsSplitQuery materialisation failure.
                memberCsos.Add(null!); // placeholder — we'll set ReferenceValue = null directly below
            }
        }

        // Create group MVO with all 5 members
        var groupMvo = CreateTestMvo();
        foreach (var memberMvo in memberMvos)
        {
            groupMvo.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                MetaverseObject = groupMvo,
                Attribute = staticMembersMvAttr,
                AttributeId = staticMembersMvAttr.Id,
                ReferenceValue = memberMvo,
                ReferenceValueId = memberMvo.Id
            });
        }

        // Create group CSO: 3 member refs with ReferenceValue populated, 2 with null
        var groupCso = CreateTestCso(groupMvo);
        for (var i = 0; i < 5; i++)
        {
            if (i < 3)
            {
                groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(),
                    ConnectedSystemObject = groupCso,
                    Attribute = targetMemberCsoAttr,
                    AttributeId = targetMemberCsoAttr.Id,
                    ReferenceValue = memberCsos[i],
                    ReferenceValueId = memberCsos[i].Id
                });
            }
            else
            {
                // Simulate AsSplitQuery failure: ReferenceValueId is set but ReferenceValue is null
                groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                {
                    Id = Guid.NewGuid(),
                    ConnectedSystemObject = groupCso,
                    Attribute = targetMemberCsoAttr,
                    AttributeId = targetMemberCsoAttr.Id,
                    ReferenceValue = null,
                    ReferenceValueId = Guid.NewGuid() // FK is set but navigation is null
                });
            }
        }
        groupMvo.ConnectedSystemObjects.Add(groupCso);

        var importMappingCache = DriftDetectionService.BuildImportMappingCache(SyncRulesData);

        // Act
        var result = Jim.DriftDetection.EvaluateDrift(
            groupCso,
            groupMvo,
            new List<SyncRule> { targetExportRule },
            importMappingCache);

        // Assert: Without the repository repair, drift IS detected because the actual set
        // is incomplete (3 instead of 5). This documents the known EF Core bug behaviour.
        // The 2 members with null ReferenceValue are missing from the actual set, so drift
        // detection sees them as "expected but not present" and creates corrective ADD exports.
        Assert.That(result.HasDrift, Is.True,
            "Without the repository repair, drift is incorrectly detected because 2 of 5 " +
            "CSO member references have null ReferenceValue navigation.");

        var addChanges = result.CorrectiveExports
            .SelectMany(pe => pe.AttributeValueChanges)
            .Where(c => c.ChangeType == PendingExportAttributeChangeType.Add)
            .ToList();
        Assert.That(addChanges, Has.Count.EqualTo(2),
            "2 spurious ADD changes created for the members with null ReferenceValue " +
            "(drift detection sees them as missing from the actual set).");
    }

    #endregion

    #region Multi-Valued Attribute Drift Tests

    /// <summary>
    /// Tests that drift detection correctly handles multi-valued attributes (like group membership).
    /// When the CSO has extra values not in the MVO, drift should be detected and corrective
    /// exports should remove the extra values.
    /// </summary>
    [Test]
    public void EvaluateDrift_WithMultiValuedAttribute_ExtraValueInCso_DetectsDrift()
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
        var result = Jim.DriftDetection.EvaluateDrift(
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
    public void EvaluateDrift_WithMultiValuedAttribute_MissingValueInCso_DetectsDrift()
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
        var result = Jim.DriftDetection.EvaluateDrift(
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

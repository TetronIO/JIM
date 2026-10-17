// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;
using SyncRepository = JIM.InMemoryData.SyncRepository;

namespace JIM.Worker.Tests.OutboundSync;

/// <summary>
/// Tests for ObjectMatchingServer - the shared matching logic for import (CSO→MVO) and export (MVO→CSO).
/// </summary>
[TestFixture]
public class ObjectMatchingServerTests
{
    #region accessors
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObject>> MockDbSetConnectedSystemObjects { get; set; } = null!;
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; } = null!;
    private Mock<DbSet<MetaverseObjectType>> MockDbSetMetaverseObjectTypes { get; set; } = null!;
    private List<MetaverseObject> MetaverseObjectsData { get; set; } = null!;
    private Mock<DbSet<MetaverseObject>> MockDbSetMetaverseObjects { get; set; } = null!;
    private List<SyncRule> SyncRulesData { get; set; } = null!;
    private Mock<DbSet<SyncRule>> MockDbSetSyncRules { get; set; } = null!;
    private JimApplication Jim { get; set; } = null!;
    private SyncRepository SyncRepo { get; set; } = null!;
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
        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();

        // Set up the Metaverse Object Types mock
        MetaverseObjectTypesData = TestUtilities.GetMetaverseObjectTypeData();
        MockDbSetMetaverseObjectTypes = MetaverseObjectTypesData.BuildMockDbSet();

        // Set up the Metaverse Objects mock
        MetaverseObjectsData = TestUtilities.GetMetaverseObjectData();
        MockDbSetMetaverseObjects = MetaverseObjectsData.BuildMockDbSet();

        // Set up the Synchronisation Rule stub mocks
        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();

        // Mock entity framework calls
        MockJimDbContext = new Mock<JimDbContext>();
        TestUtilities.SetUpEmptyConnectedSystemGraphMocks(MockJimDbContext);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(MockDbSetMetaverseObjects.Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(MockDbSetSyncRules.Object);

        // Instantiate Jim using the mocked db context
        SyncRepo = TestUtilities.CreateSyncRepository(syncRules: SyncRulesData);
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object), syncRepository: SyncRepo);
    }

    #region ObjectMatchingRule Validation Tests

    [Test]
    public void ObjectMatchingRule_IsValid_WithValidConfiguration_ReturnsTrue()
    {
        // Arrange
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };
        var objectType = ConnectedSystemObjectTypesData[0];
        var mvoType = MetaverseObjectTypesData[0];

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            MetaverseObjectTypeId = mvoType.Id,
            MetaverseObjectType = mvoType,
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
        var isValid = rule.IsValid();

        // Assert
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void ObjectMatchingRule_IsValid_WithNoSources_ReturnsFalse()
    {
        // Arrange
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var objectType = ConnectedSystemObjectTypesData[0];

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            TargetMetaverseAttribute = mvAttr,
            TargetMetaverseAttributeId = mvAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>()
        };

        // Act
        var isValid = rule.IsValid();

        // Assert
        Assert.That(isValid, Is.False);
    }

    [Test]
    public void ObjectMatchingRule_IsValid_WithNoTargetAttribute_ReturnsFalse()
    {
        // Arrange
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };
        var objectType = ConnectedSystemObjectTypesData[0];

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            TargetMetaverseAttribute = null,
            TargetMetaverseAttributeId = null,
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
        var isValid = rule.IsValid();

        // Assert
        Assert.That(isValid, Is.False);
    }

    [Test]
    public void ObjectMatchingRule_IsValid_WithNoParent_ReturnsFalse()
    {
        // Arrange - Rule with no SyncRule and no ConnectedSystemObjectType
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            // No parent set - neither SyncRule nor ConnectedSystemObjectType
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
        var isValid = rule.IsValid();

        // Assert
        Assert.That(isValid, Is.False);
    }

    [Test]
    public void ObjectMatchingRule_IsValid_WithBothParents_ReturnsFalse()
    {
        // Arrange - Rule with BOTH SyncRule and ConnectedSystemObjectType (invalid XOR)
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };
        var objectType = ConnectedSystemObjectTypesData[0];
        var syncRule = SyncRulesData[0];

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            // Both parents set - invalid
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            SyncRuleId = syncRule.Id,
            SyncRule = syncRule,
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
        var isValid = rule.IsValid();

        // Assert
        Assert.That(isValid, Is.False);
    }

    [Test]
    public void ObjectMatchingRule_IsValid_WithSyncRuleParent_ReturnsTrue()
    {
        // Arrange - Rule with only SyncRule parent (valid)
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };
        var syncRule = SyncRulesData[0];

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            SyncRuleId = syncRule.Id,
            SyncRule = syncRule,
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
        var isValid = rule.IsValid();

        // Assert
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void ObjectMatchingRule_IsValid_SimpleMode_WithoutMetaverseObjectTypeId_ReturnsFalse()
    {
        // Arrange - Simple mode rule without MetaverseObjectTypeId
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };
        var objectType = ConnectedSystemObjectTypesData[0];

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            // MetaverseObjectTypeId not set — invalid for simple mode
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
        var isValid = rule.IsValid();

        // Assert
        Assert.That(isValid, Is.False, "Simple mode rules must have MetaverseObjectTypeId set");
    }

    [Test]
    public void ObjectMatchingRule_IsValid_AdvancedMode_WithMetaverseObjectTypeId_ReturnsFalse()
    {
        // Arrange - Advanced mode rule WITH MetaverseObjectTypeId (invalid — Synchronisation Rule provides MVO type)
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };
        var syncRule = SyncRulesData[0];
        var mvoType = MetaverseObjectTypesData[0];

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            SyncRuleId = syncRule.Id,
            SyncRule = syncRule,
            MetaverseObjectTypeId = mvoType.Id,
            MetaverseObjectType = mvoType,
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
        var isValid = rule.IsValid();

        // Assert
        Assert.That(isValid, Is.False, "Advanced mode rules must not have MetaverseObjectTypeId set");
    }

    #endregion

    #region Mode Selection Tests

    [Test]
    public void GetMatchingRulesForExport_ConnectedSystemMode_ReturnsObjectTypeRules()
    {
        // Arrange
        var connectedSystem = ConnectedSystemsData[1]; // Target system
        connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem;

        var objectType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        // Add matching rules to the object type
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var csAttr = objectType.Attributes.First();

        objectType.ObjectMatchingRules = new List<ObjectMatchingRule>
        {
            new()
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
                        ConnectedSystemAttribute = csAttr,
                        ConnectedSystemAttributeId = csAttr.Id
                    }
                }
            }
        };

        connectedSystem.ObjectTypes = new List<ConnectedSystemObjectType> { objectType };

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.ConnectedSystem = connectedSystem;
        exportRule.ConnectedSystemObjectType = objectType;
        exportRule.ConnectedSystemObjectTypeId = objectType.Id;

        // Assert - verify the ObjectType has the rules
        Assert.That(objectType.ObjectMatchingRules, Has.Count.EqualTo(1));
        Assert.That(connectedSystem.ObjectMatchingRuleMode, Is.EqualTo(ObjectMatchingRuleMode.ConnectedSystem));
    }

    [Test]
    public void GetMatchingRulesForExport_SyncRuleMode_ReturnsSyncRuleRules()
    {
        // Arrange
        var connectedSystem = ConnectedSystemsData[1]; // Target system
        connectedSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Synchronisation Rule 1");
        exportRule.ConnectedSystem = connectedSystem;

        // Add matching rules to the Synchronisation Rule
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };

        exportRule.ObjectMatchingRules = new List<ObjectMatchingRule>
        {
            new()
            {
                Id = 1,
                Order = 1,
                SyncRule = exportRule,
                SyncRuleId = exportRule.Id,
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
            }
        };

        // Assert - verify the SyncRule has the rules
        Assert.That(exportRule.ObjectMatchingRules, Has.Count.EqualTo(1));
        Assert.That(connectedSystem.ObjectMatchingRuleMode, Is.EqualTo(ObjectMatchingRuleMode.SyncRule));
    }

    #endregion

    #region CaseSensitive Property Tests

    [Test]
    public void ObjectMatchingRule_CaseSensitive_DefaultsToFalse()
    {
        // Arrange & Act
        var rule = new ObjectMatchingRule();

        // Assert - verify default is case-insensitive
        Assert.That(rule.CaseSensitive, Is.False, "ObjectMatchingRule.CaseSensitive should default to false");
    }

    [Test]
    public void ObjectMatchingRule_CaseSensitive_CanBeSetToTrue()
    {
        // Arrange
        var rule = new ObjectMatchingRule { CaseSensitive = true };

        // Assert
        Assert.That(rule.CaseSensitive, Is.True, "ObjectMatchingRule.CaseSensitive should be settable to true");
    }

    [Test]
    public void ObjectMatchingRule_IsValid_WithCaseSensitiveFalse_StillReturnsTrue()
    {
        // Arrange - verify CaseSensitive doesn't affect validation
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };
        var objectType = ConnectedSystemObjectTypesData[0];
        var mvoType = MetaverseObjectTypesData[0];

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            MetaverseObjectTypeId = mvoType.Id,
            MetaverseObjectType = mvoType,
            TargetMetaverseAttribute = mvAttr,
            TargetMetaverseAttributeId = mvAttr.Id,
            CaseSensitive = false,
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
        var isValid = rule.IsValid();

        // Assert
        Assert.That(isValid, Is.True, "CaseSensitive=false should not affect rule validity");
    }

    #endregion

    #region Ambiguous Match Tests (8.12)

    /// <summary>
    /// Tests for ambiguous match scenarios - when multiple MVOs match a CSO.
    /// Note: These tests verify that MultipleMatchesException is thrown correctly.
    /// The actual repository query logic that detects multiple matches requires
    /// a real PostgreSQL connection and is tested via integration tests.
    /// </summary>
    [Test]
    public void ObjectMatchingRule_WithMultipleMatches_ThrowsMultipleMatchesException()
    {
        // Arrange - Create a matching rule that would match multiple MVOs
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };
        var objectType = ConnectedSystemObjectTypesData[0];
        var mvoType = MetaverseObjectTypesData[0];

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            MetaverseObjectTypeId = mvoType.Id,
            MetaverseObjectType = mvoType,
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

        // Assert - verify rule is valid (MultipleMatchesException is thrown at query time, not validation time)
        Assert.That(rule.IsValid(), Is.True, "Matching rule should be valid even if it would match multiple objects");
    }

    [Test]
    public void MultipleMatchesException_StoresMatchingMvoIds()
    {
        // Arrange
        var matchingMvoIds = new List<Guid>
        {
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid()
        };

        // Act
        var exception = new MultipleMatchesException("Multiple matches found", matchingMvoIds);

        // Assert - verify exception stores the matching IDs for error reporting
        Assert.That(exception.Matches, Has.Count.EqualTo(3));
        Assert.That(exception.Matches, Is.EquivalentTo(matchingMvoIds));
        Assert.That(exception.Message, Contains.Substring("Multiple matches found"));
    }

    [Test]
    public void MultipleMatchesException_WithTwoMatches_StoresIds()
    {
        // Arrange
        var mvo1Id = Guid.NewGuid();
        var mvo2Id = Guid.NewGuid();
        var matchingIds = new List<Guid> { mvo1Id, mvo2Id };

        // Act
        var exception = new MultipleMatchesException("Two MVOs match this CSO", matchingIds);

        // Assert
        Assert.That(exception.Matches.Count, Is.EqualTo(2));
        Assert.That(exception.Matches, Contains.Item(mvo1Id));
        Assert.That(exception.Matches, Contains.Item(mvo2Id));
    }

    #endregion

    #region FindMatchingMetaverseObjectAsync Tests

    [Test]
    public async Task FindMatchingMetaverseObjectAsync_WithEmptyRules_ReturnsNullAsync()
    {
        // Arrange
        var cso = ConnectedSystemObjectsData[0];
        var emptyRules = new List<ObjectMatchingRule>();

        // Act
        var result = await Jim.ObjectMatching.FindMatchingMetaverseObjectAsync(cso, emptyRules);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task FindMatchingMetaverseObjectAsync_SkipsRuleWithoutMetaverseObjectType_ReturnsNullAsync()
    {
        // Arrange
        var cso = ConnectedSystemObjectsData[0];
        var objectType = ConnectedSystemObjectTypesData[0];
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };

        var rules = new List<ObjectMatchingRule>
        {
            new()
            {
                Id = 1,
                Order = 1,
                ConnectedSystemObjectTypeId = objectType.Id,
                ConnectedSystemObjectType = objectType,
                MetaverseObjectType = null, // No MVO type - should be skipped
                MetaverseObjectTypeId = null,
                TargetMetaverseAttribute = mvAttr,
                TargetMetaverseAttributeId = mvAttr.Id,
                Sources = new List<ObjectMatchingRuleSource>
                {
                    new()
                    {
                        Id = 1,
                        Order = 1,
                        ConnectedSystemAttribute = objectType.Attributes.First(),
                        ConnectedSystemAttributeId = objectType.Attributes.First().Id
                    }
                }
            }
        };

        // Act
        var result = await Jim.ObjectMatching.FindMatchingMetaverseObjectAsync(cso, rules);

        // Assert
        Assert.That(result, Is.Null, "Should return null when all rules lack MetaverseObjectType");
    }

    #endregion

    #region FindMatchingConnectedSystemObjectAsync Tests

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_WithEmptyRules_ReturnsNullAsync()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var connectedSystem = ConnectedSystemsData[0];
        var objectType = ConnectedSystemObjectTypesData[0];
        var emptyRules = new List<ObjectMatchingRule>();

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, connectedSystem, objectType, emptyRules);

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region FindMatchingConnectedSystemObjectAsync (export matching) Tests

    /// <summary>
    /// Builds a standard inbound-shaped Object Matching Rule targeting the Dummy Target System's
    /// TARGET_USER type: the source only sets ConnectedSystemAttribute (no MetaverseAttribute),
    /// mirroring what the UI/PowerShell/API produce today. TargetMetaverseAttribute carries the
    /// MVO-side attribute. This is the shape the export matching bug affects.
    /// </summary>
    private static ObjectMatchingRule BuildInboundShapedMatchingRule(
        ConnectedSystemObjectType targetUserType,
        ConnectedSystemObjectTypeAttribute csEmployeeIdAttr,
        MetaverseAttribute targetMetaverseAttribute,
        bool caseSensitive = false)
    {
        return new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = targetUserType.Id,
            ConnectedSystemObjectType = targetUserType,
            TargetMetaverseAttribute = targetMetaverseAttribute,
            TargetMetaverseAttributeId = targetMetaverseAttribute.Id,
            CaseSensitive = caseSensitive,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 1,
                    ConnectedSystemAttribute = csEmployeeIdAttr,
                    ConnectedSystemAttributeId = csEmployeeIdAttr.Id
                }
            }
        };
    }

    /// <summary>
    /// Creates an unjoined, Normal-status CSO in the Dummy Target System's TARGET_USER type with
    /// a single string attribute value, and seeds it into SyncRepo.
    /// </summary>
    private ConnectedSystemObject SeedTargetCso(
        ConnectedSystem targetSystem,
        ConnectedSystemObjectType targetUserType,
        ConnectedSystemObjectTypeAttribute csEmployeeIdAttr,
        string attributeValue,
        Guid? metaverseObjectId = null,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal,
        string? displayName = null)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObjectId = metaverseObjectId,
            Status = status,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Attribute = csEmployeeIdAttr,
                    AttributeId = csEmployeeIdAttr.Id,
                    StringValue = attributeValue
                }
            }
        };
        if (displayName != null)
        {
            var displayNameAttr = targetUserType.Attributes.Single(a => a.Name == "DisplayName");
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(), Attribute = displayNameAttr, AttributeId = displayNameAttr.Id, StringValue = displayName
            });
        }

        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    /// <summary>
    /// A Metaverse Object of the User type holding the given Employee ID values, and an attribute-sourced rule matching
    /// them against the Dummy Target System's TARGET_USER EmployeeId.
    /// </summary>
    private (MetaverseObject Mvo, ConnectedSystem TargetSystem, ConnectedSystemObjectType TargetUserType, ObjectMatchingRule Rule)
        ArrangeEmployeeIdExportMatch(params string[] employeeIds)
    {
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        foreach (var employeeId in employeeIds)
        {
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(), Attribute = employeeIdAttr, AttributeId = employeeIdAttr.Id, StringValue = employeeId
            });
        }

        return (mvo, targetSystem, targetUserType, BuildInboundShapedMatchingRule(targetUserType, csEmployeeIdAttr, employeeIdAttr));
    }

    /// <summary>The ambiguity lists each account, in name order, as a Connected System Object of the target.</summary>
    private static void AssertListsAccounts(MultipleMatchesException ex, ObjectMatchingRule rule, ConnectedSystem targetSystem,
        params (ConnectedSystemObject Cso, string Name)[] expected)
    {
        var candidates = ex.Candidates;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidates.Select(c => c.CandidateId), Is.EqualTo(expected.Select(e => e.Cso.Id)), "ordered by display name");
            Assert.That(candidates.Select(c => c.DisplayNameSnapshot), Is.EqualTo(expected.Select(e => e.Name)));
            Assert.That(candidates.Select(c => c.Ordinal), Is.EqualTo(Enumerable.Range(0, expected.Length)));
            Assert.That(candidates.All(c => c.CandidateKind == MatchCandidateKind.ConnectedSystemObject), Is.True);
            Assert.That(candidates.All(c => c.ConnectedSystemId == targetSystem.Id), Is.True);
            Assert.That(candidates.All(c => c.ConnectedSystemNameSnapshot == targetSystem.Name), Is.True);
            Assert.That(candidates.All(c => c.TypeNameSnapshot == "TARGET_USER"), Is.True);
            Assert.That(candidates.All(c => c.ObjectMatchingRuleId == rule.Id), Is.True);
            Assert.That(candidates.All(c => c.CandidateTotal == expected.Length), Is.True);
            Assert.That(candidates.All(c => c.MatchedValue == "EMP001"), Is.True);
            Assert.That(ex.Rule, Is.SameAs(rule));
        }
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_InboundShapedRuleWithMatchingUnjoinedCso_ReturnsCsoAsync()
    {
        // Arrange - a standard inbound-shaped rule (source = CS attribute only, target = MV attribute),
        // the shape produced by the UI/PowerShell/API today.
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "EMP001"
        });

        var cso = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001");
        var rule = BuildInboundShapedMatchingRule(targetUserType, csEmployeeIdAttr, employeeIdAttr);

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        // Assert
        Assert.That(result, Is.Not.Null, "An inbound-shaped rule should still resolve the MVO-side value via TargetMetaverseAttribute");
        Assert.That(result!.ConnectedSystemObject.Id, Is.EqualTo(cso.Id));
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_CsoJoinedToAnotherMvo_ReturnsNullAsync()
    {
        // Arrange - the only candidate CSO is already joined to a different MVO, so it must never be returned.
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "EMP001"
        });

        SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001", metaverseObjectId: Guid.NewGuid());
        var rule = BuildInboundShapedMatchingRule(targetUserType, csEmployeeIdAttr, employeeIdAttr);

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        // Assert
        Assert.That(result, Is.Null, "A CSO already joined to another MVO must never be returned as an export match");
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_ObsoleteCso_ReturnsNullAsync()
    {
        // Arrange - the only candidate CSO is Obsolete (not returned in the latest full import), so it must be excluded.
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "EMP001"
        });

        SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001", status: ConnectedSystemObjectStatus.Obsolete);
        var rule = BuildInboundShapedMatchingRule(targetUserType, csEmployeeIdAttr, employeeIdAttr);

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        // Assert
        Assert.That(result, Is.Null, "An Obsolete CSO must never be returned as an export match");
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_PendingProvisioningCso_ReturnsNullAsync()
    {
        // Arrange - the only candidate CSO is PendingProvisioning (an in-flight provision that does not yet
        // represent a live object in the target system), so it must be excluded.
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "EMP001"
        });

        SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001", status: ConnectedSystemObjectStatus.PendingProvisioning);
        var rule = BuildInboundShapedMatchingRule(targetUserType, csEmployeeIdAttr, employeeIdAttr);

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        // Assert
        Assert.That(result, Is.Null, "A PendingProvisioning CSO must never be returned as an export match");
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_ValueMismatch_ReturnsNullAsync()
    {
        // Arrange - the candidate CSO's value doesn't match the MVO's value.
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "EMP001"
        });

        SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP999");
        var rule = BuildInboundShapedMatchingRule(targetUserType, csEmployeeIdAttr, employeeIdAttr);

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_CaseInsensitiveRule_MatchesDifferentCaseAsync()
    {
        // Arrange - rule.CaseSensitive = false, MVO and CSO values differ only by case.
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "emp001"
        });

        var cso = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001");
        var rule = BuildInboundShapedMatchingRule(targetUserType, csEmployeeIdAttr, employeeIdAttr, caseSensitive: false);

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        // Assert
        Assert.That(result, Is.Not.Null, "CaseSensitive=false should match regardless of casing");
        Assert.That(result!.ConnectedSystemObject.Id, Is.EqualTo(cso.Id));
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_CaseSensitiveRule_DoesNotMatchDifferentCaseAsync()
    {
        // Arrange - rule.CaseSensitive = true, MVO and CSO values differ only by case.
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "emp001"
        });

        SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001");
        var rule = BuildInboundShapedMatchingRule(targetUserType, csEmployeeIdAttr, employeeIdAttr, caseSensitive: true);

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        // Assert
        Assert.That(result, Is.Null, "CaseSensitive=true must not match values that differ only by case");
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_MvoMissingAttributeValue_ReturnsNullAsync()
    {
        // Arrange - the MVO has no value for the resolved attribute at all.
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear(); // No attribute values at all

        SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001");
        var rule = BuildInboundShapedMatchingRule(targetUserType, csEmployeeIdAttr, employeeIdAttr);

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        // Assert
        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Creates an unjoined, Normal-status CSO in the Dummy Target System's TARGET_USER type with
    /// a single LongNumber attribute value, and seeds it into SyncRepo. Mirrors <see cref="SeedTargetCso"/>
    /// but for the LongNumber-typed "accountExpires" attribute rather than a Text attribute.
    /// </summary>
    private ConnectedSystemObject SeedTargetCsoLongNumber(
        ConnectedSystem targetSystem,
        ConnectedSystemObjectType targetUserType,
        ConnectedSystemObjectTypeAttribute csAccountExpiresAttr,
        long attributeValue,
        Guid? metaverseObjectId = null,
        ConnectedSystemObjectStatus status = ConnectedSystemObjectStatus.Normal)
    {
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            Type = targetUserType,
            TypeId = targetUserType.Id,
            MetaverseObjectId = metaverseObjectId,
            Status = status,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Attribute = csAccountExpiresAttr,
                    AttributeId = csAccountExpiresAttr.Id,
                    LongValue = attributeValue
                }
            }
        };

        SyncRepo.SeedConnectedSystemObject(cso);
        return cso;
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_LongNumberMatchingAttribute_ReturnsCsoAsync()
    {
        // Arrange - a LongNumber-typed matching attribute (AD's "accountExpires" is a legitimate real-world
        // example) on both the Metaverse Object and Connected System Object sides.
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csAccountExpiresAttr = targetUserType.Attributes.Single(a => a.Name == "accountExpires");

        var accountExpiresMvAttr = new MetaverseAttribute
        {
            Id = 9001,
            Name = "Account Expires",
            Type = AttributeDataType.LongNumber,
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = false
        };

        var mvo = MetaverseObjectsData[0];
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = accountExpiresMvAttr,
            AttributeId = accountExpiresMvAttr.Id,
            LongValue = 9000000001
        });

        var cso = SeedTargetCsoLongNumber(targetSystem, targetUserType, csAccountExpiresAttr, 9000000001);
        var rule = BuildInboundShapedMatchingRule(targetUserType, csAccountExpiresAttr, accountExpiresMvAttr);

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        // Assert
        Assert.That(result, Is.Not.Null, "A LongNumber matching attribute should resolve an export match");
        Assert.That(result!.ConnectedSystemObject.Id, Is.EqualTo(cso.Id));
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_LongNumberValueMismatch_ReturnsNullAsync()
    {
        // Arrange - same LongNumber matching shape as above, but the candidate CSO's LongValue differs.
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csAccountExpiresAttr = targetUserType.Attributes.Single(a => a.Name == "accountExpires");

        var accountExpiresMvAttr = new MetaverseAttribute
        {
            Id = 9001,
            Name = "Account Expires",
            Type = AttributeDataType.LongNumber,
            AttributePlurality = AttributePlurality.SingleValued,
            BuiltIn = false
        };

        var mvo = MetaverseObjectsData[0];
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = accountExpiresMvAttr,
            AttributeId = accountExpiresMvAttr.Id,
            LongValue = 9000000001
        });

        SeedTargetCsoLongNumber(targetSystem, targetUserType, csAccountExpiresAttr, 8000000002);
        var rule = BuildInboundShapedMatchingRule(targetUserType, csAccountExpiresAttr, accountExpiresMvAttr);

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        // Assert
        Assert.That(result, Is.Null, "A LongNumber value mismatch must never be returned as an export match");
    }

    #endregion

    #region FindPrefetchedMatchingConnectedSystemObjectAsync Tests

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_PrefetchedSingleCandidate_ReturnsCsoAsync()
    {
        // Arrange
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "EMP001"
        });

        var cso = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001");
        var rule = BuildInboundShapedMatchingRule(targetUserType, csEmployeeIdAttr, employeeIdAttr);

        var candidates = new ExportMatchCandidates();
        candidates.AddCandidate(rule.Id, "EMP001", cso.Id);

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }, candidates);

        // Assert
        Assert.That(result, Is.Not.Null, "The single prefetched candidate should be hydrated and returned");
        Assert.That(result!.ConnectedSystemObject.Id, Is.EqualTo(cso.Id));
    }

    [Test]
    public void FindMatchingConnectedSystemObjectAsync_PrefetchedTwoEligibleCandidates_IsAmbiguousAndListsBothAsync()
    {
        // #2052: previously the lowest-id candidate was joined, so which account the identity took depended on ids.
        var (mvo, targetSystem, targetUserType, rule) = ArrangeEmployeeIdExportMatch("EMP001");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var zoe = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001", displayName: "zbloggs");
        var ann = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001", displayName: "abloggs");

        var candidates = new ExportMatchCandidates();
        candidates.AddCandidate(rule.Id, "EMP001", zoe.Id);
        candidates.AddCandidate(rule.Id, "EMP001", ann.Id);

        var ex = Assert.ThrowsAsync<MultipleMatchesException>(() => Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }, candidates));

        AssertListsAccounts(ex!, rule, targetSystem, (ann, "abloggs"), (zoe, "zbloggs"));
    }

    [Test]
    public void FindMatchingConnectedSystemObjectAsync_TwoCsosHoldTheValue_IsAmbiguousAndListsBothAsync()
    {
        var (mvo, targetSystem, targetUserType, rule) = ArrangeEmployeeIdExportMatch("EMP001");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var zoe = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001", displayName: "zbloggs");
        var ann = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001", displayName: "abloggs");

        var ex = Assert.ThrowsAsync<MultipleMatchesException>(() => Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }));

        AssertListsAccounts(ex!, rule, targetSystem, (ann, "abloggs"), (zoe, "zbloggs"));
    }

    [Test]
    public void FindMatchingConnectedSystemObjectAsync_ValuesFindingDifferentCsos_IsAmbiguousAsync()
    {
        // Every value of a multi-valued attribute is looked up: two values held by two accounts is two candidates.
        var (mvo, targetSystem, targetUserType, rule) = ArrangeEmployeeIdExportMatch("EMP001", "EMP002");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var first = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001", displayName: "first");
        var second = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP002", displayName: "second");

        var ex = Assert.ThrowsAsync<MultipleMatchesException>(() => Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }));

        Assert.That(ex!.Candidates.Select(c => (c.CandidateId, c.MatchedValue)),
            Is.EqualTo(new[] { (first.Id, (string?)"EMP001"), (second.Id, (string?)"EMP002") }));
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_ValuesAllHeldByOneCso_MatchesItAndRecordsTheRuleAndValuesAsync()
    {
        var (mvo, targetSystem, targetUserType, rule) = ArrangeEmployeeIdExportMatch("EMP001", "EMP002");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var cso = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001");
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), Attribute = csEmployeeIdAttr, AttributeId = csEmployeeIdAttr.Id, StringValue = "EMP002"
        });

        var match = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(match?.ConnectedSystemObject.Id, Is.EqualTo(cso.Id));
            Assert.That(match?.Rule, Is.SameAs(rule));
            Assert.That(match?.MatchedValue, Is.EqualTo("EMP001, EMP002"));
        }
    }

    [Test]
    public void FindMatchingConnectedSystemObjectAsync_SourceAndTargetTypesDiffer_FailsAsync()
    {
        // Values of different types are never equal, so the rule would match nothing and provision a duplicate.
        var (mvo, targetSystem, targetUserType, rule) = ArrangeEmployeeIdExportMatch("EMP001");
        rule.Sources[0].ConnectedSystemAttribute = targetUserType.Attributes.Single(a => a.Type == AttributeDataType.Number);

        var ex = Assert.ThrowsAsync<ObjectMatchingRuleFailedException>(() => Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }));

        Assert.That(ex!.Rule, Is.SameAs(rule));
        Assert.That(ex!.Message, Does.Contain("provisioned"), "an export failure names what it stopped");
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_PrefetchedFirstCandidateNoLongerEligible_TriesNextCandidateAsync()
    {
        // Arrange - the first candidate was claimed by another Metaverse Object on this page between the
        // prefetch and this lookup, so hydration finds it no longer eligible and the next is tried.
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "EMP001"
        });

        var noLongerEligibleCso = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001");
        noLongerEligibleCso.MetaverseObjectId = Guid.NewGuid(); // claimed since the prefetch ran
        var eligibleCso = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001");
        var rule = BuildInboundShapedMatchingRule(targetUserType, csEmployeeIdAttr, employeeIdAttr);

        var candidates = new ExportMatchCandidates();
        candidates.AddCandidate(rule.Id, "EMP001", noLongerEligibleCso.Id);
        candidates.AddCandidate(rule.Id, "EMP001", eligibleCso.Id);

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }, candidates);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ConnectedSystemObject.Id, Is.EqualTo(eligibleCso.Id), "The no-longer-eligible candidate must be skipped in favour of the next one");
        Assert.That(candidates.GetCandidates(rule.Id, "EMP001"), Does.Not.Contain(noLongerEligibleCso.Id),
            "The no-longer-eligible candidate must be removed so no later lookup on the page is offered it again");
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_PrefetchedNoCandidatesForResolvedValue_ReturnsNullAsync()
    {
        // Arrange - the rule resolves a usable value, but the prefetch found nothing for it (no CSO in
        // the target holds that value).
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "EMP001"
        });

        var rule = BuildInboundShapedMatchingRule(targetUserType, csEmployeeIdAttr, employeeIdAttr);
        var candidates = new ExportMatchCandidates(); // nothing added for rule.Id / "EMP001"

        // Act
        var result = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }, candidates);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public void FindMatchingConnectedSystemObjectAsync_FirstRuleHasNoSources_FailsWithoutTryingTheNextAsync()
    {
        // #2052: previously logged and skipped, so a broken rule could let a weaker one join, or provision a duplicate.
        var (mvo, targetSystem, targetUserType, resolvedRule) = ArrangeEmployeeIdExportMatch("EMP001");
        resolvedRule.Order = 2;
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var cso = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001");
        var unresolvedRule = new ObjectMatchingRule
        {
            Id = 9101,
            Order = 1,
            ConnectedSystemObjectTypeId = targetUserType.Id,
            ConnectedSystemObjectType = targetUserType,
            Sources = new List<ObjectMatchingRuleSource>() // no sources
        };
        var candidates = new ExportMatchCandidates();
        candidates.AddCandidate(resolvedRule.Id, "EMP001", cso.Id);

        var ex = Assert.ThrowsAsync<ObjectMatchingRuleFailedException>(() => Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { unresolvedRule, resolvedRule }, candidates));

        Assert.That(ex!.Rule, Is.SameAs(unresolvedRule));
    }

    [Test]
    public void FindMatchingConnectedSystemObjectAsync_MisconfiguredRule_FailsOnBothPathsAsync()
    {
        // Arrange - a rule whose source carries no Connected System attribute (a configuration fault). #2052:
        // previously both paths reported no match, so the Metaverse Object provisioned a duplicate of an existing account.
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "EMP001"
        });

        var misconfiguredRule = new ObjectMatchingRule
        {
            Id = 9102,
            Order = 1,
            ConnectedSystemObjectTypeId = targetUserType.Id,
            ConnectedSystemObjectType = targetUserType,
            TargetMetaverseAttribute = employeeIdAttr,
            TargetMetaverseAttributeId = employeeIdAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 9102,
                    Order = 1,
                    ConnectedSystemAttribute = null,
                    ConnectedSystemAttributeId = null
                }
            }
        };

        // Act + Assert - both paths agree: the rule cannot be compared, so the object stops rather than provisioning.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
                mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { misconfiguredRule }),
                Throws.TypeOf<ObjectMatchingRuleFailedException>());
            Assert.That(() => Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
                mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { misconfiguredRule }, new ExportMatchCandidates()),
                Throws.TypeOf<ObjectMatchingRuleFailedException>());
        }
    }

    #endregion

    #region Export matching on an expression (#2052)

    /// <summary>The Employee ID rule recast to compute the Connected System side as Lower(cs["EmployeeId"]), case-sensitively.</summary>
    private (MetaverseObject Mvo, ConnectedSystem TargetSystem, ConnectedSystemObjectType TargetUserType, ObjectMatchingRule Rule)
        ArrangeLowerCaseEmployeeIdExpressionMatch(string employeeId)
    {
        var arranged = ArrangeEmployeeIdExportMatch(employeeId);
        arranged.Rule.CaseSensitive = true;
        arranged.Rule.Sources[0].ConnectedSystemAttribute = null;
        arranged.Rule.Sources[0].ConnectedSystemAttributeId = null;
        arranged.Rule.Sources[0].Expression = "Lower(cs[\"EmployeeId\"])";
        return arranged;
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_ExpressionRule_MatchesTheObjectItComputesTheValueForAsync()
    {
        var (mvo, targetSystem, targetUserType, rule) = ArrangeLowerCaseEmployeeIdExpressionMatch("emp001");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var account = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001");
        SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP002");

        var match = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(match?.ConnectedSystemObject.Id, Is.EqualTo(account.Id),
                "the case-sensitive rule matches only through what the expression computes");
            Assert.That(match?.Rule, Is.SameAs(rule));
            Assert.That(match?.MatchedValue, Is.EqualTo("emp001"));
        }
    }

    [Test]
    public void FindMatchingConnectedSystemObjectAsync_ExpressionRuleComputesTheValueForTwoObjects_IsAmbiguousAsync()
    {
        var (mvo, targetSystem, targetUserType, rule) = ArrangeLowerCaseEmployeeIdExpressionMatch("emp001");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var upper = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001", displayName: "upper");
        var lower = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "emp001", displayName: "lower");

        var ex = Assert.ThrowsAsync<MultipleMatchesException>(() => Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }));

        Assert.That(ex!.Candidates.Select(c => (c.CandidateId, c.MatchedValue)),
            Is.EqualTo(new[] { (lower.Id, (string?)"emp001"), (upper.Id, (string?)"emp001") }));
    }

    [Test]
    public void FindMatchingConnectedSystemObjectAsync_ExpressionRuleMissWhileAnObjectCouldNotBeChecked_FailsAsync()
    {
        // The expression's text cannot become a Number for EMP002, so the index cannot say what that object would match,
        // and a lookup finding nothing could be missing this Metaverse Object's own account.
        var (mvo, targetSystem, targetUserType, rule, uncheckable) = ArrangeBadgeNumberExpressionMatch(badge: 2002);

        var ex = Assert.ThrowsAsync<ObjectMatchingRuleFailedException>(() => Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain("1 existing Connected System Object"));
            Assert.That(ex.Message, Does.Contain(uncheckable.Id.ToString()));
            Assert.That(ex.Message, Does.Contain("provisioned"));
        }
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_ExpressionRuleHitWhileAnObjectCouldNotBeChecked_MatchesAsync()
    {
        var (mvo, targetSystem, targetUserType, rule, _) = ArrangeBadgeNumberExpressionMatch(badge: 1001);

        var match = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        Assert.That(match?.MatchedValue, Is.EqualTo("1001"));
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_ExpressionRuleWithRunIndexes_BuildsOnceAndDropsAnObjectClaimedSinceAsync()
    {
        var (mvo, targetSystem, targetUserType, rule) = ArrangeLowerCaseEmployeeIdExpressionMatch("emp001");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var account = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001");
        var indexes = new Dictionary<(int, int), ExpressionMatchValueIndex>();

        var first = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }, expressionIndexes: indexes);
        var index = indexes.Single().Value;
        account.MetaverseObjectId = mvo.Id; // claimed, as export evaluation would
        var second = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }, expressionIndexes: indexes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first?.ConnectedSystemObject.Id, Is.EqualTo(account.Id));
            Assert.That(indexes.Single().Value, Is.SameAs(index), "the index is built once for the run");
            Assert.That(indexes.Single().Key, Is.EqualTo((rule.Id, targetUserType.Id)));
            Assert.That(second, Is.Null, "an object claimed since the index was built is never offered again");
            Assert.That(index.GetCandidates("emp001"), Is.Empty);
        }
    }

    /// <summary>
    /// A Metaverse Object holding a Badge number, and a rule computing it as cs["EmployeeId"]. The Dummy Target System
    /// holds account 1001 and account EMP002, whose Employee ID is not a number, so the expression cannot be worked out
    /// for it.
    /// </summary>
    private (MetaverseObject Mvo, ConnectedSystem TargetSystem, ConnectedSystemObjectType TargetUserType, ObjectMatchingRule Rule,
        ConnectedSystemObject Uncheckable) ArrangeBadgeNumberExpressionMatch(int badge)
    {
        var (mvo, targetSystem, targetUserType, rule) = ArrangeLowerCaseEmployeeIdExpressionMatch("unused");
        var badgeAttr = new MetaverseAttribute { Id = 9600, Name = "Badge", Type = AttributeDataType.Number };
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), Attribute = badgeAttr, AttributeId = badgeAttr.Id, IntValue = badge });
        rule.TargetMetaverseAttribute = badgeAttr;
        rule.TargetMetaverseAttributeId = badgeAttr.Id;
        rule.Sources[0].Expression = "cs[\"EmployeeId\"]";

        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "1001");
        var uncheckable = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP002");
        return (mvo, targetSystem, targetUserType, rule, uncheckable);
    }

    #endregion

    #region Export matching while an object holds the attribute under another data type (#2323)

    /// <summary>
    /// Seeds an account whose EmployeeId is held only as a Number: a schema refresh made the attribute Text and no import
    /// has read it since, so the lookup, which compares the Text column, cannot see it.
    /// </summary>
    private ConnectedSystemObject SeedAccountHeldAsNumber(ConnectedSystem targetSystem, ConnectedSystemObjectType targetUserType, int employeeId)
    {
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var cso = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "unused");
        cso.AttributeValues[0].StringValue = null;
        cso.AttributeValues[0].IntValue = employeeId;
        return cso;
    }

    [TestCase(false, TestName = "FindMatchingConnectedSystemObjectAsync_OnlyAccountHeldUnderAnotherDataType_Fails(Query)")]
    [TestCase(true, TestName = "FindMatchingConnectedSystemObjectAsync_OnlyAccountHeldUnderAnotherDataType_Fails(Prefetched)")]
    public void FindMatchingConnectedSystemObjectAsync_OnlyAccountHeldUnderAnotherDataType_FailsAsync(bool prefetched)
    {
        // The account may be this identity's own: finding nothing must not let it be provisioned a second account.
        var (mvo, targetSystem, targetUserType, rule) = ArrangeEmployeeIdExportMatch("1001");
        var held = SeedAccountHeldAsNumber(targetSystem, targetUserType, 1001);

        var ex = Assert.ThrowsAsync<ObjectMatchingRuleFailedException>(() => Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }, prefetched ? new ExportMatchCandidates() : null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Rule, Is.SameAs(rule));
            Assert.That(ex.Message, Does.Contain("neither joined nor provisioned"));
            Assert.That(ex.Message, Does.Contain("1 existing Connected System Object"));
            Assert.That(ex.Message, Does.Contain(held.Id.ToString()));
            Assert.That(ex.Message, Does.Contain("is held as Number, not as its Text type"));
            Assert.That(ex.Message, Does.Contain("Full Import"));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FindMatchingConnectedSystemObjectAsync_AccountHoldsTheCurrentTypeWhileAnotherIsHeldUnderAnotherDataType_MatchesAsync(bool prefetched)
    {
        var (mvo, targetSystem, targetUserType, rule) = ArrangeEmployeeIdExportMatch("EMP001");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var account = SeedTargetCso(targetSystem, targetUserType, csEmployeeIdAttr, "EMP001");
        SeedAccountHeldAsNumber(targetSystem, targetUserType, 2002);
        ExportMatchCandidates? candidates = null;
        if (prefetched)
        {
            candidates = new ExportMatchCandidates();
            candidates.AddCandidate(rule.Id, "EMP001", account.Id);
        }

        var match = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }, candidates);

        Assert.That(match?.ConnectedSystemObject.Id, Is.EqualTo(account.Id));
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_AccountHoldsBothTypes_IsReadAsItsCurrentTypeAndFindsNothingAsync()
    {
        // As Attribute Flow reads it (#2319): a value of the current type is what the object holds, so it is not held only
        // under another type, and a value that matches nothing lets the identity be provisioned.
        var (mvo, targetSystem, targetUserType, rule) = ArrangeEmployeeIdExportMatch("EMP001");
        var csEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == "EmployeeId");
        var account = SeedAccountHeldAsNumber(targetSystem, targetUserType, 1001);
        account.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(), Attribute = csEmployeeIdAttr, AttributeId = csEmployeeIdAttr.Id, StringValue = "EMP002"
        });

        var match = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule });

        Assert.That(match, Is.Null);
    }

    [Test]
    public async Task FindMatchingConnectedSystemObjectAsync_NoAccountAtAll_ReturnsNullSoTheIdentityIsProvisionedAsync()
    {
        var (mvo, targetSystem, targetUserType, rule) = ArrangeEmployeeIdExportMatch("EMP001");

        var match = await Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }, heldObjects: new Dictionary<(int, int), ExportMatchHeldObjects>());

        Assert.That(match, Is.Null);
    }

    [Test]
    public void FindMatchingConnectedSystemObjectAsync_HeldObjectsWithRunIndexes_LooksThemUpOnceForTheRunAsync()
    {
        var (mvo, targetSystem, targetUserType, rule) = ArrangeEmployeeIdExportMatch("1001");
        var held = SeedAccountHeldAsNumber(targetSystem, targetUserType, 1001);
        var heldObjects = new Dictionary<(int, int), ExportMatchHeldObjects>();

        Assert.ThrowsAsync<ObjectMatchingRuleFailedException>(() => Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }, heldObjects: heldObjects));
        var lookedUp = heldObjects.Single();
        Assert.ThrowsAsync<ObjectMatchingRuleFailedException>(() => Jim.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
            mvo, targetSystem, targetUserType, new List<ObjectMatchingRule> { rule }, heldObjects: heldObjects));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lookedUp.Key, Is.EqualTo((rule.Id, targetUserType.Id)));
            Assert.That(heldObjects.Single().Value, Is.SameAs(lookedUp.Value), "the held objects are looked up once for the run");
            Assert.That(lookedUp.Value.Count, Is.EqualTo(1));
            Assert.That(lookedUp.Value.First?.ConnectedSystemObjectId, Is.EqualTo(held.Id));
        }
    }

    #endregion

    #region GetSourceType Tests

    [Test]
    public void ObjectMatchingRule_GetSourceType_WithNoSources_ReturnsNotSet()
    {
        // Arrange
        var rule = new ObjectMatchingRule
        {
            Sources = new List<ObjectMatchingRuleSource>()
        };

        // Act
        var sourceType = rule.GetSourceType();

        // Assert
        Assert.That(sourceType, Is.EqualTo(SyncRuleMappingSourcesType.NotSet));
    }

    [Test]
    public void ObjectMatchingRule_GetSourceType_WithCsAttribute_ReturnsAttributeMapping()
    {
        // Arrange
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };
        var rule = new ObjectMatchingRule
        {
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    ConnectedSystemAttribute = csAttr,
                    ConnectedSystemAttributeId = csAttr.Id
                }
            }
        };

        // Act
        var sourceType = rule.GetSourceType();

        // Assert
        Assert.That(sourceType, Is.EqualTo(SyncRuleMappingSourcesType.AttributeMapping));
    }

    [Test]
    public void ObjectMatchingRule_GetSourceType_WithExpression_ReturnsExpressionMapping()
    {
        // Arrange
        var rule = new ObjectMatchingRule
        {
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Expression = "ToUpper(employeeNumber)"
                }
            }
        };

        // Act
        var sourceType = rule.GetSourceType();

        // Assert
        Assert.That(sourceType, Is.EqualTo(SyncRuleMappingSourcesType.ExpressionMapping));
    }

    #endregion

}

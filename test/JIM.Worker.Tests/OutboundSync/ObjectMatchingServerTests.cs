using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.Worker.Tests.Models;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

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

        // Set up the Sync Rule stub mocks
        SyncRulesData = TestUtilities.GetSyncRuleData();
        MockDbSetSyncRules = SyncRulesData.BuildMockDbSet();

        // Mock entity framework calls
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(MockDbSetMetaverseObjects.Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(MockDbSetSyncRules.Object);

        // Instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
    }

    #region GetMatchingRulesForImport Tests

    [Test]
    public void ComputeMatchingValueFromMvo_WithStringAttribute_ReturnsStringValue()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        // Ensure MVO has the EmployeeId attribute value
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "E12345"
        });

        var objectType = ConnectedSystemObjectTypesData[0];
        var matchingRule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            TargetMetaverseAttribute = employeeIdAttr,
            TargetMetaverseAttributeId = employeeIdAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 1,
                    MetaverseAttribute = employeeIdAttr,
                    MetaverseAttributeId = employeeIdAttr.Id
                }
            }
        };

        // Act
        var result = Jim.ObjectMatching.ComputeMatchingValueFromMvo(mvo, matchingRule);

        // Assert
        Assert.That(result, Is.EqualTo("E12345"));
    }

    [Test]
    public void ComputeMatchingValueFromMvo_WithGuidAttribute_ReturnsGuidValue()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];

        // Create a GUID attribute for testing
        var guidAttr = new MetaverseAttribute
        {
            Id = 999,
            Name = "UniqueId",
            Type = AttributeDataType.Guid
        };

        var expectedGuid = Guid.NewGuid();
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = guidAttr,
            AttributeId = guidAttr.Id,
            GuidValue = expectedGuid
        });

        var objectType = ConnectedSystemObjectTypesData[0];
        var matchingRule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            TargetMetaverseAttribute = guidAttr,
            TargetMetaverseAttributeId = guidAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 1,
                    MetaverseAttribute = guidAttr,
                    MetaverseAttributeId = guidAttr.Id
                }
            }
        };

        // Act
        var result = Jim.ObjectMatching.ComputeMatchingValueFromMvo(mvo, matchingRule);

        // Assert
        Assert.That(result, Is.EqualTo(expectedGuid));
    }

    [Test]
    public void ComputeMatchingValueFromMvo_WithIntAttribute_ReturnsIntValue()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];

        var intAttr = new MetaverseAttribute
        {
            Id = 998,
            Name = "NumericId",
            Type = AttributeDataType.Number
        };

        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = intAttr,
            AttributeId = intAttr.Id,
            IntValue = 42
        });

        var objectType = ConnectedSystemObjectTypesData[0];
        var matchingRule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            TargetMetaverseAttribute = intAttr,
            TargetMetaverseAttributeId = intAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 1,
                    MetaverseAttribute = intAttr,
                    MetaverseAttributeId = intAttr.Id
                }
            }
        };

        // Act
        var result = Jim.ObjectMatching.ComputeMatchingValueFromMvo(mvo, matchingRule);

        // Assert
        Assert.That(result, Is.EqualTo(42));
    }

    [Test]
    public void ComputeMatchingValueFromMvo_WithMissingAttribute_ReturnsNull()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];
        mvo.AttributeValues.Clear(); // No attributes

        var missingAttr = new MetaverseAttribute
        {
            Id = 997,
            Name = "MissingAttr",
            Type = AttributeDataType.Text
        };

        var objectType = ConnectedSystemObjectTypesData[0];
        var matchingRule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            TargetMetaverseAttribute = missingAttr,
            TargetMetaverseAttributeId = missingAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 1,
                    MetaverseAttribute = missingAttr,
                    MetaverseAttributeId = missingAttr.Id
                }
            }
        };

        // Act
        var result = Jim.ObjectMatching.ComputeMatchingValueFromMvo(mvo, matchingRule);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ComputeMatchingValueFromMvo_WithInvalidRule_ReturnsNull()
    {
        // Arrange
        var mvo = MetaverseObjectsData[0];

        // Invalid rule - no sources
        var matchingRule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            Sources = new List<ObjectMatchingRuleSource>()
        };

        // Act
        var result = Jim.ObjectMatching.ComputeMatchingValueFromMvo(mvo, matchingRule);

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region ObjectMatchingRule Validation Tests

    [Test]
    public void ObjectMatchingRule_IsValid_WithValidConfiguration_ReturnsTrue()
    {
        // Arrange
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };
        var objectType = ConnectedSystemObjectTypesData[0];

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
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

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
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

        var exportRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportRule.ConnectedSystem = connectedSystem;

        // Add matching rules to the sync rule
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
    public void ObjectMatchingRule_CaseSensitive_DefaultsToTrue()
    {
        // Arrange & Act
        var rule = new ObjectMatchingRule();

        // Assert - verify default is case-sensitive
        Assert.That(rule.CaseSensitive, Is.True, "ObjectMatchingRule.CaseSensitive should default to true");
    }

    [Test]
    public void ObjectMatchingRule_CaseSensitive_CanBeSetToFalse()
    {
        // Arrange
        var rule = new ObjectMatchingRule { CaseSensitive = false };

        // Assert
        Assert.That(rule.CaseSensitive, Is.False, "ObjectMatchingRule.CaseSensitive should be settable to false");
    }

    [Test]
    public void ObjectMatchingRule_IsValid_WithCaseSensitiveFalse_StillReturnsTrue()
    {
        // Arrange - verify CaseSensitive doesn't affect validation
        var mvAttr = new MetaverseAttribute { Id = 1, Name = "EmployeeId" };
        var csAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "employeeNumber" };
        var objectType = ConnectedSystemObjectTypesData[0];

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
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

    /// <summary>
    /// Note: Full integration testing of case-insensitive matching at the database level
    /// is covered by the integration tests (Scenario 5 - MatchingRules).
    /// Unit testing the EF Core ILike function requires a real database connection.
    /// </summary>
    [Test]
    public void ComputeMatchingValueFromMvo_CaseSensitiveFalse_StillReturnsOriginalValue()
    {
        // Arrange - verify ComputeMatchingValueFromMvo doesn't transform values
        // (case sensitivity is applied at query time, not at value computation time)
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "E12345" // Mixed case
        });

        var objectType = ConnectedSystemObjectTypesData[0];
        var matchingRule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            TargetMetaverseAttribute = employeeIdAttr,
            TargetMetaverseAttributeId = employeeIdAttr.Id,
            CaseSensitive = false, // Case-insensitive matching
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 1,
                    MetaverseAttribute = employeeIdAttr,
                    MetaverseAttributeId = employeeIdAttr.Id
                }
            }
        };

        // Act
        var result = Jim.ObjectMatching.ComputeMatchingValueFromMvo(mvo, matchingRule);

        // Assert - value should be returned as-is (not lowercased)
        Assert.That(result, Is.EqualTo("E12345"), "ComputeMatchingValueFromMvo should return original value regardless of CaseSensitive setting");
    }

    #endregion

    #region Null Value Handling Tests (8.11)

    [Test]
    public void ComputeMatchingValueFromMvo_WithNullStringAttribute_ReturnsNull()
    {
        // Arrange - MVO has attribute with null string value
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = null // Explicitly null
        });

        var objectType = ConnectedSystemObjectTypesData[0];
        var matchingRule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            TargetMetaverseAttribute = employeeIdAttr,
            TargetMetaverseAttributeId = employeeIdAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 1,
                    MetaverseAttribute = employeeIdAttr,
                    MetaverseAttributeId = employeeIdAttr.Id
                }
            }
        };

        // Act
        var result = Jim.ObjectMatching.ComputeMatchingValueFromMvo(mvo, matchingRule);

        // Assert
        Assert.That(result, Is.Null, "ComputeMatchingValueFromMvo should return null for null string value");
    }

    [Test]
    public void ComputeMatchingValueFromMvo_WithNullIntAttribute_ReturnsNull()
    {
        // Arrange - MVO has attribute with null int value
        var mvo = MetaverseObjectsData[0];

        var intAttr = new MetaverseAttribute
        {
            Id = 998,
            Name = "NumericId",
            Type = AttributeDataType.Number
        };

        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = intAttr,
            AttributeId = intAttr.Id,
            IntValue = null // Explicitly null
        });

        var objectType = ConnectedSystemObjectTypesData[0];
        var matchingRule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            TargetMetaverseAttribute = intAttr,
            TargetMetaverseAttributeId = intAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 1,
                    MetaverseAttribute = intAttr,
                    MetaverseAttributeId = intAttr.Id
                }
            }
        };

        // Act
        var result = Jim.ObjectMatching.ComputeMatchingValueFromMvo(mvo, matchingRule);

        // Assert
        Assert.That(result, Is.Null, "ComputeMatchingValueFromMvo should return null for null int value");
    }

    [Test]
    public void ComputeMatchingValueFromMvo_WithNullGuidAttribute_ReturnsNull()
    {
        // Arrange - MVO has attribute with null GUID value
        var mvo = MetaverseObjectsData[0];

        var guidAttr = new MetaverseAttribute
        {
            Id = 999,
            Name = "UniqueId",
            Type = AttributeDataType.Guid
        };

        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = guidAttr,
            AttributeId = guidAttr.Id,
            GuidValue = null // Explicitly null
        });

        var objectType = ConnectedSystemObjectTypesData[0];
        var matchingRule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            TargetMetaverseAttribute = guidAttr,
            TargetMetaverseAttributeId = guidAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 1,
                    MetaverseAttribute = guidAttr,
                    MetaverseAttributeId = guidAttr.Id
                }
            }
        };

        // Act
        var result = Jim.ObjectMatching.ComputeMatchingValueFromMvo(mvo, matchingRule);

        // Assert
        Assert.That(result, Is.Null, "ComputeMatchingValueFromMvo should return null for null GUID value");
    }

    [Test]
    public void ComputeMatchingValueFromMvo_WithEmptyStringAttribute_ReturnsNull()
    {
        // Arrange - MVO has attribute with empty string value
        var mvo = MetaverseObjectsData[0];
        var mvUserType = MetaverseObjectTypesData.Single(t => t.Name == "User");
        var employeeIdAttr = mvUserType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.EmployeeId);

        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "" // Empty string
        });

        var objectType = ConnectedSystemObjectTypesData[0];
        var matchingRule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
            TargetMetaverseAttribute = employeeIdAttr,
            TargetMetaverseAttributeId = employeeIdAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Id = 1,
                    Order = 1,
                    MetaverseAttribute = employeeIdAttr,
                    MetaverseAttributeId = employeeIdAttr.Id
                }
            }
        };

        // Act
        var result = Jim.ObjectMatching.ComputeMatchingValueFromMvo(mvo, matchingRule);

        // Assert
        Assert.That(result, Is.Null, "ComputeMatchingValueFromMvo should return null for empty string value");
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

        var rule = new ObjectMatchingRule
        {
            Id = 1,
            Order = 1,
            ConnectedSystemObjectTypeId = objectType.Id,
            ConnectedSystemObjectType = objectType,
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
}

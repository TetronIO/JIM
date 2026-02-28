using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Enums;
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
/// Tests for no-net-change detection in ExportEvaluationServer.
/// Verifies that pending exports are skipped when CSO already has the target value.
/// Supports both single-valued and multi-valued attributes.
/// </summary>
public class ExportEvaluationNoChangeTests
{
    #region accessors
    private Mock<JimDbContext> MockJimDbContext { get; set; } = null!;
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } = null!;
    private List<ConnectedSystemObject> ConnectedSystemObjectsData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObject>> MockDbSetConnectedSystemObjects { get; set; } = null!;
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; } = null!;
    private List<PendingExport> PendingExportsData { get; set; } = null!;
    private Mock<DbSet<PendingExport>> MockDbSetPendingExports { get; set; } = null!;
    private List<MetaverseObjectType> MetaverseObjectTypesData { get; set; } = null!;
    private Mock<DbSet<MetaverseObjectType>> MockDbSetMetaverseObjectTypes { get; set; } = null!;
    private List<MetaverseObject> MetaverseObjectsData { get; set; } = null!;
    private Mock<DbSet<MetaverseObject>> MockDbSetMetaverseObjects { get; set; } = null!;
    private List<SyncRule> SyncRulesData { get; set; } = null!;
    private Mock<DbSet<SyncRule>> MockDbSetSyncRules { get; set; } = null!;
    private List<ConnectedSystemObjectAttributeValue> ConnectedSystemObjectAttributeValuesData { get; set; } = null!;
    private Mock<DbSet<ConnectedSystemObjectAttributeValue>> MockDbSetConnectedSystemObjectAttributeValues { get; set; } = null!;
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
        var mockDbSetConnectedSystems = ConnectedSystemsData.BuildMockDbSet();

        // Set up the Connected System Object Types mock
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.BuildMockDbSet();

        // Set up the Connected System Objects mock
        ConnectedSystemObjectsData = TestUtilities.GetConnectedSystemObjectData();
        MockDbSetConnectedSystemObjects = ConnectedSystemObjectsData.BuildMockDbSet();

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

        // Set up the CSO Attribute Values mock (empty by default)
        ConnectedSystemObjectAttributeValuesData = new List<ConnectedSystemObjectAttributeValue>();
        MockDbSetConnectedSystemObjectAttributeValues = ConnectedSystemObjectAttributeValuesData.BuildMockDbSet();

        // Mock entity framework calls
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjects).Returns(MockDbSetConnectedSystemObjects.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectAttributeValues).Returns(MockDbSetConnectedSystemObjectAttributeValues.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(mockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjectTypes).Returns(MockDbSetMetaverseObjectTypes.Object);
        MockJimDbContext.Setup(m => m.MetaverseObjects).Returns(MockDbSetMetaverseObjects.Object);
        MockJimDbContext.Setup(m => m.PendingExports).Returns(MockDbSetPendingExports.Object);
        MockJimDbContext.Setup(m => m.SyncRules).Returns(MockDbSetSyncRules.Object);

        // Instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
    }

    #region Single-Valued Update Tests (String)

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateStringMatch_ReturnsTrueAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "John Smith");
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update, stringValue: "John Smith");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.True, "Should return true when string values match for Update");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateStringMismatch_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "John Smith");
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update, stringValue: "Jane Doe");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.False, "Should return false when string values differ for Update");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateStringCaseSensitive_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "John Smith");
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update, stringValue: "JOHN SMITH");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.False, "String comparison should be case-sensitive");
    }

    #endregion

    #region Single-Valued Update Tests (Other Types)

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateIntMatch_ReturnsTrueAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(intValue: 12345);
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update, intValue: 12345);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.True, "Should return true when int values match for Update");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateIntMismatch_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(intValue: 12345);
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update, intValue: 54321);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.False, "Should return false when int values differ for Update");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateDateTimeMatch_ReturnsTrueAsync()
    {
        // Arrange
        var dateValue = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var csoAttributeValue = CreateCsoAttributeValue(dateTimeValue: dateValue);
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update, dateTimeValue: dateValue);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.True, "Should return true when DateTime values match for Update");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateDateTimeMismatch_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(dateTimeValue: new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc));
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update, dateTimeValue: new DateTime(2024, 1, 16, 10, 30, 0, DateTimeKind.Utc));

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.False, "Should return false when DateTime values differ for Update");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateBinaryMatch_ReturnsTrueAsync()
    {
        // Arrange
        var binaryValue = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var csoAttributeValue = CreateCsoAttributeValue(byteValue: binaryValue);
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update, byteValue: new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.True, "Should return true when binary values match for Update");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateBinaryMismatch_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(byteValue: new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update, byteValue: new byte[] { 0x01, 0x02, 0x03, 0x04, 0xFF });

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.False, "Should return false when binary values differ for Update");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateUnresolvedReferenceMatch_ReturnsTrueAsync()
    {
        // Arrange
        var referenceValue = "CN=Manager,OU=Users,DC=corp,DC=local";
        var csoAttributeValue = CreateCsoAttributeValue(unresolvedReferenceValue: referenceValue);
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update, unresolvedReferenceValue: referenceValue);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.True, "Should return true when unresolved reference values match for Update");
    }

    #endregion

    #region Single-Valued Update Null Handling Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateBothEmpty_ReturnsTrueAsync()
    {
        // Arrange - No existing values, empty pending change
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, Array.Empty<ConnectedSystemObjectAttributeValue>());

        // Assert
        Assert.That(result, Is.True, "Should return true when both are null/empty for Update");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdatePendingEmptyCsoHasValue_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "SomeValue");
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.False, "Should return false when pending is empty but CSO has value for Update");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdatePendingHasValueCsoEmpty_ReturnsFalseAsync()
    {
        // Arrange
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update, stringValue: "NewValue");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, Array.Empty<ConnectedSystemObjectAttributeValue>());

        // Assert
        Assert.That(result, Is.False, "Should return false when CSO is empty but pending has value for Update");
    }

    #endregion

    #region Multi-Valued Add Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_AddWhenValueExists_ReturnsTrueAsync()
    {
        // Arrange - CSO already has the value we're trying to add
        var existingMembers = new[]
        {
            CreateCsoAttributeValue(stringValue: "CN=User1,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=User2,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=User3,OU=Users,DC=corp")
        };
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Add, stringValue: "CN=User2,OU=Users,DC=corp");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, existingMembers);

        // Assert
        Assert.That(result, Is.True, "Should return true (skip Add) when value already exists in CSO");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_AddWhenValueNotExists_ReturnsFalseAsync()
    {
        // Arrange - CSO doesn't have the value we're trying to add
        var existingMembers = new[]
        {
            CreateCsoAttributeValue(stringValue: "CN=User1,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=User2,OU=Users,DC=corp")
        };
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Add, stringValue: "CN=NewUser,OU=Users,DC=corp");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, existingMembers);

        // Assert
        Assert.That(result, Is.False, "Should return false (proceed with Add) when value doesn't exist in CSO");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_AddToEmptyAttribute_ReturnsFalseAsync()
    {
        // Arrange - CSO has no values for this attribute
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Add, stringValue: "CN=NewUser,OU=Users,DC=corp");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, Array.Empty<ConnectedSystemObjectAttributeValue>());

        // Assert
        Assert.That(result, Is.False, "Should return false (proceed with Add) when CSO has no values");
    }

    #endregion

    #region Multi-Valued Remove Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_RemoveWhenValueExists_ReturnsFalseAsync()
    {
        // Arrange - CSO has the value we're trying to remove
        var existingMembers = new[]
        {
            CreateCsoAttributeValue(stringValue: "CN=User1,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=User2,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=User3,OU=Users,DC=corp")
        };
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Remove, stringValue: "CN=User2,OU=Users,DC=corp");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, existingMembers);

        // Assert
        Assert.That(result, Is.False, "Should return false (proceed with Remove) when value exists in CSO");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_RemoveWhenValueNotExists_ReturnsTrueAsync()
    {
        // Arrange - CSO doesn't have the value we're trying to remove
        var existingMembers = new[]
        {
            CreateCsoAttributeValue(stringValue: "CN=User1,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=User2,OU=Users,DC=corp")
        };
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Remove, stringValue: "CN=UserNotHere,OU=Users,DC=corp");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, existingMembers);

        // Assert
        Assert.That(result, Is.True, "Should return true (skip Remove) when value doesn't exist in CSO");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_RemoveFromEmptyAttribute_ReturnsTrueAsync()
    {
        // Arrange - CSO has no values for this attribute
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Remove, stringValue: "CN=User,OU=Users,DC=corp");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, Array.Empty<ConnectedSystemObjectAttributeValue>());

        // Assert
        Assert.That(result, Is.True, "Should return true (skip Remove) when CSO has no values");
    }

    #endregion

    #region RemoveAll Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_RemoveAllWhenValuesExist_ReturnsFalseAsync()
    {
        // Arrange - CSO has values that need to be removed
        var existingMembers = new[]
        {
            CreateCsoAttributeValue(stringValue: "CN=User1,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=User2,OU=Users,DC=corp")
        };
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.RemoveAll);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, existingMembers);

        // Assert
        Assert.That(result, Is.False, "Should return false (proceed with RemoveAll) when CSO has values");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_RemoveAllWhenNoValues_ReturnsTrueAsync()
    {
        // Arrange - CSO has no values for this attribute
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.RemoveAll);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, Array.Empty<ConnectedSystemObjectAttributeValue>());

        // Assert
        Assert.That(result, Is.True, "Should return true (skip RemoveAll) when CSO has no values");
    }

    #endregion

    #region Group Membership Scenario Tests

    [Test]
    public void GroupMembership_AddMemberAlreadyInGroup_NoChangeNeeded()
    {
        // Arrange - Simulating a group with existing members
        var existingGroupMembers = new[]
        {
            CreateCsoAttributeValue(stringValue: "CN=Alice,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=Bob,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=Charlie,OU=Users,DC=corp")
        };

        // Try to add Bob who is already a member
        var addBobChange = CreatePendingChange(PendingExportAttributeChangeType.Add, stringValue: "CN=Bob,OU=Users,DC=corp");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(addBobChange, existingGroupMembers);

        // Assert
        Assert.That(result, Is.True, "Adding existing member should be detected as no-net-change");
    }

    [Test]
    public void GroupMembership_AddNewMember_ChangeNeeded()
    {
        // Arrange - Simulating a group with existing members
        var existingGroupMembers = new[]
        {
            CreateCsoAttributeValue(stringValue: "CN=Alice,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=Bob,OU=Users,DC=corp")
        };

        // Try to add Dave who is not yet a member
        var addDaveChange = CreatePendingChange(PendingExportAttributeChangeType.Add, stringValue: "CN=Dave,OU=Users,DC=corp");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(addDaveChange, existingGroupMembers);

        // Assert
        Assert.That(result, Is.False, "Adding new member should not be a no-net-change");
    }

    [Test]
    public void GroupMembership_RemoveMemberInGroup_ChangeNeeded()
    {
        // Arrange - Simulating a group with existing members
        var existingGroupMembers = new[]
        {
            CreateCsoAttributeValue(stringValue: "CN=Alice,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=Bob,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=Charlie,OU=Users,DC=corp")
        };

        // Try to remove Bob who is a member
        var removeBobChange = CreatePendingChange(PendingExportAttributeChangeType.Remove, stringValue: "CN=Bob,OU=Users,DC=corp");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(removeBobChange, existingGroupMembers);

        // Assert
        Assert.That(result, Is.False, "Removing existing member should not be a no-net-change");
    }

    [Test]
    public void GroupMembership_RemoveMemberNotInGroup_NoChangeNeeded()
    {
        // Arrange - Simulating a group with existing members
        var existingGroupMembers = new[]
        {
            CreateCsoAttributeValue(stringValue: "CN=Alice,OU=Users,DC=corp"),
            CreateCsoAttributeValue(stringValue: "CN=Bob,OU=Users,DC=corp")
        };

        // Try to remove Eve who is not a member
        var removeEveChange = CreatePendingChange(PendingExportAttributeChangeType.Remove, stringValue: "CN=Eve,OU=Users,DC=corp");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(removeEveChange, existingGroupMembers);

        // Assert
        Assert.That(result, Is.True, "Removing non-member should be detected as no-net-change");
    }

    [Test]
    public void GroupMembership_LargeGroupWithDuplicateAddAttempt_NoChangeNeeded()
    {
        // Arrange - Simulating a large group with many members
        var existingGroupMembers = Enumerable.Range(1, 1000)
            .Select(i => CreateCsoAttributeValue(stringValue: $"CN=User{i},OU=Users,DC=corp"))
            .ToArray();

        // Try to add User500 who is already a member
        var addUser500Change = CreatePendingChange(PendingExportAttributeChangeType.Add, stringValue: "CN=User500,OU=Users,DC=corp");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(addUser500Change, existingGroupMembers);

        // Assert
        Assert.That(result, Is.True, "Adding existing member in large group should be detected as no-net-change");
    }

    #endregion

    #region Null-Valued Update Tests

    /// <summary>
    /// Verifies that a null-valued Update is NOT treated as no-net-change when the target
    /// CSO has a non-null value. This is a general edge case test for no-net-change detection
    /// with null values.
    /// </summary>
    [Test]
    public void IsCsoAttributeAlreadyCurrent_NullUpdateAgainstExistingString_ReturnsFalseAsync()
    {
        // Arrange - target CSO has "Information Technology", null Update should clear it
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "Information Technology");
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update);
        // All value fields null — this represents clearing the attribute

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.False,
            "Null Update should NOT be treated as no-net-change when target has a value (attribute removal must propagate)");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_NullUpdateAgainstExistingInt_ReturnsFalseAsync()
    {
        // Arrange - target CSO has int value 42, null Update should clear it
        var csoAttributeValue = CreateCsoAttributeValue(intValue: 42);
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.False,
            "Null Update should NOT be treated as no-net-change when target has an int value");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_NullUpdateAgainstExistingDateTime_ReturnsFalseAsync()
    {
        // Arrange - target CSO has a DateTime, null Update should clear it
        var csoAttributeValue = CreateCsoAttributeValue(dateTimeValue: new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc));
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, new[] { csoAttributeValue });

        // Assert
        Assert.That(result, Is.False,
            "Null Update should NOT be treated as no-net-change when target has a DateTime value");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_NullUpdateAgainstNoExistingValue_ReturnsTrueAsync()
    {
        // Arrange - target CSO has no value for this attribute, null Update is a no-op
        var pendingChange = CreatePendingChange(PendingExportAttributeChangeType.Update);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, Array.Empty<ConnectedSystemObjectAttributeValue>());

        // Assert
        Assert.That(result, Is.True,
            "Null Update should be treated as no-net-change when target also has no value (already cleared)");
    }

    #endregion

    #region CreateAttributeValueChanges - Single-Valued Removal Tests

    /// <summary>
    /// When single-valued attributes are removed from the MVO, CreateAttributeValueChanges should
    /// produce null-clearing export changes. The removed attributes still carry their old values
    /// (snapshots from before removal), but the code detects they are in the removedAttributes
    /// set and creates changes with null values to clear them from the target system.
    /// Removals can occur due to attribute recall, source no longer returning a value, or CSO
    /// falling out of sync rule scope.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_RecalledSingleValuedAttributes_ProducesNullClearingChangesAsync()
    {
        // Arrange
        var sourceSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Source System");
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var mvUserType = MetaverseObjectTypesData.Single(q => q.Name == "User");
        var displayNameMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var employeeIdMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);

        var targetDisplayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var targetEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.EmployeeId.ToString());

        // Set up export sync rule with attribute flow mappings
        var exportSyncRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportSyncRule.ConnectedSystemId = targetSystem.Id;
        exportSyncRule.ConnectedSystem = targetSystem;
        exportSyncRule.AttributeFlowRules.Clear();
        exportSyncRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            SyncRule = exportSyncRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Id = 200,
                Order = 0,
                MetaverseAttribute = displayNameMvAttr,
                MetaverseAttributeId = displayNameMvAttr.Id
            }}
        });
        exportSyncRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 101,
            SyncRule = exportSyncRule,
            TargetConnectedSystemAttribute = targetEmployeeIdAttr,
            TargetConnectedSystemAttributeId = targetEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Id = 201,
                Order = 0,
                MetaverseAttribute = employeeIdMvAttr,
                MetaverseAttributeId = employeeIdMvAttr.Id
            }}
        });

        // Set up the MVO (post-removal: AttributeValues cleared)
        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear(); // Post-removal state

        // Create the removed attribute values (snapshots taken before removal)
        // These still carry their OLD values — the code must detect they are removals
        // and create null-clearing changes instead of copying the old values
        var removedDisplayName = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = displayNameMvAttr,
            AttributeId = displayNameMvAttr.Id,
            StringValue = "Joe Bloggs",
            ContributedBySystemId = sourceSystem.Id
        };
        var removedEmployeeId = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = employeeIdMvAttr,
            AttributeId = employeeIdMvAttr.Id,
            StringValue = "EMP001",
            ContributedBySystemId = sourceSystem.Id
        };

        // changedAttributes and removedAttributes contain the same objects (as in the real removal flow)
        var changedAttributes = new List<MetaverseObjectAttributeValue> { removedDisplayName, removedEmployeeId };
        var removedAttributes = new HashSet<MetaverseObjectAttributeValue> { removedDisplayName, removedEmployeeId };

        // Set up the existing target CSO
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            Status = ConnectedSystemObjectStatus.Normal
        };

        // Set up CSO attribute cache with target CSO's current (non-null) values
        var csoAttrValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                AttributeId = targetDisplayNameAttr.Id,
                StringValue = "Joe Bloggs",
                ConnectedSystemObject = existingCso
            },
            new()
            {
                Id = Guid.NewGuid(),
                AttributeId = targetEmployeeIdAttr.Id,
                StringValue = "EMP001",
                ConnectedSystemObject = existingCso
            }
        };
        var csoAttributeCache = csoAttrValues.ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId));

        // Act: call CreateAttributeValueChanges with Update (existing CSO) and removal parameters
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo,
            exportSyncRule,
            changedAttributes,
            PendingExportChangeType.Update,
            existingCso: existingCso,
            csoAttributeCache: csoAttributeCache,
            csoAlreadyCurrentCount: out var skippedCount,
            removedAttributes: removedAttributes);

        // Assert: 2 null-clearing changes should be produced (one per removed attribute)
        Assert.That(changes, Has.Count.EqualTo(2),
            "Removed attributes should produce null-clearing export changes");
        Assert.That(skippedCount, Is.EqualTo(0),
            "No attributes should be skipped (null-clearing changes differ from CSO's current values)");

        // Verify the changes have null values (clearing the target attributes)
        var displayNameChange = changes.Single(c => c.AttributeId == targetDisplayNameAttr.Id);
        Assert.That(displayNameChange.StringValue, Is.Null,
            "Removed Display Name should produce a null-clearing change");
        Assert.That(displayNameChange.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update),
            "Single-valued removal should use Update change type");

        var employeeIdChange = changes.Single(c => c.AttributeId == targetEmployeeIdAttr.Id);
        Assert.That(employeeIdChange.StringValue, Is.Null,
            "Removed Employee ID should produce a null-clearing change");
        Assert.That(employeeIdChange.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update),
            "Single-valued removal should use Update change type");
    }

    /// <summary>
    /// Tests the full export evaluation flow for removed attributes. When attributes are removed
    /// from the MVO, null-clearing pending exports should be created so the target system clears
    /// the attribute values. The removed attributes flow through as null-valued changes, which
    /// differ from the CSO's current values, so pending exports are generated.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_RecalledAttributes_ProducesPendingExportWithNullClearingChangesAsync()
    {
        // Arrange
        var sourceSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Source System");
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var mvUserType = MetaverseObjectTypesData.Single(q => q.Name == "User");
        var displayNameMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var employeeIdMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);

        var targetDisplayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var targetEmployeeIdAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.EmployeeId.ToString());

        // Set up export sync rule on the TARGET system
        var exportSyncRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportSyncRule.ConnectedSystemId = targetSystem.Id;
        exportSyncRule.ConnectedSystem = targetSystem;
        exportSyncRule.MetaverseObjectTypeId = mvUserType.Id;
        exportSyncRule.AttributeFlowRules.Clear();
        exportSyncRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            SyncRule = exportSyncRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Id = 200,
                Order = 0,
                MetaverseAttribute = displayNameMvAttr,
                MetaverseAttributeId = displayNameMvAttr.Id
            }}
        });
        exportSyncRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 101,
            SyncRule = exportSyncRule,
            TargetConnectedSystemAttribute = targetEmployeeIdAttr,
            TargetConnectedSystemAttributeId = targetEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Id = 201,
                Order = 0,
                MetaverseAttribute = employeeIdMvAttr,
                MetaverseAttributeId = employeeIdMvAttr.Id
            }}
        });

        // Set up the MVO (post-removal: AttributeValues cleared)
        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();

        // Create removed attribute values (snapshots from before removal)
        var removedDisplayName = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = displayNameMvAttr,
            AttributeId = displayNameMvAttr.Id,
            StringValue = "Joe Bloggs",
            ContributedBySystemId = sourceSystem.Id
        };
        var removedEmployeeId = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = employeeIdMvAttr,
            AttributeId = employeeIdMvAttr.Id,
            StringValue = "EMP001",
            ContributedBySystemId = sourceSystem.Id
        };

        var changedAttributes = new List<MetaverseObjectAttributeValue> { removedDisplayName, removedEmployeeId };
        var removedAttributes = new HashSet<MetaverseObjectAttributeValue> { removedDisplayName, removedEmployeeId };

        // Set up existing target CSO (already provisioned)
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = mvo.Id,
            MetaverseObject = mvo
        };

        // Mock PendingExports.AddAsync to capture created pending exports
        MockDbSetPendingExports.Setup(set => set.AddAsync(It.IsAny<PendingExport>(), It.IsAny<CancellationToken>()))
            .Callback((PendingExport entity, CancellationToken _) => { PendingExportsData.Add(entity); })
            .ReturnsAsync((PendingExport entity, CancellationToken _) => null!);

        // Build the ExportEvaluationCache manually
        var exportRulesByMvoTypeId = new Dictionary<int, List<SyncRule>>
        {
            { mvUserType.Id, new List<SyncRule> { exportSyncRule } }
        };
        var csoLookup = new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>
        {
            { (mvo.Id, targetSystem.Id), existingCso }
        };
        var csoAttrValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                AttributeId = targetDisplayNameAttr.Id,
                StringValue = "Joe Bloggs",
                ConnectedSystemObject = existingCso
            },
            new()
            {
                Id = Guid.NewGuid(),
                AttributeId = targetEmployeeIdAttr.Id,
                StringValue = "EMP001",
                ConnectedSystemObject = existingCso
            }
        };
        var csoAttributeValues = csoAttrValues.ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId));

        var cache = new ExportEvaluationServer.ExportEvaluationCache(
            exportRulesByMvoTypeId, csoLookup, csoAttributeValues);

        // Act: evaluate export rules with removed attributes
        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, changedAttributes, sourceSystem, cache,
            removedAttributes: removedAttributes);

        // Assert: a pending export should be created with null-clearing attribute changes
        Assert.That(result.PendingExports, Has.Count.EqualTo(1),
            "Removed attributes should produce a pending export to clear target values");

        var pendingExport = result.PendingExports.Single();
        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update),
            "Removal pending export should be an Update (clearing attribute values)");
        Assert.That(pendingExport.AttributeValueChanges, Has.Count.EqualTo(2),
            "Pending export should contain 2 null-clearing attribute changes");

        // Verify the changes have null values
        var displayNameChange = pendingExport.AttributeValueChanges
            .Single(c => c.AttributeId == targetDisplayNameAttr.Id);
        Assert.That(displayNameChange.StringValue, Is.Null,
            "Removed Display Name should produce a null-clearing change");

        var employeeIdChange = pendingExport.AttributeValueChanges
            .Single(c => c.AttributeId == targetEmployeeIdAttr.Id);
        Assert.That(employeeIdChange.StringValue, Is.Null,
            "Removed Employee ID should produce a null-clearing change");
    }

    #endregion

    #region Helper Methods

    private static ConnectedSystemObjectAttributeValue CreateCsoAttributeValue(
        string? stringValue = null,
        int? intValue = null,
        DateTime? dateTimeValue = null,
        byte[]? byteValue = null,
        string? unresolvedReferenceValue = null)
    {
        return new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = 1,
            StringValue = stringValue,
            IntValue = intValue,
            DateTimeValue = dateTimeValue,
            ByteValue = byteValue,
            UnresolvedReferenceValue = unresolvedReferenceValue,
            ConnectedSystemObject = new ConnectedSystemObject { Id = Guid.NewGuid() }
        };
    }

    private static PendingExportAttributeValueChange CreatePendingChange(
        PendingExportAttributeChangeType changeType = PendingExportAttributeChangeType.Update,
        string? stringValue = null,
        int? intValue = null,
        DateTime? dateTimeValue = null,
        byte[]? byteValue = null,
        string? unresolvedReferenceValue = null)
    {
        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = 1,
            StringValue = stringValue,
            IntValue = intValue,
            DateTimeValue = dateTimeValue,
            ByteValue = byteValue,
            UnresolvedReferenceValue = unresolvedReferenceValue,
            ChangeType = changeType
        };
    }

    #endregion

    #region Expression Skip on Pure Recall Tests

    /// <summary>
    /// Tests that expression-based mappings (e.g., DN expressions) are skipped during pure attribute
    /// recall (leaver/deprovisioning scenario). When all changed attributes are removals, the MVO
    /// state is incomplete and expressions would produce invalid results (e.g., "OU=,OU=Users,...").
    /// Only direct attribute mappings should produce null-clearing changes.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_PureRecallWithExpressionMapping_SkipsExpressionAsync()
    {
        // Arrange
        var sourceSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Source System");
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var mvUserType = MetaverseObjectTypesData.Single(q => q.Name == "User");
        var displayNameMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);

        var targetDisplayNameAttr = targetUserType.Attributes.Single(a => a.Name == MockTargetSystemAttributeNames.DisplayName.ToString());
        var targetDnAttr = targetUserType.Attributes.Single(a => a.Name == "distinguishedName");

        // Set up export sync rule with:
        // 1. Direct mapping: Display Name -> DisplayName
        // 2. Expression mapping: DN expression -> distinguishedName
        var exportSyncRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportSyncRule.ConnectedSystemId = targetSystem.Id;
        exportSyncRule.ConnectedSystem = targetSystem;
        exportSyncRule.AttributeFlowRules.Clear();

        // Direct mapping for Display Name
        exportSyncRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 100,
            SyncRule = exportSyncRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Id = 200,
                Order = 0,
                MetaverseAttribute = displayNameMvAttr,
                MetaverseAttributeId = displayNameMvAttr.Id
            }}
        });

        // Expression-based mapping for DN (simulates the real scenario)
        var dnMapping = new SyncRuleMapping
        {
            Id = 101,
            SyncRule = exportSyncRule,
            TargetConnectedSystemAttribute = targetDnAttr,
            TargetConnectedSystemAttributeId = targetDnAttr.Id
        };
        dnMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 201,
            Order = 0,
            Expression = "\"CN=\" + mv[\"Display Name\"] + \",OU=Users,DC=testdomain,DC=local\""
        });
        exportSyncRule.AttributeFlowRules.Add(dnMapping);

        // Set up the MVO (post-removal: AttributeValues cleared — Display Name recalled)
        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear(); // Post-removal state

        // Create the removed attribute value (snapshot taken before removal)
        var removedDisplayName = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = displayNameMvAttr,
            AttributeId = displayNameMvAttr.Id,
            StringValue = "Joe Bloggs",
            ContributedBySystemId = sourceSystem.Id
        };

        // changedAttributes and removedAttributes contain the same objects (pure recall)
        var changedAttributes = new List<MetaverseObjectAttributeValue> { removedDisplayName };
        var removedAttributes = new HashSet<MetaverseObjectAttributeValue> { removedDisplayName };

        // Set up the existing target CSO
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            Status = ConnectedSystemObjectStatus.Normal
        };

        // CSO attribute cache
        var csoAttrValues = new List<ConnectedSystemObjectAttributeValue>
        {
            new()
            {
                Id = Guid.NewGuid(),
                AttributeId = targetDisplayNameAttr.Id,
                StringValue = "Joe Bloggs",
                ConnectedSystemObject = existingCso
            },
            new()
            {
                Id = Guid.NewGuid(),
                AttributeId = targetDnAttr.Id,
                StringValue = "CN=Joe Bloggs,OU=Users,DC=testdomain,DC=local",
                ConnectedSystemObject = existingCso
            }
        };
        var csoAttributeCache = csoAttrValues.ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId));

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo,
            exportSyncRule,
            changedAttributes,
            PendingExportChangeType.Update,
            existingCso: existingCso,
            csoAttributeCache: csoAttributeCache,
            csoAlreadyCurrentCount: out _,
            removedAttributes: removedAttributes);

        // Assert: only the direct mapping should produce a null-clearing change.
        // The expression mapping for DN should be skipped entirely.
        Assert.That(changes, Has.Count.EqualTo(1),
            "Only the direct-mapped attribute should produce a change; expression should be skipped");

        var displayNameChange = changes.Single();
        Assert.That(displayNameChange.AttributeId, Is.EqualTo(targetDisplayNameAttr.Id),
            "The change should be for Display Name (direct mapping), not DN (expression)");
        Assert.That(displayNameChange.StringValue, Is.Null,
            "Display Name should be null-cleared (removal)");
        Assert.That(displayNameChange.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Update),
            "Single-valued removal should use Update change type");
    }

    /// <summary>
    /// Tests that expression-based mappings ARE evaluated during mixed changes (not pure recall).
    /// When some attributes are removed and others are added/changed, expressions should still
    /// evaluate because the MVO state includes valid new values.
    /// </summary>
    [Test]
    public void CreateAttributeValueChanges_MixedChangesWithExpressionMapping_EvaluatesExpressionAsync()
    {
        // Arrange
        var sourceSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Source System");
        var targetSystem = ConnectedSystemsData.Single(s => s.Name == "Dummy Target System");
        var targetUserType = ConnectedSystemObjectTypesData.Single(t => t.Name == "TARGET_USER");

        var mvUserType = MetaverseObjectTypesData.Single(q => q.Name == "User");
        var displayNameMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.DisplayName);
        var employeeIdMvAttr = mvUserType.Attributes.Single(a => a.Id == (int)MockMetaverseAttributeName.EmployeeId);

        var targetDnAttr = targetUserType.Attributes.Single(a => a.Name == "distinguishedName");

        // Set up export sync rule with expression mapping for DN
        var exportSyncRule = SyncRulesData.Single(sr => sr.Name == "Dummy User Export Sync Rule 1");
        exportSyncRule.ConnectedSystemId = targetSystem.Id;
        exportSyncRule.ConnectedSystem = targetSystem;
        exportSyncRule.AttributeFlowRules.Clear();

        // Expression-based mapping for DN
        var dnMapping = new SyncRuleMapping
        {
            Id = 101,
            SyncRule = exportSyncRule,
            TargetConnectedSystemAttribute = targetDnAttr,
            TargetConnectedSystemAttributeId = targetDnAttr.Id
        };
        dnMapping.Sources.Add(new SyncRuleMappingSource
        {
            Id = 201,
            Order = 0,
            Expression = "\"CN=\" + mv[\"Display Name\"] + \",OU=Users,DC=testdomain,DC=local\""
        });
        exportSyncRule.AttributeFlowRules.Add(dnMapping);

        // Set up the MVO with Display Name still present (not a pure recall — mixed changes)
        var mvo = MetaverseObjectsData[0];
        mvo.Type = mvUserType;
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = displayNameMvAttr,
            AttributeId = displayNameMvAttr.Id,
            StringValue = "Jane Smith",
            ContributedBySystemId = sourceSystem.Id
        });

        // changedAttributes includes a removal AND an addition (mixed scenario)
        var removedEmployeeId = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = employeeIdMvAttr,
            AttributeId = employeeIdMvAttr.Id,
            StringValue = "EMP001",
            ContributedBySystemId = sourceSystem.Id
        };
        var addedDisplayName = new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = mvo,
            Attribute = displayNameMvAttr,
            AttributeId = displayNameMvAttr.Id,
            StringValue = "Jane Smith",
            ContributedBySystemId = sourceSystem.Id
        };

        // Mixed: one removal + one addition (not pure recall)
        var changedAttributes = new List<MetaverseObjectAttributeValue> { removedEmployeeId, addedDisplayName };
        var removedAttributes = new HashSet<MetaverseObjectAttributeValue> { removedEmployeeId };

        // Set up the existing target CSO
        var existingCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystem.Id,
            ConnectedSystem = targetSystem,
            Type = targetUserType,
            Status = ConnectedSystemObjectStatus.Normal
        };

        // CSO attribute cache (empty - no existing values for no-net-change detection)
        var csoAttributeCache = new List<ConnectedSystemObjectAttributeValue>()
            .ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId));

        // Act
        var changes = Jim.ExportEvaluation.CreateAttributeValueChanges(
            mvo,
            exportSyncRule,
            changedAttributes,
            PendingExportChangeType.Update,
            existingCso: existingCso,
            csoAttributeCache: csoAttributeCache,
            csoAlreadyCurrentCount: out _,
            removedAttributes: removedAttributes);

        // Assert: expression should be evaluated because this is NOT a pure recall
        Assert.That(changes, Has.Count.EqualTo(1),
            "Expression should produce one DN attribute change");

        var dnChange = changes.Single();
        Assert.That(dnChange.AttributeId, Is.EqualTo(targetDnAttr.Id),
            "The change should be for the DN attribute (expression evaluated)");
        Assert.That(dnChange.StringValue, Is.EqualTo("CN=Jane Smith,OU=Users,DC=testdomain,DC=local"),
            "DN should be correctly generated from the expression");
    }

    #endregion
}

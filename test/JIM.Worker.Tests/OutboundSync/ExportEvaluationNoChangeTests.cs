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

    #region String Value Comparison Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_StringMatch_ReturnsTrueAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "John Smith");
        var pendingChange = CreatePendingChange(stringValue: "John Smith");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.True, "Should return true when string values match");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_StringMismatch_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "John Smith");
        var pendingChange = CreatePendingChange(stringValue: "Jane Doe");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.False, "Should return false when string values differ");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_StringCaseSensitive_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "John Smith");
        var pendingChange = CreatePendingChange(stringValue: "JOHN SMITH");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.False, "String comparison should be case-sensitive");
    }

    #endregion

    #region Integer Value Comparison Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_IntMatch_ReturnsTrueAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(intValue: 12345);
        var pendingChange = CreatePendingChange(intValue: 12345);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.True, "Should return true when int values match");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_IntMismatch_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(intValue: 12345);
        var pendingChange = CreatePendingChange(intValue: 54321);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.False, "Should return false when int values differ");
    }

    #endregion

    #region DateTime Value Comparison Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_DateTimeMatch_ReturnsTrueAsync()
    {
        // Arrange
        var dateValue = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var csoAttributeValue = CreateCsoAttributeValue(dateTimeValue: dateValue);
        var pendingChange = CreatePendingChange(dateTimeValue: dateValue);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.True, "Should return true when DateTime values match");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_DateTimeMismatch_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(dateTimeValue: new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc));
        var pendingChange = CreatePendingChange(dateTimeValue: new DateTime(2024, 1, 16, 10, 30, 0, DateTimeKind.Utc));

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.False, "Should return false when DateTime values differ");
    }

    #endregion

    #region Binary Value Comparison Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_BinaryMatch_ReturnsTrueAsync()
    {
        // Arrange
        var binaryValue = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var csoAttributeValue = CreateCsoAttributeValue(byteValue: binaryValue);
        var pendingChange = CreatePendingChange(byteValue: new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.True, "Should return true when binary values match");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_BinaryMismatch_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(byteValue: new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        var pendingChange = CreatePendingChange(byteValue: new byte[] { 0x01, 0x02, 0x03, 0x04, 0xFF });

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.False, "Should return false when binary values differ");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_BinaryDifferentLength_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(byteValue: new byte[] { 0x01, 0x02, 0x03 });
        var pendingChange = CreatePendingChange(byteValue: new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.False, "Should return false when binary arrays have different lengths");
    }

    #endregion

    #region Guid Value Comparison Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_GuidMatch_ReturnsTrueAsync()
    {
        // Arrange - Guid is stored as string in PendingExportAttributeValueChange
        var guidValue = Guid.NewGuid();
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: guidValue.ToString());
        var pendingChange = CreatePendingChange(stringValue: guidValue.ToString());

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.True, "Should return true when Guid values (as strings) match");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_GuidMismatch_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: Guid.NewGuid().ToString());
        var pendingChange = CreatePendingChange(stringValue: Guid.NewGuid().ToString());

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.False, "Should return false when Guid values differ");
    }

    #endregion

    #region Bool Value Comparison Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_BoolTrueMatch_ReturnsTrueAsync()
    {
        // Arrange - Bool is stored as string in PendingExportAttributeValueChange
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "True");
        var pendingChange = CreatePendingChange(stringValue: "True");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.True, "Should return true when bool values (as strings) match");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_BoolFalseMatch_ReturnsTrueAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "False");
        var pendingChange = CreatePendingChange(stringValue: "False");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.True, "Should return true when bool False values match");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_BoolMismatch_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "True");
        var pendingChange = CreatePendingChange(stringValue: "False");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.False, "Should return false when bool values differ");
    }

    #endregion

    #region Unresolved Reference Value Comparison Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UnresolvedReferenceMatch_ReturnsTrueAsync()
    {
        // Arrange
        var referenceValue = "CN=Manager,OU=Users,DC=corp,DC=local";
        var csoAttributeValue = CreateCsoAttributeValue(unresolvedReferenceValue: referenceValue);
        var pendingChange = CreatePendingChange(unresolvedReferenceValue: referenceValue);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.True, "Should return true when unresolved reference values match");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UnresolvedReferenceMismatch_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(unresolvedReferenceValue: "CN=Manager1,OU=Users,DC=corp,DC=local");
        var pendingChange = CreatePendingChange(unresolvedReferenceValue: "CN=Manager2,OU=Users,DC=corp,DC=local");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.False, "Should return false when unresolved reference values differ");
    }

    #endregion

    #region Null Handling Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_BothNull_ReturnsTrueAsync()
    {
        // Arrange - CSO attribute doesn't exist (null)
        var pendingChange = CreatePendingChange(); // Empty pending change (no value set)

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, null);

        // Assert
        Assert.That(result, Is.True, "Should return true when both are null/empty");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_PendingNullCsoHasValue_ReturnsFalseAsync()
    {
        // Arrange
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "SomeValue");
        var pendingChange = CreatePendingChange(); // Empty pending change

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.False, "Should return false when pending is empty but CSO has value");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_PendingHasValueCsoNull_ReturnsFalseAsync()
    {
        // Arrange
        var pendingChange = CreatePendingChange(stringValue: "NewValue");

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, null);

        // Assert
        Assert.That(result, Is.False, "Should return false when CSO is null but pending has value");
    }

    #endregion

    #region Priority Value Type Tests

    [Test]
    public void IsCsoAttributeAlreadyCurrent_StringHasPriority_ReturnsTrueAsync()
    {
        // Arrange - String value takes priority in comparison logic
        // In practice, attributes only have one value type set at a time
        // But if multiple are set, string is checked first
        var csoAttributeValue = CreateCsoAttributeValue(stringValue: "Test", intValue: 42);
        var pendingChange = CreatePendingChange(stringValue: "Test", intValue: 99);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.True, "String match takes priority when string is set");
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_OnlyIntSet_ComparesIntAsync()
    {
        // Arrange - When only int is set, it's compared
        var csoAttributeValue = CreateCsoAttributeValue(intValue: 42);
        var pendingChange = CreatePendingChange(intValue: 99);

        // Act
        var result = ExportEvaluationServer.IsCsoAttributeAlreadyCurrent(pendingChange, csoAttributeValue);

        // Assert
        Assert.That(result, Is.False, "Should return false when int values differ");
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
            ChangeType = PendingExportAttributeChangeType.Update
        };
    }

    #endregion
}

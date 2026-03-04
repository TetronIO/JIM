using JIM.Application.Utilities;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Worker.Tests.Utilities;

/// <summary>
/// Tests for <see cref="ExportChangeHistoryBuilder"/> to ensure export change history records
/// are correctly built from pending export data.
/// </summary>
[TestFixture]
public class ExportChangeHistoryBuilderTests
{
    private ConnectedSystemObjectTypeAttribute _textAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _numberAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _guidAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _boolAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _dateTimeAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _binaryAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _referenceAttribute = null!;
    private ConnectedSystemObjectTypeAttribute _longNumberAttribute = null!;

    [SetUp]
    public void SetUp()
    {
        _textAttribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "displayName", Type = AttributeDataType.Text };
        _numberAttribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "employeeId", Type = AttributeDataType.Number };
        _guidAttribute = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "objectGuid", Type = AttributeDataType.Guid };
        _boolAttribute = new ConnectedSystemObjectTypeAttribute { Id = 4, Name = "isActive", Type = AttributeDataType.Boolean };
        _dateTimeAttribute = new ConnectedSystemObjectTypeAttribute { Id = 5, Name = "hireDate", Type = AttributeDataType.DateTime };
        _binaryAttribute = new ConnectedSystemObjectTypeAttribute { Id = 6, Name = "photo", Type = AttributeDataType.Binary };
        _referenceAttribute = new ConnectedSystemObjectTypeAttribute { Id = 7, Name = "manager", Type = AttributeDataType.Reference };
        _longNumberAttribute = new ConnectedSystemObjectTypeAttribute { Id = 8, Name = "bigNumber", Type = AttributeDataType.LongNumber };
    }

    #region BuildFromProcessedExportItem

    [Test]
    public void BuildFromProcessedExportItem_WithUpdateChangeType_SetsExportedObjectChangeTypeAsync()
    {
        // Arrange
        var exportItem = CreateExportItem(PendingExportChangeType.Update);

        // Act
        var result = ExportChangeHistoryBuilder.BuildFromProcessedExportItem(
            exportItem, connectedSystemId: 42, CreateRpei(),
            ActivityInitiatorType.System, Guid.NewGuid(), "Nightly Sync");

        // Assert
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.Exported));
        Assert.That(result.ConnectedSystemId, Is.EqualTo(42));
    }

    [Test]
    public void BuildFromProcessedExportItem_WithDeleteChangeType_SetsDeprovisionedObjectChangeType()
    {
        // Arrange
        var exportItem = CreateExportItem(PendingExportChangeType.Delete);

        // Act
        var result = ExportChangeHistoryBuilder.BuildFromProcessedExportItem(
            exportItem, connectedSystemId: 42, CreateRpei(),
            ActivityInitiatorType.User, Guid.NewGuid(), "Admin User");

        // Assert
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.Deprovisioned));
    }

    [Test]
    public void BuildFromProcessedExportItem_SetsInitiatorFields()
    {
        // Arrange
        var exportItem = CreateExportItem(PendingExportChangeType.Update);
        var initiatorId = Guid.NewGuid();

        // Act
        var result = ExportChangeHistoryBuilder.BuildFromProcessedExportItem(
            exportItem, connectedSystemId: 1, CreateRpei(),
            ActivityInitiatorType.User, initiatorId, "Test User");

        // Assert
        Assert.That(result.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(result.InitiatedById, Is.EqualTo(initiatorId));
        Assert.That(result.InitiatedByName, Is.EqualTo("Test User"));
    }

    [Test]
    public void BuildFromProcessedExportItem_LinksToExecutionItem()
    {
        // Arrange
        var exportItem = CreateExportItem(PendingExportChangeType.Update);
        var rpei = CreateRpei();

        // Act
        var result = ExportChangeHistoryBuilder.BuildFromProcessedExportItem(
            exportItem, connectedSystemId: 1, rpei,
            ActivityInitiatorType.System, null, null);

        // Assert
        Assert.That(result.ActivityRunProfileExecutionItem, Is.SameAs(rpei));
        Assert.That(result.ActivityRunProfileExecutionItemId, Is.EqualTo(rpei.Id));
    }

    [Test]
    public void BuildFromProcessedExportItem_MapsTextAttributeChange()
    {
        // Arrange
        var exportItem = CreateExportItem(PendingExportChangeType.Update,
            new PendingExportAttributeValueChange
            {
                Attribute = _textAttribute,
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = "John Smith"
            });

        // Act
        var result = ExportChangeHistoryBuilder.BuildFromProcessedExportItem(
            exportItem, connectedSystemId: 1, CreateRpei(),
            ActivityInitiatorType.System, null, null);

        // Assert
        Assert.That(result.AttributeChanges, Has.Count.EqualTo(1));
        var attrChange = result.AttributeChanges.First();
        Assert.That(attrChange.Attribute.Name, Is.EqualTo("displayName"));
        Assert.That(attrChange.ValueChanges, Has.Count.EqualTo(1));
        Assert.That(attrChange.ValueChanges.First().StringValue, Is.EqualTo("John Smith"));
        Assert.That(attrChange.ValueChanges.First().ValueChangeType, Is.EqualTo(ValueChangeType.Add));
    }

    #endregion

    #region BuildFromPendingExport

    [Test]
    public void BuildFromPendingExport_SetsPendingExportObjectChangeType()
    {
        // Arrange
        var pendingExport = CreatePendingExport();

        // Act
        var result = ExportChangeHistoryBuilder.BuildFromPendingExport(
            pendingExport, ActivityInitiatorType.System, null, null);

        // Assert
        Assert.That(result.ChangeType, Is.EqualTo(ObjectChangeType.PendingExport));
        Assert.That(result.ConnectedSystemId, Is.EqualTo(pendingExport.ConnectedSystemId));
    }

    [Test]
    public void BuildFromPendingExport_SetsInitiatorFields()
    {
        // Arrange
        var pendingExport = CreatePendingExport();
        var initiatorId = Guid.NewGuid();

        // Act
        var result = ExportChangeHistoryBuilder.BuildFromPendingExport(
            pendingExport, ActivityInitiatorType.User, initiatorId, "Admin");

        // Assert
        Assert.That(result.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(result.InitiatedById, Is.EqualTo(initiatorId));
        Assert.That(result.InitiatedByName, Is.EqualTo("Admin"));
    }

    [Test]
    public void BuildFromPendingExport_MapsAttributeChanges()
    {
        // Arrange
        var pendingExport = CreatePendingExport(
            new PendingExportAttributeValueChange
            {
                Attribute = _textAttribute,
                ChangeType = PendingExportAttributeChangeType.Update,
                StringValue = "Updated Name"
            });

        // Act
        var result = ExportChangeHistoryBuilder.BuildFromPendingExport(
            pendingExport, ActivityInitiatorType.System, null, null);

        // Assert
        Assert.That(result.AttributeChanges, Has.Count.EqualTo(1));
        Assert.That(result.AttributeChanges.First().ValueChanges.First().StringValue, Is.EqualTo("Updated Name"));
    }

    #endregion

    #region MapChangeType

    [Test]
    public void MapChangeType_Add_ReturnsAdd()
    {
        Assert.That(ExportChangeHistoryBuilder.MapChangeType(PendingExportAttributeChangeType.Add),
            Is.EqualTo(ValueChangeType.Add));
    }

    [Test]
    public void MapChangeType_Update_ReturnsAdd()
    {
        // Update sets a new value, which is semantically an Add in the change history model
        Assert.That(ExportChangeHistoryBuilder.MapChangeType(PendingExportAttributeChangeType.Update),
            Is.EqualTo(ValueChangeType.Add));
    }

    [Test]
    public void MapChangeType_Remove_ReturnsRemove()
    {
        Assert.That(ExportChangeHistoryBuilder.MapChangeType(PendingExportAttributeChangeType.Remove),
            Is.EqualTo(ValueChangeType.Remove));
    }

    [Test]
    public void MapChangeType_RemoveAll_ReturnsRemove()
    {
        Assert.That(ExportChangeHistoryBuilder.MapChangeType(PendingExportAttributeChangeType.RemoveAll),
            Is.EqualTo(ValueChangeType.Remove));
    }

    #endregion

    #region MapAttributeValueChanges — all data types

    [Test]
    public void MapAttributeValueChanges_NumberAttribute_MapsIntValue()
    {
        // Arrange
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _numberAttribute, ChangeType = PendingExportAttributeChangeType.Add, IntValue = 12345 }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges);

        // Assert
        Assert.That(change.AttributeChanges, Has.Count.EqualTo(1));
        Assert.That(change.AttributeChanges.First().ValueChanges.First().IntValue, Is.EqualTo(12345));
    }

    [Test]
    public void MapAttributeValueChanges_LongNumberAttribute_MapsLongValue()
    {
        // Arrange
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _longNumberAttribute, ChangeType = PendingExportAttributeChangeType.Add, LongValue = 9999999999L }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges);

        // Assert
        Assert.That(change.AttributeChanges.First().ValueChanges.First().LongValue, Is.EqualTo(9999999999L));
    }

    [Test]
    public void MapAttributeValueChanges_GuidAttribute_MapsGuidValue()
    {
        // Arrange
        var testGuid = Guid.NewGuid();
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _guidAttribute, ChangeType = PendingExportAttributeChangeType.Add, GuidValue = testGuid }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges);

        // Assert
        Assert.That(change.AttributeChanges.First().ValueChanges.First().GuidValue, Is.EqualTo(testGuid));
    }

    [Test]
    public void MapAttributeValueChanges_BooleanAttribute_MapsBoolValue()
    {
        // Arrange
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _boolAttribute, ChangeType = PendingExportAttributeChangeType.Add, BoolValue = true }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges);

        // Assert
        Assert.That(change.AttributeChanges.First().ValueChanges.First().BoolValue, Is.True);
    }

    [Test]
    public void MapAttributeValueChanges_DateTimeAttribute_MapsDateTimeValue()
    {
        // Arrange
        var testDate = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _dateTimeAttribute, ChangeType = PendingExportAttributeChangeType.Add, DateTimeValue = testDate }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges);

        // Assert
        Assert.That(change.AttributeChanges.First().ValueChanges.First().DateTimeValue, Is.EqualTo(testDate));
    }

    [Test]
    public void MapAttributeValueChanges_BinaryAttribute_MapsBinaryMetadata()
    {
        // Arrange
        var binaryData = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _binaryAttribute, ChangeType = PendingExportAttributeChangeType.Add, ByteValue = binaryData }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges);

        // Assert
        var valueChange = change.AttributeChanges.First().ValueChanges.First();
        Assert.That(valueChange.ByteValueLength, Is.EqualTo(4));
    }

    [Test]
    public void MapAttributeValueChanges_ReferenceAttribute_MapsUnresolvedReferenceAsString()
    {
        // Arrange
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _referenceAttribute, ChangeType = PendingExportAttributeChangeType.Add, UnresolvedReferenceValue = "CN=Manager,OU=Users" }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges);

        // Assert
        Assert.That(change.AttributeChanges.First().ValueChanges.First().StringValue, Is.EqualTo("CN=Manager,OU=Users"));
    }

    [Test]
    public void MapAttributeValueChanges_MultipleChangesForSameAttribute_GroupsUnderOneAttributeChange()
    {
        // Arrange — two value changes for the same multi-valued attribute
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _textAttribute, ChangeType = PendingExportAttributeChangeType.Add, StringValue = "Value 1" },
            new() { Attribute = _textAttribute, ChangeType = PendingExportAttributeChangeType.Add, StringValue = "Value 2" }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges);

        // Assert — one attribute change with two value changes
        Assert.That(change.AttributeChanges, Has.Count.EqualTo(1));
        Assert.That(change.AttributeChanges.First().ValueChanges, Has.Count.EqualTo(2));
    }

    [Test]
    public void MapAttributeValueChanges_MultipleAttributes_CreatesMultipleAttributeChanges()
    {
        // Arrange
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _textAttribute, ChangeType = PendingExportAttributeChangeType.Add, StringValue = "Name" },
            new() { Attribute = _numberAttribute, ChangeType = PendingExportAttributeChangeType.Add, IntValue = 100 }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges);

        // Assert
        Assert.That(change.AttributeChanges, Has.Count.EqualTo(2));
    }

    [Test]
    public void MapAttributeValueChanges_RemoveAllWithNullValue_CreatesAttributeChangeWithNoValues()
    {
        // Arrange — RemoveAll typically has no value, just clears the attribute
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _textAttribute, ChangeType = PendingExportAttributeChangeType.RemoveAll, StringValue = null }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges);

        // Assert — attribute change created but no value change (null value skipped)
        Assert.That(change.AttributeChanges, Has.Count.EqualTo(1));
        Assert.That(change.AttributeChanges.First().ValueChanges, Has.Count.EqualTo(0));
    }

    [Test]
    public void MapAttributeValueChanges_EmptyList_CreatesNoAttributeChanges()
    {
        // Arrange
        var change = new ConnectedSystemObjectChange();

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, []);

        // Assert
        Assert.That(change.AttributeChanges, Has.Count.EqualTo(0));
    }

    #endregion

    #region Helpers

    private ProcessedExportItem CreateExportItem(
        PendingExportChangeType changeType,
        params PendingExportAttributeValueChange[] attributeChanges)
    {
        return new ProcessedExportItem
        {
            ConnectedSystemObject = new ConnectedSystemObject(),
            ChangeType = changeType,
            Succeeded = true,
            AttributeValueChanges = attributeChanges.ToList()
        };
    }

    private static ActivityRunProfileExecutionItem CreateRpei()
    {
        return new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Exported
        };
    }

    private PendingExport CreatePendingExport(params PendingExportAttributeValueChange[] attributeChanges)
    {
        return new PendingExport
        {
            ConnectedSystemId = 10,
            ConnectedSystemObject = new ConnectedSystemObject(),
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges = attributeChanges.ToList()
        };
    }

    #endregion
}

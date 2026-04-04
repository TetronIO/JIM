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
    public void BuildFromProcessedExportItem_WithEmptyRpeiId_FkMustBeUpdatedAfterIdAssignment()
    {
        // Regression test: ExportChangeHistoryBuilder sets ActivityRunProfileExecutionItemId
        // from executionItem.Id at creation time. If the RPEI ID is Guid.Empty (not yet assigned),
        // the FK will be Guid.Empty. Callers (ProcessExportResultAsync) must fix up the FK after
        // assigning the RPEI ID — otherwise BulkInsertCsoChangesRawAsync hits a unique constraint
        // violation because all CSO changes share the same Guid.Empty FK.
        //
        // This test documents the expected behaviour: the FK captures whatever ID the RPEI has
        // at build time, and callers are responsible for fixing it up later.

        // Arrange — RPEI with Guid.Empty ID (as created by ProcessExportResultAsync before ID assignment)
        var exportItem = CreateExportItem(PendingExportChangeType.Update,
            new PendingExportAttributeValueChange
            {
                Attribute = _textAttribute,
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = "test"
            });
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.Empty,
            ActivityId = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Exported
        };

        // Act — build with Guid.Empty RPEI ID, then simulate ProcessExportResultAsync fix-up
        var change = ExportChangeHistoryBuilder.BuildFromProcessedExportItem(
            exportItem, connectedSystemId: 1, rpei,
            ActivityInitiatorType.System, null, null);

        // Verify FK is initially Guid.Empty (the bug scenario)
        Assert.That(change.ActivityRunProfileExecutionItemId, Is.EqualTo(Guid.Empty));

        // Simulate the fix-up in ProcessExportResultAsync
        rpei.Id = Guid.NewGuid();
        change.ActivityRunProfileExecutionItemId = rpei.Id;

        // Assert — FK now matches the assigned RPEI ID
        Assert.That(change.ActivityRunProfileExecutionItemId, Is.EqualTo(rpei.Id));
        Assert.That(change.ActivityRunProfileExecutionItemId, Is.Not.EqualTo(Guid.Empty));
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
        Assert.That(attrChange.Attribute!.Name, Is.EqualTo("displayName"));
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
    public void MapAttributeValueChanges_ResolvedReferenceAttribute_MapsStringValueAsReference()
    {
        // Arrange — deferred export with resolved reference (UnresolvedReferenceValue cleared, StringValue set)
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _referenceAttribute, ChangeType = PendingExportAttributeChangeType.Add, StringValue = "CN=User1,OU=Users,DC=test,DC=local" }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges);

        // Assert — resolved DN stored as string value with pending stub flag
        var valueChange = change.AttributeChanges.First().ValueChanges.First();
        Assert.That(valueChange.StringValue, Is.EqualTo("CN=User1,OU=Users,DC=test,DC=local"));
        Assert.That(valueChange.IsPendingExportStub, Is.True);
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
    public void MapAttributeValueChanges_NullAttribute_SkipsEntry()
    {
        // Arrange — PE attribute value change with null Attribute (navigation not loaded)
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = null!, ChangeType = PendingExportAttributeChangeType.Add, StringValue = "test" }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges);

        // Assert — entry skipped, no attribute changes created
        Assert.That(change.AttributeChanges, Has.Count.EqualTo(0));
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

    #region MapAttributeValueChanges — resolved pending export references

    [Test]
    public void MapAttributeValueChanges_ReferenceWithResolvedStubCso_StoresDisplayNameWithPendingFlag()
    {
        // Arrange — MVO GUID in UnresolvedReferenceValue, with a matching stub CSO that has a secondary external ID
        var mvoGuid = Guid.NewGuid();
        var secondaryIdAttr = new ConnectedSystemObjectTypeAttribute { Id = 100, Name = "distinguishedName", Type = AttributeDataType.Text };
        var stubCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = mvoGuid,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            SecondaryExternalIdAttributeId = 100,
            ExternalIdAttributeId = 99,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { AttributeId = 100, Attribute = secondaryIdAttr, StringValue = "CN=User1,OU=Users,DC=test,DC=local" }
            }
        };

        var resolvedReferences = new Dictionary<Guid, ConnectedSystemObject> { { mvoGuid, stubCso } };

        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _referenceAttribute, ChangeType = PendingExportAttributeChangeType.Add, UnresolvedReferenceValue = mvoGuid.ToString() }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges, resolvedReferences);

        // Assert — stored as StringValue with the display identifier and IsPendingExportStub flag
        var valueChange = change.AttributeChanges.First().ValueChanges.First();
        Assert.That(valueChange.StringValue, Is.EqualTo("CN=User1,OU=Users,DC=test,DC=local"));
        Assert.That(valueChange.IsPendingExportStub, Is.True);
        Assert.That(valueChange.ReferenceValue, Is.Null);
    }

    [Test]
    public void MapAttributeValueChanges_ReferenceWithNoResolvedCso_FallsBackToStringValue()
    {
        // Arrange — MVO GUID with no matching stub CSO in the lookup
        var mvoGuid = Guid.NewGuid();
        var resolvedReferences = new Dictionary<Guid, ConnectedSystemObject>();

        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _referenceAttribute, ChangeType = PendingExportAttributeChangeType.Add, UnresolvedReferenceValue = mvoGuid.ToString() }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges, resolvedReferences);

        // Assert — falls back to the existing behaviour (raw string)
        var valueChange = change.AttributeChanges.First().ValueChanges.First();
        Assert.That(valueChange.StringValue, Is.EqualTo(mvoGuid.ToString()));
        Assert.That(valueChange.ReferenceValue, Is.Null);
        Assert.That(valueChange.IsPendingExportStub, Is.False);
    }

    [Test]
    public void MapAttributeValueChanges_ReferenceWithNullResolvedReferences_FallsBackToStringValue()
    {
        // Arrange — no resolved references dictionary provided (null)
        var mvoGuid = Guid.NewGuid();
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _referenceAttribute, ChangeType = PendingExportAttributeChangeType.Add, UnresolvedReferenceValue = mvoGuid.ToString() }
        };

        // Act — null resolvedReferences (backwards compatible)
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges, null);

        // Assert — same as existing behaviour
        var valueChange = change.AttributeChanges.First().ValueChanges.First();
        Assert.That(valueChange.StringValue, Is.EqualTo(mvoGuid.ToString()));
        Assert.That(valueChange.ReferenceValue, Is.Null);
    }

    [Test]
    public void MapAttributeValueChanges_ReferenceWithNonGuidUnresolvedValue_FallsBackToStringValue()
    {
        // Arrange — UnresolvedReferenceValue is a DN string, not a GUID (e.g. from an LDAP import)
        var resolvedReferences = new Dictionary<Guid, ConnectedSystemObject>();
        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _referenceAttribute, ChangeType = PendingExportAttributeChangeType.Add, UnresolvedReferenceValue = "CN=Manager,OU=Users,DC=test,DC=local" }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges, resolvedReferences);

        // Assert — non-GUID values cannot be looked up, stored as string
        var valueChange = change.AttributeChanges.First().ValueChanges.First();
        Assert.That(valueChange.StringValue, Is.EqualTo("CN=Manager,OU=Users,DC=test,DC=local"));
        Assert.That(valueChange.ReferenceValue, Is.Null);
    }

    [Test]
    public void MapAttributeValueChanges_MultipleReferencesWithMixedResolution_ResolvesCorrectly()
    {
        // Arrange — two references: one resolvable, one not
        var mvoGuid1 = Guid.NewGuid();
        var mvoGuid2 = Guid.NewGuid();
        var secondaryIdAttr = new ConnectedSystemObjectTypeAttribute { Id = 100, Name = "distinguishedName", Type = AttributeDataType.Text };
        var stubCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = mvoGuid1,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            SecondaryExternalIdAttributeId = 100,
            ExternalIdAttributeId = 99,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { AttributeId = 100, Attribute = secondaryIdAttr, StringValue = "CN=User1,OU=Users,DC=test,DC=local" }
            }
        };

        var resolvedReferences = new Dictionary<Guid, ConnectedSystemObject> { { mvoGuid1, stubCso } };

        var change = new ConnectedSystemObjectChange();
        var peChanges = new List<PendingExportAttributeValueChange>
        {
            new() { Attribute = _referenceAttribute, ChangeType = PendingExportAttributeChangeType.Add, UnresolvedReferenceValue = mvoGuid1.ToString() },
            new() { Attribute = _referenceAttribute, ChangeType = PendingExportAttributeChangeType.Add, UnresolvedReferenceValue = mvoGuid2.ToString() }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, peChanges, resolvedReferences);

        // Assert — first reference resolved with display name, second falls back to raw GUID string
        var valueChanges = change.AttributeChanges.First().ValueChanges;
        Assert.That(valueChanges, Has.Count.EqualTo(2));

        var resolved = valueChanges.First(vc => vc.IsPendingExportStub);
        Assert.That(resolved.StringValue, Is.EqualTo("CN=User1,OU=Users,DC=test,DC=local"));
        Assert.That(resolved.ReferenceValue, Is.Null);

        var unresolved = valueChanges.First(vc => !vc.IsPendingExportStub);
        Assert.That(unresolved.StringValue, Is.EqualTo(mvoGuid2.ToString()));
    }

    [Test]
    public void BuildFromPendingExport_WithResolvedReferences_PassesToMapAttributeValueChanges()
    {
        // Arrange
        var mvoGuid = Guid.NewGuid();
        var secondaryIdAttr = new ConnectedSystemObjectTypeAttribute { Id = 100, Name = "distinguishedName", Type = AttributeDataType.Text };
        var stubCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = mvoGuid,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            SecondaryExternalIdAttributeId = 100,
            ExternalIdAttributeId = 99,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { AttributeId = 100, Attribute = secondaryIdAttr, StringValue = "CN=User1,OU=Users,DC=test,DC=local" }
            }
        };

        var resolvedReferences = new Dictionary<Guid, ConnectedSystemObject> { { mvoGuid, stubCso } };

        var pendingExport = CreatePendingExport(
            new PendingExportAttributeValueChange
            {
                Attribute = _referenceAttribute,
                ChangeType = PendingExportAttributeChangeType.Add,
                UnresolvedReferenceValue = mvoGuid.ToString()
            });

        // Act
        var result = ExportChangeHistoryBuilder.BuildFromPendingExport(
            pendingExport, ActivityInitiatorType.System, null, null, resolvedReferences);

        // Assert
        var valueChange = result.AttributeChanges.First().ValueChanges.First();
        Assert.That(valueChange.StringValue, Is.EqualTo("CN=User1,OU=Users,DC=test,DC=local"));
        Assert.That(valueChange.IsPendingExportStub, Is.True);
        Assert.That(valueChange.ReferenceValue, Is.Null);
    }

    [Test]
    public void GetCsoDisplayIdentifier_PrefersDisplayName()
    {
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "displayName", Type = AttributeDataType.Text };
        var externalIdAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "objectGUID", Type = AttributeDataType.Text };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ExternalIdAttributeId = 1,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { AttributeId = 3, Attribute = displayNameAttr, StringValue = "Benjamin Myers" },
                new() { AttributeId = 1, Attribute = externalIdAttr, StringValue = "external-id-123" }
            }
        };

        Assert.That(ExportChangeHistoryBuilder.GetCsoDisplayIdentifier(cso), Is.EqualTo("Benjamin Myers"));
    }

    [Test]
    public void GetCsoDisplayIdentifier_FallsBackToExternalId()
    {
        var externalIdAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "objectGUID", Type = AttributeDataType.Text };
        var secondaryIdAttr = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "distinguishedName", Type = AttributeDataType.Text };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ExternalIdAttributeId = 1,
            SecondaryExternalIdAttributeId = 2,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { AttributeId = 1, Attribute = externalIdAttr, StringValue = "external-id-123" },
                new() { AttributeId = 2, Attribute = secondaryIdAttr, StringValue = "CN=User,OU=Test" }
            }
        };

        Assert.That(ExportChangeHistoryBuilder.GetCsoDisplayIdentifier(cso), Is.EqualTo("external-id-123"));
    }

    [Test]
    public void GetCsoDisplayIdentifier_FallsBackToSecondaryExternalId()
    {
        var secondaryIdAttr = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "distinguishedName", Type = AttributeDataType.Text };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ExternalIdAttributeId = 1,
            SecondaryExternalIdAttributeId = 2,
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>
            {
                new() { AttributeId = 2, Attribute = secondaryIdAttr, StringValue = "CN=User,OU=Test" }
            }
        };

        Assert.That(ExportChangeHistoryBuilder.GetCsoDisplayIdentifier(cso), Is.EqualTo("CN=User,OU=Test"));
    }

    [Test]
    public void GetCsoDisplayIdentifier_FallsBackToCsoId()
    {
        var csoId = Guid.NewGuid();
        var cso = new ConnectedSystemObject
        {
            Id = csoId,
            ExternalIdAttributeId = 1,
            SecondaryExternalIdAttributeId = 2
        };

        Assert.That(ExportChangeHistoryBuilder.GetCsoDisplayIdentifier(cso), Is.EqualTo(csoId.ToString()));
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

    #region Attribute name/type snapshot (Issue #58)

    [Test]
    public void MapAttributeValueChanges_PopulatesAttributeNameAndType()
    {
        // Arrange
        var change = new ConnectedSystemObjectChange();
        var attributeValueChanges = new List<PendingExportAttributeValueChange>
        {
            new()
            {
                Attribute = _textAttribute,
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = "Test User"
            }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, attributeValueChanges);

        // Assert - sibling properties populated from the attribute definition
        var attrChange = change.AttributeChanges.Single();
        Assert.That(attrChange.AttributeName, Is.EqualTo("displayName"));
        Assert.That(attrChange.AttributeType, Is.EqualTo(AttributeDataType.Text));
    }

    [Test]
    public void MapAttributeValueChanges_MultipleAttributes_EachGetsCorrectNameAndType()
    {
        // Arrange
        var change = new ConnectedSystemObjectChange();
        var attributeValueChanges = new List<PendingExportAttributeValueChange>
        {
            new()
            {
                Attribute = _textAttribute,
                ChangeType = PendingExportAttributeChangeType.Add,
                StringValue = "Test User"
            },
            new()
            {
                Attribute = _numberAttribute,
                ChangeType = PendingExportAttributeChangeType.Add,
                IntValue = 42
            }
        };

        // Act
        ExportChangeHistoryBuilder.MapAttributeValueChanges(change, attributeValueChanges);

        // Assert
        Assert.That(change.AttributeChanges, Has.Count.EqualTo(2));
        var textChange = change.AttributeChanges.Single(ac => ac.AttributeName == "displayName");
        Assert.That(textChange.AttributeType, Is.EqualTo(AttributeDataType.Text));

        var numberChange = change.AttributeChanges.Single(ac => ac.AttributeName == "employeeId");
        Assert.That(numberChange.AttributeType, Is.EqualTo(AttributeDataType.Number));
    }

    #endregion
}

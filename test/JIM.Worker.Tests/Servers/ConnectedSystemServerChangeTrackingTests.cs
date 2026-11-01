// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for ConnectedSystemServer change history recording (AddChangeAttributeValueObject).
/// Specifically covering edge cases in Reference attribute change recording.
/// </summary>
[TestFixture]
public class ConnectedSystemServerChangeTrackingTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepo = null!;
    private JimApplication _jim = null!;

    private ConnectedSystemObjectTypeAttribute _memberAttr = null!;
    private ConnectedSystemObjectTypeAttribute _externalIdAttr = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockServiceSettingsRepo = new Mock<IServiceSettingsRepository>();

        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepo.Object);

        // Change tracking enabled (null setting → default true)
        _mockServiceSettingsRepo
            .Setup(r => r.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((ServiceSetting?)null);

        // Repository bulk insert is a no-op (change history is what we're testing)
        _mockCsRepo
            .Setup(r => r.CreateConnectedSystemObjectsAsync(It.IsAny<List<ConnectedSystemObject>>(), It.IsAny<Func<int, Task>?>()))
            .Returns(Task.CompletedTask);

        _jim = new JimApplication(_mockRepository.Object);

        _externalIdAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 1,
            Name = "objectGUID",
            Type = AttributeDataType.Guid,
            IsExternalId = true,
            Selected = true
        };

        _memberAttr = new ConnectedSystemObjectTypeAttribute
        {
            Id = 2,
            Name = "member",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued,
            Selected = true
        };
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
    }

    /// <summary>
    /// Verifies that when a group CSO is created before its member user CSOs are persisted
    /// (i.e., ReferenceValue.Id == Guid.Empty due to cross-batch ordering), the change history
    /// records the DN string via UnresolvedReferenceValue rather than storing null values.
    ///
    /// Bug: When groups are in an earlier batch than their members, ReferenceValue.Id == Guid.Empty
    /// at change history recording time. BulkInsertCsoChangeAttributeValuesRawAsync converts
    /// Guid.Empty to null, resulting in change history rows with all-null values that display
    /// as "(identifier not recorded)" in the UI.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsAsync_GroupWithUnpersistedMemberReference_RecordsUnresolvedReferenceValueInChangeHistoryAsync()
    {
        // Arrange
        var groupType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "Group",
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { _externalIdAttr, _memberAttr }
        };

        // Simulate a user CSO that has been resolved in-memory but NOT yet persisted (Guid.Empty)
        var userCso = new ConnectedSystemObject
        {
            Id = Guid.Empty, // not yet persisted — batch ordering means group comes first
            ConnectedSystemId = 1,
            TypeId = 1
        };

        var memberDn = "CN=Alice Adams,OU=Users,OU=Corp,DC=resurgam,DC=local";

        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.Empty,
            ConnectedSystemId = 1,
            TypeId = 1,
            Type = groupType,
            ExternalIdAttributeId = _externalIdAttr.Id
        };

        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = _externalIdAttr,
            AttributeId = _externalIdAttr.Id,
            GuidValue = Guid.NewGuid(),
            ConnectedSystemObject = groupCso
        });

        // The member attribute value: resolved in-memory (ReferenceValue set) but Id == Guid.Empty,
        // and UnresolvedReferenceValue still holds the original DN string
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = _memberAttr,
            AttributeId = _memberAttr.Id,
            ReferenceValue = userCso,           // resolved to in-memory CSO (Id == Guid.Empty)
            UnresolvedReferenceValue = memberDn, // original DN preserved (not cleared during resolution)
            ConnectedSystemObject = groupCso
        });

        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = groupCso
        };

        // Act
        await _jim.ConnectedSystems.CreateConnectedSystemObjectsAsync(
            new List<ConnectedSystemObject> { groupCso },
            new List<ActivityRunProfileExecutionItem> { rpei });

        // Assert: change history should record the DN string for the member attribute,
        // not an all-null value that displays as "(identifier not recorded)"
        Assert.That(rpei.ConnectedSystemObjectChange, Is.Not.Null,
            "Change record should be created for the group CSO");

        var memberAttributeChange = rpei.ConnectedSystemObjectChange!.AttributeChanges
            .SingleOrDefault(ac => ac.AttributeName == "member");
        Assert.That(memberAttributeChange, Is.Not.Null,
            "Change record should include the member attribute");

        Assert.That(memberAttributeChange!.ValueChanges, Has.Count.EqualTo(1),
            "Should have exactly one member value change");

        var valueChange = memberAttributeChange.ValueChanges.Single();
        Assert.That(valueChange.StringValue, Is.EqualTo(memberDn),
            "Member change history should record the DN string via UnresolvedReferenceValue, " +
            "not null (which would display as '(identifier not recorded)' in the UI)");
        Assert.That(valueChange.ReferenceValue, Is.Null,
            "ReferenceValue should be null since the CSO has Guid.Empty Id (not yet persisted)");
    }

    /// <summary>
    /// Verifies that when a group CSO is created and its member user CSOs are already persisted
    /// (ReferenceValue.Id != Guid.Empty), the change history records the CSO reference correctly.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsAsync_GroupWithPersistedMemberReference_RecordsCsoReferenceInChangeHistoryAsync()
    {
        // Arrange
        var groupType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "Group",
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { _externalIdAttr, _memberAttr }
        };

        // Simulate a user CSO that is already persisted (has a real Id)
        var userCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), // already persisted — has a real ID
            ConnectedSystemId = 1,
            TypeId = 1
        };

        var memberDn = "CN=Bob Brown,OU=Users,OU=Corp,DC=resurgam,DC=local";

        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.Empty,
            ConnectedSystemId = 1,
            TypeId = 1,
            Type = groupType,
            ExternalIdAttributeId = _externalIdAttr.Id
        };

        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = _externalIdAttr,
            AttributeId = _externalIdAttr.Id,
            GuidValue = Guid.NewGuid(),
            ConnectedSystemObject = groupCso
        });

        // The member attribute value: resolved to a persisted CSO (Id != Guid.Empty)
        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = _memberAttr,
            AttributeId = _memberAttr.Id,
            ReferenceValue = userCso,           // resolved to persisted CSO (Id != Guid.Empty)
            ReferenceValueId = userCso.Id,      // FK already set
            UnresolvedReferenceValue = memberDn, // original DN preserved
            ConnectedSystemObject = groupCso
        });

        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = groupCso
        };

        // Act
        await _jim.ConnectedSystems.CreateConnectedSystemObjectsAsync(
            new List<ConnectedSystemObject> { groupCso },
            new List<ActivityRunProfileExecutionItem> { rpei });

        // Assert: change history should record the CSO reference (not the DN string)
        Assert.That(rpei.ConnectedSystemObjectChange, Is.Not.Null,
            "Change record should be created for the group CSO");

        var memberAttributeChange = rpei.ConnectedSystemObjectChange!.AttributeChanges
            .SingleOrDefault(ac => ac.AttributeName == "member");
        Assert.That(memberAttributeChange, Is.Not.Null,
            "Change record should include the member attribute");

        Assert.That(memberAttributeChange!.ValueChanges, Has.Count.EqualTo(1),
            "Should have exactly one member value change");

        var valueChange = memberAttributeChange.ValueChanges.Single();
        Assert.That(valueChange.ReferenceValue, Is.EqualTo(userCso),
            "Member change history should record the CSO reference when the CSO is already persisted");
        Assert.That(valueChange.StringValue, Is.EqualTo(memberDn),
            "StringValue should preserve the DN as a fallback for when the FK cannot be written during bulk persistence");
    }

    #region Attribute name/type snapshot (Issue #58)

    [Test]
    public async Task CreateConnectedSystemObjectsAsync_PopulatesAttributeNameAndTypeOnChangeAttributeAsync()
    {
        // Arrange
        var groupType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "Group",
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { _externalIdAttr, _memberAttr }
        };

        var groupCso = new ConnectedSystemObject
        {
            Id = Guid.Empty,
            ConnectedSystemId = 1,
            TypeId = 1,
            Type = groupType,
            ExternalIdAttributeId = _externalIdAttr.Id
        };

        groupCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = _externalIdAttr,
            AttributeId = _externalIdAttr.Id,
            GuidValue = Guid.NewGuid(),
            ConnectedSystemObject = groupCso
        });

        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = groupCso
        };

        // Act
        await _jim.ConnectedSystems.CreateConnectedSystemObjectsAsync(
            new List<ConnectedSystemObject> { groupCso },
            new List<ActivityRunProfileExecutionItem> { rpei });

        // Assert - sibling properties populated from the attribute definition
        var attrChange = rpei.ConnectedSystemObjectChange!.AttributeChanges.Single();
        Assert.That(attrChange.AttributeName, Is.EqualTo("objectGUID"));
        Assert.That(attrChange.AttributeType, Is.EqualTo(AttributeDataType.Guid));
    }

    [Test]
    public void CsoChangeAttribute_WhenAttributeIsNull_SiblingPropertiesStillAvailable()
    {
        // Arrange - simulate a change attribute where the FK has been set to null (attribute deleted)
        var change = new ConnectedSystemObjectChange();
        var attrChange = new ConnectedSystemObjectChangeAttribute
        {
            Attribute = null,
            AttributeName = "deletedAttribute",
            AttributeType = AttributeDataType.Text,
            ConnectedSystemChange = change
        };

        // Assert - sibling properties are accessible even with null Attribute
        Assert.That(attrChange.AttributeName, Is.EqualTo("deletedAttribute"));
        Assert.That(attrChange.AttributeType, Is.EqualTo(AttributeDataType.Text));
        Assert.That(attrChange.Attribute, Is.Null);
        Assert.That(attrChange.ToString(), Is.EqualTo("deletedAttribute"));
    }

    #endregion

    #region RPEI ConnectedSystemObjectId preservation

    /// <summary>
    /// Verifies that CreateChangeRecordForCso does NOT overwrite the RPEI's ConnectedSystemObjectId
    /// when creating a change record for a provisioning CSO.
    ///
    /// Bug: When a CSO is projected from CS 1, the RPEI is created with ConnectedSystemObjectId
    /// pointing to the source CSO. Later, when export evaluation provisions a new CSO in CS 2,
    /// CreateChangeRecordForCso was overwriting rpei.ConnectedSystemObjectId with the provisioning
    /// CSO's ID. Since the RPEI is stored by reference in _pendingMvoChanges, the MVO change record
    /// ended up pointing to the wrong CSO (provisioning CSO in CS 2 instead of source CSO in CS 1).
    /// </summary>
    [Test]
    public void CreateChangeRecordForCso_DoesNotOverwriteRpeiConnectedSystemObjectId_PreservesSourceCsoAsync()
    {
        // Arrange
        var sourceCsoId = Guid.NewGuid();
        var provisioningCsoId = Guid.NewGuid();

        var personType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "person",
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { _externalIdAttr }
        };

        // Source CSO from CS 1 (the one that triggered projection)
        var sourceCso = new ConnectedSystemObject
        {
            Id = sourceCsoId,
            ConnectedSystemId = 1,
            TypeId = 1,
            Type = personType,
            ExternalIdAttributeId = _externalIdAttr.Id
        };

        // Provisioning CSO in CS 2 (created by export evaluation)
        var provisioningCso = new ConnectedSystemObject
        {
            Id = provisioningCsoId,
            ConnectedSystemId = 2,
            TypeId = 1,
            Type = personType,
            ExternalIdAttributeId = _externalIdAttr.Id
        };

        // RPEI created during projection, pointing to source CSO
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = sourceCso,
            ConnectedSystemObjectId = sourceCsoId
        };

        // Act - create change record for the provisioning CSO using the same RPEI
        _jim.ConnectedSystems.CreateChangeRecordForCso(provisioningCso, rpei, changeTrackingEnabled: true);

        // Assert - RPEI must still point to the source CSO, not the provisioning CSO
        Assert.That(rpei.ConnectedSystemObjectId, Is.EqualTo(sourceCsoId),
            "RPEI's ConnectedSystemObjectId must not be overwritten by CreateChangeRecordForCso; " +
            "it must continue to reference the source CSO that triggered the sync");

        Assert.That(rpei.ConnectedSystemObject, Is.EqualTo(sourceCso),
            "RPEI's ConnectedSystemObject navigation must still reference the source CSO");
    }

    /// <summary>
    /// A schema refresh can redefine an attribute whose Connected System Objects already hold values (#2128), and the
    /// next import removes what was stored under the old type. That value holds nothing in the column of the attribute's
    /// current type, which is what a corrupt value looks like; recorded as corrupt, it would error every such object.
    /// </summary>
    [Test]
    public void LinkUpdateChangeRecords_ValueRemovedFromUnderAnotherDataType_RecordsItAsTheValueItWas()
    {
        var (cso, sAMAccountType) = CsoWithAttribute(AttributeDataType.Number);
        var heldAsBoolean = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = sAMAccountType, AttributeId = sAMAccountType.Id, BoolValue = false };
        cso.AttributeValues.Add(heldAsBoolean);
        cso.PendingAttributeValueRemovals.Add(heldAsBoolean);
        cso.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, Attribute = sAMAccountType, AttributeId = sAMAccountType.Id, IntValue = 805306368 });
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ConnectedSystemObject = cso };

        _jim.ConnectedSystems.LinkUpdateChangeRecords([cso], [rpei], changeTrackingEnabled: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet).Or.Null);
            Assert.That(rpei.ConnectedSystemObjectChange?.AttributeChanges.SelectMany(c => c.ValueChanges).Select(v => (v.ValueChangeType, v.BoolValue, v.IntValue)),
                Is.EquivalentTo(new (ValueChangeType, bool?, int?)[]
                {
                    (ValueChangeType.Remove, false, null),
                    (ValueChangeType.Add, null, 805306368)
                }), "The history says what went, as the value it was, and what replaced it.");
        }
    }

    /// <summary>
    /// Only a removal can be a value left from an earlier type: nothing adds a value under a type its attribute does not
    /// have, so an addition holding nothing in its attribute's column is still reported as corrupt.
    /// </summary>
    [Test]
    public void LinkUpdateChangeRecords_AdditionHoldingNothingInItsAttributesColumn_ErrorsTheObject()
    {
        var (cso, sAMAccountType) = CsoWithAttribute(AttributeDataType.Number);
        cso.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, Attribute = sAMAccountType, AttributeId = sAMAccountType.Id, BoolValue = false });
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ConnectedSystemObject = cso };

        _jim.ConnectedSystems.LinkUpdateChangeRecords([cso], [rpei], changeTrackingEnabled: true);

        Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnhandledError));
    }

    /// <summary>
    /// A value that holds nothing in any column has nothing to record (#2183). Another component stored it (optimistic
    /// export apply did, for an expression result of another type), so removing it must not error the object; the
    /// history records no value change for it, and leaves no empty attribute change behind.
    /// </summary>
    [TestCase(AttributeDataType.DateTime)]
    [TestCase(AttributeDataType.Boolean)]
    [TestCase(AttributeDataType.Text)]
    [TestCase(AttributeDataType.Number)]
    public void LinkUpdateChangeRecords_RemovalOfValueHoldingNothing_RecordsNoValueChangeAndDoesNotErrorTheObject(AttributeDataType type)
    {
        var (cso, attribute) = CsoWithAttribute(type);
        var holdingNothing = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = attribute, AttributeId = attribute.Id };
        cso.AttributeValues.Add(holdingNothing);
        cso.PendingAttributeValueRemovals.Add(holdingNothing);
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ConnectedSystemObject = cso };

        _jim.ConnectedSystems.LinkUpdateChangeRecords([cso], [rpei], changeTrackingEnabled: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet).Or.Null, rpei.ErrorMessage);
            Assert.That(rpei.ConnectedSystemObjectChange, Is.Not.Null, "The update is still recorded.");
            Assert.That(rpei.ConnectedSystemObjectChange!.AttributeChanges, Is.Empty,
                "Nothing was held, so nothing is recorded as removed, and no attribute change is left with no values.");
            Assert.That(cso.AttributeValues, Does.Not.Contain(holdingNothing), "The value is still removed from the object.");
        }
    }

    /// <summary>
    /// The confirming import after an optimistic export apply stored a Boolean value holding nothing: the import diff
    /// removes it and adds the value the target holds (#2183). The addition is recorded as usual beside the removal that
    /// records nothing.
    /// </summary>
    [Test]
    public void LinkUpdateChangeRecords_ValueHoldingNothingReplacedByImportedValue_RecordsOnlyTheAddition()
    {
        var (cso, attribute) = CsoWithAttribute(AttributeDataType.Boolean);
        var holdingNothing = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = attribute, AttributeId = attribute.Id };
        cso.AttributeValues.Add(holdingNothing);
        cso.PendingAttributeValueRemovals.Add(holdingNothing);
        cso.PendingAttributeValueAdditions.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, Attribute = attribute, AttributeId = attribute.Id, BoolValue = true });
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ConnectedSystemObject = cso };

        _jim.ConnectedSystems.LinkUpdateChangeRecords([cso], [rpei], changeTrackingEnabled: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet).Or.Null, rpei.ErrorMessage);
            Assert.That(rpei.ConnectedSystemObjectChange?.AttributeChanges.SelectMany(c => c.ValueChanges).Select(v => (v.ValueChangeType, v.BoolValue)),
                Is.EquivalentTo(new (ValueChangeType, bool?)[] { (ValueChangeType.Add, true) }));
        }
    }

    /// <summary>
    /// An object deleted while it holds a value that holds nothing (#2183): its deletion is recorded with every value it
    /// held, and the value holding nothing is left out rather than erroring the object.
    /// </summary>
    [Test]
    public void LinkDeleteChangeRecords_ObjectHoldingValueThatHoldsNothing_RecordsItsOtherValuesAndDoesNotErrorTheObject()
    {
        var (cso, attribute) = CsoWithAttribute(AttributeDataType.DateTime);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = attribute, AttributeId = attribute.Id });
        var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ConnectedSystemObject = cso };

        _jim.ConnectedSystems.LinkDeleteChangeRecords([cso], [rpei], changeTrackingEnabled: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.NotSet).Or.Null, rpei.ErrorMessage);
            Assert.That(rpei.ConnectedSystemObjectChange?.AttributeChanges.Select(c => c.AttributeName),
                Is.EqualTo(new[] { _externalIdAttr.Name }), "The External ID is recorded as removed; the value holding nothing is not.");
        }
    }

    /// <summary>
    /// A quiet deletion has no execution item to hang its record on, so the record stands alone, attributed to the run
    /// that deleted the object. It carries what every deletion record carries, the object's id and External ID above
    /// all: they are what find the object's execution items once its row is gone.
    /// </summary>
    [Test]
    public void BuildQuietDeletionChangeRecords_ChangeTrackingOn_RecordsEachDeletionWithoutAnExecutionItem()
    {
        var (cso, _) = CsoWithAttribute(AttributeDataType.Text);
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = cso.Type.Attributes[1], AttributeId = 3, StringValue = "805306368" });
        var externalId = cso.AttributeValues[0].GuidValue!.Value.ToString();
        var activity = new Activity { Id = Guid.NewGuid(), InitiatedByType = ActivityInitiatorType.User, InitiatedById = Guid.NewGuid(), InitiatedByName = "Joe Bloggs" };

        var changes = _jim.ConnectedSystems.BuildQuietDeletionChangeRecords([cso], activity, changeTrackingEnabled: true);

        var change = changes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            Assert.That(change.DeletedConnectedSystemObjectId, Is.EqualTo(cso.Id));
            Assert.That(change.ConnectedSystemId, Is.EqualTo(cso.ConnectedSystemId));
            Assert.That(change.DeletedObjectType, Is.SameAs(cso.Type));
            Assert.That(change.DeletedObjectExternalId, Is.EqualTo(externalId));
            Assert.That(change.ActivityRunProfileExecutionItem, Is.Null);
            Assert.That(change.ActivityRunProfileExecutionItemId, Is.Null);
            Assert.That(change.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
            Assert.That(change.InitiatedById, Is.EqualTo(activity.InitiatedById));
            Assert.That(change.InitiatedByName, Is.EqualTo("Joe Bloggs"));
            Assert.That(change.AttributeChanges.Select(c => c.AttributeName), Is.EquivalentTo(new[] { _externalIdAttr.Name, "sAMAccountType" }),
                "its final values are recorded as removed, as on every other deletion");
        }
    }

    [Test]
    public void BuildQuietDeletionChangeRecords_ChangeTrackingOff_RecordsNothing()
    {
        var (cso, _) = CsoWithAttribute(AttributeDataType.Text);

        var changes = _jim.ConnectedSystems.BuildQuietDeletionChangeRecords([cso], new Activity(), changeTrackingEnabled: false);

        Assert.That(changes, Is.Empty);
    }

    private (ConnectedSystemObject Cso, ConnectedSystemObjectTypeAttribute Attribute) CsoWithAttribute(AttributeDataType type)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "sAMAccountType", Type = type, Selected = true };
        var userType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "user",
            Attributes = new List<ConnectedSystemObjectTypeAttribute> { _externalIdAttr, attribute }
        };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            TypeId = 1,
            Type = userType,
            ExternalIdAttributeId = _externalIdAttr.Id
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, Attribute = _externalIdAttr, AttributeId = _externalIdAttr.Id, GuidValue = Guid.NewGuid() });
        return (cso, attribute);
    }

    #endregion
}

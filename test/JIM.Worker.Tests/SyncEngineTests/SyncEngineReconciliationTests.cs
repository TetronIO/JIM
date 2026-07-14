// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Pure unit tests for SyncEngine Pending Export reconciliation methods — no mocking, no database.
/// These test the comprehensive attribute matching that replaces the old basic AttributeValuesMatch.
/// </summary>
public class SyncEngineReconciliationTests
{
    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new SyncEngine();
    }

    #region IsAttributeChangeConfirmed — Add/Update

    [Test]
    public void IsAttributeChangeConfirmed_TextAdd_ValuePresent_ReturnsTrue()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "hello");
        var change = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "hello");

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.True);
    }

    [Test]
    public void IsAttributeChangeConfirmed_TextAdd_ValueMismatch_ReturnsFalse()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "wrong");
        var change = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "expected");

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.False);
    }

    [Test]
    public void IsAttributeChangeConfirmed_TextAdd_NoCsoValues_ReturnsFalse()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var change = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "expected");

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.False);
    }

    [Test]
    public void IsAttributeChangeConfirmed_NumberAdd_ValuePresent_ReturnsTrue()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Number, intValue: 42);
        var change = CreateAttrChange(1, AttributeDataType.Number, PendingExportAttributeChangeType.Add, intValue: 42);

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.True);
    }

    [Test]
    public void IsAttributeChangeConfirmed_LongNumberAdd_ValuePresent_ReturnsTrue()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.LongNumber, longValue: 9999999999L);
        var change = CreateAttrChange(1, AttributeDataType.LongNumber, PendingExportAttributeChangeType.Add, longValue: 9999999999L);

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.True);
    }

    [Test]
    public void IsAttributeChangeConfirmed_LongNumberAdd_ValueMismatch_ReturnsFalse()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.LongNumber, longValue: 1L);
        var change = CreateAttrChange(1, AttributeDataType.LongNumber, PendingExportAttributeChangeType.Add, longValue: 2L);

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.False);
    }

    [Test]
    public void IsAttributeChangeConfirmed_DateTimeAdd_ValuePresent_ReturnsTrue()
    {
        var dt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.DateTime, dateTimeValue: dt);
        var change = CreateAttrChange(1, AttributeDataType.DateTime, PendingExportAttributeChangeType.Add, dateTimeValue: dt);

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.True);
    }

    [Test]
    public void IsAttributeChangeConfirmed_BinaryAdd_ValuePresent_ReturnsTrue()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03 };
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Binary, byteValue: bytes);
        var change = CreateAttrChange(1, AttributeDataType.Binary, PendingExportAttributeChangeType.Add, byteValue: new byte[] { 0x01, 0x02, 0x03 });

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.True);
    }

    [Test]
    public void IsAttributeChangeConfirmed_BooleanAdd_ValuePresent_ReturnsTrue()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Boolean, boolValue: true);
        var change = CreateAttrChange(1, AttributeDataType.Boolean, PendingExportAttributeChangeType.Add, boolValue: true);

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.True);
    }

    [Test]
    public void IsAttributeChangeConfirmed_BooleanAdd_ValueMismatch_ReturnsFalse()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Boolean, boolValue: false);
        var change = CreateAttrChange(1, AttributeDataType.Boolean, PendingExportAttributeChangeType.Add, boolValue: true);

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.False);
    }

    [Test]
    public void IsAttributeChangeConfirmed_GuidAdd_ValuePresent_ReturnsTrue()
    {
        var guid = Guid.NewGuid();
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Guid, guidValue: guid);
        var change = CreateAttrChange(1, AttributeDataType.Guid, PendingExportAttributeChangeType.Add, guidValue: guid);

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.True);
    }

    [Test]
    public void IsAttributeChangeConfirmed_GuidAdd_ValueMismatch_ReturnsFalse()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Guid, guidValue: Guid.NewGuid());
        var change = CreateAttrChange(1, AttributeDataType.Guid, PendingExportAttributeChangeType.Add, guidValue: Guid.NewGuid());

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.False);
    }

    [Test]
    public void IsAttributeChangeConfirmed_ReferenceAdd_UnresolvedMatch_ReturnsTrue()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Reference, unresolvedReferenceValue: "CN=User1,DC=test");
        var change = CreateAttrChange(1, AttributeDataType.Reference, PendingExportAttributeChangeType.Add, unresolvedReferenceValue: "CN=User1,DC=test");

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.True);
    }

    [Test]
    public void IsAttributeChangeConfirmed_ReferenceAdd_StringValueMatchesUnresolved_ReturnsTrue()
    {
        // When export resolution clears UnresolvedReferenceValue and sets StringValue instead,
        // we need to check StringValue against the CSO's UnresolvedReferenceValue
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Reference, unresolvedReferenceValue: "CN=User1,DC=test");
        var change = CreateAttrChange(1, AttributeDataType.Reference, PendingExportAttributeChangeType.Add, stringValue: "CN=User1,DC=test");

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.True);
    }

    #endregion

    #region IsAttributeChangeConfirmed — Remove/RemoveAll

    [Test]
    public void IsAttributeChangeConfirmed_Remove_ValueAbsent_ReturnsTrue()
    {
        // CSO has a different value for the attribute, but the removed value is gone
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "other");
        var change = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Remove, stringValue: "removed");

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.True);
    }

    [Test]
    public void IsAttributeChangeConfirmed_Remove_ValueStillPresent_ReturnsFalse()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "stillhere");
        var change = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Remove, stringValue: "stillhere");

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.False);
    }

    [Test]
    public void IsAttributeChangeConfirmed_RemoveAll_NoValuesLeft_ReturnsTrue()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var change = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.RemoveAll);

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.True);
    }

    [Test]
    public void IsAttributeChangeConfirmed_RemoveAll_ValuesStillExist_ReturnsFalse()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "still");
        var change = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.RemoveAll);

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.False);
    }

    #endregion

    #region IsAttributeChangeConfirmed — Empty value clearing

    [Test]
    public void IsAttributeChangeConfirmed_AddEmptyValue_NoCsoValues_ReturnsTrue()
    {
        // Clearing a single-valued attribute: pending change has all nulls, CSO should have no values
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var change = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Update);
        // All values are null — represents clearing

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.True);
    }

    [Test]
    public void IsAttributeChangeConfirmed_AddEmptyValue_CsoStillHasValues_ReturnsFalse()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "not cleared");
        var change = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Update);
        // All values are null — represents clearing

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.False);
    }

    #endregion

    #region IsAttributeChangeConfirmed — Null attribute

    [Test]
    public void IsAttributeChangeConfirmed_NullAttribute_ReturnsFalse()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var change = new PendingExportAttributeValueChange
        {
            AttributeId = 1,
            Attribute = null!,
            ChangeType = PendingExportAttributeChangeType.Add,
            StringValue = "test"
        };

        Assert.That(_engine.IsAttributeChangeConfirmed(cso, change), Is.False);
    }

    #endregion

    #region IsPendingChangeEmpty

    [Test]
    public void IsPendingChangeEmpty_AllNull_ReturnsTrue()
    {
        var change = new PendingExportAttributeValueChange();
        Assert.That(SyncEngine.IsPendingChangeEmpty(change), Is.True);
    }

    [Test]
    public void IsPendingChangeEmpty_HasStringValue_ReturnsFalse()
    {
        var change = new PendingExportAttributeValueChange { StringValue = "value" };
        Assert.That(SyncEngine.IsPendingChangeEmpty(change), Is.False);
    }

    [Test]
    public void IsPendingChangeEmpty_HasBoolValue_ReturnsFalse()
    {
        var change = new PendingExportAttributeValueChange { BoolValue = false };
        Assert.That(SyncEngine.IsPendingChangeEmpty(change), Is.False);
    }

    [Test]
    public void IsPendingChangeEmpty_HasGuidValue_ReturnsFalse()
    {
        var change = new PendingExportAttributeValueChange { GuidValue = Guid.NewGuid() };
        Assert.That(SyncEngine.IsPendingChangeEmpty(change), Is.False);
    }

    [Test]
    public void IsPendingChangeEmpty_HasLongValue_ReturnsFalse()
    {
        var change = new PendingExportAttributeValueChange { LongValue = 1L };
        Assert.That(SyncEngine.IsPendingChangeEmpty(change), Is.False);
    }

    #endregion

    #region ShouldMarkAsFailed

    [Test]
    public void ShouldMarkAsFailed_BelowMaxRetries_ReturnsFalse()
    {
        var change = new PendingExportAttributeValueChange { ExportAttemptCount = 1 };
        Assert.That(SyncEngine.ShouldMarkAsFailed(change), Is.False);
    }

    [Test]
    public void ShouldMarkAsFailed_AtMaxRetries_ReturnsTrue()
    {
        var change = new PendingExportAttributeValueChange { ExportAttemptCount = SyncEngine.DefaultMaxRetries };
        Assert.That(SyncEngine.ShouldMarkAsFailed(change), Is.True);
    }

    [Test]
    public void ShouldMarkAsFailed_AboveMaxRetries_ReturnsTrue()
    {
        var change = new PendingExportAttributeValueChange { ExportAttemptCount = SyncEngine.DefaultMaxRetries + 1 };
        Assert.That(SyncEngine.ShouldMarkAsFailed(change), Is.True);
    }

    #endregion

    #region TransitionCreateToUpdateIfSecondaryExternalIdConfirmed

    [Test]
    public void TransitionCreateToUpdate_SecondaryIdConfirmed_TransitionsToUpdate()
    {
        var secondaryAttr = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "dn", IsSecondaryExternalId = true, Type = AttributeDataType.Text };
        var remainingChange = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = 2,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "mail", Type = AttributeDataType.Text },
            StringValue = "test@test.com",
            ChangeType = PendingExportAttributeChangeType.Add
        };

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            AttributeValueChanges = [remainingChange]
        };

        var result = new PendingExportReconciliationResult();
        result.ConfirmedChanges.Add(new PendingExportAttributeValueChange
        {
            AttributeId = 1,
            Attribute = secondaryAttr,
            StringValue = "CN=User1,DC=test"
        });

        SyncEngine.TransitionCreateToUpdateIfSecondaryExternalIdConfirmed(pendingExport, result);

        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
    }

    [Test]
    public void TransitionCreateToUpdate_NotCreate_DoesNothing()
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            AttributeValueChanges = [new PendingExportAttributeValueChange { AttributeId = 1 }]
        };

        var result = new PendingExportReconciliationResult();

        SyncEngine.TransitionCreateToUpdateIfSecondaryExternalIdConfirmed(pendingExport, result);

        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Update));
    }

    [Test]
    public void TransitionCreateToUpdate_NoSecondaryIdInConfirmed_StaysCreate()
    {
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Create,
            AttributeValueChanges = [new PendingExportAttributeValueChange { AttributeId = 1 }]
        };

        var result = new PendingExportReconciliationResult();
        result.ConfirmedChanges.Add(new PendingExportAttributeValueChange
        {
            AttributeId = 2,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "mail", IsSecondaryExternalId = false, Type = AttributeDataType.Text }
        });

        SyncEngine.TransitionCreateToUpdateIfSecondaryExternalIdConfirmed(pendingExport, result);

        Assert.That(pendingExport.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
    }

    #endregion

    #region UpdatePendingExportStatus

    [Test]
    public void UpdatePendingExportStatus_AllFailed_SetsFailed()
    {
        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.Exported,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange { Status = PendingExportAttributeChangeStatus.Failed },
                new PendingExportAttributeValueChange { Status = PendingExportAttributeChangeStatus.Failed }
            ]
        };

        SyncEngine.UpdatePendingExportStatus(pe);

        Assert.That(pe.Status, Is.EqualTo(PendingExportStatus.Failed));
    }

    [Test]
    public void UpdatePendingExportStatus_SomePendingRetry_SetsExportNotConfirmed()
    {
        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.Exported,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange { Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed },
                new PendingExportAttributeValueChange { Status = PendingExportAttributeChangeStatus.Failed }
            ]
        };

        SyncEngine.UpdatePendingExportStatus(pe);

        Assert.That(pe.Status, Is.EqualTo(PendingExportStatus.ExportNotConfirmed));
    }

    [Test]
    public void UpdatePendingExportStatus_SomeFailedNoPending_SetsExported()
    {
        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.ExportNotConfirmed,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange { Status = PendingExportAttributeChangeStatus.Failed },
                new PendingExportAttributeValueChange { Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation }
            ]
        };

        SyncEngine.UpdatePendingExportStatus(pe);

        // ExportedPendingConfirmation is not Pending or ExportedNotConfirmed, so anyPendingOrRetry is false
        // But anyFailed is true and not allFailed, so Exported
        Assert.That(pe.Status, Is.EqualTo(PendingExportStatus.Exported));
    }

    #endregion

    #region ReconcileCsoAgainstPendingExport (full orchestration)

    [Test]
    public void ReconcileCsoAgainstPendingExport_NullPendingExport_NoChanges()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, null, result);

        Assert.That(result.HasChanges, Is.False);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_PendingStatus_Skipped()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.Pending,
            AttributeValueChanges = [CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "test")]
        };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.HasChanges, Is.False);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_AllConfirmed_MarksForDeletion()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "expected");
        var attrChange = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "expected");
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.Exported,
            AttributeValueChanges = [attrChange]
        };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.PendingExportDeleted, Is.True);
        Assert.That(result.ConfirmedChanges, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_NotConfirmed_MarksForRetry()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "wrong");
        var attrChange = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "expected");
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        attrChange.ExportAttemptCount = 1;

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.Exported,
            AttributeValueChanges = [attrChange]
        };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.RetryChanges, Has.Count.EqualTo(1));
        Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.ExportedNotConfirmed));
        Assert.That(result.PendingExportToUpdate, Is.Not.Null);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_ExceedsMaxRetries_MarksFailed()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "wrong");
        var attrChange = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "expected");
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        attrChange.ExportAttemptCount = SyncEngine.DefaultMaxRetries;

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.Exported,
            AttributeValueChanges = [attrChange]
        };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.FailedChanges, Has.Count.EqualTo(1));
        Assert.That(attrChange.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Failed));
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_BooleanAttribute_Confirmed()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Boolean, boolValue: true);
        var attrChange = CreateAttrChange(1, AttributeDataType.Boolean, PendingExportAttributeChangeType.Update, boolValue: true);
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.Exported,
            AttributeValueChanges = [attrChange]
        };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.PendingExportDeleted, Is.True, "Boolean attribute should be confirmed — this was a bug in the old AttributeValuesMatch");
    }

    #endregion

    #region ReconcileCsoAgainstPendingExport: per data type, via full orchestration (issue #988 pin)
    //
    // These pin ReconcileCsoAgainstPendingExport's behaviour per data type and change type directly
    // (rather than via the single-CSO IsAttributeChangeConfirmed convenience overload above), because
    // this is the method issue #988 refactors to use a per-attribute value index instead of repeated
    // List.Any() scans. Written before the refactor to confirm the refactor changes nothing observable.

    [Test]
    public void ReconcileCsoAgainstPendingExport_NumberAdd_Confirmed_MarksForDeletion()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Number, intValue: 42);
        var attrChange = CreateAttrChange(1, AttributeDataType.Number, PendingExportAttributeChangeType.Add, intValue: 42);
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.PendingExportDeleted, Is.True);
        Assert.That(result.ConfirmedChanges, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_NumberAdd_Unconfirmed_MarksForRetry()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Number, intValue: 1);
        var attrChange = CreateAttrChange(1, AttributeDataType.Number, PendingExportAttributeChangeType.Add, intValue: 42);
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.RetryChanges, Has.Count.EqualTo(1));
        Assert.That(result.ConfirmedChanges, Is.Empty);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_LongNumberAdd_Confirmed_MarksForDeletion()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.LongNumber, longValue: 9999999999L);
        var attrChange = CreateAttrChange(1, AttributeDataType.LongNumber, PendingExportAttributeChangeType.Add, longValue: 9999999999L);
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.PendingExportDeleted, Is.True);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_DateTimeAdd_Confirmed_MarksForDeletion()
    {
        var dt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.DateTime, dateTimeValue: dt);
        var attrChange = CreateAttrChange(1, AttributeDataType.DateTime, PendingExportAttributeChangeType.Add, dateTimeValue: dt);
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.PendingExportDeleted, Is.True);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_GuidAdd_Confirmed_MarksForDeletion()
    {
        var guid = Guid.NewGuid();
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Guid, guidValue: guid);
        var attrChange = CreateAttrChange(1, AttributeDataType.Guid, PendingExportAttributeChangeType.Add, guidValue: guid);
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.PendingExportDeleted, Is.True);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_BinaryAdd_Confirmed_MarksForDeletion()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Binary, byteValue: [0x01, 0x02, 0x03]);
        var attrChange = CreateAttrChange(1, AttributeDataType.Binary, PendingExportAttributeChangeType.Add, byteValue: [0x01, 0x02, 0x03]);
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.PendingExportDeleted, Is.True);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_BinaryAdd_Unconfirmed_MarksForRetry()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Binary, byteValue: [0x09, 0x09]);
        var attrChange = CreateAttrChange(1, AttributeDataType.Binary, PendingExportAttributeChangeType.Add, byteValue: [0x01, 0x02, 0x03]);
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.RetryChanges, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_ReferenceAdd_ViaUnresolvedReferenceValue_MarksForDeletion()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Reference, unresolvedReferenceValue: "CN=User1,DC=test");
        var attrChange = CreateAttrChange(1, AttributeDataType.Reference, PendingExportAttributeChangeType.Add, unresolvedReferenceValue: "CN=User1,DC=test");
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.PendingExportDeleted, Is.True);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_ReferenceAdd_ViaStringValueMatchingUnresolved_MarksForDeletion()
    {
        // Export resolution clears UnresolvedReferenceValue and sets StringValue instead; the CSO's
        // UnresolvedReferenceValue must still be matched against the change's StringValue.
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Reference, unresolvedReferenceValue: "CN=User1,DC=test");
        var attrChange = CreateAttrChange(1, AttributeDataType.Reference, PendingExportAttributeChangeType.Add, stringValue: "CN=User1,DC=test");
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.PendingExportDeleted, Is.True);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_TextRemove_ValueAbsent_Confirmed_MarksForDeletion()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "other");
        var attrChange = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Remove, stringValue: "removed");
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.PendingExportDeleted, Is.True);
        Assert.That(result.ConfirmedChanges, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_TextRemove_ValueStillPresent_Unconfirmed_MarksForRetry()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "stillhere");
        var attrChange = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Remove, stringValue: "stillhere");
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.RetryChanges, Has.Count.EqualTo(1));
        Assert.That(result.ConfirmedChanges, Is.Empty);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_RemoveAll_NoValuesLeft_Confirmed_MarksForDeletion()
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var attrChange = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.RemoveAll);
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.PendingExportDeleted, Is.True);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_RemoveAll_ValuesStillExist_Unconfirmed_MarksForRetry()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "still");
        var attrChange = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.RemoveAll);
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.RetryChanges, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_UpdateEmptyValue_NoCsoValues_Confirmed_MarksForDeletion()
    {
        // Clearing a single-valued attribute: pending change has all nulls, CSO should have no values.
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var attrChange = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Update);
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.PendingExportDeleted, Is.True);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_UpdateEmptyValue_CsoStillHasValues_Unconfirmed_MarksForRetry()
    {
        var cso = CreateCsoWithAttributeValue(1, attributeType: AttributeDataType.Text, stringValue: "not cleared");
        var attrChange = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Update);
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.RetryChanges, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Pins the per-attribute value-index helper introduced by #988: several independent pending
    /// changes targeting the same multi-valued attribute must each be evaluated correctly against
    /// the CSO's values for that attribute (confirmed only when actually present/absent as expected),
    /// with the shared index reused across all of them rather than rebuilt or bled between changes.
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_MultipleChangesSameMultiValuedAttribute_EachEvaluatedIndependently()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "member", Type = AttributeDataType.Reference };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        foreach (var dn in new[] { "CN=A,DC=test", "CN=B,DC=test", "CN=C,DC=test" })
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                AttributeId = 1,
                Attribute = attribute,
                UnresolvedReferenceValue = dn
            });
        }

        var confirmedAdd = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = 1, Attribute = attribute,
            ChangeType = PendingExportAttributeChangeType.Add, UnresolvedReferenceValue = "CN=A,DC=test",
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation
        };
        var unconfirmedAdd = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = 1, Attribute = attribute,
            ChangeType = PendingExportAttributeChangeType.Add, UnresolvedReferenceValue = "CN=NOT-THERE,DC=test",
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation
        };
        var confirmedRemove = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = 1, Attribute = attribute,
            ChangeType = PendingExportAttributeChangeType.Remove, UnresolvedReferenceValue = "CN=ALREADY-GONE,DC=test",
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation
        };
        var unconfirmedRemove = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = 1, Attribute = attribute,
            ChangeType = PendingExportAttributeChangeType.Remove, UnresolvedReferenceValue = "CN=C,DC=test",
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation
        };

        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            Status = PendingExportStatus.Exported,
            AttributeValueChanges = [confirmedAdd, unconfirmedAdd, confirmedRemove, unconfirmedRemove]
        };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.ConfirmedChanges, Is.EquivalentTo(new[] { confirmedAdd, confirmedRemove }));
        Assert.That(result.RetryChanges, Is.EquivalentTo(new[] { unconfirmedAdd, unconfirmedRemove }));
        // Confirmed changes are removed from the Pending Export; unconfirmed ones remain.
        Assert.That(pe.AttributeValueChanges, Is.EquivalentTo(new[] { unconfirmedAdd, unconfirmedRemove }));
    }

    /// <summary>
    /// Issue #988 revision: the per-attribute value index derives its type from the CSO values' OWN
    /// Attribute navigation. If that navigation is null (values loaded without the Attribute include,
    /// e.g. a partially hydrated entity), the index is built as NotSet with no typed sets, and a naive
    /// set-only probe would report an actually-confirmed change as unconfirmed - a silent divergence
    /// from ValueExistsOnCso, which switches on the CHANGE's attribute type and compares raw value
    /// fields regardless of the value-side navigation. The fallback path must preserve the original
    /// semantics exactly: a Text Add whose value is present on the CSO is confirmed.
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_TextAddWithNullAttributeNavigationOnCsoValue_StillConfirmed()
    {
        // CSO value carries the AttributeId but NOT the Attribute navigation.
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 1,
            Attribute = null!,
            StringValue = "expected"
        });

        var attrChange = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "expected");
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.ConfirmedChanges, Has.Count.EqualTo(1),
            "The exported value IS present on the CSO; a null value-side Attribute navigation must not stop confirmation.");
        Assert.That(result.PendingExportDeleted, Is.True);
        Assert.That(result.RetryChanges, Is.Empty,
            "A falsely-unconfirmed change here would surface spurious retries and, after max retries, false export confirmation errors.");
    }

    /// <summary>
    /// Issue #988 revision, mirror case of the null-navigation Add test above: a Remove change whose
    /// value is STILL present on the CSO (with a null value-side Attribute navigation) must NOT be
    /// confirmed. This exercises the fallback finding the value (returning true internally), where a
    /// naive set-only probe would miss it and falsely confirm the removal.
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_TextRemoveWithNullAttributeNavigationOnCsoValue_NotConfirmed()
    {
        // CSO value carries the AttributeId but NOT the Attribute navigation, and the value the
        // Remove change asserts is gone is in fact still present.
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = 1,
            Attribute = null!,
            StringValue = "stillhere"
        });

        var attrChange = CreateAttrChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Remove, stringValue: "stillhere");
        attrChange.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = [attrChange] };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.ConfirmedChanges, Is.Empty,
            "The value is still on the CSO; confirming its removal would silently corrupt reconciliation state.");
        Assert.That(result.RetryChanges, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Issue #988: the confirmed-change removal loop used List.Remove per confirmed change (O(n) scan
    /// each), quadratic for a Pending Export with many confirmed changes. This is a functional pin
    /// (correct end state), with the accompanying scale-guard test below proving the complexity fix.
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_ManyConfirmedChanges_AllRemovedFromPendingExport()
    {
        const int changeCount = 500;
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "member", Type = AttributeDataType.Reference };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var changes = new List<PendingExportAttributeValueChange>(changeCount);
        for (var i = 0; i < changeCount; i++)
        {
            var dn = $"CN=user{i},DC=test";
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 1, Attribute = attribute, UnresolvedReferenceValue = dn });
            changes.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = 1, Attribute = attribute,
                ChangeType = PendingExportAttributeChangeType.Add, UnresolvedReferenceValue = dn,
                Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation
            });
        }

        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = changes };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);

        Assert.That(result.ConfirmedChanges, Has.Count.EqualTo(changeCount));
        Assert.That(result.PendingExportDeleted, Is.True, "All changes confirmed - Pending Export should be marked for deletion.");
    }

    /// <summary>
    /// Issue #988 scale guard: reconciling a large group's worth of confirmed Reference changes must
    /// stay fast. Before the fix, ValueExistsOnCso's per-change List.Any() scan and the per-change
    /// List.Remove in the confirmed-change removal loop are both O(n) per change, i.e. O(n²) overall -
    /// this is a stable red/green discriminator (the unfixed code takes tens of seconds here, not a
    /// flaky micro-benchmark), not a tight timing assertion.
    /// </summary>
    [Test]
    public void ReconcileCsoAgainstPendingExport_LargeGroupManyReferenceChanges_CompletesWithinBound()
    {
        const int memberCount = 50_000;
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "member", Type = AttributeDataType.Reference };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var changes = new List<PendingExportAttributeValueChange>(memberCount);
        for (var i = 0; i < memberCount; i++)
        {
            var dn = $"CN=user{i},DC=test";
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 1, Attribute = attribute, UnresolvedReferenceValue = dn });
            changes.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = 1, Attribute = attribute,
                ChangeType = PendingExportAttributeChangeType.Add, UnresolvedReferenceValue = dn,
                Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation
            });
        }

        var pe = new PendingExport { Id = Guid.NewGuid(), Status = PendingExportStatus.Exported, AttributeValueChanges = changes };
        var result = new PendingExportReconciliationResult();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _engine.ReconcileCsoAgainstPendingExport(cso, pe, result);
        stopwatch.Stop();

        Assert.That(result.ConfirmedChanges, Has.Count.EqualTo(memberCount));
        Assert.That(result.PendingExportDeleted, Is.True);
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)),
            $"Reconciling a {memberCount:N0}-member group with {memberCount:N0} pending Reference changes took " +
            $"{stopwatch.Elapsed.TotalSeconds:N1}s; expected well under 5s with the #988 fix applied.");
    }

    #endregion

    #region Helpers

    private static ConnectedSystemObject CreateCsoWithAttributeValue(
        int attributeId,
        AttributeDataType attributeType,
        string? stringValue = null,
        int? intValue = null,
        long? longValue = null,
        DateTime? dateTimeValue = null,
        byte[]? byteValue = null,
        bool? boolValue = null,
        Guid? guidValue = null,
        string? unresolvedReferenceValue = null)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = attributeId,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = attributeId, Type = attributeType },
            StringValue = stringValue,
            IntValue = intValue,
            LongValue = longValue,
            DateTimeValue = dateTimeValue,
            ByteValue = byteValue,
            BoolValue = boolValue,
            GuidValue = guidValue,
            UnresolvedReferenceValue = unresolvedReferenceValue
        });
        return cso;
    }

    private static PendingExportAttributeValueChange CreateAttrChange(
        int attributeId,
        AttributeDataType attributeType,
        PendingExportAttributeChangeType changeType,
        string? stringValue = null,
        int? intValue = null,
        long? longValue = null,
        DateTime? dateTimeValue = null,
        byte[]? byteValue = null,
        bool? boolValue = null,
        Guid? guidValue = null,
        string? unresolvedReferenceValue = null)
    {
        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = attributeId,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = attributeId, Type = attributeType },
            ChangeType = changeType,
            StringValue = stringValue,
            IntValue = intValue,
            LongValue = longValue,
            DateTimeValue = dateTimeValue,
            ByteValue = byteValue,
            BoolValue = boolValue,
            GuidValue = guidValue,
            UnresolvedReferenceValue = unresolvedReferenceValue
        };
    }

    #endregion
}

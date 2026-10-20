// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Pure unit tests for <see cref="OptimisticExportApplyCalculator"/> (issue #1079) - no mocking,
/// no database. Verifies the projection of a batch's successfully exported Pending Export
/// attribute changes onto the Connected System Objects' current in-memory attribute values.
/// </summary>
public class OptimisticExportApplyCalculatorTests
{
    [TestCase(PendingExportAttributeChangeType.Add)]
    [TestCase(PendingExportAttributeChangeType.Update)]
    public void CalculateDelta_DecimalValue_CreatesAddition(PendingExportAttributeChangeType operation)
    {
        var change = CreateChange(1, AttributeDataType.Decimal, operation);
        change.DecimalValue = 12.34m;
        var delta = OptimisticExportApplyCalculator.CalculateDelta([CreatePendingExport(CreateCso(), change)]);
        Assert.That(delta.Additions.Select(v => v.DecimalValue), Is.EqualTo(new[] { 12.34m }));
        Assert.That(delta.SkippedChangeCount, Is.Zero);
    }

    [TestCase(PendingExportAttributeChangeType.Update)]
    [TestCase(PendingExportAttributeChangeType.Remove)]
    [TestCase(PendingExportAttributeChangeType.RemoveAll)]
    public void CalculateDelta_DecimalExistingValue_RemovesOldValue(PendingExportAttributeChangeType operation)
    {
        var cso = CreateCso();
        var existing = AddCsoValue(cso, 1, AttributeDataType.Decimal);
        existing.DecimalValue = 5.00m;
        var change = CreateChange(1, AttributeDataType.Decimal, operation);
        change.DecimalValue = operation == PendingExportAttributeChangeType.Update ? 6m : 5.0m;
        var delta = OptimisticExportApplyCalculator.CalculateDelta([CreatePendingExport(cso, change)]);
        Assert.That(delta.RemovalValueIds, Is.EqualTo(new[] { existing.Id }));
        Assert.That(delta.Additions.Select(v => v.DecimalValue), Is.EqualTo(
            operation == PendingExportAttributeChangeType.Update ? new decimal?[] { 6m } : Array.Empty<decimal?>()));
    }

    [TestCase(PendingExportAttributeChangeType.Add)]
    [TestCase(PendingExportAttributeChangeType.Update)]
    public void CalculateDelta_DecimalScaleOnlyDifference_SkipsChange(PendingExportAttributeChangeType operation)
    {
        var cso = CreateCso();
        AddCsoValue(cso, 1, AttributeDataType.Decimal).DecimalValue = 5.00m;
        var change = CreateChange(1, AttributeDataType.Decimal, operation);
        change.DecimalValue = 5.0m;
        var delta = OptimisticExportApplyCalculator.CalculateDelta([CreatePendingExport(cso, change)]);
        Assert.That(delta.Additions, Is.Empty);
        Assert.That(delta.RemovalValueIds, Is.Empty);
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    [TestCase(PendingExportAttributeChangeType.Remove)]
    [TestCase(PendingExportAttributeChangeType.RemoveAll)]
    public void CalculateDelta_DecimalChangesInOneBatch_IndexFollowsMutations(PendingExportAttributeChangeType removal)
    {
        var cso = CreateCso();
        var existing = AddCsoValue(cso, 1, AttributeDataType.Decimal);
        existing.DecimalValue = 5.00m;
        PendingExportAttributeValueChange Change(PendingExportAttributeChangeType operation, decimal value)
        {
            var change = CreateChange(1, AttributeDataType.Decimal, operation);
            change.DecimalValue = value;
            return change;
        }
        var delta = OptimisticExportApplyCalculator.CalculateDelta([CreatePendingExport(cso,
            Change(removal, 5m),
            Change(PendingExportAttributeChangeType.Add, 5.0m),
            Change(PendingExportAttributeChangeType.Add, 5.00m))]);
        Assert.That(delta.Additions.Select(v => v.DecimalValue), Is.EqualTo(new[] { 5m }));
        Assert.That(delta.RemovalValueIds, Is.EqualTo(new[] { existing.Id }));
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    [Test]
    public void CalculateDelta_AllDeclaredDataTypes_SupportsEverySetType()
    {
        foreach (var type in Enum.GetValues<AttributeDataType>().Where(t => t != AttributeDataType.NotSet))
        {
            var cso = CreateCso();
            var existing = AddCsoValue(cso, 1, type);
            var change = CreateChange(1, type, PendingExportAttributeChangeType.RemoveAll);
            var delta = OptimisticExportApplyCalculator.CalculateDelta([CreatePendingExport(cso, change)]);
            Assert.That(delta.RemovalValueIds, Does.Contain(existing.Id), $"Unsupported data type: {type}");
        }
    }

    #region Add

    [Test]
    public void CalculateDelta_AddChangeType_ValueAbsent_CreatesAddition()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "foo");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.Additions[0].StringValue, Is.EqualTo("foo"));
        Assert.That(delta.Additions[0].ConnectedSystemObject, Is.SameAs(cso));
        Assert.That(delta.RemovalValueIds, Is.Empty);
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(0));
    }

    [Test]
    public void CalculateDelta_AddChangeType_ValuePresent_NoOp()
    {
        var cso = CreateCso();
        AddCsoValue(cso, 1, AttributeDataType.Text, stringValue: "foo");
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "foo");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Is.Empty);
        Assert.That(delta.RemovalValueIds, Is.Empty);
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    [Test]
    public void CalculateDelta_AddChangeType_EmptyPayload_Skipped()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Is.Empty);
        Assert.That(delta.RemovalValueIds, Is.Empty);
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    [Test]
    public void CalculateDelta_DuplicateAddChangesSameAttributeAndValue_OnlyOneAddition()
    {
        var cso = CreateCso();
        var change1 = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "dup");
        var change2 = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "dup");
        var pe = CreatePendingExport(cso, change1, change2);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    #endregion

    #region Update (single-valued set semantics)

    [Test]
    public void CalculateDelta_UpdateChangeType_NoExistingValue_CreatesAddition()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Update, stringValue: "new");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.Additions[0].StringValue, Is.EqualTo("new"));
        Assert.That(delta.RemovalValueIds, Is.Empty);
    }

    [Test]
    public void CalculateDelta_UpdateChangeType_SingleExistingValueMatches_CompleteNoOp()
    {
        var cso = CreateCso();
        AddCsoValue(cso, 1, AttributeDataType.Text, stringValue: "same");
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Update, stringValue: "same");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Is.Empty, "identical value should not be re-inserted");
        Assert.That(delta.RemovalValueIds, Is.Empty, "identical value should not be deleted");
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    [Test]
    public void CalculateDelta_UpdateChangeType_SingleExistingValueDiffers_RemovesAndInserts()
    {
        var cso = CreateCso();
        var existing = AddCsoValue(cso, 1, AttributeDataType.Text, stringValue: "old");
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Update, stringValue: "new");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.RemovalValueIds, Is.EquivalentTo(new[] { existing.Id }));
        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.Additions[0].StringValue, Is.EqualTo("new"));
    }

    [Test]
    public void CalculateDelta_UpdateChangeType_MultipleExistingValues_RemovesAllAndInsertsOne()
    {
        // Defensive: a single-valued attribute should never carry >1 CSO row, but the calculator
        // must not misbehave (e.g. leave a stray row) if it ever does.
        var cso = CreateCso();
        var existing1 = AddCsoValue(cso, 1, AttributeDataType.Text, stringValue: "old1");
        var existing2 = AddCsoValue(cso, 1, AttributeDataType.Text, stringValue: "old2");
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Update, stringValue: "new");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.RemovalValueIds, Is.EquivalentTo(new[] { existing1.Id, existing2.Id }));
        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.Additions[0].StringValue, Is.EqualTo("new"));
    }

    [Test]
    public void CalculateDelta_UpdateChangeType_EmptyPayload_RemovesTheRecordedValue()
    {
        // #1936: an Update with no value payload clears a single-valued attribute, so the export that sent it has
        // cleared it in the target, and the Connected System Object must stop recording the old value. Left in place,
        // an auto-confirmed target that is never imported from (the File Connector's Export Only mode) would hold it
        // for ever, and every later evaluation would see a difference and stage the clear again. Reconciliation agrees:
        // it confirms an empty Update when the attribute holds no values.
        var cso = CreateCso();
        var existing = AddCsoValue(cso, 1, AttributeDataType.Text, stringValue: "cleared");
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Update);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Is.Empty);
        Assert.That(delta.RemovalValueIds, Is.EqualTo(new[] { existing.Id }));
    }

    [Test]
    public void CalculateDelta_UpdateChangeType_EmptyPayloadWithNothingRecorded_Skipped()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Update);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Is.Empty);
        Assert.That(delta.RemovalValueIds, Is.Empty);
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    #endregion

    #region Remove / RemoveAll

    [Test]
    public void CalculateDelta_RemoveChangeType_ValuePresent_RemovesMatchingRow()
    {
        var cso = CreateCso();
        var existing = AddCsoValue(cso, 1, AttributeDataType.Text, stringValue: "gone");
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Remove, stringValue: "gone");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.RemovalValueIds, Is.EquivalentTo(new[] { existing.Id }));
        Assert.That(delta.Additions, Is.Empty);
    }

    [Test]
    public void CalculateDelta_RemoveChangeType_ValueAbsent_NoOp()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Remove, stringValue: "not there");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.RemovalValueIds, Is.Empty);
        Assert.That(delta.Additions, Is.Empty);
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    [Test]
    public void CalculateDelta_RemoveChangeType_OnlyMatchingValueRemoved_OtherValuesUntouched()
    {
        var cso = CreateCso();
        var toRemove = AddCsoValue(cso, 1, AttributeDataType.Text, stringValue: "remove-me");
        AddCsoValue(cso, 1, AttributeDataType.Text, stringValue: "keep-me");
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Remove, stringValue: "remove-me");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.RemovalValueIds, Is.EquivalentTo(new[] { toRemove.Id }));
    }

    [Test]
    public void CalculateDelta_RemoveAllChangeType_ValuesPresent_RemovesAll()
    {
        var cso = CreateCso();
        var v1 = AddCsoValue(cso, 1, AttributeDataType.Text, stringValue: "a");
        var v2 = AddCsoValue(cso, 1, AttributeDataType.Text, stringValue: "b");
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.RemoveAll);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.RemovalValueIds, Is.EquivalentTo(new[] { v1.Id, v2.Id }));
        Assert.That(delta.Additions, Is.Empty);
    }

    [Test]
    public void CalculateDelta_RemoveAllChangeType_NoValues_NoOp()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.RemoveAll);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.RemovalValueIds, Is.Empty);
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    #endregion

    #region Data type mapping (Add, value absent, per SyncEngine.Reconciliation.ValueExistsOnCso mirroring)

    [Test]
    public void CalculateDelta_NumberAdd_ValueAbsent_CreatesAdditionWithIntValue()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Number, PendingExportAttributeChangeType.Add, intValue: 42);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.Additions[0].IntValue, Is.EqualTo(42));
    }

    [Test]
    public void CalculateDelta_LongNumberAdd_ValueAbsent_CreatesAdditionWithLongValue()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.LongNumber, PendingExportAttributeChangeType.Add, longValue: 9999999999L);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.Additions[0].LongValue, Is.EqualTo(9999999999L));
    }

    [Test]
    public void CalculateDelta_DateTimeAdd_ValueAbsent_CreatesAdditionWithDateTimeValue()
    {
        var dt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.DateTime, PendingExportAttributeChangeType.Add, dateTimeValue: dt);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.Additions[0].DateTimeValue, Is.EqualTo(dt));
    }

    /// <summary>
    /// Pins the #1079 perf fix's key derivation: the per-attribute index must key DateTime values
    /// by Ticks (Kind-insensitive), exactly like the equality comparison it replaces
    /// (<c>v.DateTimeValue == change.DateTimeValue</c>, which itself ignores <see cref="DateTimeKind"/>
    /// - mirroring the reconciliation's #988 DateTimeTicksValues rationale). A naive alternative key
    /// derivation (e.g. one that folds Kind into the key, or normalises to UTC first) would wrongly
    /// treat this as a non-match.
    /// </summary>
    [Test]
    public void CalculateDelta_DateTimeRemove_SameTicksDifferentKind_RemovesMatchingRow()
    {
        var cso = CreateCso();
        var existing = AddCsoValue(cso, 1, AttributeDataType.DateTime,
            dateTimeValue: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        var change = CreateChange(1, AttributeDataType.DateTime, PendingExportAttributeChangeType.Remove,
            dateTimeValue: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Unspecified));
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.RemovalValueIds, Is.EquivalentTo(new[] { existing.Id }),
            "a DateTime value with different Kind but equal Ticks must still match, exactly like the == comparison it replaces");
    }

    [Test]
    public void CalculateDelta_BinaryAdd_ValuePresentWithDifferentArrayInstance_NoOpViaSequenceEqual()
    {
        var cso = CreateCso();
        AddCsoValue(cso, 1, AttributeDataType.Binary, byteValue: new byte[] { 0x01, 0x02, 0x03 });
        var change = CreateChange(1, AttributeDataType.Binary, PendingExportAttributeChangeType.Add, byteValue: new byte[] { 0x01, 0x02, 0x03 });
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Is.Empty, "a different array instance with the same bytes must compare equal (SequenceEqual)");
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    [Test]
    public void CalculateDelta_BinaryAdd_ValueAbsent_CreatesAdditionWithByteValue()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Binary, PendingExportAttributeChangeType.Add, byteValue: new byte[] { 0x0a, 0x0b });
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.Additions[0].ByteValue, Is.EqualTo(new byte[] { 0x0a, 0x0b }));
    }

    [Test]
    public void CalculateDelta_BooleanAdd_ValueAbsent_CreatesAdditionWithBoolValue()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Boolean, PendingExportAttributeChangeType.Add, boolValue: true);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.Additions[0].BoolValue, Is.True);
    }

    [Test]
    public void CalculateDelta_GuidAdd_ValueAbsent_CreatesAdditionWithGuidValue()
    {
        var guid = Guid.NewGuid();
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Guid, PendingExportAttributeChangeType.Add, guidValue: guid);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.Additions[0].GuidValue, Is.EqualTo(guid));
    }

    #endregion

    #region Reference

    [Test]
    public void CalculateDelta_ReferenceAdd_Resolved_UsesResolvedReferenceCsoId()
    {
        var resolvedCsoId = Guid.NewGuid();
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Reference, PendingExportAttributeChangeType.Add, stringValue: "CN=User1,DC=test");
        change.ResolvedReferenceCsoId = resolvedCsoId;
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.Additions[0].ReferenceValueId, Is.EqualTo(resolvedCsoId));
        Assert.That(delta.Additions[0].UnresolvedReferenceValue, Is.EqualTo("CN=User1,DC=test"));
        Assert.That(delta.UnresolvedReferenceCount, Is.EqualTo(0));
    }

    [Test]
    public void CalculateDelta_ReferenceAdd_Unresolvable_RowKeepsDnWithNullReferenceValueId()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Reference, PendingExportAttributeChangeType.Add, stringValue: "CN=Ghost,DC=test");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Has.Count.EqualTo(1));
        Assert.That(delta.Additions[0].ReferenceValueId, Is.Null);
        Assert.That(delta.Additions[0].UnresolvedReferenceValue, Is.EqualTo("CN=Ghost,DC=test"));
        Assert.That(delta.UnresolvedReferenceCount, Is.EqualTo(1));
    }

    [Test]
    public void CalculateDelta_ReferenceAdd_ValuePresentViaUnresolvedReferenceValue_NoOp()
    {
        var cso = CreateCso();
        AddCsoValue(cso, 1, AttributeDataType.Reference, unresolvedReferenceValue: "CN=User3,DC=test");
        var change = CreateChange(1, AttributeDataType.Reference, PendingExportAttributeChangeType.Add, unresolvedReferenceValue: "CN=User3,DC=test");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Is.Empty);
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    #endregion

    #region Delete Pending Export skip (D6)

    [Test]
    public void CalculateDelta_DeleteChangeTypePendingExport_SkippedEntirely()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "should not apply");
        var pe = CreatePendingExport(cso, change);
        pe.ChangeType = PendingExportChangeType.Delete;

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Is.Empty);
        Assert.That(delta.RemovalValueIds, Is.Empty);
    }

    #endregion

    #region Defensive edge cases

    [Test]
    public void CalculateDelta_PendingExportWithNullConnectedSystemObject_SkipsEntirely()
    {
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "orphaned");
        var pe = new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = null,
            AttributeValueChanges = [change]
        };

        Assert.That(() => OptimisticExportApplyCalculator.CalculateDelta([pe]), Throws.Nothing);
        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Is.Empty);
        Assert.That(delta.RemovalValueIds, Is.Empty);
    }

    [Test]
    public void CalculateDelta_ChangeWithNoAttributeNavigation_Skipped()
    {
        var cso = CreateCso();
        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = 1,
            Attribute = null!,
            ChangeType = PendingExportAttributeChangeType.Add,
            StringValue = "unknown type"
        };
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Is.Empty);
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    /// <summary>
    /// Orchestrator review finding #2: an Add/Update change whose Attribute.Type is NotSet (or any
    /// other value outside the supported types) must not create a row with an Id, CSO and
    /// AttributeId but no populated value field. IsPendingChangeEmpty only inspects the typed value
    /// fields, so a change with a payload (e.g. StringValue set) but an unrecognised Type is not
    /// "empty", and ValueExistsOnCso's default arm returns false regardless of CSO state - both
    /// would otherwise let CreateAttributeValue's switch (which has no arm for NotSet) fall through
    /// and insert a payload-less row.
    /// </summary>
    [Test]
    public void CalculateDelta_AddChangeType_UnsupportedAttributeType_SkippedNoRowCreated()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.NotSet, PendingExportAttributeChangeType.Add, stringValue: "junk");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Is.Empty);
        Assert.That(delta.RemovalValueIds, Is.Empty);
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    /// <summary>
    /// Orchestrator review finding #2 (removal side): RemoveAll operates on the CSO's existing rows
    /// for the attribute regardless of the pending change's own value fields, so without a type
    /// guard it would happily stage removal of rows for an attribute whose Type the calculator does
    /// not understand. Skip entirely instead.
    /// </summary>
    [Test]
    public void CalculateDelta_RemoveAllChangeType_UnsupportedAttributeType_SkippedNoRemoval()
    {
        var cso = CreateCso();
        AddCsoValue(cso, 1, AttributeDataType.NotSet, stringValue: "existing-junk");
        var change = CreateChange(1, AttributeDataType.NotSet, PendingExportAttributeChangeType.RemoveAll);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.RemovalValueIds, Is.Empty);
        Assert.That(delta.SkippedChangeCount, Is.EqualTo(1));
    }

    /// <summary>
    /// An export Attribute Flow expression stages its result in the column of the result's own type, so "TRUE" for a
    /// Boolean attribute is staged as text (#2183). The Connector writes it, but the calculator copies only the column of
    /// the attribute's type, which holds nothing: it must not store a value holding nothing (whose removal by the
    /// confirming import errored the object). The Update still replaced what the target held, so the recorded value is
    /// removed, as an Update with no value removes it (#1936): kept, it would let a later export of that same value be
    /// judged already current and never sent. The change is reported as unrecorded, not counted as a no-op.
    /// </summary>
    [TestCase(AttributeDataType.Boolean, "TRUE", null, AttributeDataType.Text)]
    [TestCase(AttributeDataType.DateTime, "2026-10-12T09:30:00Z", null, AttributeDataType.Text)]
    [TestCase(AttributeDataType.Number, "42", null, AttributeDataType.Text)]
    [TestCase(AttributeDataType.Text, null, 7, AttributeDataType.Number)]
    [TestCase(AttributeDataType.Reference, null, 7, AttributeDataType.Number)]
    public void CalculateDelta_UpdatePayloadHoldsNothingInItsAttributesColumn_RemovesTheRecordedValueAndReportsTheChangeUnrecorded(
        AttributeDataType type, string? stringValue, int? intValue, AttributeDataType heldType)
    {
        var cso = CreateCso();
        var recorded = AddRecordedValue(cso, 1, type);
        var change = CreateChange(1, type, PendingExportAttributeChangeType.Update, stringValue: stringValue, intValue: intValue);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delta.Additions, Is.Empty, "No value holding nothing in its attribute's column is stored.");
            Assert.That(delta.RemovalValueIds, Is.EqualTo(new[] { recorded.Id }), "The target no longer holds the recorded value.");
            Assert.That(delta.SkippedChangeCount, Is.EqualTo(0), "A change that could not be recorded is not a no-op.");
            Assert.That(delta.UnresolvedReferenceCount, Is.EqualTo(0));
            Assert.That(delta.UnrecordedChanges.Select(u => (u.PendingExportId, u.ConnectedSystemObjectId, u.AttributeType, u.HeldDataType)),
                Is.EqualTo(new (Guid, Guid, AttributeDataType, AttributeDataType?)[] { (pe.Id, cso.Id, type, heldType) }));
        }
    }

    [TestCase(AttributeDataType.Boolean, "TRUE", null)]
    [TestCase(AttributeDataType.DateTime, "2026-10-12T09:30:00Z", null)]
    [TestCase(AttributeDataType.Number, "42", null)]
    [TestCase(AttributeDataType.Text, null, 7)]
    [TestCase(AttributeDataType.Reference, null, 7)]
    public void CalculateDelta_AddPayloadHoldsNothingInItsAttributesColumn_AddsNoValueAndReportsTheChangeUnrecorded(
        AttributeDataType type, string? stringValue, int? intValue)
    {
        var cso = CreateCso();
        var change = CreateChange(1, type, PendingExportAttributeChangeType.Add, stringValue: stringValue, intValue: intValue);
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delta.Additions, Is.Empty);
            Assert.That(delta.SkippedChangeCount, Is.EqualTo(0));
            Assert.That(delta.UnresolvedReferenceCount, Is.EqualTo(0));
            Assert.That(delta.UnrecordedChanges.Select(u => u.PendingExportId), Is.EqualTo(new[] { pe.Id }));
        }
    }

    /// <summary>
    /// D3 idempotency holds for a change that cannot be recorded (#2183): once the recorded value is removed, applying
    /// the same export again (an auto-confirmed export left unconfirmed is sent again) removes and adds nothing, and the
    /// change is still reported as unrecorded.
    /// </summary>
    [Test]
    public void CalculateDelta_UnrecordedUpdateAppliedAgain_ChangesNothingAndStillReportsItUnrecorded()
    {
        var cso = CreateCso();
        var recorded = AddRecordedValue(cso, 1, AttributeDataType.Boolean);
        var pe = CreatePendingExport(cso, CreateChange(1, AttributeDataType.Boolean, PendingExportAttributeChangeType.Update, stringValue: "TRUE"));
        OptimisticExportApplyCalculator.CalculateDelta([pe]);
        cso.AttributeValues.Remove(recorded);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delta.Additions, Is.Empty);
            Assert.That(delta.RemovalValueIds, Is.Empty);
            Assert.That(delta.UnrecordedChanges, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void CalculateDelta_ExportWithARecordableAndAnUnrecordableChange_RecordsTheOneItCanAndReportsTheExport()
    {
        var cso = CreateCso();
        var pe = CreatePendingExport(cso,
            CreateChange(1, AttributeDataType.Boolean, PendingExportAttributeChangeType.Update, stringValue: "TRUE"),
            CreateChange(2, AttributeDataType.Text, PendingExportAttributeChangeType.Update, stringValue: "foo"));

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delta.Additions.Select(a => (a.AttributeId, a.StringValue)), Is.EqualTo(new (int, string?)[] { (2, "foo") }));
            Assert.That(delta.UnrecordedChanges.Select(u => u.PendingExportId), Is.EqualTo(new[] { pe.Id }));
        }
    }

    #endregion

    #region External-Id dedupe (D9)

    [Test]
    public void CalculateDelta_ValueAlreadyPresentFromExternalIdUpdate_DoesNotDuplicate()
    {
        // Simulates BatchUpdateCsosAfterSuccessfulExportAsync having already appended a value for
        // this attribute to cso.AttributeValues before optimistic apply runs (D11 ordering); the
        // calculator must see it and not insert a second, duplicate row for the same attribute+value.
        var cso = CreateCso();
        AddCsoValue(cso, 1, AttributeDataType.Text, stringValue: "already-there");
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "already-there");
        var pe = CreatePendingExport(cso, change);

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(delta.Additions, Is.Empty);
    }

    #endregion

    #region Idempotency

    [Test]
    public void CalculateDelta_ReappliedAfterApplyingDelta_YieldsEmptyDelta()
    {
        var cso = CreateCso();
        var change = CreateChange(1, AttributeDataType.Text, PendingExportAttributeChangeType.Add, stringValue: "idempotent");
        var pe = CreatePendingExport(cso, change);

        var firstDelta = OptimisticExportApplyCalculator.CalculateDelta([pe]);
        Assert.That(firstDelta.Additions, Has.Count.EqualTo(1), "sanity check: first pass produces work");

        // Simulate persistence + the D10 in-memory sync step: apply the delta to the CSO.
        foreach (var addition in firstDelta.Additions)
            cso.AttributeValues.Add(addition);
        var removalIds = new HashSet<Guid>(firstDelta.RemovalValueIds);
        cso.AttributeValues.RemoveAll(av => removalIds.Contains(av.Id));

        // AttributeValueChanges are unchanged (D11: apply must not mutate them).
        var secondDelta = OptimisticExportApplyCalculator.CalculateDelta([pe]);

        Assert.That(secondDelta.Additions, Is.Empty, "re-running apply against the updated CSO must be a no-op");
        Assert.That(secondDelta.RemovalValueIds, Is.Empty);
        Assert.That(secondDelta.SkippedChangeCount, Is.EqualTo(1));
    }

    #endregion

    #region Helpers

    private static ConnectedSystemObject CreateCso()
    {
        return new ConnectedSystemObject { Id = Guid.NewGuid() };
    }

    private static ConnectedSystemObjectAttributeValue AddCsoValue(
        ConnectedSystemObject cso,
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
        var value = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
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
        };
        cso.AttributeValues.Add(value);
        return value;
    }

    private static ConnectedSystemObjectAttributeValue AddRecordedValue(ConnectedSystemObject cso, int attributeId, AttributeDataType attributeType) => attributeType switch
    {
        AttributeDataType.Text => AddCsoValue(cso, attributeId, attributeType, stringValue: "recorded"),
        AttributeDataType.Number => AddCsoValue(cso, attributeId, attributeType, intValue: 1),
        AttributeDataType.DateTime => AddCsoValue(cso, attributeId, attributeType, dateTimeValue: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
        AttributeDataType.Boolean => AddCsoValue(cso, attributeId, attributeType, boolValue: false),
        AttributeDataType.Reference => AddCsoValue(cso, attributeId, attributeType, unresolvedReferenceValue: "CN=Recorded,DC=test"),
        _ => throw new ArgumentOutOfRangeException(nameof(attributeType), attributeType, "No recorded value is defined for this type.")
    };

    private static PendingExportAttributeValueChange CreateChange(
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

    private static PendingExport CreatePendingExport(ConnectedSystemObject cso, params PendingExportAttributeValueChange[] changes)
    {
        return new PendingExport
        {
            Id = Guid.NewGuid(),
            ChangeType = PendingExportChangeType.Update,
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            AttributeValueChanges = changes.ToList()
        };
    }

    #endregion
}

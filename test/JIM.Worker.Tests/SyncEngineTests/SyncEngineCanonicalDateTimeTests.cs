// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// The synchronisation engine compares and creates dates in their canonical form (#2153): UTC, to the microsecond.
/// PostgreSQL keeps a date to the microsecond and Npgsql reads it back as Kind Local, while a value made in memory is
/// Kind Utc and may carry 100ns ticks, so every comparison of a held value with a new one must use canonical ticks, and
/// every value the engine creates is written canonical.
/// </summary>
[TestFixture]
public class SyncEngineCanonicalDateTimeTests
{
    /// <summary>A date as a source holds it, to the 100ns.</summary>
    private static readonly DateTime SourceDate = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

    /// <summary>The same date as JIM holds it after a round trip through the database: the microsecond, Kind Local.</summary>
    private static readonly DateTime StoredDate = new DateTime(SourceDate.Ticks - 7, DateTimeKind.Utc).ToLocalTime();

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    #region Inbound Attribute Flow

    [Test]
    public void FlowInboundAttributes_HeldDateEqualToTheSourceToTheMicrosecond_StagesNoChange()
    {
        var (mvoAttribute, csoAttribute, csoType, mapping) = DateFlow();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { MetaverseObject = mvo, Attribute = mvoAttribute, AttributeId = mvoAttribute.Id, DateTimeValue = SourceDate });
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = csoType.Id, ConnectedSystemId = 5, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = csoAttribute.Id, Attribute = csoAttribute, DateTimeValue = StoredDate });

        _engine.FlowInboundAttributes(cso, new SyncRule { AttributeFlowRules = [mapping] }, [csoType]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_NewDateFromAStoredValue_IsAddedCanonical()
    {
        var (_, csoAttribute, csoType, mapping) = DateFlow();
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = csoType.Id, ConnectedSystemId = 5, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = csoAttribute.Id, Attribute = csoAttribute, DateTimeValue = StoredDate });

        _engine.FlowInboundAttributes(cso, new SyncRule { AttributeFlowRules = [mapping] }, [csoType]);

        var added = mvo.PendingAttributeValueAdditions.Single().DateTimeValue!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(added.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(added.Ticks, Is.EqualTo(SourceDate.Ticks - 7));
        }
    }

    private static (MetaverseAttribute, ConnectedSystemObjectTypeAttribute, ConnectedSystemObjectType, SyncRuleMapping) DateFlow()
    {
        var mvoAttribute = new MetaverseAttribute { Id = 100, Name = "Start Date", Type = AttributeDataType.DateTime };
        var csoAttribute = new ConnectedSystemObjectTypeAttribute { Id = 200, Name = "startDate", Type = AttributeDataType.DateTime };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttribute] };
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttribute };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = csoAttribute.Id, ConnectedSystemAttribute = csoAttribute, Order = 1 });
        return (mvoAttribute, csoAttribute, csoType, mapping);
    }

    #endregion

    #region Export staging

    [Test]
    public void ComputeAttributeValueChanges_DateHeldAsTheDatabaseReturnsIt_IsStagedCanonical()
    {
        var source = new MetaverseAttribute { Id = 300, Name = "Start Date", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.SingleValued };
        var target = new ConnectedSystemObjectTypeAttribute { Id = 400, Name = "startDate", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.SingleValued };
        var value = new MetaverseObjectAttributeValue { AttributeId = source.Id, Attribute = source, DateTimeValue = StoredDate };
        var mvo = MetaverseObjectHolding(value);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [value],
            PendingExportChangeType.Create, existingCso: null, csoAttributeCache: null, out _);

        var staged = changes.Single().DateTimeValue!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(staged.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(staged.Ticks, Is.EqualTo(SourceDate.Ticks - 7));
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_TargetAlreadyHoldsTheDateToTheMicrosecond_StagesNothing()
    {
        var source = new MetaverseAttribute { Id = 301, Name = "Start Date", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.SingleValued };
        var target = new ConnectedSystemObjectTypeAttribute { Id = 401, Name = "startDate", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.SingleValued };
        var value = new MetaverseObjectAttributeValue { AttributeId = source.Id, Attribute = source, DateTimeValue = SourceDate };
        var mvo = MetaverseObjectHolding(value);
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = target.Id, Attribute = target, DateTimeValue = StoredDate });
        var cache = cso.AttributeValues.ToLookup(v => (cso.Id, v.AttributeId));

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [value],
            PendingExportChangeType.Update, cso, cache, out var alreadyCurrent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.Empty);
            Assert.That(alreadyCurrent, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// The removal tuple matches an unsaved removed value by its content, so a removed date and the changed value naming
    /// the same instant must match whatever precision and Kind each carries.
    /// </summary>
    [Test]
    public void ComputeAttributeValueChanges_RemovedDateMatchedByContent_IsStagedAsARemoval()
    {
        var source = new MetaverseAttribute { Id = 302, Name = "Leave Dates", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.MultiValued };
        var target = new ConnectedSystemObjectTypeAttribute { Id = 402, Name = "leaveDates", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.MultiValued };
        var removed = new MetaverseObjectAttributeValue { AttributeId = source.Id, Attribute = source, DateTimeValue = StoredDate };
        var changed = new MetaverseObjectAttributeValue { AttributeId = source.Id, Attribute = source, DateTimeValue = SourceDate };
        var mvo = MetaverseObjectHolding();

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [changed],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _, removedAttributes: [removed]);

        Assert.That(changes.Single().ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Remove));
    }

    [Test]
    public void IsCsoAttributeAlreadyCurrent_UpdateToTheDateTheTargetHoldsToTheMicrosecond_IsCurrent()
    {
        var target = new ConnectedSystemObjectTypeAttribute { Id = 403, Name = "startDate", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.SingleValued };
        var change = new PendingExportAttributeValueChange { AttributeId = target.Id, Attribute = target, ChangeType = PendingExportAttributeChangeType.Update, DateTimeValue = SourceDate };
        var held = new ConnectedSystemObjectAttributeValue { AttributeId = target.Id, Attribute = target, DateTimeValue = StoredDate };

        Assert.That(SyncEngine.IsCsoAttributeAlreadyCurrent(change, [held]), Is.True);
    }

    /// <summary>
    /// The merge key must not depend on Kind (#2153): a database-read value is Kind Local and an in-memory one Kind Utc, so a
    /// round-trip format string gave the same instant two keys and defeated the merge.
    /// </summary>
    [Test]
    public void GetAttributeChangeKey_SameInstantInDifferentKindsAndPrecisions_IsOneKey()
    {
        var stored = new PendingExportAttributeValueChange { AttributeId = 9, DateTimeValue = StoredDate };
        var fresh = new PendingExportAttributeValueChange { AttributeId = 9, DateTimeValue = SourceDate };

        Assert.That(SyncEngine.GetAttributeChangeKey(stored), Is.EqualTo(SyncEngine.GetAttributeChangeKey(fresh)));
    }

    [Test]
    public void GetAttributeChangeKey_DatesAMicrosecondApart_AreTwoKeys()
    {
        var a = new PendingExportAttributeValueChange { AttributeId = 9, DateTimeValue = SourceDate };
        var b = new PendingExportAttributeValueChange { AttributeId = 9, DateTimeValue = SourceDate.AddTicks(10) };

        Assert.That(SyncEngine.GetAttributeChangeKey(a), Is.Not.EqualTo(SyncEngine.GetAttributeChangeKey(b)));
    }

    #endregion

    #region Export confirmation (reconciliation)

    [Test]
    public void ValueExistsOnCso_DateImportedToTheMicrosecond_ConfirmsTheExportedValue()
    {
        var change = new PendingExportAttributeValueChange
        {
            AttributeId = 7,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = 7, Type = AttributeDataType.DateTime },
            DateTimeValue = SourceDate
        };

        Assert.That(SyncEngine.ValueExistsOnCso([new ConnectedSystemObjectAttributeValue { AttributeId = 7, DateTimeValue = StoredDate }], change), Is.True);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_DateImportedToTheMicrosecond_ConfirmsTheChange()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = 7, Name = "startDate", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.SingleValued };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = attribute.Id, Attribute = attribute, DateTimeValue = StoredDate });
        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = attribute.Id,
            Attribute = attribute,
            ChangeType = PendingExportAttributeChangeType.Update,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            DateTimeValue = SourceDate
        };
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemObject = cso,
            ConnectedSystemObjectId = cso.Id,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Exported,
            AttributeValueChanges = [change]
        };
        var result = new PendingExportReconciliationResult();

        _engine.ReconcileCsoAgainstPendingExport(cso, pendingExport, result);

        Assert.That(result.ConfirmedChanges, Does.Contain(change));
    }

    [Test]
    public void GetExpectedValueAsString_SameInstantInDifferentKinds_ReadsTheSame()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = 7, Type = AttributeDataType.DateTime };
        var stored = new PendingExportAttributeValueChange { Attribute = attribute, DateTimeValue = StoredDate };
        var fresh = new PendingExportAttributeValueChange { Attribute = attribute, DateTimeValue = SourceDate };

        Assert.That(SyncEngine.GetExpectedValueAsString(stored), Is.EqualTo(SyncEngine.GetExpectedValueAsString(fresh)));
    }

    #endregion

    #region Optimistic export apply

    [Test]
    public void OptimisticExportApply_UpdateToTheDateTheObjectHoldsToTheMicrosecond_IsANoOp()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = 7, Type = AttributeDataType.DateTime };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = 7, Attribute = attribute, DateTimeValue = StoredDate });
        var change = new PendingExportAttributeValueChange { Id = Guid.NewGuid(), AttributeId = 7, Attribute = attribute, ChangeType = PendingExportAttributeChangeType.Update, DateTimeValue = SourceDate };
        var pendingExport = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Update, ConnectedSystemObject = cso, ConnectedSystemObjectId = cso.Id, AttributeValueChanges = [change] };

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pendingExport]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(delta.Additions, Is.Empty);
            Assert.That(delta.RemovalValueIds, Is.Empty);
        }
    }

    [Test]
    public void OptimisticExportApply_NewDate_IsAppliedCanonical()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = 7, Type = AttributeDataType.DateTime };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var change = new PendingExportAttributeValueChange { Id = Guid.NewGuid(), AttributeId = 7, Attribute = attribute, ChangeType = PendingExportAttributeChangeType.Update, DateTimeValue = StoredDate };
        var pendingExport = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Update, ConnectedSystemObject = cso, ConnectedSystemObjectId = cso.Id, AttributeValueChanges = [change] };

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pendingExport]);

        Assert.That(delta.Additions.Single().DateTimeValue!.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    #endregion

    #region builders

    private static MetaverseObject MetaverseObjectHolding(params MetaverseObjectAttributeValue[] values)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        foreach (var value in values)
        {
            value.MetaverseObject = mvo;
            mvo.AttributeValues.Add(value);
        }

        return mvo;
    }

    private static SyncRule ExportRule(MetaverseAttribute source, ConnectedSystemObjectTypeAttribute target)
    {
        var rule = new SyncRule { Id = 7, Name = "Export", Direction = SyncRuleDirection.Export };
        rule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 70,
            SyncRule = rule,
            TargetConnectedSystemAttribute = target,
            TargetConnectedSystemAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Id = 700, MetaverseAttribute = source, MetaverseAttributeId = source.Id } }
        });
        return rule;
    }

    #endregion
}

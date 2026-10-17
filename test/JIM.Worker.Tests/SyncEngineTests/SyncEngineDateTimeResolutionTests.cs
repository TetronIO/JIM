// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Export at the target's resolution (#2153, decision D2). A Connected System that holds dates to the second cannot hold
/// a sub-second difference, so export sends each date floored to the second, computes multi-valued deltas on floored
/// sets (never by flooring each delta), and no-net-change detection, confirmation and optimistic apply compare at the
/// resolution. Without that a value the target cannot hold would be exported again on every synchronisation.
/// </summary>
[TestFixture]
public class SyncEngineDateTimeResolutionTests
{
    private static readonly DateTime Second = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime A = Second.AddMilliseconds(100);
    private static readonly DateTime B = Second.AddMilliseconds(700);
    private static readonly DateTime NextSecond = Second.AddSeconds(1);
    private static readonly DateTime C = NextSecond.AddMilliseconds(250);

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    #region Single-valued target

    [Test]
    public void ComputeAttributeValueChanges_SingleValuedTargetHeldToTheSecond_StagesTheDateFlooredToTheSecond()
    {
        var (source, target) = Attributes(AttributePlurality.SingleValued, AttributePlurality.SingleValued, DateTimeResolution.Second);
        var value = Value(source, A);

        var changes = _engine.ComputeAttributeValueChanges(MetaverseObjectHolding(value), ExportRule(source, target), [value],
            PendingExportChangeType.Create, existingCso: null, csoAttributeCache: null, out _);

        Assert.That(changes.Single().DateTimeValue, Is.EqualTo(Second));
    }

    [Test]
    public void ComputeAttributeValueChanges_SubSecondChangeTheTargetCannotHold_StagesNothing()
    {
        // The Metaverse value moved within one second; the target, which holds the second, already holds it.
        var (source, target) = Attributes(AttributePlurality.SingleValued, AttributePlurality.SingleValued, DateTimeResolution.Second);
        var value = Value(source, B);
        var (cso, cache) = ConnectedSystemObjectHolding(target, Second);

        var changes = _engine.ComputeAttributeValueChanges(MetaverseObjectHolding(value), ExportRule(source, target), [value],
            PendingExportChangeType.Update, cso, cache, out var alreadyCurrent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.Empty);
            Assert.That(alreadyCurrent, Is.EqualTo(1));
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_NoResolution_KeepsTheMicrosecond()
    {
        // An attribute discovered before resolutions existed holds none, which is the microsecond JIM holds.
        var (source, target) = Attributes(AttributePlurality.SingleValued, AttributePlurality.SingleValued, resolution: null);
        var value = Value(source, A);

        var changes = _engine.ComputeAttributeValueChanges(MetaverseObjectHolding(value), ExportRule(source, target), [value],
            PendingExportChangeType.Create, existingCso: null, csoAttributeCache: null, out _);

        Assert.That(changes.Single().DateTimeValue, Is.EqualTo(A));
    }

    #endregion

    #region Multi-valued target: floored-set deltas

    [Test]
    public void ComputeAttributeValueChanges_RemovingOneOfTwoDatesInTheSameSecond_StagesNothing()
    {
        // {A, B} in one second is one value to the target; removing A leaves B, which still needs that second.
        var (source, target) = Attributes(AttributePlurality.MultiValued, AttributePlurality.MultiValued, DateTimeResolution.Second);
        var held = Value(source, B, persisted: true);
        var removed = Value(source, A, persisted: true);
        var mvo = MetaverseObjectHolding(held, removed);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [removed],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _, removedAttributes: [removed]);

        Assert.That(changes, Is.Empty);
    }

    [Test]
    public void ComputeAttributeValueChanges_RemovingTheLastDateOfASecond_RemovesTheFlooredValue()
    {
        var (source, target) = Attributes(AttributePlurality.MultiValued, AttributePlurality.MultiValued, DateTimeResolution.Second);
        var held = Value(source, C, persisted: true);
        var removed = Value(source, A, persisted: true);
        var mvo = MetaverseObjectHolding(held, removed);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [removed],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _, removedAttributes: [removed]);

        var change = changes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Remove));
            Assert.That(change.DateTimeValue, Is.EqualTo(Second));
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_AddingADateInASecondAlreadyHeld_StagesNothing()
    {
        var (source, target) = Attributes(AttributePlurality.MultiValued, AttributePlurality.MultiValued, DateTimeResolution.Second);
        var held = Value(source, A, persisted: true);
        var added = Value(source, B);
        var mvo = MetaverseObjectHolding(held, added);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [added],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _, flowErrors: []);

        Assert.That(changes, Is.Empty);
    }

    [Test]
    public void ComputeAttributeValueChanges_AddingADateInANewSecond_AddsTheFlooredValue()
    {
        var (source, target) = Attributes(AttributePlurality.MultiValued, AttributePlurality.MultiValued, DateTimeResolution.Second);
        var held = Value(source, A, persisted: true);
        var added = Value(source, C);
        var mvo = MetaverseObjectHolding(held, added);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [added],
            PendingExportChangeType.Update, existingCso: null, csoAttributeCache: null, out _);

        var change = changes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.ChangeType, Is.EqualTo(PendingExportAttributeChangeType.Add));
            Assert.That(change.DateTimeValue, Is.EqualTo(NextSecond));
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_CreateWithDatesThatCollapse_AddsEachFlooredValueOnceAndReportsTheCollapse()
    {
        var (source, target) = Attributes(AttributePlurality.MultiValued, AttributePlurality.MultiValued, DateTimeResolution.Second);
        var values = new[] { Value(source, A), Value(source, B), Value(source, C) };
        var flowErrors = new List<AttributeFlowError>();

        var changes = _engine.ComputeAttributeValueChanges(MetaverseObjectHolding(values), ExportRule(source, target), [.. values],
            PendingExportChangeType.Create, existingCso: null, csoAttributeCache: null, out _, flowErrors: flowErrors);

        var error = flowErrors.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Select(c => c.DateTimeValue), Is.EquivalentTo(new DateTime?[] { Second, NextSecond }));
            Assert.That(changes.Select(c => c.ChangeType), Has.All.EqualTo(PendingExportAttributeChangeType.Add));
            Assert.That(error.Kind, Is.EqualTo(AttributeFlowErrorKind.DatesCollapseAtTargetResolution));
            Assert.That(error.ValueCount, Is.EqualTo(3));
            Assert.That(error.TargetValueCount, Is.EqualTo(2));
            Assert.That(error.TargetDateTimeResolution, Is.EqualTo(DateTimeResolution.Second));
            Assert.That(error.TargetAttributeName, Is.EqualTo(target.Name));
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_DatesThatDoNotCollapse_ReportNothing()
    {
        var (source, target) = Attributes(AttributePlurality.MultiValued, AttributePlurality.MultiValued, DateTimeResolution.Second);
        var values = new[] { Value(source, A), Value(source, C) };
        var flowErrors = new List<AttributeFlowError>();

        _engine.ComputeAttributeValueChanges(MetaverseObjectHolding(values), ExportRule(source, target), [.. values],
            PendingExportChangeType.Create, existingCso: null, csoAttributeCache: null, out _, flowErrors: flowErrors);

        Assert.That(flowErrors, Is.Empty);
    }

    [Test]
    public void ComputeAttributeValueChanges_TargetHoldingTheFlooredValue_IsAlreadyCurrent()
    {
        var (source, target) = Attributes(AttributePlurality.MultiValued, AttributePlurality.MultiValued, DateTimeResolution.Second);
        var held = Value(source, A, persisted: true);
        var added = Value(source, C);
        var mvo = MetaverseObjectHolding(held, added);
        var (cso, cache) = ConnectedSystemObjectHolding(target, Second, NextSecond);

        var changes = _engine.ComputeAttributeValueChanges(mvo, ExportRule(source, target), [added],
            PendingExportChangeType.Update, cso, cache, out var alreadyCurrent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes, Is.Empty);
            Assert.That(alreadyCurrent, Is.EqualTo(1));
        }
    }

    #endregion

    #region Expression results

    [Test]
    public void ComputeAttributeValueChanges_ExpressionListIntoATargetHeldToTheSecond_StagesFlooredDistinctValuesAndReportsTheCollapse()
    {
        var (source, target) = Attributes(AttributePlurality.MultiValued, AttributePlurality.MultiValued, DateTimeResolution.Second);
        var held = Value(source, A, persisted: true);
        var mvo = MetaverseObjectHolding(held, Value(source, B, persisted: true), Value(source, C, persisted: true));
        var rule = ExportRule(source, target);
        var mapping = rule.AttributeFlowRules.Single();
        mapping.Sources.Clear();
        mapping.Sources.Add(new SyncRuleMappingSource { Id = 701, Expression = "mv[\"Review Dates\"]" });
        var flowErrors = new List<AttributeFlowError>();

        var changes = _engine.ComputeAttributeValueChanges(mvo, rule, [held],
            PendingExportChangeType.Create, existingCso: null, csoAttributeCache: null, out _, flowErrors: flowErrors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.Select(c => c.DateTimeValue), Is.EquivalentTo(new DateTime?[] { Second, NextSecond }));
            Assert.That(flowErrors.Single().Kind, Is.EqualTo(AttributeFlowErrorKind.DatesCollapseAtTargetResolution));
        }
    }

    [Test]
    public void ComputeAttributeValueChanges_ExpressionDateIntoASingleValuedTargetHeldToTheSecond_StagesTheFlooredDate()
    {
        var (source, target) = Attributes(AttributePlurality.SingleValued, AttributePlurality.SingleValued, DateTimeResolution.Second);
        var held = Value(source, A, persisted: true);
        var rule = ExportRule(source, target);
        var mapping = rule.AttributeFlowRules.Single();
        mapping.Sources.Clear();
        mapping.Sources.Add(new SyncRuleMappingSource { Id = 701, Expression = "mv[\"Review Dates\"]" });

        var changes = _engine.ComputeAttributeValueChanges(MetaverseObjectHolding(held), rule, [held],
            PendingExportChangeType.Create, existingCso: null, csoAttributeCache: null, out _);

        Assert.That(changes.Single().DateTimeValue, Is.EqualTo(Second));
    }

    #endregion

    #region No-net-change, confirmation and optimistic apply

    [Test]
    public void IsCsoAttributeAlreadyCurrent_TargetHoldingAFinerValueInTheSameSecond_IsCurrent()
    {
        // A value imported before the attribute's resolution was discovered can carry a fraction the target no longer
        // reports; at the second it is the value being sent.
        var target = new ConnectedSystemObjectTypeAttribute { Id = 400, Name = "reviewDates", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.MultiValued, DateTimeResolution = DateTimeResolution.Second };
        var change = new PendingExportAttributeValueChange { AttributeId = target.Id, Attribute = target, ChangeType = PendingExportAttributeChangeType.Add, DateTimeValue = Second };

        Assert.That(SyncEngine.IsCsoAttributeAlreadyCurrent(change, [new ConnectedSystemObjectAttributeValue { AttributeId = target.Id, Attribute = target, DateTimeValue = B }]), Is.True);
    }

    [Test]
    public void ValueExistsOnCso_AtTheTargetsResolution_ConfirmsTheFlooredValue()
    {
        var target = new ConnectedSystemObjectTypeAttribute { Id = 7, Type = AttributeDataType.DateTime, DateTimeResolution = DateTimeResolution.Second };
        var change = new PendingExportAttributeValueChange { AttributeId = 7, Attribute = target, DateTimeValue = Second };

        Assert.That(SyncEngine.ValueExistsOnCso([new ConnectedSystemObjectAttributeValue { AttributeId = 7, Attribute = target, DateTimeValue = A }], change), Is.True);
    }

    [Test]
    public void ReconcileCsoAgainstPendingExport_ImportedValueInTheExportedSecond_ConfirmsTheChange()
    {
        var target = new ConnectedSystemObjectTypeAttribute { Id = 7, Name = "reviewDates", Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.MultiValued, DateTimeResolution = DateTimeResolution.Second };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = target.Id, Attribute = target, DateTimeValue = A });
        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            AttributeId = target.Id,
            Attribute = target,
            ChangeType = PendingExportAttributeChangeType.Add,
            Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation,
            DateTimeValue = Second
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
    public void OptimisticExportApply_RemoveOfAFlooredValue_RemovesTheHeldValueInThatSecond()
    {
        var target = new ConnectedSystemObjectTypeAttribute { Id = 7, Type = AttributeDataType.DateTime, AttributePlurality = AttributePlurality.MultiValued, DateTimeResolution = DateTimeResolution.Second };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        var held = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = 7, Attribute = target, DateTimeValue = A };
        cso.AttributeValues.Add(held);
        var change = new PendingExportAttributeValueChange { Id = Guid.NewGuid(), AttributeId = 7, Attribute = target, ChangeType = PendingExportAttributeChangeType.Remove, DateTimeValue = Second };
        var pendingExport = new PendingExport { Id = Guid.NewGuid(), ChangeType = PendingExportChangeType.Update, ConnectedSystemObject = cso, ConnectedSystemObjectId = cso.Id, AttributeValueChanges = [change] };

        var delta = OptimisticExportApplyCalculator.CalculateDelta([pendingExport]);

        Assert.That(delta.RemovalValueIds, Does.Contain(held.Id));
    }

    #endregion

    #region builders

    private static (MetaverseAttribute Source, ConnectedSystemObjectTypeAttribute Target) Attributes(
        AttributePlurality sourcePlurality, AttributePlurality targetPlurality, DateTimeResolution? resolution) =>
        (new MetaverseAttribute { Id = 300, Name = "Review Dates", Type = AttributeDataType.DateTime, AttributePlurality = sourcePlurality },
         new ConnectedSystemObjectTypeAttribute { Id = 400, Name = "reviewDates", Type = AttributeDataType.DateTime, AttributePlurality = targetPlurality, DateTimeResolution = resolution });

    private static MetaverseObjectAttributeValue Value(MetaverseAttribute attribute, DateTime date, bool persisted = false) =>
        new() { Id = persisted ? Guid.NewGuid() : Guid.Empty, AttributeId = attribute.Id, Attribute = attribute, DateTimeValue = date };

    private static MetaverseObject MetaverseObjectHolding(params MetaverseObjectAttributeValue[] values)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = new MetaverseObjectType { Id = 1, Name = "Person" } };
        foreach (var value in values)
        {
            value.MetaverseObject = mvo;
            mvo.AttributeValues.Add(value);
        }

        return mvo;
    }

    private static (ConnectedSystemObject Cso, ILookup<(Guid CsoId, int AttributeId), ConnectedSystemObjectAttributeValue> Cache) ConnectedSystemObjectHolding(
        ConnectedSystemObjectTypeAttribute target, params DateTime[] dates)
    {
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid() };
        foreach (var date in dates)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), ConnectedSystemObject = cso, AttributeId = target.Id, Attribute = target, DateTimeValue = date });

        return (cso, cso.AttributeValues.ToLookup(v => (cso.Id, v.AttributeId)));
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

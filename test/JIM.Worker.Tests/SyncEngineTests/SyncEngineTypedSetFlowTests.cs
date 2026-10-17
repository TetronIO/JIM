// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Inbound Attribute Flow treats every typed attribute as a set (#2153 slice S4): the typed writers diff the source's
/// values against the Metaverse Object's as sets, keyed canonically for dates, so every value of a multi-valued source
/// flows, order never matters and duplicates collapse. Rows that hold no value of the writer's type are ignored. A
/// Connected System Object holding none of that type is covered by <see cref="SyncEngineHeldUnderAnotherDataTypeFlowTests"/>
/// (#2184).
/// </summary>
[TestFixture]
public class SyncEngineTypedSetFlowTests
{
    private const int MvoTypeId = 10;
    private const int MvoAttributeId = 100;
    private const int CsoAttributeId = 200;
    private const int ConnectedSystemId = 5;

    /// <summary>A date as a source holds it, to the 100ns.</summary>
    private static readonly DateTime DateA = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

    /// <summary><see cref="DateA"/> as JIM holds it after a database round trip: the microsecond, Kind Local.</summary>
    private static readonly DateTime DateAStored = new DateTime(DateA.Ticks - 7, DateTimeKind.Utc).ToLocalTime();

    private static readonly DateTime DateB = new(2026, 4, 2, 10, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime DateC = new(2026, 5, 3, 11, 45, 0, DateTimeKind.Utc);

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    #region Dates as sets

    [Test]
    public void FlowInboundAttributes_TwoDatesIntoAMultiValuedTarget_AddsBoth()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.DateTime, AttributePlurality.MultiValued);
        AddCsoValue(cso, v => v.DateTimeValue = DateA);
        AddCsoValue(cso, v => v.DateTimeValue = DateB);

        _engine.FlowInboundAttributes(cso, rule, types);

        Assert.That(mvo.PendingAttributeValueAdditions.Select(v => v.DateTimeValue!.Value.Ticks),
            Is.EquivalentTo(new[] { DateA.Ticks - 7, DateB.Ticks }), "every value flows, written canonical");
    }

    [Test]
    public void FlowInboundAttributes_DatesAgainstAnOverlappingHeldSet_RemovesAndAddsOnlyTheDifference()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.DateTime, AttributePlurality.MultiValued);
        AddCsoValue(cso, v => v.DateTimeValue = DateA);
        AddCsoValue(cso, v => v.DateTimeValue = DateB);
        AddMvoValue(mvo, v => v.DateTimeValue = DateB);
        var heldC = AddMvoValue(mvo, v => v.DateTimeValue = DateC);

        _engine.FlowInboundAttributes(cso, rule, types);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.EqualTo(new[] { heldC }));
            Assert.That(mvo.PendingAttributeValueAdditions.Single().DateTimeValue!.Value.Ticks, Is.EqualTo(DateA.Ticks - 7));
        }
    }

    [Test]
    public void FlowInboundAttributes_SameDatesInAnotherOrderAndKind_StagesNothing()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.DateTime, AttributePlurality.MultiValued);
        AddCsoValue(cso, v => v.DateTimeValue = DateB);
        AddCsoValue(cso, v => v.DateTimeValue = DateA);
        AddMvoValue(mvo, v => v.DateTimeValue = DateAStored);
        AddMvoValue(mvo, v => v.DateTimeValue = DateB.ToLocalTime());

        _engine.FlowInboundAttributes(cso, rule, types);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
        }
    }

    /// <summary>
    /// A Metaverse Object holding two dates for a single-valued attribute (left by an earlier, failed run or a widening
    /// reversed by hand) made the old writer's <c>SingleOrDefault</c> throw, failing the object.
    /// </summary>
    [Test]
    public void FlowInboundAttributes_OneDateAgainstTwoHeldDates_KeepsTheMatchAndRemovesTheOther()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.DateTime, AttributePlurality.SingleValued);
        AddCsoValue(cso, v => v.DateTimeValue = DateA);
        AddMvoValue(mvo, v => v.DateTimeValue = DateAStored);
        var heldB = AddMvoValue(mvo, v => v.DateTimeValue = DateB);

        Assert.That(() => _engine.FlowInboundAttributes(cso, rule, types), Throws.Nothing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.EqualTo(new[] { heldB }));
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_TheSameDateTwiceInTwoKinds_AddsItOnce()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.DateTime, AttributePlurality.MultiValued);
        AddCsoValue(cso, v => v.DateTimeValue = DateA);
        AddCsoValue(cso, v => v.DateTimeValue = DateAStored);

        _engine.FlowInboundAttributes(cso, rule, types);

        Assert.That(mvo.PendingAttributeValueAdditions.Single().DateTimeValue!.Value.Ticks, Is.EqualTo(DateA.Ticks - 7));
    }

    [Test]
    public void FlowInboundAttributes_DateSetUnchanged_TakesOverProvenanceOnTheHeldValues()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.DateTime, AttributePlurality.MultiValued);
        AddCsoValue(cso, v => v.DateTimeValue = DateA);
        AddCsoValue(cso, v => v.DateTimeValue = DateB);
        var heldA = AddMvoValue(mvo, v => v.DateTimeValue = DateAStored);
        var heldB = AddMvoValue(mvo, v => v.DateTimeValue = DateB);
        heldA.ContributedBySyncRuleId = heldB.ContributedBySyncRuleId = null;

        _engine.FlowInboundAttributes(cso, rule, types);

        Assert.That(new[] { heldA.ContributedBySyncRuleId, heldB.ContributedBySyncRuleId }, Is.All.EqualTo(rule.Id));
    }

    #endregion

    #region Booleans as sets

    [Test]
    public void FlowInboundAttributes_LegacyMultiValuedBooleanTargetHoldingBothValues_KeepsTheSourceValueWithoutThrowing()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.Boolean, AttributePlurality.MultiValued);
        AddCsoValue(cso, v => v.BoolValue = true);
        AddMvoValue(mvo, v => v.BoolValue = true);
        var heldFalse = AddMvoValue(mvo, v => v.BoolValue = false);

        Assert.That(() => _engine.FlowInboundAttributes(cso, rule, types), Throws.Nothing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.EqualTo(new[] { heldFalse }));
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_LegacyMultiValuedBooleanSourceHoldingBothValues_FlowsTheSetToAMultiValuedTarget()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.Boolean, AttributePlurality.MultiValued);
        AddCsoValue(cso, v => v.BoolValue = true);
        AddCsoValue(cso, v => v.BoolValue = false);

        Assert.That(() => _engine.FlowInboundAttributes(cso, rule, types), Throws.Nothing);

        Assert.That(mvo.PendingAttributeValueAdditions.Select(v => v.BoolValue), Is.EquivalentTo(new bool?[] { true, false }));
    }

    [Test]
    public void FlowInboundAttributes_BooleanHeldTwiceForASingleValuedTarget_CollapsesToOneValue()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.Boolean, AttributePlurality.SingleValued);
        AddCsoValue(cso, v => v.BoolValue = true);
        AddMvoValue(mvo, v => v.BoolValue = true);
        var duplicate = AddMvoValue(mvo, v => v.BoolValue = true);

        Assert.That(() => _engine.FlowInboundAttributes(cso, rule, types), Throws.Nothing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.EqualTo(new[] { duplicate }));
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        }
    }

    #endregion

    #region Rows without the writer's type

    [TestCaseSource(nameof(TypedWriters))]
    public void FlowInboundAttributes_ValueBesideAValuelessRow_FlowsOnlyTheValue(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(type, AttributePlurality.MultiValued);
        AddCsoValue(cso, v => SetTypedValue(v, type));
        AddCsoValue(cso, _ => { });

        _engine.FlowInboundAttributes(cso, rule, types);

        var added = mvo.PendingAttributeValueAdditions.Single();
        Assert.That(HoldsTypedValue(added, type), Is.True, "the value-less row is not staged as a value");
    }

    [Test]
    public void FlowInboundAttributes_NumberHeldTwiceBySource_AddsItOnce()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.Number, AttributePlurality.MultiValued);
        AddCsoValue(cso, v => v.IntValue = 7);
        AddCsoValue(cso, v => v.IntValue = 7);

        _engine.FlowInboundAttributes(cso, rule, types);

        Assert.That(mvo.PendingAttributeValueAdditions.Select(v => v.IntValue), Is.EqualTo(new int?[] { 7 }));
    }

    #endregion

    #region The MVA->SVA guard

    /// <summary>
    /// A stale row held under another type (#2128) is not a second value: the guard counts only rows holding the
    /// source type, so the date still flows and no MultiValuedToSingleValued is raised.
    /// </summary>
    [Test]
    public void FlowInboundAttributes_SingleValuedTargetWithADateAndAStaleTextRow_FlowsTheDateWithoutAnError()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.DateTime, AttributePlurality.SingleValued);
        AddCsoValue(cso, v => v.DateTimeValue = DateA);
        AddCsoValue(cso, v => v.StringValue = "stale text");

        var errors = _engine.FlowInboundAttributes(cso, rule, types);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions.Single().DateTimeValue!.Value.Ticks, Is.EqualTo(DateA.Ticks - 7));
        }
    }

    [Test]
    public void FlowInboundAttributes_SingleValuedTargetWithTheSameDateInTwoKinds_FlowsOneValueWithoutAnError()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.DateTime, AttributePlurality.SingleValued);
        AddCsoValue(cso, v => v.DateTimeValue = DateA);
        AddCsoValue(cso, v => v.DateTimeValue = DateAStored);

        var errors = _engine.FlowInboundAttributes(cso, rule, types);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void FlowInboundAttributes_SingleValuedTargetWithTwoDistinctDates_ReportsTheDistinctCount()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.DateTime, AttributePlurality.SingleValued);
        AddCsoValue(cso, v => v.DateTimeValue = DateA);
        AddCsoValue(cso, v => v.DateTimeValue = DateAStored);
        AddCsoValue(cso, v => v.DateTimeValue = DateB);

        var errors = _engine.FlowInboundAttributes(cso, rule, types);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors.Single().Kind, Is.EqualTo(AttributeFlowErrorKind.MultiValuedToSingleValued));
            Assert.That(errors.Single().ValueCount, Is.EqualTo(2));
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_SingleValuedBooleanTargetWithBothValues_ReportsMultiValuedToSingleValued()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.Boolean, AttributePlurality.SingleValued);
        AddCsoValue(cso, v => v.BoolValue = true);
        AddCsoValue(cso, v => v.BoolValue = false);

        var errors = _engine.FlowInboundAttributes(cso, rule, types);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors.Single().Kind, Is.EqualTo(AttributeFlowErrorKind.MultiValuedToSingleValued));
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        }
    }

    #endregion

    #region Exact-set no-net-change

    [TestCaseSource(nameof(TypedWriters))]
    public void FlowInboundAttributes_MultiValuedSetUnchangedInAnotherOrder_StagesNothing(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(type, AttributePlurality.MultiValued);
        AddCsoValue(cso, v => SetTypedValue(v, type, second: true));
        AddCsoValue(cso, v => SetTypedValue(v, type));
        AddMvoValue(mvo, v => SetTypedValue(v, type));
        AddMvoValue(mvo, v => SetTypedValue(v, type, second: true));

        _engine.FlowInboundAttributes(cso, rule, types);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
            Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
        }
    }

    #endregion

    #region Fixtures

    private static IEnumerable<AttributeDataType> TypedWriters() =>
    [
        AttributeDataType.Number,
        AttributeDataType.LongNumber,
        AttributeDataType.Decimal,
        AttributeDataType.DateTime,
        AttributeDataType.Boolean,
        AttributeDataType.Guid,
        AttributeDataType.Binary
    ];

    private static readonly Guid GuidA = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    private static readonly Guid GuidB = Guid.Parse("7a2c4b10-1d3e-4f50-9a6b-7c8d9e0f1a2b");

    private static void SetTypedValue(ConnectedSystemObjectAttributeValue value, AttributeDataType type, bool second = false)
    {
        switch (type)
        {
            case AttributeDataType.Number: value.IntValue = second ? 2 : 1; break;
            case AttributeDataType.LongNumber: value.LongValue = second ? 20_000_000_000L : 10_000_000_000L; break;
            case AttributeDataType.Decimal: value.DecimalValue = second ? 2.50m : 1.25m; break;
            case AttributeDataType.DateTime: value.DateTimeValue = second ? DateB : DateA; break;
            case AttributeDataType.Boolean: value.BoolValue = !second; break;
            case AttributeDataType.Guid: value.GuidValue = second ? GuidB : GuidA; break;
            case AttributeDataType.Binary: value.ByteValue = second ? [4, 5, 6] : [1, 2, 3]; break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    /// <summary>The same values as <see cref="SetTypedValue(ConnectedSystemObjectAttributeValue, AttributeDataType, bool)"/>,
    /// with dates as the database returns them (Kind Local, to the microsecond) and decimals at another scale.</summary>
    private static void SetTypedValue(MetaverseObjectAttributeValue value, AttributeDataType type, bool second = false)
    {
        switch (type)
        {
            case AttributeDataType.Number: value.IntValue = second ? 2 : 1; break;
            case AttributeDataType.LongNumber: value.LongValue = second ? 20_000_000_000L : 10_000_000_000L; break;
            case AttributeDataType.Decimal: value.DecimalValue = second ? 2.5m : 1.250m; break;
            case AttributeDataType.DateTime: value.DateTimeValue = second ? DateB.ToLocalTime() : DateAStored; break;
            case AttributeDataType.Boolean: value.BoolValue = !second; break;
            case AttributeDataType.Guid: value.GuidValue = second ? GuidB : GuidA; break;
            case AttributeDataType.Binary: value.ByteValue = second ? [4, 5, 6] : [1, 2, 3]; break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static bool HoldsTypedValue(MetaverseObjectAttributeValue value, AttributeDataType type) => type switch
    {
        AttributeDataType.Number => value.IntValue.HasValue,
        AttributeDataType.LongNumber => value.LongValue.HasValue,
        AttributeDataType.Decimal => value.DecimalValue.HasValue,
        AttributeDataType.DateTime => value.DateTimeValue.HasValue,
        AttributeDataType.Boolean => value.BoolValue.HasValue,
        AttributeDataType.Guid => value.GuidValue.HasValue,
        AttributeDataType.Binary => value.ByteValue != null,
        _ => false
    };

    private static (MetaverseObject Mvo, ConnectedSystemObject Cso, List<ConnectedSystemObjectType> Types, SyncRule Rule) Arrange(
        AttributeDataType type, AttributePlurality targetPlurality, bool nullIsValue = false, int syncRuleId = 7, int priority = 1)
    {
        var mvoAttribute = new MetaverseAttribute { Id = MvoAttributeId, Name = "target", Type = type, AttributePlurality = targetPlurality };
        var csoAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = CsoAttributeId, Name = "source", Type = type, AttributePlurality = AttributePlurality.MultiValued
        };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttribute] };

        var mapping = new SyncRuleMapping
        {
            Id = syncRuleId, SyncRuleId = syncRuleId, TargetMetaverseAttribute = mvoAttribute, Priority = priority, NullIsValue = nullIsValue
        };
        mapping.Sources.Add(new SyncRuleMappingSource { ConnectedSystemAttributeId = CsoAttributeId, ConnectedSystemAttribute = csoAttribute, Order = 1 });
        var rule = new SyncRule
        {
            Id = syncRuleId,
            MetaverseObjectTypeId = MvoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            AttributeFlowRules = [mapping]
        };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = csoType.Id, ConnectedSystemId = ConnectedSystemId, MetaverseObject = mvo };
        return (mvo, cso, [csoType], rule);
    }

    private static void AddCsoValue(ConnectedSystemObject cso, Action<ConnectedSystemObjectAttributeValue> set)
    {
        var value = new ConnectedSystemObjectAttributeValue { AttributeId = CsoAttributeId, ConnectedSystemObject = cso };
        set(value);
        cso.AttributeValues.Add(value);
    }

    private static MetaverseObjectAttributeValue AddMvoValue(MetaverseObject mvo, Action<MetaverseObjectAttributeValue> set)
    {
        var value = new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo,
            AttributeId = MvoAttributeId,
            ContributedBySystemId = ConnectedSystemId,
            ContributedBySyncRuleId = 7
        };
        set(value);
        mvo.AttributeValues.Add(value);
        return value;
    }

    #endregion
}

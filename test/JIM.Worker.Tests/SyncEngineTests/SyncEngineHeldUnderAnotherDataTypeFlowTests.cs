// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Inbound Attribute Flow when the Connected System Object holds the source attribute only in another data type's
/// column (#2184): a schema refresh redefined the attribute's type (#2128) and no import has read it since. Such a row
/// is a value JIM cannot read as the attribute's type, not the absence of a value (#2066), so the contribution leaves
/// the Metaverse Object exactly as it is: no removal, no addition, no asserted-null marker and no provenance takeover.
/// A row holding nothing at all is still no value. Every one of the eight typed writers follows the same rule; Text is
/// the control.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SyncEngineHeldUnderAnotherDataTypeFlowTests
{
    private const int MvoTypeId = 10;
    private const int MvoAttributeId = 100;
    private const int CsoAttributeId = 200;
    private const int ConnectedSystemId = 5;

    /// <summary>A Connected System that is not the Connected System Object's, so a provenance takeover is visible.</summary>
    private const int OtherConnectedSystemId = 99;

    private static readonly DateTime DateA = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DateB = new(2026, 4, 2, 10, 30, 0, DateTimeKind.Utc);
    private static readonly Guid GuidA = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    private static readonly Guid GuidB = Guid.Parse("7a2c4b10-1d3e-4f50-9a6b-7c8d9e0f1a2b");

    private SyncEngine _engine = null!;

    [SetUp]
    public void SetUp() => _engine = new SyncEngine();

    #region Held under another data type: left as held

    /// <summary>#2184 case 1: a winning higher-priority rule does not remove a lower-priority rule's value.</summary>
    [TestCaseSource(nameof(AllTypedWriters))]
    public void FlowInboundAttributes_HigherPriorityRuleHoldingOnlyAnotherType_LeavesTheIncumbentAndStagesNothing(AttributeDataType type)
    {
        var (mvo, cso, types, highRule) = Arrange(type, AttributePlurality.SingleValued, syncRuleId: 1, priority: 1);
        var (_, _, _, lowRule) = Arrange(type, AttributePlurality.SingleValued, syncRuleId: 2, priority: 2);
        AddCsoValue(cso, v => SetAnotherTypeValue(v, type));
        var incumbent = AddMvoValue(mvo, v => SetTypedValue(v, type), syncRuleId: 2, systemId: OtherConnectedSystemId);
        var context = new AttributePriorityContext([highRule, lowRule]);

        var errors = _engine.FlowInboundAttributes(cso, highRule, types, priorityContext: context);

        using (Assert.EnterMultipleScope())
        {
            AssertNothingStaged(mvo, errors);
            Assert.That(incumbent.ContributedBySyncRuleId, Is.EqualTo(2), "no provenance takeover");
            Assert.That(incumbent.ContributedBySystemId, Is.EqualTo(OtherConnectedSystemId), "no provenance takeover");
        }
    }

    /// <summary>
    /// #2184 case 2: under "Null is a value" the source does not assert null for a value it holds, just not in the
    /// attribute's current type.
    /// </summary>
    [TestCaseSource(nameof(AllTypedWriters))]
    public void FlowInboundAttributes_NullIsAValueHoldingOnlyAnotherType_WritesNoMarkerAndChangesNothing(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(type, AttributePlurality.SingleValued, nullIsValue: true);
        AddCsoValue(cso, v => SetAnotherTypeValue(v, type));
        AddMvoValue(mvo, v => SetTypedValue(v, type));
        var context = new AttributePriorityContext([rule]);

        var errors = _engine.FlowInboundAttributes(cso, rule, types, priorityContext: context);

        AssertNothingStaged(mvo, errors);
    }

    /// <summary>
    /// #2184 case 3: a sole contributor's existing value stays as it is, with or without a priority context; the next
    /// import that reads the attribute replaces the row.
    /// </summary>
    [TestCaseSource(nameof(AllTypedWritersWithAndWithoutContext))]
    public void FlowInboundAttributes_SoleContributorHoldingOnlyAnotherType_LeavesTheValueAsHeld(AttributeDataType type, bool withPriorityContext)
    {
        var (mvo, cso, types, rule) = Arrange(type, AttributePlurality.SingleValued);
        AddCsoValue(cso, v => SetAnotherTypeValue(v, type));
        var held = AddMvoValue(mvo, v => SetTypedValue(v, type), syncRuleId: 7, systemId: OtherConnectedSystemId);
        var context = withPriorityContext ? new AttributePriorityContext([rule]) : null;

        var errors = _engine.FlowInboundAttributes(cso, rule, types, priorityContext: context);

        using (Assert.EnterMultipleScope())
        {
            AssertNothingStaged(mvo, errors);
            Assert.That(held.ContributedBySystemId, Is.EqualTo(OtherConnectedSystemId), "no provenance takeover");
        }
    }

    /// <summary>Several rows, all held under another type, are left as held too; none is a value of the writer's type.</summary>
    [TestCaseSource(nameof(AllTypedWriters))]
    public void FlowInboundAttributes_MultiValuedTargetHoldingOnlyAnotherTypeInTwoRows_LeavesTheValuesAsHeld(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(type, AttributePlurality.MultiValued);
        AddCsoValue(cso, v => SetAnotherTypeValue(v, type));
        AddCsoValue(cso, v => SetAnotherTypeValue(v, type, second: true));
        AddMvoValue(mvo, v => SetTypedValue(v, type));
        AddMvoValue(mvo, v => SetTypedValue(v, type, second: true));

        var errors = _engine.FlowInboundAttributes(cso, rule, types, priorityContext: new AttributePriorityContext([rule]));

        AssertNothingStaged(mvo, errors);
    }

    /// <summary>
    /// The value is left as held, but never silently: a Warning names the Connected System Object, the attribute and
    /// the data type the value is held under.
    /// </summary>
    [TestCaseSource(nameof(AllTypedWriters))]
    public void FlowInboundAttributes_HoldingOnlyAnotherType_LogsAWarningNamingTheObjectAttributeAndHeldType(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(type, AttributePlurality.SingleValued);
        AddCsoValue(cso, v => SetAnotherTypeValue(v, type));
        AddMvoValue(mvo, v => SetTypedValue(v, type));

        var events = CaptureLogs(() => _engine.FlowInboundAttributes(cso, rule, types, priorityContext: new AttributePriorityContext([rule])));

        var warning = events.SingleOrDefault(e => e.Level == LogEventLevel.Warning);
        Assert.That(warning, Is.Not.Null, "a Warning is logged");
        var message = warning!.RenderMessage();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain(cso.Id.ToString()), "names the Connected System Object");
            Assert.That(message, Does.Contain("source"), "names the attribute");
            Assert.That(message, Does.Contain(AnotherType(type).ToString()), "names the held data type");
        }
    }

    #endregion

    #region Values of the writer's type beside rows of another

    /// <summary>
    /// #2184 case 4: only the value of the writer's type flows, and only it is counted by the single-valued guard, so
    /// no MultiValuedToSingleValued error is raised.
    /// </summary>
    [TestCaseSource(nameof(AllTypedWriters))]
    public void FlowInboundAttributes_TypedRowBesideAnotherTypeIntoASingleValuedTarget_FlowsTheTypedValueWithoutAnError(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(type, AttributePlurality.SingleValued);
        AddCsoValue(cso, v => SetTypedValue(v, type));
        AddCsoValue(cso, v => SetAnotherTypeValue(v, type));

        var errors = _engine.FlowInboundAttributes(cso, rule, types, priorityContext: new AttributePriorityContext([rule]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty, "a value held under another type is not a second value");
            Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
            Assert.That(HoldsTypedValue(mvo.PendingAttributeValueAdditions.Single(), type), Is.True, "the value of the writer's type flows");
            AssertNoValuelessAddition(mvo);
        }
    }

    #endregion

    #region No value at all

    /// <summary>#2184 case 5, under priority: a row holding nothing is no value, so the higher-priority rule abstains.</summary>
    [TestCaseSource(nameof(AllTypedWriters))]
    public void FlowInboundAttributes_HigherPriorityRuleWithARowHoldingNothing_Abstains(AttributeDataType type)
    {
        var (mvo, cso, types, highRule) = Arrange(type, AttributePlurality.SingleValued, syncRuleId: 1, priority: 1);
        var (_, _, _, lowRule) = Arrange(type, AttributePlurality.SingleValued, syncRuleId: 2, priority: 2);
        AddCsoValue(cso, _ => { });
        AddMvoValue(mvo, v => SetTypedValue(v, type), syncRuleId: 2);
        var context = new AttributePriorityContext([highRule, lowRule]);

        var errors = _engine.FlowInboundAttributes(cso, highRule, types, priorityContext: context);

        AssertNothingStaged(mvo, errors);
    }

    /// <summary>#2184 case 5, under "Null is a value": a row holding nothing asserts null with a marker.</summary>
    [TestCaseSource(nameof(AllTypedWriters))]
    public void FlowInboundAttributes_NullIsAValueWithARowHoldingNothing_AssertsNull(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(type, AttributePlurality.SingleValued, nullIsValue: true);
        AddCsoValue(cso, _ => { });
        var held = AddMvoValue(mvo, v => SetTypedValue(v, type));

        _engine.FlowInboundAttributes(cso, rule, types, priorityContext: new AttributePriorityContext([rule]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.EqualTo(new[] { held }));
            Assert.That(mvo.PendingAttributeValueAdditions.Single().NullValue, Is.True, "an asserted-null marker");
            AssertNoValuelessAddition(mvo);
        }
    }

    /// <summary>A sole contributor whose row holds nothing clears the attribute, as with no row at all.</summary>
    [TestCaseSource(nameof(AllTypedWriters))]
    public void FlowInboundAttributes_SoleContributorWithARowHoldingNothing_ClearsTheValue(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(type, AttributePlurality.SingleValued);
        AddCsoValue(cso, _ => { });
        var held = AddMvoValue(mvo, v => SetTypedValue(v, type));

        _engine.FlowInboundAttributes(cso, rule, types, priorityContext: new AttributePriorityContext([rule]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.PendingAttributeValueRemovals, Is.EqualTo(new[] { held }));
            Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        }
    }

    #endregion

    #region Settled after one run

    /// <summary>#2184 case 7: once the first run's changes are applied, a second run stages nothing.</summary>
    [TestCaseSource(nameof(AllTypedWriters))]
    public void FlowInboundAttributes_RunTwiceWithATypedRowBesideAnotherType_StagesNothingTheSecondTime(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(type, AttributePlurality.SingleValued);
        AddCsoValue(cso, v => SetTypedValue(v, type));
        AddCsoValue(cso, v => SetAnotherTypeValue(v, type));
        var context = new AttributePriorityContext([rule]);

        _engine.FlowInboundAttributes(cso, rule, types, priorityContext: context);
        ApplyPendingChanges(mvo);
        var errors = _engine.FlowInboundAttributes(cso, rule, types, priorityContext: context);

        AssertNothingStaged(mvo, errors);
    }

    /// <summary>#2184 case 7: a value left as held stays left as held on every later run; nothing churns.</summary>
    [TestCaseSource(nameof(AllTypedWriters))]
    public void FlowInboundAttributes_RunTwiceHoldingOnlyAnotherType_StagesNothingEitherTime(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(type, AttributePlurality.SingleValued);
        AddCsoValue(cso, v => SetAnotherTypeValue(v, type));
        AddMvoValue(mvo, v => SetTypedValue(v, type));
        var context = new AttributePriorityContext([rule]);

        var firstErrors = _engine.FlowInboundAttributes(cso, rule, types, priorityContext: context);
        AssertNothingStaged(mvo, firstErrors);
        ApplyPendingChanges(mvo);
        var secondErrors = _engine.FlowInboundAttributes(cso, rule, types, priorityContext: context);

        AssertNothingStaged(mvo, secondErrors);
    }

    #endregion

    #region Fixtures

    /// <summary>The seven typed writers #2184 concerns, and Text as the control.</summary>
    private static IEnumerable<AttributeDataType> AllTypedWriters() =>
    [
        AttributeDataType.Number,
        AttributeDataType.LongNumber,
        AttributeDataType.Decimal,
        AttributeDataType.DateTime,
        AttributeDataType.Boolean,
        AttributeDataType.Guid,
        AttributeDataType.Binary,
        AttributeDataType.Text
    ];

    private static IEnumerable<TestCaseData> AllTypedWritersWithAndWithoutContext() =>
        AllTypedWriters().SelectMany(type => new[] { new TestCaseData(type, true), new TestCaseData(type, false) });

    /// <summary>
    /// The data type a stale row is held under for a writer of <paramref name="type"/>: a type that is not
    /// <paramref name="type"/>, as after a schema refresh redefined the attribute (#2128).
    /// </summary>
    private static AttributeDataType AnotherType(AttributeDataType type) => type switch
    {
        AttributeDataType.Text => AttributeDataType.Number,
        AttributeDataType.Number => AttributeDataType.Boolean,
        _ => AttributeDataType.Text
    };

    private static void SetAnotherTypeValue(ConnectedSystemObjectAttributeValue value, AttributeDataType type, bool second = false)
    {
        switch (AnotherType(type))
        {
            case AttributeDataType.Number: value.IntValue = second ? 43 : 42; break;
            case AttributeDataType.Boolean: value.BoolValue = !second; break;
            default: value.StringValue = second ? "stale text 2" : "stale text"; break;
        }
    }

    private static void SetTypedValue(ConnectedSystemObjectAttributeValue value, AttributeDataType type, bool second = false)
    {
        switch (type)
        {
            case AttributeDataType.Text: value.StringValue = second ? "beta" : "alpha"; break;
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

    private static void SetTypedValue(MetaverseObjectAttributeValue value, AttributeDataType type, bool second = false)
    {
        switch (type)
        {
            case AttributeDataType.Text: value.StringValue = second ? "beta" : "alpha"; break;
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

    private static bool HoldsTypedValue(MetaverseObjectAttributeValue value, AttributeDataType type) => type switch
    {
        AttributeDataType.Text => value.StringValue != null,
        AttributeDataType.Number => value.IntValue.HasValue,
        AttributeDataType.LongNumber => value.LongValue.HasValue,
        AttributeDataType.Decimal => value.DecimalValue.HasValue,
        AttributeDataType.DateTime => value.DateTimeValue.HasValue,
        AttributeDataType.Boolean => value.BoolValue.HasValue,
        AttributeDataType.Guid => value.GuidValue.HasValue,
        AttributeDataType.Binary => value.ByteValue != null,
        _ => false
    };

    /// <summary>
    /// Nothing staged for the Metaverse Object and no error. #2184 case 6 (no staged addition is a row holding no
    /// value) is asserted in every case through here or <see cref="AssertNoValuelessAddition"/>.
    /// </summary>
    private static void AssertNothingStaged(MetaverseObject mvo, List<AttributeFlowError> errors)
    {
        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "no removal");
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "no addition, and so no asserted-null marker");
        Assert.That(errors, Is.Empty, "no error");
        AssertNoValuelessAddition(mvo);
    }

    /// <summary>#2184 case 6: no staged addition holds no value at all (a non-marker row with every value field null).</summary>
    private static void AssertNoValuelessAddition(MetaverseObject mvo) =>
        Assert.That(mvo.PendingAttributeValueAdditions.Where(av => av.IsValuelessReferenceRow()), Is.Empty,
            "a row holding no value is never staged");

    /// <summary>Applies the staged changes to the Metaverse Object, as persisting a page does.</summary>
    private static void ApplyPendingChanges(MetaverseObject mvo)
    {
        mvo.AttributeValues.RemoveAll(mvo.PendingAttributeValueRemovals.Contains);
        mvo.AttributeValues.AddRange(mvo.PendingAttributeValueAdditions);
        mvo.PendingAttributeValueRemovals.Clear();
        mvo.PendingAttributeValueAdditions.Clear();
    }

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

    private static MetaverseObjectAttributeValue AddMvoValue(
        MetaverseObject mvo, Action<MetaverseObjectAttributeValue> set, int syncRuleId = 7, int systemId = ConnectedSystemId)
    {
        var value = new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo,
            AttributeId = MvoAttributeId,
            ContributedBySystemId = systemId,
            ContributedBySyncRuleId = syncRuleId
        };
        set(value);
        mvo.AttributeValues.Add(value);
        return value;
    }

    private static List<LogEvent> CaptureLogs(Action action)
    {
        var sink = new CapturingSink();
        var original = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        try
        {
            action();
        }
        finally
        {
            Log.Logger = original;
        }

        return sink.Events;
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public List<LogEvent> Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events)
                _events.Add(logEvent);
        }
    }

    #endregion
}

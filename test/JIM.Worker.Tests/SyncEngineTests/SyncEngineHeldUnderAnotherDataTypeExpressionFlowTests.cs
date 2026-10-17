// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// Inbound Expression mappings when the Connected System Object holds an input only in another data type's column
/// (#2319): a schema refresh redefined the attribute's type (#2128) and no import has read it since. Such an input is a
/// value JIM cannot read as the attribute's type, not the absence of a value, so the Expression is not evaluated and
/// the Metaverse Object is left exactly as it is, whatever the Missing Input Behaviour: no removal, no addition, no
/// asserted-null marker, no provenance takeover and no error. A Warning says why. Only an input the Expression reads
/// does this; the derived pass and a Generated Value's base expression follow the same rule.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SyncEngineHeldUnderAnotherDataTypeExpressionFlowTests
{
    private const int MvoTypeId = 10;
    private const int MvoAttributeId = 100;
    private const int SourceAttributeId = 200;
    private const int OtherAttributeId = 201;
    private const int SyncRuleId = 7;
    private const int ConnectedSystemId = 5;

    private static readonly DateTime DateA = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid GuidA = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    private SyncEngine _engine = null!;
    private DynamicExpressoEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new SyncEngine();
        _evaluator = new DynamicExpressoEvaluator();
    }

    #region Ordinary inbound pass

    /// <summary>
    /// The issue's first test: under every Missing Input Behaviour, an Expression over an input held only under another
    /// type stages nothing, and the sole contributor's value survives with its provenance.
    /// </summary>
    [TestCaseSource(nameof(AllTypesAndBehaviours))]
    public void FlowInboundAttributes_ExpressionReadingAnInputHeldOnlyUnderAnotherType_StagesNothingAndKeepsTheValue(
        AttributeDataType type, MissingInputBehaviour behaviour)
    {
        var (mvo, cso, types, rule) = Arrange(type, "cs[\"source\"]", behaviour);
        AddCsoValue(cso, SourceAttributeId, v => SetAnotherTypeValue(v, type));
        var held = AddMvoValue(mvo, v => SetTypedValue(v, type));

        List<AttributeFlowError>? errors = null;
        Assert.That(() => errors = _engine.FlowInboundAttributes(cso, rule, types, _evaluator, priorityContext: new AttributePriorityContext([rule])),
            Throws.Nothing, "the object is not failed for a value it holds");

        using (Assert.EnterMultipleScope())
        {
            AssertNothingStaged(mvo, errors!);
            Assert.That(held.ContributedBySyncRuleId, Is.EqualTo(SyncRuleId), "no provenance change");
        }
    }

    /// <summary>"Null is a value" asserts nothing for a value the source holds, just not as the attribute's type.</summary>
    [TestCaseSource(nameof(AllTypes))]
    public void FlowInboundAttributes_NullIsAValueExpressionReadingAnInputHeldOnlyUnderAnotherType_WritesNoMarker(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(type, "cs[\"source\"]", MissingInputBehaviour.EvaluateAnyway, nullIsValue: true);
        AddCsoValue(cso, SourceAttributeId, v => SetAnotherTypeValue(v, type));
        AddMvoValue(mvo, v => SetTypedValue(v, type));

        var errors = _engine.FlowInboundAttributes(cso, rule, types, _evaluator, priorityContext: new AttributePriorityContext([rule]));

        AssertNothingStaged(mvo, errors);
    }

    /// <summary>
    /// An Expression that builds a value around the input would otherwise flow a structurally wrong value
    /// ("ada.@corp.local"); it flows nothing instead.
    /// </summary>
    [Test]
    public void FlowInboundAttributes_ExpressionBuildingAroundAnInputHeldOnlyUnderAnotherType_FlowsNoWrongValue()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.Text, "\"ada.\" + cs[\"source\"] + \"@corp.local\"", MissingInputBehaviour.EvaluateAnyway);
        AddCsoValue(cso, SourceAttributeId, v => v.IntValue = 42);
        AddMvoValue(mvo, v => v.StringValue = "ada.lovelace@corp.local");

        var errors = _engine.FlowInboundAttributes(cso, rule, types, _evaluator, priorityContext: new AttributePriorityContext([rule]));

        AssertNothingStaged(mvo, errors);
    }

    /// <summary>Never silent: a Warning names the Connected System Object, the input and the type it is held as.</summary>
    [Test]
    public void FlowInboundAttributes_ExpressionReadingAnInputHeldOnlyUnderAnotherType_LogsAWarning()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.Number, "cs[\"source\"]", MissingInputBehaviour.EvaluateAnyway);
        AddCsoValue(cso, SourceAttributeId, v => v.StringValue = "42");
        AddMvoValue(mvo, v => v.IntValue = 42);

        var events = CaptureLogs(() => _engine.FlowInboundAttributes(cso, rule, types, _evaluator, priorityContext: new AttributePriorityContext([rule])));

        var warning = events.SingleOrDefault(e => e.Level == LogEventLevel.Warning);
        Assert.That(warning, Is.Not.Null, "a Warning is logged");
        var message = warning!.RenderMessage();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(message, Does.Contain(cso.Id.ToString()), "names the Connected System Object");
            Assert.That(message, Does.Contain("source"), "names the input");
            Assert.That(message, Does.Contain(nameof(AttributeDataType.Text)), "names the type the input is held as");
        }
    }

    /// <summary>Only an input the Expression reads leaves the attribute as held; one it does not read changes nothing.</summary>
    [TestCaseSource(nameof(AllTypes))]
    public void FlowInboundAttributes_ExpressionNotReadingTheInputHeldUnderAnotherType_FlowsAsUsual(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.Text, "cs[\"other\"]", MissingInputBehaviour.EvaluateAnyway, sourceType: type);
        AddCsoValue(cso, SourceAttributeId, v => SetAnotherTypeValue(v, type));
        AddCsoValue(cso, OtherAttributeId, v => v.StringValue = "beta");
        AddMvoValue(mvo, v => v.StringValue = "alpha");

        var errors = _engine.FlowInboundAttributes(cso, rule, types, _evaluator, priorityContext: new AttributePriorityContext([rule]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions.Select(v => v.StringValue), Is.EqualTo(new[] { "beta" }));
            Assert.That(mvo.PendingAttributeValueRemovals, Has.Count.EqualTo(1));
        }
    }

    /// <summary>A value of the attribute's type beside one held under another is what the Expression reads.</summary>
    [TestCaseSource(nameof(AllTypes))]
    public void FlowInboundAttributes_ExpressionReadingATypedValueBesideOneHeldUnderAnotherType_FlowsTheTypedValue(AttributeDataType type)
    {
        var (mvo, cso, types, rule) = Arrange(type, "cs[\"source\"]", MissingInputBehaviour.EvaluateAnyway);
        AddCsoValue(cso, SourceAttributeId, v => SetTypedValue(v, type));
        AddCsoValue(cso, SourceAttributeId, v => SetAnotherTypeValue(v, type));

        var errors = _engine.FlowInboundAttributes(cso, rule, types, _evaluator, priorityContext: new AttributePriorityContext([rule]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1), "the typed value flows");
        }
    }

    /// <summary>An input with no value at all is still a missing input, resolved by Missing Input Behaviour as before.</summary>
    [Test]
    public void FlowInboundAttributes_ExpressionReadingAnInputWithNoRow_IsAMissingInputAsBefore()
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.Text, "cs[\"source\"]", MissingInputBehaviour.FailMapping);
        AddMvoValue(mvo, v => v.StringValue = "alpha");

        var errors = _engine.FlowInboundAttributes(cso, rule, types, _evaluator, priorityContext: new AttributePriorityContext([rule]));

        Assert.That(errors.Select(e => e.Kind), Is.EqualTo(new[] { AttributeFlowErrorKind.ExpressionMissingInput }));
    }

    #endregion

    #region Derived pass

    /// <summary>
    /// The derived pass (Metaverse-Derived Attribute Flows, #1750) reads the Connected System Object the same way, so an
    /// Expression reading both sides leaves the attribute as held too.
    /// </summary>
    [TestCaseSource(nameof(AllBehaviours))]
    public void EvaluateDerivedMapping_ExpressionReadingAnInputHeldOnlyUnderAnotherType_StagesNothing(MissingInputBehaviour behaviour)
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.Text, "mv[\"Given Name\"] + \".\" + cs[\"source\"]", behaviour);
        AddCsoValue(cso, SourceAttributeId, v => v.IntValue = 42);
        AddMvoValue(mvo, v => v.StringValue = "ada.lovelace");
        var errors = new List<AttributeFlowError>();
        var metaverseAttributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Given Name"] = "ada" };

        Assert.That(() => SyncEngine.EvaluateDerivedMapping(cso, rule, rule.AttributeFlowRules[0], types, _evaluator,
            new AttributePriorityContext([rule]), metaverseAttributes, errors), Throws.Nothing);

        AssertNothingStaged(mvo, errors);
    }

    #endregion

    #region Generated Value base expression

    /// <summary>
    /// A Generated Value's base expression over an input held only under another type waits, whatever the Missing Input
    /// Behaviour: nothing is generated from a base built without the input, and a value already generated is kept.
    /// </summary>
    [TestCaseSource(nameof(AllBehaviours))]
    public void FlowInboundAttributes_GeneratedBaseReadingAnInputHeldOnlyUnderAnotherType_Waits(MissingInputBehaviour behaviour)
    {
        var (mvo, cso, types, rule) = Arrange(AttributeDataType.Text, "Lower(cs[\"source\"]) + \".x\"", behaviour);
        rule.AttributeFlowRules[0].Generation = new SyncRuleMappingGeneration();
        AddCsoValue(cso, SourceAttributeId, v => v.IntValue = 42);

        List<AttributeFlowError>? errors = null;
        Assert.That(() => errors = _engine.FlowInboundAttributes(cso, rule, types, _evaluator, priorityContext: new AttributePriorityContext([rule])),
            Throws.Nothing);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty, "no error");
            Assert.That(mvo.PendingGeneratedValues, Has.Count.EqualTo(1));
            Assert.That(mvo.PendingGeneratedValues[0].BaseUnavailable, Is.True, "generation waits");
            Assert.That(mvo.PendingGeneratedValues[0].BaseValue, Is.Null, "no base built without the input");
        }
    }

    #endregion

    #region Fixtures

    private static IEnumerable<AttributeDataType> AllTypes() =>
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

    private static IEnumerable<MissingInputBehaviour> AllBehaviours() =>
    [
        MissingInputBehaviour.EvaluateAnyway,
        MissingInputBehaviour.ContributeNoValue,
        MissingInputBehaviour.FailMapping,
        MissingInputBehaviour.FailObject
    ];

    private static IEnumerable<TestCaseData> AllTypesAndBehaviours() =>
        AllTypes().SelectMany(type => AllBehaviours().Select(behaviour => new TestCaseData(type, behaviour)));

    /// <summary>The data type a stale row is held under for an attribute of <paramref name="type"/>.</summary>
    private static AttributeDataType AnotherType(AttributeDataType type) => type switch
    {
        AttributeDataType.Text => AttributeDataType.Number,
        AttributeDataType.Number => AttributeDataType.Boolean,
        _ => AttributeDataType.Text
    };

    private static void SetAnotherTypeValue(ConnectedSystemObjectAttributeValue value, AttributeDataType type)
    {
        switch (AnotherType(type))
        {
            case AttributeDataType.Number: value.IntValue = 42; break;
            case AttributeDataType.Boolean: value.BoolValue = true; break;
            default: value.StringValue = "stale text"; break;
        }
    }

    private static void SetTypedValue(ConnectedSystemObjectAttributeValue value, AttributeDataType type)
    {
        switch (type)
        {
            case AttributeDataType.Text: value.StringValue = "alpha"; break;
            case AttributeDataType.Number: value.IntValue = 1; break;
            case AttributeDataType.LongNumber: value.LongValue = 10_000_000_000L; break;
            case AttributeDataType.Decimal: value.DecimalValue = 1.25m; break;
            case AttributeDataType.DateTime: value.DateTimeValue = DateA; break;
            case AttributeDataType.Boolean: value.BoolValue = true; break;
            case AttributeDataType.Guid: value.GuidValue = GuidA; break;
            case AttributeDataType.Binary: value.ByteValue = [1, 2, 3]; break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static void SetTypedValue(MetaverseObjectAttributeValue value, AttributeDataType type)
    {
        switch (type)
        {
            case AttributeDataType.Text: value.StringValue = "alpha"; break;
            case AttributeDataType.Number: value.IntValue = 1; break;
            case AttributeDataType.LongNumber: value.LongValue = 10_000_000_000L; break;
            case AttributeDataType.Decimal: value.DecimalValue = 1.25m; break;
            case AttributeDataType.DateTime: value.DateTimeValue = DateA; break;
            case AttributeDataType.Boolean: value.BoolValue = true; break;
            case AttributeDataType.Guid: value.GuidValue = GuidA; break;
            case AttributeDataType.Binary: value.ByteValue = [1, 2, 3]; break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static void AssertNothingStaged(MetaverseObject mvo, List<AttributeFlowError> errors)
    {
        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "no removal");
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "no addition, and so no asserted-null marker");
        Assert.That(mvo.PendingGeneratedValues, Is.Empty, "no generation request");
        Assert.That(errors, Is.Empty, "no error");
    }

    private static (MetaverseObject Mvo, ConnectedSystemObject Cso, List<ConnectedSystemObjectType> Types, SyncRule Rule) Arrange(
        AttributeDataType targetType,
        string expression,
        MissingInputBehaviour behaviour,
        bool nullIsValue = false,
        AttributeDataType? sourceType = null)
    {
        var mvoAttribute = new MetaverseAttribute { Id = MvoAttributeId, Name = "target", Type = targetType, AttributePlurality = AttributePlurality.SingleValued };
        var sourceAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = SourceAttributeId, Name = "source", Type = sourceType ?? targetType, AttributePlurality = AttributePlurality.MultiValued
        };
        var otherAttribute = new ConnectedSystemObjectTypeAttribute { Id = OtherAttributeId, Name = "other", Type = AttributeDataType.Text };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [sourceAttribute, otherAttribute] };

        var mapping = new SyncRuleMapping
        {
            Id = SyncRuleId, SyncRuleId = SyncRuleId, TargetMetaverseAttribute = mvoAttribute, Priority = 1, NullIsValue = nullIsValue
        };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = expression, Order = 1, MissingInputBehaviour = behaviour });
        var rule = new SyncRule
        {
            Id = SyncRuleId,
            Name = "HR Import",
            MetaverseObjectTypeId = MvoTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            AttributeFlowRules = [mapping]
        };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = csoType.Id, ConnectedSystemId = ConnectedSystemId, MetaverseObject = mvo };
        return (mvo, cso, [csoType], rule);
    }

    private static void AddCsoValue(ConnectedSystemObject cso, int attributeId, Action<ConnectedSystemObjectAttributeValue> set)
    {
        var value = new ConnectedSystemObjectAttributeValue { AttributeId = attributeId, ConnectedSystemObject = cso };
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
            ContributedBySyncRuleId = SyncRuleId
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

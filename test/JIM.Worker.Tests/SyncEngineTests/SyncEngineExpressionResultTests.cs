// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.SyncEngineTests;

/// <summary>
/// How an inbound Attribute Flow Expression's result becomes the value a Metaverse attribute holds (#2179): typed
/// results compared as typed values, a result that cannot be converted kept out of the Metaverse rather than
/// silently clearing it, arrays stored in the target's own type, and dates read and written the same way whatever
/// the host's culture. Every test runs the real evaluator unless the result shape cannot be written as an
/// Expression.
/// </summary>
[TestFixture]
public class SyncEngineExpressionResultTests
{
    private const int TargetAttributeId = 100;
    private const int SourceAttributeId = 200;

    private Application.Servers.SyncEngine _engine = null!;
    private DynamicExpressoEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
        _evaluator = new DynamicExpressoEvaluator();
    }

    private sealed class Scenario
    {
        public required ConnectedSystemObject Cso { get; init; }
        public required MetaverseObject Mvo { get; init; }
        public required SyncRule SyncRule { get; init; }
        public required ConnectedSystemObjectType CsoType { get; init; }
        public required MetaverseAttribute Target { get; init; }
    }

    /// <summary>
    /// A Connected System Object joined to a Metaverse Object, with one Expression mapping from the object's single
    /// source attribute (<c>cs["src"]</c>) to a Metaverse attribute of <paramref name="targetType"/>.
    /// </summary>
    private static Scenario Build(
        string expression,
        AttributeDataType targetType,
        AttributeDataType sourceType = AttributeDataType.Text,
        Action<ConnectedSystemObjectAttributeValue>? sourceValue = null,
        AttributePlurality plurality = AttributePlurality.SingleValued)
    {
        var target = new MetaverseAttribute { Id = TargetAttributeId, Name = "target", Type = targetType, AttributePlurality = plurality };
        var source = new ConnectedSystemObjectTypeAttribute { Id = SourceAttributeId, Name = "src", Type = sourceType };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [source] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo };
        if (sourceValue != null)
        {
            var value = new ConnectedSystemObjectAttributeValue { AttributeId = SourceAttributeId, Attribute = source };
            sourceValue(value);
            cso.AttributeValues.Add(value);
        }

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = target };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = expression, Order = 1 });

        return new Scenario
        {
            Cso = cso,
            Mvo = mvo,
            SyncRule = new SyncRule { AttributeFlowRules = [mapping] },
            CsoType = csoType,
            Target = target
        };
    }

    private static MetaverseObjectAttributeValue Hold(Scenario scenario, Action<MetaverseObjectAttributeValue> value)
    {
        var held = new MetaverseObjectAttributeValue { Attribute = scenario.Target, AttributeId = TargetAttributeId, MetaverseObject = scenario.Mvo };
        value(held);
        scenario.Mvo.AttributeValues.Add(held);
        return held;
    }

    private List<AttributeFlowError> Flow(Scenario scenario, IExpressionEvaluator? evaluator = null) =>
        _engine.FlowInboundAttributes(scenario.Cso, scenario.SyncRule, [scenario.CsoType], evaluator ?? _evaluator);

    private static IExpressionEvaluator Returning(object result)
    {
        var evaluator = new Mock<IExpressionEvaluator>();
        evaluator.Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>())).Returns(result);
        return evaluator.Object;
    }

    private static void RunUnder(string cultureName, Action action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        try
        {
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    #region Typed comparison: an unchanged result stages nothing

    [Test]
    public void FlowInboundAttributes_NumberTargetResultUnchanged_StagesNothing()
    {
        var scenario = Build("ToInt(cs[\"src\"])", AttributeDataType.Number, sourceValue: v => v.StringValue = "42");
        Hold(scenario, v => v.IntValue = 42);

        var errors = Flow(scenario);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.Mvo.PendingAttributeValueRemovals, Is.Empty, "an unchanged Number must not be removed and re-added");
            Assert.That(scenario.Mvo.PendingAttributeValueAdditions, Is.Empty);
            Assert.That(errors, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_BooleanTargetResultUnchanged_StagesNothing()
    {
        var scenario = Build("IIF(Eq(cs[\"src\"], \"Active\"), true, false)", AttributeDataType.Boolean, sourceValue: v => v.StringValue = "Active");
        Hold(scenario, v => v.BoolValue = true);

        Flow(scenario);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.Mvo.PendingAttributeValueRemovals, Is.Empty, "an unchanged Boolean must not be removed and re-added");
            Assert.That(scenario.Mvo.PendingAttributeValueAdditions, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_DateTimeTargetFromFileTimeUnchanged_StagesNothing()
    {
        // The documented accountExpires recipe: the same instant on every synchronisation must stage nothing.
        var expires = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var scenario = Build("FromFileTime(cs[\"src\"])", AttributeDataType.DateTime, AttributeDataType.LongNumber,
            v => v.LongValue = expires.ToFileTimeUtc());
        Hold(scenario, v => v.DateTimeValue = expires);

        Flow(scenario);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.Mvo.PendingAttributeValueRemovals, Is.Empty, "an unchanged date must not be removed and re-added");
            Assert.That(scenario.Mvo.PendingAttributeValueAdditions, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_NumberTargetResultChanged_ReplacesTheValue()
    {
        var scenario = Build("ToInt(cs[\"src\"])", AttributeDataType.Number, sourceValue: v => v.StringValue = "43");
        var held = Hold(scenario, v => v.IntValue = 42);

        Flow(scenario);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.Mvo.PendingAttributeValueRemovals, Is.EqualTo(new[] { held }));
            Assert.That(scenario.Mvo.PendingAttributeValueAdditions.Select(v => v.IntValue), Is.EqualTo(new int?[] { 43 }));
        }
    }

    #endregion

    #region A result that cannot be converted keeps the value and is reported

    [Test]
    public void FlowInboundAttributes_ResultNotConvertibleToTheTargetType_KeepsTheValueAndRecordsAnError()
    {
        var scenario = Build("cs[\"src\"]", AttributeDataType.Number, sourceValue: v => v.StringValue = "EMP-48213");
        var held = Hold(scenario, v => v.IntValue = 42);

        var errors = Flow(scenario);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.Mvo.PendingAttributeValueRemovals, Is.Empty, "a refused conversion must never clear the value");
            Assert.That(scenario.Mvo.PendingAttributeValueAdditions, Is.Empty);
            Assert.That(scenario.Mvo.AttributeValues, Contains.Item(held));
            Assert.That(errors, Has.Count.EqualTo(1), "the refusal must be reported, never only logged");
            Assert.That(errors[0].Kind, Is.EqualTo(AttributeFlowErrorKind.ExpressionResultNotConvertible));
            Assert.That(errors[0].TargetAttributeName, Is.EqualTo("target"));
            Assert.That(errors[0].Expression, Is.EqualTo("cs[\"src\"]"));
        }
    }

    #endregion

    #region Array results are stored in the target's own type

    [Test]
    public void FlowInboundAttributes_SplitIntoMultiValuedNumber_StoresTypedNumbers()
    {
        var scenario = Build("Split(cs[\"src\"], \";\")", AttributeDataType.Number, sourceValue: v => v.StringValue = "1;2",
            plurality: AttributePlurality.MultiValued);

        var errors = Flow(scenario);

        var added = scenario.Mvo.PendingAttributeValueAdditions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(errors, Is.Empty);
            Assert.That(added.Select(v => v.IntValue), Is.EquivalentTo(new int?[] { 1, 2 }));
            Assert.That(added.Select(v => v.StringValue), Is.All.Null, "a Number attribute must never be given text rows");
        }
    }

    [Test]
    public void FlowInboundAttributes_IntArrayLiteralIntoMultiValuedNumber_StoresTypedNumbers()
    {
        var scenario = Build("new int[] { 1, 2 }", AttributeDataType.Number, plurality: AttributePlurality.MultiValued);

        Flow(scenario);

        Assert.That(scenario.Mvo.PendingAttributeValueAdditions.Select(v => v.IntValue), Is.EquivalentTo(new int?[] { 1, 2 }));
    }

    [Test]
    public void FlowInboundAttributes_DateTimeArrayIntoMultiValuedDateTime_StoresTypedDates()
    {
        var first = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc);
        var second = new DateTime(2026, 3, 16, 8, 0, 0, DateTimeKind.Utc);
        var scenario = Build("anything", AttributeDataType.DateTime, plurality: AttributePlurality.MultiValued);

        Flow(scenario, Returning(new[] { first, second }));

        var added = scenario.Mvo.PendingAttributeValueAdditions;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(added.Select(v => v.DateTimeValue), Is.EquivalentTo(new DateTime?[] { first, second }));
            Assert.That(added.Select(v => v.StringValue), Is.All.Null);
        }
    }

    [Test]
    public void FlowInboundAttributes_MultiValuedNumberArrayUnchanged_StagesNothing()
    {
        var scenario = Build("Split(cs[\"src\"], \";\")", AttributeDataType.Number, sourceValue: v => v.StringValue = "2;1",
            plurality: AttributePlurality.MultiValued);
        Hold(scenario, v => v.IntValue = 1);
        Hold(scenario, v => v.IntValue = 2);

        Flow(scenario);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.Mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(scenario.Mvo.PendingAttributeValueAdditions, Is.Empty);
        }
    }

    [Test]
    public void FlowInboundAttributes_ScalarIntoMultiValuedTargetHoldingTwoValues_ReplacesTheSet()
    {
        var scenario = Build("cs[\"src\"]", AttributeDataType.Text, sourceValue: v => v.StringValue = "c",
            plurality: AttributePlurality.MultiValued);
        var a = Hold(scenario, v => v.StringValue = "a");
        var b = Hold(scenario, v => v.StringValue = "b");

        Assert.That(() => Flow(scenario), Throws.Nothing, "a scalar into a set of two values must not fail the object");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.Mvo.PendingAttributeValueRemovals, Is.EquivalentTo(new[] { a, b }));
            Assert.That(scenario.Mvo.PendingAttributeValueAdditions.Select(v => v.StringValue), Is.EqualTo(new[] { "c" }));
        }
    }

    [Test]
    public void FlowInboundAttributes_SeveralValuesIntoSingleValuedTarget_KeepsTheValueAndRecordsAnError()
    {
        var scenario = Build("Split(cs[\"src\"], \";\")", AttributeDataType.Text, sourceValue: v => v.StringValue = "a;b");
        var held = Hold(scenario, v => v.StringValue = "kept");

        var errors = Flow(scenario);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.Mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(scenario.Mvo.PendingAttributeValueAdditions, Is.Empty, "a single-valued attribute must never be given two rows");
            Assert.That(scenario.Mvo.AttributeValues, Contains.Item(held));
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0].Kind, Is.EqualTo(AttributeFlowErrorKind.ExpressionResultMultiValued));
            Assert.That(errors[0].ValueCount, Is.EqualTo(2));
        }
    }

    #endregion

    #region Dates read and written the same way whatever the host's culture

    [TestCase("en-GB")]
    [TestCase("en-US")]
    public void FlowInboundAttributes_DateConcatenatedIntoText_UsesJimsDateTextFormUnderAnyCulture(string culture)
    {
        var when = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc);
        var scenario = Build("\"Changed \" + FromFileTime(cs[\"src\"])", AttributeDataType.Text, AttributeDataType.LongNumber,
            v => v.LongValue = when.ToFileTimeUtc());

        RunUnder(culture, () => Flow(scenario));

        Assert.That(scenario.Mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("Changed 2026-03-15 10:30:00Z"));
    }

    [TestCase("en-GB")]
    [TestCase("en-US")]
    public void FlowInboundAttributes_DateResultIntoText_UsesJimsDateTextFormUnderAnyCulture(string culture)
    {
        var when = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc).AddTicks(1_234_560);
        var scenario = Build("FromFileTime(cs[\"src\"])", AttributeDataType.Text, AttributeDataType.LongNumber,
            v => v.LongValue = when.ToFileTimeUtc());

        RunUnder(culture, () => Flow(scenario));

        Assert.That(scenario.Mvo.PendingAttributeValueAdditions.Single().StringValue, Is.EqualTo("2026-03-15 10:30:00.123456Z"),
            "a fraction is written only when it is non-zero, to the microsecond");
    }

    [TestCase("en-GB")]
    [TestCase("en-US")]
    public void FlowInboundAttributes_NumericDateTextIntoDateTime_RefusedUnderAnyCulture(string culture)
    {
        // "03/04/2024" is 3 April under en-GB and 4 March under en-US; JIM must not depend on the host to decide.
        var scenario = Build("cs[\"src\"]", AttributeDataType.DateTime, sourceValue: v => v.StringValue = "03/04/2024");
        var held = Hold(scenario, v => v.DateTimeValue = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var errors = new List<AttributeFlowError>();
        RunUnder(culture, () => errors = Flow(scenario));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.Mvo.PendingAttributeValueAdditions, Is.Empty, "an ambiguous date must not be read");
            Assert.That(scenario.Mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(scenario.Mvo.AttributeValues, Contains.Item(held));
            Assert.That(errors.Select(e => e.Kind), Is.EqualTo(new[] { AttributeFlowErrorKind.ExpressionResultNotConvertible }));
        }
    }

    [TestCase("2026-03-15T11:30:00+01:00")]
    [TestCase("2026-03-15T10:30:00Z")]
    [TestCase("2026-03-15 10:30:00")]
    [TestCase("2026-03-15T10:30")]
    public void FlowInboundAttributes_IsoDateTextIntoDateTime_StoredAsUtc(string text)
    {
        var scenario = Build("cs[\"src\"]", AttributeDataType.DateTime, sourceValue: v => v.StringValue = text);

        Flow(scenario);

        var stored = scenario.Mvo.PendingAttributeValueAdditions.Single().DateTimeValue!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Is.EqualTo(new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc)));
            Assert.That(stored.Kind, Is.EqualTo(DateTimeKind.Utc), "an offset is honoured and no offset means UTC, never the host's zone");
        }
    }

    #endregion

    #region Every expression failure is an expression error

    [TestCase("cs[\"missing\"].ToString()")]
    [TestCase("Split(cs[\"src\"], \",\")[5]")]
    public void FlowInboundAttributes_ExpressionFailsAtRunTime_IsAnExpressionErrorAndKeepsTheValue(string expression)
    {
        var scenario = Build(expression, AttributeDataType.Text, sourceValue: v => v.StringValue = "a,b");
        var held = Hold(scenario, v => v.StringValue = "kept");

        Assert.That(() => Flow(scenario), Throws.TypeOf<SyncExpressionEvaluationException>(),
            "a null reference or an index out of range is the administrator's expression failing, not a JIM fault");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scenario.Mvo.PendingAttributeValueRemovals, Is.Empty);
            Assert.That(scenario.Mvo.AttributeValues, Contains.Item(held));
        }
    }

    #endregion

    #region Binary inputs are copies

    [Test]
    public void FlowInboundAttributes_ExpressionChangesABinaryInput_StoredBytesAreUnchanged()
    {
        var scenario = Build("((byte[])cs[\"src\"]).SetValue((byte)9, 0)", AttributeDataType.Text, AttributeDataType.Binary,
            v => v.ByteValue = [1, 2, 3]);

        Flow(scenario);

        Assert.That(scenario.Cso.AttributeValues.Single().ByteValue, Is.EqualTo(new byte[] { 1, 2, 3 }),
            "an expression is handed a copy of a Binary value, so it cannot change the stored bytes");
    }

    #endregion
}

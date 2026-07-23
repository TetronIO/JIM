// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
/// Pure unit tests for inbound expression-based attribute flow in SyncEngine.FlowInboundAttributes (#842).
/// Verifies that a thrown expression is surfaced as a SyncExpressionEvaluationException (never silently
/// swallowed, never conflated with a deliberate null), while null and value results keep their behaviour.
/// </summary>
public class SyncEngineExpressionFlowTests
{
    private Application.Servers.SyncEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Application.Servers.SyncEngine();
    }

    /// <summary>
    /// Builds a CSO joined to an MVO that already holds an existing value for the target text attribute,
    /// with a single expression-based inbound mapping flowing to that attribute.
    /// </summary>
    private static (ConnectedSystemObject Cso, MetaverseObject Mvo, SyncRule SyncRule, ConnectedSystemObjectType CsoType, MetaverseObjectAttributeValue ExistingValue)
        BuildExpressionScenario(string expression)
    {
        var mvoAttr = new MetaverseAttribute { Id = 100, Name = "displayName", Type = AttributeDataType.Text };
        var csoAttr = new ConnectedSystemObjectTypeAttribute { Id = 200, Name = "num", Type = AttributeDataType.Number };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var existingValue = new MetaverseObjectAttributeValue { Attribute = mvoAttr, AttributeId = 100, StringValue = "Original Value" };
        mvo.AttributeValues.Add(existingValue);

        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, IntValue = 42 });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = expression, Order = 1 });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        return (cso, mvo, syncRule, csoType, existingValue);
    }

    private static IEnumerable<Exception> EvaluatorRuntimeExceptions()
    {
        yield return new ArgumentException("bad argument");
        yield return new FormatException("bad format");
        yield return new InvalidOperationException("bad operation");
        yield return new OverflowException("overflow");
        yield return new DivideByZeroException("divide by zero"); // ArithmeticException
        yield return new InvalidCastException("bad cast");
        yield return new KeyNotFoundException("missing key");
    }

    [TestCaseSource(nameof(EvaluatorRuntimeExceptions))]
    public void FlowInboundAttributes_ExpressionThrows_RethrowsAsSyncExpressionEvaluationException(Exception thrownByEvaluator)
    {
        // Arrange
        var (cso, mvo, syncRule, csoType, existingValue) = BuildExpressionScenario("Upper(cs[\"num\"])");
        var evaluator = new Mock<IExpressionEvaluator>();
        evaluator
            .Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>()))
            .Throws(thrownByEvaluator);

        // Act + Assert — the thrown expression surfaces as a typed sync exception, not swallowed
        var ex = Assert.Throws<SyncExpressionEvaluationException>(() =>
            _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType }, evaluator.Object));

        Assert.That(ex!.InnerException, Is.SameAs(thrownByEvaluator));
        Assert.That(ex.Expression, Is.EqualTo("Upper(cs[\"num\"])"));
        Assert.That(ex.TargetAttributeName, Is.EqualTo("displayName"));

        // Assert — the MVO value is untouched: no clear, no replacement queued
        Assert.That(mvo.AttributeValues, Contains.Item(existingValue));
        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty, "A thrown expression must not clear the target value.");
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty, "A thrown expression must not flow a new value.");
    }

    [Test]
    public void FlowInboundAttributes_ExpressionReturnsNull_ClearsExistingValue()
    {
        // Arrange — a deliberate null result keeps its existing single-contributor clear behaviour
        var (cso, mvo, syncRule, csoType, existingValue) = BuildExpressionScenario("cs[\"missing\"]");
        var evaluator = new Mock<IExpressionEvaluator>();
        evaluator
            .Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>()))
            .Returns((object?)null);

        // Act
        Assert.DoesNotThrow(() =>
            _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType }, evaluator.Object));

        // Assert — the existing value is queued for removal (distinct from the throw path)
        Assert.That(mvo.PendingAttributeValueRemovals, Contains.Item(existingValue));
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_ExpressionReturnsValue_FlowsValue()
    {
        // Arrange
        var (cso, mvo, syncRule, csoType, existingValue) = BuildExpressionScenario("Upper(cs[\"num\"])");
        var evaluator = new Mock<IExpressionEvaluator>();
        evaluator
            .Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>()))
            .Returns("New Value");

        // Act
        Assert.DoesNotThrow(() =>
            _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType }, evaluator.Object));

        // Assert — old value removed, new value added
        Assert.That(mvo.PendingAttributeValueRemovals, Contains.Item(existingValue));
        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().StringValue, Is.EqualTo("New Value"));
    }

    [Test]
    public void FlowInboundAttributes_RealEvaluatorRuntimeThrow_RethrowsAsSyncExpressionEvaluationException()
    {
        // Arrange — ToFileTime rejects an Int32 (Number attribute) with ArgumentException at invoke time.
        // Confirms the typed catch covers the concrete exception DynamicExpresso 2.x actually propagates.
        var (cso, mvo, syncRule, csoType, existingValue) = BuildExpressionScenario("ToFileTime(cs[\"num\"])");
        var evaluator = new DynamicExpressoEvaluator();

        // Act + Assert
        var ex = Assert.Throws<SyncExpressionEvaluationException>(() =>
            _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType }, evaluator));

        Assert.That(ex!.InnerException, Is.TypeOf<ArgumentException>());
        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
        Assert.That(mvo.AttributeValues, Contains.Item(existingValue));
    }

    [Test]
    public void FlowInboundAttributes_RealEvaluatorParseError_RethrowsAsSyncExpressionEvaluationException()
    {
        // Arrange — an unparseable expression throws a DynamicExpressoException (UnknownIdentifierException).
        var (cso, mvo, syncRule, csoType, _) = BuildExpressionScenario("@@@ not valid @@@");
        var evaluator = new DynamicExpressoEvaluator();

        // Act + Assert
        Assert.Throws<SyncExpressionEvaluationException>(() =>
            _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType }, evaluator));

        Assert.That(mvo.PendingAttributeValueRemovals, Is.Empty);
        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
    }

    #region Decimal expression targets (#1046)

    /// <summary>
    /// Builds a CSO joined to an MVO (no existing target value) with a single expression-based inbound
    /// mapping flowing to a Decimal Metaverse attribute. The CSO carries one Decimal attribute value so
    /// real-evaluator tests can prove decimals enter the expression context as System.Decimal.
    /// </summary>
    private static (ConnectedSystemObject Cso, MetaverseObject Mvo, SyncRule SyncRule, ConnectedSystemObjectType CsoType)
        BuildDecimalTargetScenario(string expression)
    {
        var mvoAttr = new MetaverseAttribute { Id = 100, Name = "salary", Type = AttributeDataType.Decimal };
        var csoAttr = new ConnectedSystemObjectTypeAttribute { Id = 200, Name = "salary", Type = AttributeDataType.Decimal };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [csoAttr] };

        var mvo = new MetaverseObject { Id = Guid.NewGuid() };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = 1, ConnectedSystemId = 5, MetaverseObject = mvo };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 200, DecimalValue = 10.5m });

        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = mvoAttr };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = expression, Order = 1 });
        var syncRule = new SyncRule { AttributeFlowRules = [mapping] };

        return (cso, mvo, syncRule, csoType);
    }

    private void FlowWithMockedResult(object result, ConnectedSystemObject cso, SyncRule syncRule, ConnectedSystemObjectType csoType)
    {
        var evaluator = new Mock<IExpressionEvaluator>();
        evaluator
            .Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>()))
            .Returns(result);

        Assert.DoesNotThrow(() =>
            _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType }, evaluator.Object));
    }

    [Test]
    public void FlowInboundAttributes_DecimalTarget_DecimalResult_StoredExactly()
    {
        var (cso, mvo, syncRule, csoType) = BuildDecimalTargetScenario("cs[\"salary\"]");

        FlowWithMockedResult(123.45m, cso, syncRule, csoType);

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().DecimalValue, Is.EqualTo(123.45m));
    }

    [Test]
    public void FlowInboundAttributes_DecimalTarget_IntResult_WidenedExactly()
    {
        var (cso, mvo, syncRule, csoType) = BuildDecimalTargetScenario("cs[\"salary\"]");

        FlowWithMockedResult(42, cso, syncRule, csoType);

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().DecimalValue, Is.EqualTo(42m));
    }

    [Test]
    public void FlowInboundAttributes_DecimalTarget_LongResult_WidenedExactly()
    {
        var (cso, mvo, syncRule, csoType) = BuildDecimalTargetScenario("cs[\"salary\"]");

        FlowWithMockedResult(9999999999L, cso, syncRule, csoType);

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().DecimalValue, Is.EqualTo(9999999999m));
    }

    [Test]
    public void FlowInboundAttributes_DecimalTarget_ExponentStringResult_ParsedAndCanonicalised()
    {
        var (cso, mvo, syncRule, csoType) = BuildDecimalTargetScenario("cs[\"salary\"]");

        FlowWithMockedResult("1.5E3", cso, syncRule, csoType);

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().DecimalValue, Is.EqualTo(1500m));
    }

    [Test]
    public void FlowInboundAttributes_DecimalTarget_DoubleResult_RejectedAndNoValueStaged()
    {
        // Binary floating point must never be silently converted to decimal; the result is rejected
        // (warning logged, no value staged), exactly as an unconvertible string would be.
        var (cso, mvo, syncRule, csoType) = BuildDecimalTargetScenario("cs[\"salary\"]");

        FlowWithMockedResult(1.5d, cso, syncRule, csoType);

        Assert.That(mvo.PendingAttributeValueAdditions, Is.Empty);
    }

    [Test]
    public void FlowInboundAttributes_DecimalTarget_StringResult_ParsedWithInvariantCulture()
    {
        // "1.5" must parse as one and a half even when the current culture uses a decimal comma.
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var (cso, mvo, syncRule, csoType) = BuildDecimalTargetScenario("cs[\"salary\"]");

            FlowWithMockedResult("1.5", cso, syncRule, csoType);

            Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
            Assert.That(mvo.PendingAttributeValueAdditions.First().DecimalValue, Is.EqualTo(1.5m));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void FlowInboundAttributes_DecimalTarget_RealEvaluator_CsoDecimalEntersContextAsDecimal()
    {
        // End to end with the real evaluator: BuildCsoAttributeDictionary must expose the CSO's Decimal
        // attribute value as System.Decimal, and the result must land in DecimalValue unchanged.
        var (cso, mvo, syncRule, csoType) = BuildDecimalTargetScenario("cs[\"salary\"]");
        var evaluator = new DynamicExpressoEvaluator();

        Assert.DoesNotThrow(() =>
            _engine.FlowInboundAttributes(cso, syncRule, new List<ConnectedSystemObjectType> { csoType }, evaluator));

        Assert.That(mvo.PendingAttributeValueAdditions, Has.Count.EqualTo(1));
        Assert.That(mvo.PendingAttributeValueAdditions.First().DecimalValue, Is.EqualTo(10.5m));
    }

    #endregion
}

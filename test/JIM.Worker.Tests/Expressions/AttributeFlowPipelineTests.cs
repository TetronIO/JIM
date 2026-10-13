// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// The one Attribute Flow evaluation pipeline (#2055): Missing Input Behaviour, evaluation, value processing and
/// conversion, in synchronisation's order, with no side effects. Synchronisation and the tester both run it; the
/// paired fidelity tests prove they agree.
/// </summary>
[TestFixture]
public class AttributeFlowPipelineTests
{
    private DynamicExpressoEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _evaluator = new DynamicExpressoEvaluator();
    }

    private static Dictionary<string, object?> Attributes(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    #region EvaluateSource: Missing Input Behaviour, then the Expression

    [Test]
    public void EvaluateSource_InputAbsentContributeNoValue_ReturnsNoValueWithoutEvaluating()
    {
        var evaluator = new Mock<IExpressionEvaluator>(MockBehavior.Strict);

        var source = AttributeFlowPipeline.EvaluateSource(evaluator.Object, "cs[\"sn\"]", MissingInputBehaviour.ContributeNoValue,
            Attributes(), metaverseAttributes: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Outcome, Is.EqualTo(ExpressionSourceOutcome.NoValue));
            Assert.That(source.MissingInputs, Is.EqualTo(new[] { "cs[\"sn\"]" }));
        }
    }

    [Test]
    public void EvaluateSource_InputAbsentFailMapping_ReturnsMissingInputFailedMapping()
    {
        var source = AttributeFlowPipeline.EvaluateSource(_evaluator, "cs[\"givenName\"] + cs[\"sn\"]", MissingInputBehaviour.FailMapping,
            Attributes(("givenName", "Priya")), metaverseAttributes: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Outcome, Is.EqualTo(ExpressionSourceOutcome.MissingInputFailedMapping));
            Assert.That(source.MissingInputs, Is.EqualTo(new[] { "cs[\"sn\"]" }));
        }
    }

    [Test]
    public void EvaluateSource_InputAbsentFailObject_ReturnsMissingInputFailedObject()
    {
        var source = AttributeFlowPipeline.EvaluateSource(_evaluator, "cs[\"sn\"]", MissingInputBehaviour.FailObject,
            Attributes(), metaverseAttributes: null);

        Assert.That(source.Outcome, Is.EqualTo(ExpressionSourceOutcome.MissingInputFailedObject));
    }

    [Test]
    public void EvaluateSource_InputAbsentEvaluateAnyway_Evaluates()
    {
        var source = AttributeFlowPipeline.EvaluateSource(_evaluator, "\"x\" + cs[\"sn\"]", MissingInputBehaviour.EvaluateAnyway,
            Attributes(), metaverseAttributes: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Outcome, Is.EqualTo(ExpressionSourceOutcome.Result));
            Assert.That(source.Result, Is.EqualTo("x"));
        }
    }

    [Test]
    public void EvaluateSource_MetaverseViewSupplied_CountsAbsentMetaverseInputs()
    {
        var source = AttributeFlowPipeline.EvaluateSource(_evaluator, "mv[\"Department\"]", MissingInputBehaviour.FailMapping,
            Attributes(), Attributes());

        Assert.That(source.MissingInputs, Is.EqualTo(new[] { "mv[\"Department\"]" }));
    }

    [Test]
    public void EvaluateSource_NoMetaverseView_IgnoresMetaverseInputs()
    {
        var source = AttributeFlowPipeline.EvaluateSource(_evaluator, "\"a\" + mv[\"Department\"]", MissingInputBehaviour.FailMapping,
            Attributes(), metaverseAttributes: null);

        Assert.That(source.Outcome, Is.EqualTo(ExpressionSourceOutcome.Result));
    }

    [Test]
    public void EvaluateSource_ExpressionThrows_ReturnsEvaluationFailedWithTheException()
    {
        var failure = new ExpressionEvaluationException("boom");
        var evaluator = new Mock<IExpressionEvaluator>();
        evaluator.Setup(e => e.Evaluate(It.IsAny<string>(), It.IsAny<ExpressionContext>())).Throws(failure);

        var source = AttributeFlowPipeline.EvaluateSource(evaluator.Object, "cs[\"a\"]", MissingInputBehaviour.EvaluateAnyway,
            Attributes(("a", "1")), metaverseAttributes: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Outcome, Is.EqualTo(ExpressionSourceOutcome.EvaluationFailed));
            Assert.That(source.EvaluationException, Is.SameAs(failure));
        }
    }

    [Test]
    public void EvaluateSource_ExpressionReturnsNull_ReturnsNoValue()
    {
        var source = AttributeFlowPipeline.EvaluateSource(_evaluator, "cs[\"a\"]", MissingInputBehaviour.EvaluateAnyway,
            Attributes(), metaverseAttributes: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Outcome, Is.EqualTo(ExpressionSourceOutcome.NoValue));
            Assert.That(source.MissingInputs, Is.Empty, "a null result is not a missing input");
        }
    }

    #endregion

    #region Resolve: value processing and conversion

    [Test]
    public void Resolve_ResultTheTargetCannotHold_IsResultNotConvertibleError()
    {
        var evaluation = AttributeFlowPipeline.Resolve(ExpressionSourceEvaluation.Returned("not a number"),
            AttributeDataType.Number, AttributePlurality.SingleValued, processText: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evaluation.Outcome, Is.EqualTo(AttributeFlowOutcome.Error));
            Assert.That(evaluation.ErrorReason, Is.EqualTo(AttributeFlowErrorReason.ResultNotConvertible));
            Assert.That(evaluation.Resolution!.UnconvertibleValue, Is.EqualTo("not a number"));
        }
    }

    [Test]
    public void Resolve_SeveralValuesForSingleValuedTarget_IsResultMultiValuedError()
    {
        var evaluation = AttributeFlowPipeline.Resolve(ExpressionSourceEvaluation.Returned(new[] { "a", "b" }),
            AttributeDataType.Text, AttributePlurality.SingleValued, processText: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evaluation.Outcome, Is.EqualTo(AttributeFlowOutcome.Error));
            Assert.That(evaluation.ErrorReason, Is.EqualTo(AttributeFlowErrorReason.ResultMultiValued));
        }
    }

    [Test]
    public void Resolve_TextProcessingCollapsesEveryValue_IsNoValue()
    {
        var evaluation = AttributeFlowPipeline.Resolve(ExpressionSourceEvaluation.Returned("   "),
            AttributeDataType.Text, AttributePlurality.SingleValued, processText: _ => null);

        Assert.That(evaluation.Outcome, Is.EqualTo(AttributeFlowOutcome.NoValue));
    }

    [Test]
    public void Resolve_ConvertibleResult_IsValuesInTheTargetsType()
    {
        var evaluation = AttributeFlowPipeline.Resolve(ExpressionSourceEvaluation.Returned("42"),
            AttributeDataType.Number, AttributePlurality.SingleValued, processText: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evaluation.Outcome, Is.EqualTo(AttributeFlowOutcome.Values));
            Assert.That(evaluation.Values, Is.EqualTo(new object[] { 42 }));
        }
    }

    [TestCase(ExpressionSourceOutcome.MissingInputFailedMapping, AttributeFlowErrorReason.MissingInputFailedMapping)]
    [TestCase(ExpressionSourceOutcome.MissingInputFailedObject, AttributeFlowErrorReason.MissingInputFailedObject)]
    public void Resolve_SourceStoppedByMissingInput_IsThatError(ExpressionSourceOutcome outcome, AttributeFlowErrorReason expected)
    {
        var evaluation = AttributeFlowPipeline.Resolve(ExpressionSourceEvaluation.MissingInput(outcome, ["cs[\"sn\"]"]),
            AttributeDataType.Text, AttributePlurality.SingleValued, processText: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evaluation.Outcome, Is.EqualTo(AttributeFlowOutcome.Error));
            Assert.That(evaluation.ErrorReason, Is.EqualTo(expected));
            Assert.That(evaluation.Resolution, Is.Null);
        }
    }

    #endregion

    #region EvaluateInbound and EvaluateExport: the context as synchronisation builds it

    [Test]
    public void EvaluateInbound_DateTimeInputCastInExpression_FormatsTheDate()
    {
        // Scenario 14: a DateTime attribute reaches the Expression as a DateTime, so the cast succeeds.
        var hireDate = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "hireDate", Type = AttributeDataType.DateTime };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [hireDate] };
        var cso = new ConnectedSystemObject { TypeId = 1 };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Attribute = hireDate, AttributeId = 1, DateTimeValue = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc)
        });
        var target = new MetaverseAttribute { Id = 9, Name = "Hire Date Text", Type = AttributeDataType.Text };
        var mapping = new SyncRuleMapping { TargetMetaverseAttribute = target };
        var source = new SyncRuleMappingSource { Expression = "FormatDate((DateTime)cs[\"hireDate\"], \"yyyy-MM-dd\")" };
        mapping.Sources.Add(source);

        var evaluation = AttributeFlowPipeline.EvaluateInbound(cso, csoType, mapping, source, _evaluator, metaverseAttributes: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evaluation.Outcome, Is.EqualTo(AttributeFlowOutcome.Values));
            Assert.That(evaluation.Values, Is.EqualTo(new object[] { "2026-03-02" }));
        }
    }

    [Test]
    public void EvaluateInbound_TextTarget_AppliesTheMappingsValueProcessing()
    {
        var givenName = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "givenName", Type = AttributeDataType.Text };
        var csoType = new ConnectedSystemObjectType { Id = 1, Attributes = [givenName] };
        var cso = new ConnectedSystemObject { TypeId = 1 };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = givenName, AttributeId = 1, StringValue = "  PRIYA " });
        var mapping = new SyncRuleMapping
        {
            TargetMetaverseAttribute = new MetaverseAttribute { Id = 9, Name = "First Name", Type = AttributeDataType.Text },
            InboundValueProcessing = InboundValueProcessing.TrimWhitespace,
            CaseNormalisation = InboundCaseNormalisation.Lower
        };
        var source = new SyncRuleMappingSource { Expression = "cs[\"givenName\"]" };
        mapping.Sources.Add(source);

        var evaluation = AttributeFlowPipeline.EvaluateInbound(cso, csoType, mapping, source, _evaluator, metaverseAttributes: null);

        Assert.That(evaluation.Values, Is.EqualTo(new object[] { "priya" }));
    }

    [Test]
    public void EvaluateExport_ReferenceTarget_ResolvesAsText()
    {
        var mapping = new SyncRuleMapping
        {
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "manager", Type = AttributeDataType.Reference }
        };
        var source = new SyncRuleMappingSource { Expression = "\"CN=\" + mv[\"Manager Name\"]" };
        mapping.Sources.Add(source);

        var evaluation = AttributeFlowPipeline.EvaluateExport(Attributes(("Manager Name", "Ada")), mapping, source, _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(evaluation.Outcome, Is.EqualTo(AttributeFlowOutcome.Values));
            Assert.That(evaluation.Values, Is.EqualTo(new object[] { "CN=Ada" }));
            Assert.That(evaluation.TargetType, Is.EqualTo(AttributeDataType.Text));
        }
    }

    [Test]
    public void EvaluateExport_ExportAppliesNoInboundValueProcessing()
    {
        var mapping = new SyncRuleMapping
        {
            TargetConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "displayName", Type = AttributeDataType.Text },
            InboundValueProcessing = InboundValueProcessing.TrimWhitespace
        };
        var source = new SyncRuleMappingSource { Expression = "\" padded \"" };
        mapping.Sources.Add(source);

        var evaluation = AttributeFlowPipeline.EvaluateExport(Attributes(), mapping, source, _evaluator);

        Assert.That(evaluation.Values, Is.EqualTo(new object[] { " padded " }));
    }

    #endregion
}

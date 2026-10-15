// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// The Attribute Flow tester on a Generated Value (#2055 Phase 2d, #242): it tests the base expression and stops before
/// generation, as synchronisation does. Generation needs the database to know which candidates are taken, so no
/// candidate is shown.
/// </summary>
[TestFixture]
public class AttributeFlowTesterGeneratedValueTests
{
    private DynamicExpressoEvaluator _evaluator = null!;
    private ConnectedSystemObjectType _csoType = null!;
    private MetaverseObjectType _mvType = null!;

    [SetUp]
    public void SetUp()
    {
        _evaluator = new DynamicExpressoEvaluator();
        _csoType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "person",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "givenName", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 13, Name = "sn", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 14, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued },
                new ConnectedSystemObjectTypeAttribute { Id = 16, Name = "sAMAccountName", Type = AttributeDataType.Text }
            ]
        };
        _mvType = new MetaverseObjectType
        {
            Id = 1,
            Name = "Person",
            Attributes =
            [
                new MetaverseAttribute { Id = 21, Name = "Account Name", Type = AttributeDataType.Text },
                new MetaverseAttribute { Id = 22, Name = "Employee Number", Type = AttributeDataType.Number },
                new MetaverseAttribute { Id = 24, Name = "Last Name", Type = AttributeDataType.Text }
            ]
        };
    }

    private (SyncRule Rule, SyncRuleMapping Mapping, SyncRuleMappingSource? Source) Generated(
        string? baseExpression, string targetName = "Account Name", SyncRuleDirection direction = SyncRuleDirection.Import,
        InboundValueProcessing processing = InboundValueProcessing.TreatWhitespaceAsNoValue,
        InboundCaseNormalisation caseNormalisation = InboundCaseNormalisation.None,
        MissingInputBehaviour missingInputBehaviour = MissingInputBehaviour.ContributeNoValue)
    {
        var rule = new SyncRule { Direction = direction, ConnectedSystemObjectType = _csoType, MetaverseObjectType = _mvType };
        var mapping = new SyncRuleMapping
        {
            InboundValueProcessing = processing, CaseNormalisation = caseNormalisation,
            Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken }
        };
        if (direction == SyncRuleDirection.Export)
            mapping.TargetConnectedSystemAttribute = _csoType.Attributes.Single(a => a.Name == targetName);
        else
            mapping.TargetMetaverseAttribute = _mvType.Attributes.Single(a => a.Name == targetName);

        SyncRuleMappingSource? source = null;
        if (baseExpression != null)
        {
            source = new SyncRuleMappingSource { Expression = baseExpression, MissingInputBehaviour = missingInputBehaviour };
            mapping.Sources.Add(source);
        }
        return (rule, mapping, source);
    }

    private static AttributeFlowTestInput Cs(string name, params object[] values) => new(ExpressionInputSource.ConnectedSystem, name, values);

    private static AttributeFlowTestInput Mv(string name, params object[] values) => new(ExpressionInputSource.Metaverse, name, values);

    [Test]
    public void Test_GeneratedMapping_GivesTheBaseAfterValueProcessingAndNoOrdinaryEvaluation()
    {
        var (rule, mapping, source) = Generated("Left(cs[\"givenName\"], 1) + cs[\"sn\"]",
            processing: InboundValueProcessing.TrimWhitespace, caseNormalisation: InboundCaseNormalisation.Lower);

        var result = AttributeFlowTester.Test(rule, mapping, source, [Cs("givenName", "John"), Cs("sn", "Smith ")], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Evaluation, Is.Null, "a generated mapping is not an ordinary Attribute Flow");
            Assert.That(result.GeneratedBase!.Outcome, Is.EqualTo(GeneratedBaseOutcome.Base));
            Assert.That(result.GeneratedBase!.BaseValue, Is.EqualTo("jsmith"));
            Assert.That(result.GeneratedBase!.UnprocessedBaseValue, Is.EqualTo("JSmith "));
            Assert.That(result.ValueProcessing!.Changed, Is.True);
        }
    }

    [Test]
    public void Test_GeneratedMappingInputAbsent_Waits()
    {
        var (rule, mapping, source) = Generated("cs[\"givenName\"] + \".\" + cs[\"sn\"]");

        var result = AttributeFlowTester.Test(rule, mapping, source, [Cs("givenName", "John")], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GeneratedBase!.Outcome, Is.EqualTo(GeneratedBaseOutcome.Waits));
            Assert.That(result.GeneratedBase!.Source!.MissingInputs, Is.EqualTo(new[] { "cs[\"sn\"]" }));
        }
    }

    [Test]
    public void Test_GeneratedMappingSeveralValues_IsGeneratedBaseNotSingleValueError()
    {
        var (rule, mapping, source) = Generated("cs[\"mail\"]");

        var result = AttributeFlowTester.Test(rule, mapping, source, [Cs("mail", "a@corp.local", "b@corp.local")], _evaluator);

        Assert.That(result.GeneratedBase!.ErrorReason, Is.EqualTo(AttributeFlowErrorReason.GeneratedBaseNotSingleValue));
    }

    [Test]
    public void Test_GeneratedMappingWithNoBaseExpression_IsNoBaseExpression()
    {
        var (rule, mapping, _) = Generated(baseExpression: null, targetName: "Employee Number");

        var result = AttributeFlowTester.Test(rule, mapping, source: null, [], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GeneratedBase!.Outcome, Is.EqualTo(GeneratedBaseOutcome.NoBaseExpression));
            Assert.That(result.GeneratedBaseIfInputHasNoValue, Is.Empty);
        }
    }

    [Test]
    public void Test_GeneratedMapping_ReportsEachInputsAbsenceAsAWait()
    {
        var (rule, mapping, source) = Generated("cs[\"givenName\"] + cs[\"sn\"]");

        var result = AttributeFlowTester.Test(rule, mapping, source, [Cs("givenName", "John"), Cs("sn", "Smith")], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GeneratedBaseIfInputHasNoValue.Select(o => o.Accessor), Is.EqualTo(new[] { "cs[\"givenName\"]", "cs[\"sn\"]" }));
            Assert.That(result.GeneratedBaseIfInputHasNoValue.Select(o => o.GeneratedBase.Outcome), Is.All.EqualTo(GeneratedBaseOutcome.Waits));
            Assert.That(result.IfInputHasNoValue, Is.Empty);
        }
    }

    [Test]
    public void Test_GeneratedExportMapping_UsesTheMetaverseViewWithNoValueProcessing()
    {
        var (rule, mapping, source) = Generated("mv[\"Last Name\"]", targetName: "sAMAccountName", direction: SyncRuleDirection.Export,
            processing: InboundValueProcessing.TrimWhitespace);

        var result = AttributeFlowTester.Test(rule, mapping, source, [Mv("Last Name", " Smith ")], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GeneratedBase!.BaseValue, Is.EqualTo(" Smith "));
            Assert.That(result.ValueProcessing, Is.Null);
        }
    }

    [Test]
    public void Describe_GeneratedBase_SaysGenerationAddsTheTokenAndNamesTheBase()
    {
        var (rule, mapping, source) = Generated("cs[\"sn\"]");
        var result = AttributeFlowTester.Test(rule, mapping, source, [Cs("sn", "smith")], _evaluator);

        var description = AttributeFlowTester.DescribeGeneratedBase(result.GeneratedBase!, mapping.Generation!, "Account Name");

        Assert.That(description, Does.Contain("smith").And.Contain("Account Name"));
    }

    [TestCase(GeneratedBaseOutcome.Waits)]
    [TestCase(GeneratedBaseOutcome.NoBaseExpression)]
    public void Describe_GeneratedBaseOtherOutcomes_DescribesEach(GeneratedBaseOutcome outcome)
    {
        var generated = new GeneratedBaseEvaluation
        {
            Outcome = outcome,
            Source = outcome == GeneratedBaseOutcome.Waits ? ExpressionSourceEvaluation.ReturnedNull() : null
        };

        var description = AttributeFlowTester.DescribeGeneratedBase(generated, new SyncRuleMappingGeneration(), "Account Name");

        Assert.That(description, Is.Not.Empty);
    }

    [TestCase(AttributeFlowErrorReason.GeneratedBaseNotSingleValue)]
    [TestCase(AttributeFlowErrorReason.ResultNotConvertible)]
    [TestCase(AttributeFlowErrorReason.MissingInputFailedMapping)]
    [TestCase(AttributeFlowErrorReason.MissingInputFailedObject)]
    [TestCase(AttributeFlowErrorReason.EvaluationFailed)]
    public void Describe_GeneratedBaseEveryErrorReason_DescribesIt(AttributeFlowErrorReason reason)
    {
        var generated = new GeneratedBaseEvaluation
        {
            Outcome = GeneratedBaseOutcome.Error, ErrorReason = reason,
            Source = reason == AttributeFlowErrorReason.EvaluationFailed
                ? ExpressionSourceEvaluation.Failed(new ExpressionEvaluationException("boom"))
                : ExpressionSourceEvaluation.MissingInput(ExpressionSourceOutcome.MissingInputFailedMapping, ["cs[\"sn\"]"])
        };

        var description = AttributeFlowTester.DescribeGeneratedBase(generated, new SyncRuleMappingGeneration(), "Account Name");

        Assert.That(description, Is.Not.Empty);
    }
}

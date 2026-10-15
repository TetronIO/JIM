// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// The Example Data expression tester (#2055 Phase 2d): an Example Data Template attribute's expression evaluated
/// against sample values for the attributes it reads, by the same evaluation generating objects runs, so a test cannot
/// pass what generation fails. Each sample is typed by the template attribute that generates it on the same object.
/// </summary>
[TestFixture]
public class ExampleDataExpressionTesterTests
{
    private DynamicExpressoEvaluator _evaluator = null!;
    private ExampleDataObjectType _objectType = null!;
    private ExampleDataTemplateAttribute _accountName = null!;

    [SetUp]
    public void SetUp()
    {
        _evaluator = new DynamicExpressoEvaluator();
        var person = new MetaverseObjectType { Id = 1, Name = "Person" };
        _objectType = new ExampleDataObjectType { Id = 1, MetaverseObjectType = person };
        _objectType.TemplateAttributes.Add(Generated(1, "First Name", AttributeDataType.Text));
        _objectType.TemplateAttributes.Add(Generated(2, "Last Name", AttributeDataType.Text));
        _objectType.TemplateAttributes.Add(Generated(3, "Employee Number", AttributeDataType.Number));
        _accountName = Generated(5, "Account Name", AttributeDataType.Text);
        _accountName.Expression = "Lower(mv[\"First Name\"]) + \".\" + Lower(mv[\"Last Name\"])";
        _objectType.TemplateAttributes.Add(_accountName);
    }

    private static ExampleDataTemplateAttribute Generated(int id, string name, AttributeDataType type) => new()
    {
        Id = id, MetaverseAttribute = new MetaverseAttribute { Id = 100 + id, Name = name, Type = type }
    };

    private ExampleDataExpressionTestResult Test(string expression, params AttributeSample[] samples) =>
        ExampleDataServer.TestAttributeExpression(_objectType, _accountName, expression, samples, _evaluator);

    private static AttributeSample Sample(string name, params object[] values) => new(name, values);

    [Test]
    public void TestAttributeExpression_SamplesForTheAttributesItReads_GivesTheValueGenerationWould()
    {
        var result = Test(_accountName.Expression!, Sample("First Name", "John"), Sample("Last Name", "Smith"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.InputErrors, Is.Empty);
            Assert.That(result.Evaluation!.Outcome, Is.EqualTo(ExampleDataExpressionOutcome.Value));
            Assert.That(result.Evaluation!.Value, Is.EqualTo("john.smith"));
            Assert.That(result.Inputs.Select(i => i.AttributeName), Is.EqualTo(new[] { "First Name", "Last Name" }));
        }
    }

    [Test]
    public void TestAttributeExpression_NumberInput_ReachesTheExpressionAsANumber()
    {
        var result = Test("(mv[\"Employee Number\"] + 1).ToString()", Sample("Employee Number", "41"));

        Assert.That(result.Evaluation!.Value, Is.EqualTo("42"));
    }

    [Test]
    public void TestAttributeExpression_ExpressionReturnsNull_IsNoValue()
    {
        var result = Test("mv[\"First Name\"]");

        Assert.That(result.Evaluation!.Outcome, Is.EqualTo(ExampleDataExpressionOutcome.NoValue));
    }

    [Test]
    public void TestAttributeExpression_SeveralValues_IsTheErrorGenerationRaises()
    {
        var result = Test("Split(\"a;b\", \";\")");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Evaluation!.Outcome, Is.EqualTo(ExampleDataExpressionOutcome.Error));
            Assert.That(result.Evaluation!.ErrorReason, Is.EqualTo(ExampleDataExpressionErrorReason.SeveralValues));
        }
    }

    [Test]
    public void TestAttributeExpression_ExpressionThatDoesNotParse_IsEvaluationFailedWithTheReason()
    {
        var result = Test("Upper(");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Evaluation!.ErrorReason, Is.EqualTo(ExampleDataExpressionErrorReason.EvaluationFailed));
            Assert.That(result.Evaluation!.EvaluationError, Is.Not.Empty);
        }
    }

    [Test]
    public void TestAttributeExpression_InputTheTemplateDoesNotGenerate_IsListedAndNeverHasAValue()
    {
        var result = Test("mv[\"Department\"]");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.InputsNotGenerated, Is.EqualTo(new[] { "Department" }));
            Assert.That(result.Evaluation!.Outcome, Is.EqualTo(ExampleDataExpressionOutcome.NoValue));
        }
    }

    [Test]
    public void TestAttributeExpression_SampleForAnInputTheTemplateDoesNotGenerate_IsAnInputError()
    {
        var result = Test("mv[\"Department\"]", Sample("Department", "Finance"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.InputErrors.Single(), Does.Contain("Department"));
            Assert.That(result.Evaluation, Is.Null);
        }
    }

    [Test]
    public void TestAttributeExpression_SampleNotOfItsAttributesType_IsAnInputError()
    {
        var result = Test("(mv[\"Employee Number\"] + 1).ToString()", Sample("Employee Number", "forty-one"));

        Assert.That(result.InputErrors, Is.Not.Empty);
    }

    [Test]
    public void TestAttributeExpression_AgreesWithGeneration()
    {
        // The tester and generation share one evaluation, so the generated object's value is the tested one.
        var generated = new MetaverseObject { Type = _objectType.MetaverseObjectType };
        generated.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = _objectType.TemplateAttributes[0].MetaverseAttribute!, StringValue = "Ada" });
        generated.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = _objectType.TemplateAttributes[1].MetaverseAttribute!, StringValue = "Lovelace" });

        var generatedValue = ExampleDataServer.EvaluateAttributeExpression(_evaluator, generated, _accountName);
        var tested = Test(_accountName.Expression!, Sample("First Name", "Ada"), Sample("Last Name", "Lovelace"));

        Assert.That(tested.Evaluation!.Value, Is.EqualTo(generatedValue));
    }

    [TestCase(ExampleDataExpressionOutcome.Value, null)]
    [TestCase(ExampleDataExpressionOutcome.NoValue, null)]
    [TestCase(ExampleDataExpressionOutcome.Error, ExampleDataExpressionErrorReason.EvaluationFailed)]
    [TestCase(ExampleDataExpressionOutcome.Error, ExampleDataExpressionErrorReason.SeveralValues)]
    [TestCase(ExampleDataExpressionOutcome.Error, ExampleDataExpressionErrorReason.NoTextForm)]
    public void DescribeExpressionOutcome_EveryOutcome_NamesTheAttribute(ExampleDataExpressionOutcome outcome, ExampleDataExpressionErrorReason? reason)
    {
        var evaluation = new ExampleDataExpressionEvaluation { Outcome = outcome, ErrorReason = reason, Value = "x", EvaluationError = "boom" };

        Assert.That(ExampleDataServer.DescribeExpressionOutcome(evaluation, "Account Name"), Does.Contain("Account Name"));
    }
}

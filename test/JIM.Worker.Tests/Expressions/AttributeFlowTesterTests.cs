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
/// What the Attribute Flow tester (#2055, Phase 2) gives beyond the outcome: each input described from the schema so
/// a surface can offer the right editor, inputs loaded from a real object, the value processing step of the trace, and
/// what each input's absence would do. The outcome itself is proven against synchronisation by
/// <see cref="AttributeFlowTesterFidelityTests"/>.
/// </summary>
[TestFixture]
public class AttributeFlowTesterTests
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
                new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "hireDate", Type = AttributeDataType.DateTime },
                new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "givenName", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 13, Name = "sn", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 14, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued },
                new ConnectedSystemObjectTypeAttribute { Id = 15, Name = "manager", Type = AttributeDataType.Reference }
            ]
        };
        _mvType = new MetaverseObjectType
        {
            Id = 1,
            Name = "Person",
            Attributes =
            [
                new MetaverseAttribute { Id = 21, Name = "Display Name", Type = AttributeDataType.Text },
                new MetaverseAttribute { Id = 22, Name = "Employee Number", Type = AttributeDataType.Number },
                new MetaverseAttribute { Id = 23, Name = "Hire Date", Type = AttributeDataType.DateTime }
            ]
        };
    }

    private (SyncRule Rule, SyncRuleMapping Mapping, SyncRuleMappingSource Source) Flow(
        string expression, string targetName = "Display Name", SyncRuleDirection direction = SyncRuleDirection.Import,
        InboundValueProcessing processing = InboundValueProcessing.TreatWhitespaceAsNoValue,
        InboundCaseNormalisation caseNormalisation = InboundCaseNormalisation.None,
        MissingInputBehaviour missingInputBehaviour = MissingInputBehaviour.EvaluateAnyway)
    {
        var rule = new SyncRule
        {
            Direction = direction, ConnectedSystemObjectType = _csoType, MetaverseObjectType = _mvType
        };
        var mapping = new SyncRuleMapping { InboundValueProcessing = processing, CaseNormalisation = caseNormalisation };
        if (direction == SyncRuleDirection.Export)
            mapping.TargetConnectedSystemAttribute = _csoType.Attributes.Single(a => a.Name == targetName);
        else
            mapping.TargetMetaverseAttribute = _mvType.Attributes.Single(a => a.Name == targetName);
        var source = new SyncRuleMappingSource { Expression = expression, MissingInputBehaviour = missingInputBehaviour };
        mapping.Sources.Add(source);
        return (rule, mapping, source);
    }

    private static AttributeFlowTestInput Cs(string name, params object[] values) => new(ExpressionInputSource.ConnectedSystem, name, values);

    #region DescribeInputs

    [Test]
    public void DescribeInputs_ImportFlow_TypesEachInputFromTheSchema()
    {
        var (rule, _, _) = Flow("FormatDate((DateTime)cs[\"hireDate\"], \"yyyy\") + cs[\"mail\"]");

        var inputs = AttributeFlowTester.DescribeInputs(rule, "FormatDate((DateTime)cs[\"hireDate\"], \"yyyy\") + cs[\"mail\"]");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inputs.Select(i => i.Accessor), Is.EqualTo(new[] { "cs[\"hireDate\"]", "cs[\"mail\"]" }));
            Assert.That(inputs[0].Type, Is.EqualTo(AttributeDataType.DateTime));
            Assert.That(inputs[0].Plurality, Is.EqualTo(AttributePlurality.SingleValued));
            Assert.That(inputs[1].Plurality, Is.EqualTo(AttributePlurality.MultiValued));
        }
    }

    [Test]
    public void DescribeInputs_AttributeNotInTheSchema_HasNoType()
    {
        var (rule, _, _) = Flow("cs[\"nickname\"]");

        var input = AttributeFlowTester.DescribeInputs(rule, "cs[\"nickname\"]").Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(input.Type, Is.Null);
            Assert.That(input.InSchema, Is.False);
        }
    }

    [Test]
    public void DescribeInputs_ExportFlowReadingConnectedSystemInput_HasNoType()
    {
        // An export Attribute Flow reads only the Metaverse Object, so a cs[...] input can never have a value.
        var (rule, _, _) = Flow("cs[\"sn\"] + mv[\"Display Name\"]", "sn", SyncRuleDirection.Export);

        var inputs = AttributeFlowTester.DescribeInputs(rule, "cs[\"sn\"] + mv[\"Display Name\"]");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inputs.Single(i => i.Source == ExpressionInputSource.ConnectedSystem).InSchema, Is.False);
            Assert.That(inputs.Single(i => i.Source == ExpressionInputSource.Metaverse).Type, Is.EqualTo(AttributeDataType.Text));
        }
    }

    #endregion

    #region Inputs from an object

    [Test]
    public void InputsFromConnectedSystemObject_TakesEveryValueOfEachInputTheExpressionReads()
    {
        var cso = new ConnectedSystemObject { TypeId = 1 };
        var mail = _csoType.Attributes.Single(a => a.Name == "mail");
        var hireDate = _csoType.Attributes.Single(a => a.Name == "hireDate");
        var sn = _csoType.Attributes.Single(a => a.Name == "sn");
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = mail, AttributeId = mail.Id, StringValue = "a@example.org" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = mail, AttributeId = mail.Id, StringValue = "b@example.org" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = hireDate, AttributeId = hireDate.Id, DateTimeValue = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc) });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = sn, AttributeId = sn.Id, StringValue = "Shah" });

        var inputs = AttributeFlowTester.InputsFromConnectedSystemObject(cso, _csoType, "cs[\"mail\"] + cs[\"hireDate\"] + cs[\"givenName\"]");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inputs.Select(i => i.AttributeName), Is.EqualTo(new[] { "mail", "hireDate", "givenName" }),
                "one input per attribute the Expression reads, and none for attributes it does not");
            Assert.That(inputs[0].Values, Is.EqualTo(new object[] { "a@example.org", "b@example.org" }));
            Assert.That(inputs[1].Values, Is.EqualTo(new object[] { new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc) }));
            Assert.That(inputs[2].Values, Is.Empty, "an attribute the object has no value for is an input with no value");
        }
    }

    [Test]
    public void InputsFromMetaverseObject_SkipsAssertedNullMarkers()
    {
        var displayName = _mvType.Attributes.Single(a => a.Name == "Display Name");
        var mvo = new MetaverseObject { Type = _mvType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Attribute = displayName, AttributeId = displayName.Id, NullValue = true });

        var input = AttributeFlowTester.InputsFromMetaverseObject(mvo, "mv[\"Display Name\"]").Single();

        Assert.That(input.Values, Is.Empty, "an asserted-null marker carries no value, as synchronisation reads it");
    }

    [Test]
    public void InputsFromConnectedSystemObject_RunThroughTheTester_GiveTheObjectsOutcome()
    {
        var (rule, mapping, source) = Flow("FormatDate((DateTime)cs[\"hireDate\"], \"yyyy-MM-dd\")");
        var cso = new ConnectedSystemObject { TypeId = 1 };
        var hireDate = _csoType.Attributes.Single(a => a.Name == "hireDate");
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = hireDate, AttributeId = hireDate.Id, DateTimeValue = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc) });

        var inputs = AttributeFlowTester.InputsFromConnectedSystemObject(cso, _csoType, source.Expression!);
        var result = AttributeFlowTester.Test(rule, mapping, source, inputs, _evaluator);

        Assert.That(result.Evaluation!.Values, Is.EqualTo(new object[] { "2026-03-02" }));
    }

    #endregion

    #region The value processing step

    [Test]
    public void Test_ImportToTextWithProcessing_ReportsTheStepsAndWhetherTheyChangedTheValue()
    {
        var (rule, mapping, source) = Flow("cs[\"givenName\"]",
            processing: InboundValueProcessing.TrimWhitespace | InboundValueProcessing.TreatWhitespaceAsNoValue,
            caseNormalisation: InboundCaseNormalisation.Lower);

        var result = AttributeFlowTester.Test(rule, mapping, source, [Cs("givenName", " PRIYA ")], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ValueProcessing, Is.Not.Null);
            Assert.That(result.ValueProcessing!.Steps, Is.EqualTo(new[]
            {
                AttributeFlowValueProcessingStep.TrimWhitespace,
                AttributeFlowValueProcessingStep.LowerCase,
                AttributeFlowValueProcessingStep.TreatWhitespaceAsNoValue
            }), "in the order synchronisation applies them");
            Assert.That(result.ValueProcessing!.Changed, Is.True);
        }
    }

    [Test]
    public void Test_ProcessingLeavesTheValueAsItWas_ReportsUnchanged()
    {
        var (rule, mapping, source) = Flow("cs[\"givenName\"]");

        var result = AttributeFlowTester.Test(rule, mapping, source, [Cs("givenName", "Priya")], _evaluator);

        Assert.That(result.ValueProcessing!.Changed, Is.False);
    }

    [Test]
    public void Test_NonTextTarget_HasNoValueProcessingStep()
    {
        var (rule, mapping, source) = Flow("cs[\"hireDate\"]", "Hire Date");

        var result = AttributeFlowTester.Test(rule, mapping, source, [Cs("hireDate", "2026-03-02")], _evaluator);

        Assert.That(result.ValueProcessing, Is.Null, "value processing applies to Text attributes only");
    }

    [Test]
    public void Test_ExportFlow_HasNoValueProcessingStep()
    {
        var (rule, mapping, source) = Flow("mv[\"Display Name\"]", "sn", SyncRuleDirection.Export);

        var result = AttributeFlowTester.Test(rule, mapping, source,
            [new AttributeFlowTestInput(ExpressionInputSource.Metaverse, "Display Name", ["Priya"])], _evaluator);

        Assert.That(result.ValueProcessing, Is.Null, "export applies no inbound value processing");
    }

    #endregion

    #region If an input has no value

    [Test]
    public void Test_EachInputWithAValue_IsAlsoTestedWithItAbsent()
    {
        var (rule, mapping, source) = Flow("cs[\"givenName\"] + \".\" + cs[\"sn\"]", missingInputBehaviour: MissingInputBehaviour.FailMapping);

        var result = AttributeFlowTester.Test(rule, mapping, source, [Cs("givenName", "Priya"), Cs("sn", "Shah")], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IfInputHasNoValue.Select(o => o.Accessor), Is.EqualTo(new[] { "cs[\"givenName\"]", "cs[\"sn\"]" }));
            Assert.That(result.IfInputHasNoValue.Select(o => o.Evaluation.ErrorReason),
                Is.All.EqualTo(AttributeFlowErrorReason.MissingInputFailedMapping));
        }
    }

    [Test]
    public void Test_InputAlreadyWithoutAValue_IsNotRepeatedInTheAbsentTable()
    {
        var (rule, mapping, source) = Flow("\"x\" + cs[\"givenName\"] + cs[\"sn\"]");

        var result = AttributeFlowTester.Test(rule, mapping, source, [Cs("givenName", "Priya"), Cs("sn")], _evaluator);

        Assert.That(result.IfInputHasNoValue.Select(o => o.Accessor), Is.EqualTo(new[] { "cs[\"givenName\"]" }));
    }

    [Test]
    public void Test_InputErrors_HaveNoAbsentTable()
    {
        var (rule, mapping, source) = Flow("cs[\"hireDate\"]", "Hire Date");

        var result = AttributeFlowTester.Test(rule, mapping, source, [Cs("hireDate", "next Tuesday")], _evaluator);

        Assert.That(result.IfInputHasNoValue, Is.Empty);
    }

    #endregion
}

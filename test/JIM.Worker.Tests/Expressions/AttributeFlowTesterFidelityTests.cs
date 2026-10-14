// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// Paired fidelity tests for the Attribute Flow tester (#2055): each case runs the same values through a real
/// synchronisation (<see cref="SyncEngine.FlowInboundAttributes"/>, <see cref="SyncEngine.ComputeAttributeValueChanges"/>)
/// and through <see cref="AttributeFlowTester"/>, given as the text an administrator types, and asserts the two agree
/// on the values, no value, or the error. Covers typed, multi-valued and absent inputs, value processing, Missing Input
/// Behaviour, and the false failure and false pass the tester used to give.
/// </summary>
[TestFixture]
public class AttributeFlowTesterFidelityTests
{
    private const int CsoTypeId = 1;

    private SyncEngine _engine = null!;
    private DynamicExpressoEvaluator _evaluator = null!;
    private ConnectedSystemObjectType _csoType = null!;
    private MetaverseObjectType _mvType = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new SyncEngine();
        _evaluator = new DynamicExpressoEvaluator();
        _csoType = new ConnectedSystemObjectType
        {
            Id = CsoTypeId,
            Name = "person",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "hireDate", Type = AttributeDataType.DateTime },
                new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "givenName", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 13, Name = "employeeNumber", Type = AttributeDataType.Number },
                new ConnectedSystemObjectTypeAttribute { Id = 14, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued },
                new ConnectedSystemObjectTypeAttribute { Id = 15, Name = "active", Type = AttributeDataType.Boolean }
            ]
        };
        _mvType = new MetaverseObjectType
        {
            Id = 1,
            Name = "Person",
            Attributes =
            [
                new MetaverseAttribute { Id = 21, Name = "Hire Date", Type = AttributeDataType.DateTime },
                new MetaverseAttribute { Id = 22, Name = "Display Name", Type = AttributeDataType.Text },
                new MetaverseAttribute { Id = 23, Name = "Employee Number", Type = AttributeDataType.Number },
                new MetaverseAttribute { Id = 24, Name = "Proxy Addresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued }
            ]
        };
    }

    #region Import: the tester against FlowInboundAttributes

    private sealed record ImportCase(SyncRule Rule, SyncRuleMapping Mapping, SyncRuleMappingSource Source);

    private ImportCase Import(string expression, MetaverseAttribute target,
        MissingInputBehaviour missingInputBehaviour = MissingInputBehaviour.EvaluateAnyway,
        InboundValueProcessing processing = InboundValueProcessing.TreatWhitespaceAsNoValue,
        InboundCaseNormalisation caseNormalisation = InboundCaseNormalisation.None)
    {
        var rule = new SyncRule
        {
            Id = 1, Name = "HR Inbound", Direction = SyncRuleDirection.Import, Enabled = true,
            ConnectedSystemObjectType = _csoType, ConnectedSystemObjectTypeId = CsoTypeId,
            MetaverseObjectType = _mvType, MetaverseObjectTypeId = _mvType.Id
        };
        var mapping = new SyncRuleMapping
        {
            Id = 1, SyncRule = rule, TargetMetaverseAttribute = target, TargetMetaverseAttributeId = target.Id,
            InboundValueProcessing = processing, CaseNormalisation = caseNormalisation
        };
        var source = new SyncRuleMappingSource { Order = 1, Expression = expression, MissingInputBehaviour = missingInputBehaviour };
        mapping.Sources.Add(source);
        rule.AttributeFlowRules.Add(mapping);
        return new ImportCase(rule, mapping, source);
    }

    private MetaverseAttribute Mv(string name) => _mvType.Attributes.Single(a => a.Name == name);

    private ConnectedSystemObjectTypeAttribute Cs(string name) => _csoType.Attributes.Single(a => a.Name == name);

    /// <summary>What a real synchronisation does: the values it would add, the errors it records, or the exception that fails the object.</summary>
    private (List<object> Values, List<AttributeFlowError> Errors, Exception? Thrown) Synchronise(
        ImportCase scenario, params (string Attribute, Action<ConnectedSystemObjectAttributeValue> Set)[] values)
    {
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvType };
        var cso = new ConnectedSystemObject { Id = Guid.NewGuid(), TypeId = CsoTypeId, ConnectedSystemId = 5, MetaverseObject = mvo };
        foreach (var (attribute, set) in values)
        {
            var value = new ConnectedSystemObjectAttributeValue { Attribute = Cs(attribute), AttributeId = Cs(attribute).Id, ConnectedSystemObject = cso };
            set(value);
            cso.AttributeValues.Add(value);
        }

        try
        {
            var errors = _engine.FlowInboundAttributes(cso, scenario.Rule, [_csoType], _evaluator);
            var added = mvo.PendingAttributeValueAdditions
                .Select(v => (object?)v.StringValue ?? (object?)v.IntValue ?? (object?)v.DateTimeValue ?? (object?)v.BoolValue ?? v.LongValue)
                .Select(v => v!)
                .ToList();
            return (added, errors, null);
        }
        catch (SyncExpressionEvaluationException ex)
        {
            return ([], [], ex);
        }
        catch (SyncExpressionMissingInputException ex)
        {
            return ([], [], ex);
        }
    }

    private AttributeFlowTestResult Test(ImportCase scenario, params AttributeFlowTestInput[] inputs) =>
        AttributeFlowTester.Test(scenario.Rule, scenario.Mapping, scenario.Source, inputs, _evaluator);

    private static AttributeFlowTestInput CsInput(string name, params object[] values) => new(ExpressionInputSource.ConnectedSystem, name, values);

    [Test]
    public void Test_DateTimeInputCastInExpression_AgreesWithSynchronisation()
    {
        // Scenario 14: the tester used to pass the sample as text, so the cast failed; synchronisation reads a date.
        var scenario = Import("FormatDate((DateTime)cs[\"hireDate\"], \"yyyy-MM-dd\")", Mv("Display Name"));

        var synchronised = Synchronise(scenario, ("hireDate", v => v.DateTimeValue = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc)));
        var tested = Test(scenario, CsInput("hireDate", "2026-03-02"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Values, Is.EqualTo(new object[] { "2026-03-02" }));
            Assert.That(tested.InputErrors, Is.Empty);
            Assert.That(tested.Evaluation!.Outcome, Is.EqualTo(AttributeFlowOutcome.Values));
            Assert.That(tested.Evaluation!.Values, Is.EqualTo(synchronised.Values));
        }
    }

    [Test]
    public void Test_NumberInputInArithmetic_AgreesWithSynchronisation()
    {
        // As text, the cast fails; synchronisation reads the number 41.
        var scenario = Import("(int)cs[\"employeeNumber\"] + 1", Mv("Employee Number"));

        var synchronised = Synchronise(scenario, ("employeeNumber", v => v.IntValue = 41));
        var tested = Test(scenario, CsInput("employeeNumber", "41"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Values, Is.EqualTo(new object[] { 42 }));
            Assert.That(tested.Evaluation!.Values, Is.EqualTo(synchronised.Values));
        }
    }

    [Test]
    public void Test_BooleanInput_AgreesWithSynchronisation()
    {
        var scenario = Import("IIF((bool)cs[\"active\"], \"Active\", \"Inactive\")", Mv("Display Name"));

        var synchronised = Synchronise(scenario, ("active", v => v.BoolValue = true));
        var tested = Test(scenario, CsInput("active", "true"));

        Assert.That(tested.Evaluation!.Values, Is.EqualTo(synchronised.Values));
    }

    [Test]
    public void Test_MultiValuedInput_AgreesWithSynchronisation()
    {
        // Whatever synchronisation reads from a multi-valued attribute today, the tester reads the same: they share
        // the dictionary builder, so #2053's change reaches both at once.
        var scenario = Import("cs[\"mail\"]", Mv("Proxy Addresses"));

        var synchronised = Synchronise(scenario,
            ("mail", v => v.StringValue = "priya@example.org"),
            ("mail", v => v.StringValue = "p.shah@example.org"));
        var tested = Test(scenario, CsInput("mail", "priya@example.org", "p.shah@example.org"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Values, Is.Not.Empty);
            Assert.That(tested.Evaluation!.Values, Is.EqualTo(synchronised.Values));
        }
    }

    [Test]
    public void Test_ValueProcessing_AgreesWithSynchronisation()
    {
        var scenario = Import("cs[\"givenName\"]", Mv("Display Name"),
            processing: InboundValueProcessing.TrimWhitespace | InboundValueProcessing.TreatWhitespaceAsNoValue,
            caseNormalisation: InboundCaseNormalisation.Lower);

        var synchronised = Synchronise(scenario, ("givenName", v => v.StringValue = " PRIYA "));
        var tested = Test(scenario, CsInput("givenName", " PRIYA "));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Values, Is.EqualTo(new object[] { "priya" }));
            Assert.That(tested.Evaluation!.Values, Is.EqualTo(synchronised.Values));
        }
    }

    [Test]
    public void Test_WhitespaceCollapsesToNoValue_AgreesWithSynchronisation()
    {
        var scenario = Import("cs[\"givenName\"]", Mv("Display Name"));

        var synchronised = Synchronise(scenario, ("givenName", v => v.StringValue = "   "));
        var tested = Test(scenario, CsInput("givenName", "   "));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Values, Is.Empty);
            Assert.That(synchronised.Errors, Is.Empty);
            Assert.That(tested.Evaluation!.Outcome, Is.EqualTo(AttributeFlowOutcome.NoValue));
        }
    }

    [Test]
    public void Test_AbsentInputContributeNoValue_AgreesWithSynchronisation()
    {
        var scenario = Import("cs[\"givenName\"] + \"!\"", Mv("Display Name"), MissingInputBehaviour.ContributeNoValue);

        var synchronised = Synchronise(scenario);
        var tested = Test(scenario, CsInput("givenName"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Values, Is.Empty);
            Assert.That(synchronised.Errors, Is.Empty);
            Assert.That(tested.Evaluation!.Outcome, Is.EqualTo(AttributeFlowOutcome.NoValue));
            Assert.That(tested.Evaluation!.Source.MissingInputs, Is.EqualTo(new[] { "cs[\"givenName\"]" }));
        }
    }

    [Test]
    public void Test_AbsentInputEvaluateAnyway_AgreesWithSynchronisation()
    {
        var scenario = Import("\"x\" + cs[\"givenName\"]", Mv("Display Name"));

        var synchronised = Synchronise(scenario);
        var tested = Test(scenario);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Values, Is.EqualTo(new object[] { "x" }));
            Assert.That(tested.Evaluation!.Values, Is.EqualTo(synchronised.Values));
        }
    }

    [Test]
    public void Test_AbsentInputFailMapping_AgreesWithSynchronisation()
    {
        var scenario = Import("cs[\"givenName\"]", Mv("Display Name"), MissingInputBehaviour.FailMapping);

        var synchronised = Synchronise(scenario);
        var tested = Test(scenario, CsInput("givenName"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Errors.Single().Kind, Is.EqualTo(AttributeFlowErrorKind.ExpressionMissingInput));
            Assert.That(tested.Evaluation!.ErrorReason, Is.EqualTo(AttributeFlowErrorReason.MissingInputFailedMapping));
            Assert.That(tested.Evaluation!.Source.MissingInputs, Is.EqualTo(synchronised.Errors.Single().MissingInputs));
        }
    }

    [Test]
    public void Test_AbsentInputFailObject_AgreesWithSynchronisation()
    {
        var scenario = Import("cs[\"givenName\"]", Mv("Display Name"), MissingInputBehaviour.FailObject);

        var synchronised = Synchronise(scenario);
        var tested = Test(scenario);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Thrown, Is.TypeOf<SyncExpressionMissingInputException>());
            Assert.That(tested.Evaluation!.ErrorReason, Is.EqualTo(AttributeFlowErrorReason.MissingInputFailedObject));
        }
    }

    [Test]
    public void Test_ExpressionFails_AgreesWithSynchronisation()
    {
        var scenario = Import("(DateTime)cs[\"givenName\"]", Mv("Hire Date"));

        var synchronised = Synchronise(scenario, ("givenName", v => v.StringValue = "Priya"));
        var tested = Test(scenario, CsInput("givenName", "Priya"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Thrown, Is.TypeOf<SyncExpressionEvaluationException>());
            Assert.That(tested.Evaluation!.ErrorReason, Is.EqualTo(AttributeFlowErrorReason.EvaluationFailed));
        }
    }

    [Test]
    public void Test_ResultTheTargetCannotHold_FailsAsSynchronisationDoes()
    {
        // The false pass: the Expression evaluates, but a Number attribute cannot hold its result.
        var scenario = Import("cs[\"givenName\"]", Mv("Employee Number"));

        var synchronised = Synchronise(scenario, ("givenName", v => v.StringValue = "Priya"));
        var tested = Test(scenario, CsInput("givenName", "Priya"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Errors.Single().Kind, Is.EqualTo(AttributeFlowErrorKind.ExpressionResultNotConvertible));
            Assert.That(tested.Evaluation!.Outcome, Is.EqualTo(AttributeFlowOutcome.Error));
            Assert.That(tested.Evaluation!.ErrorReason, Is.EqualTo(AttributeFlowErrorReason.ResultNotConvertible));
            Assert.That(tested.Evaluation!.Resolution!.UnconvertibleValue, Is.EqualTo(synchronised.Errors.Single().Value));
        }
    }

    [Test]
    public void Test_SeveralValuesForSingleValuedTarget_FailsAsSynchronisationDoes()
    {
        var scenario = Import("Split(cs[\"givenName\"], \";\")", Mv("Display Name"));

        var synchronised = Synchronise(scenario, ("givenName", v => v.StringValue = "a;b"));
        var tested = Test(scenario, CsInput("givenName", "a;b"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Errors.Single().Kind, Is.EqualTo(AttributeFlowErrorKind.ExpressionResultMultiValued));
            Assert.That(tested.Evaluation!.ErrorReason, Is.EqualTo(AttributeFlowErrorReason.ResultMultiValued));
        }
    }

    [Test]
    public void Test_EveryValueOfAMultiValuedAttributeForASingleValuedTarget_NamesTheAttributeAsSynchronisationDoes()
    {
        // #2053: cs["mail"] reads both values, so a single-valued target is refused, and both name the attribute.
        var scenario = Import("cs[\"mail\"]", Mv("Display Name"));

        var synchronised = Synchronise(scenario, ("mail", v => v.StringValue = "a@example.org"), ("mail", v => v.StringValue = "b@example.org"));
        var tested = Test(scenario, CsInput("mail", "a@example.org", "b@example.org"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(synchronised.Errors.Single().SourceAttributeName, Is.EqualTo("mail"));
            Assert.That(tested.Evaluation!.Resolution!.SourceAttributeName, Is.EqualTo("mail"));
            Assert.That(tested.Evaluation!.Resolution!.ValueCount, Is.EqualTo(2));
        }
    }

    #endregion

    #region Export: the tester against ComputeAttributeValueChanges

    [Test]
    public void Test_ExportDateTimeInput_AgreesWithExportStaging()
    {
        var target = new ConnectedSystemObjectTypeAttribute { Id = 50, Name = "description", Type = AttributeDataType.Text };
        var rule = new SyncRule
        {
            Id = 2, Name = "Directory Outbound", Direction = SyncRuleDirection.Export, Enabled = true,
            ConnectedSystemObjectType = _csoType, MetaverseObjectType = _mvType, MetaverseObjectTypeId = _mvType.Id
        };
        var mapping = new SyncRuleMapping { Id = 2, SyncRule = rule, SyncRuleId = rule.Id, TargetConnectedSystemAttribute = target, TargetConnectedSystemAttributeId = target.Id };
        var source = new SyncRuleMappingSource { Order = 0, Expression = "\"Hired \" + FormatDate((DateTime)mv[\"Hire Date\"], \"yyyy-MM-dd\")" };
        mapping.Sources.Add(source);
        rule.AttributeFlowRules.Add(mapping);

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = _mvType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), MetaverseObject = mvo, Attribute = Mv("Hire Date"), AttributeId = 21,
            DateTimeValue = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc)
        });
        var staged = _engine.ComputeAttributeValueChanges(mvo, rule, [], PendingExportChangeType.Create,
            existingCso: null, csoAttributeCache: null, out _, flowErrors: []);

        var tested = AttributeFlowTester.Test(rule, mapping, source,
            [new AttributeFlowTestInput(ExpressionInputSource.Metaverse, "Hire Date", ["2026-03-02T00:00:00Z"])], _evaluator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(staged.Select(c => c.StringValue), Is.EqualTo(new[] { "Hired 2026-03-02" }));
            Assert.That(tested.Evaluation!.Values, Is.EqualTo(staged.Select(c => (object)c.StringValue!)));
        }
    }

    #endregion

    #region Inputs the object could not hold

    [Test]
    public void Test_TextThatIsNotADateForADateAttribute_IsAnInputErrorNotAnEvaluation()
    {
        var scenario = Import("cs[\"hireDate\"]", Mv("Hire Date"));

        var tested = Test(scenario, CsInput("hireDate", "next Tuesday"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tested.Evaluation, Is.Null);
            Assert.That(tested.InputErrors, Has.Count.EqualTo(1));
            Assert.That(tested.InputErrors[0], Does.Contain("cs[\"hireDate\"]"));
        }
    }

    [Test]
    public void Test_SeveralValuesForASingleValuedInput_IsAnInputError()
    {
        var scenario = Import("cs[\"givenName\"]", Mv("Display Name"));

        var tested = Test(scenario, CsInput("givenName", "a", "b"));

        Assert.That(tested.InputErrors, Has.Count.EqualTo(1));
    }

    [Test]
    public void Test_ValueForAnAttributeNotInTheSchema_IsAnInputError()
    {
        var scenario = Import("cs[\"nickname\"]", Mv("Display Name"));

        var tested = Test(scenario, CsInput("nickname", "Pri"));

        Assert.That(tested.InputErrors.Single(), Does.Contain("person"));
    }

    [Test]
    public void Test_NoValueForAnAttributeNotInTheSchema_IsAbsent()
    {
        var scenario = Import("\"x\" + cs[\"nickname\"]", Mv("Display Name"));

        var tested = Test(scenario, CsInput("nickname"));

        Assert.That(tested.Evaluation!.Values, Is.EqualTo(new object[] { "x" }));
    }

    #endregion

    #region Describe: what synchronisation would do, in words

    [Test]
    public void Describe_MissingInputFailMapping_NamesTheInputAndTheTarget()
    {
        var scenario = Import("cs[\"givenName\"]", Mv("Display Name"), MissingInputBehaviour.FailMapping);
        var evaluation = Test(scenario).Evaluation!;

        var description = AttributeFlowTester.Describe(evaluation, "Display Name");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(description, Does.Contain("cs[\"givenName\"]"));
            Assert.That(description, Does.Contain("'Display Name' keeps its current value"));
        }
    }

    [Test]
    public void Describe_ResultTheTargetCannotHold_NamesTheValueAndTheType()
    {
        var scenario = Import("cs[\"givenName\"]", Mv("Employee Number"));
        var evaluation = Test(scenario, CsInput("givenName", "Priya")).Evaluation!;

        var description = AttributeFlowTester.Describe(evaluation, "Employee Number");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(description, Does.Contain("'Priya'"));
            Assert.That(description, Does.Contain(nameof(AttributeDataType.Number)));
        }
    }

    [Test]
    public void Describe_SeveralValues_CountsThem()
    {
        var scenario = Import("Split(cs[\"givenName\"], \";\")", Mv("Proxy Addresses"));
        var evaluation = Test(scenario, CsInput("givenName", "a;b")).Evaluation!;

        Assert.That(AttributeFlowTester.Describe(evaluation, "Proxy Addresses"), Does.Contain("2 values"));
    }

    #endregion
}

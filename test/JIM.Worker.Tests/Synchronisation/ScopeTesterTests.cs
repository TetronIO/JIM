// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Application.Servers.Scoping;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Search;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// The Scope tester (#2055): sample values typed from the schema, evaluated by the scoping evaluation synchronisation
/// runs, as of a chosen instant, and when the outcome would next change as time passes. Fidelity tests compare its
/// explanation with the one <see cref="ScopingEvaluationServer"/> gives for a real object holding the same values.
/// </summary>
[TestFixture]
public class ScopeTesterTests
{
    private static readonly DateTime AsOf = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private ConnectedSystemObjectType _csoType = null!;
    private MetaverseObjectType _mvType = null!;

    [SetUp]
    public void SetUp()
    {
        _csoType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "person",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "department", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "leaveDate", Type = AttributeDataType.DateTime },
                new ConnectedSystemObjectTypeAttribute { Id = 13, Name = "startDate", Type = AttributeDataType.DateTime },
                new ConnectedSystemObjectTypeAttribute { Id = 14, Name = "grade", Type = AttributeDataType.Number }
            ]
        };
        _mvType = new MetaverseObjectType
        {
            Id = 1,
            Name = "Person",
            Attributes =
            [
                new MetaverseAttribute { Id = 21, Name = "Roles", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued },
                new MetaverseAttribute { Id = 22, Name = "Department", Type = AttributeDataType.Text }
            ]
        };
    }

    private SyncRule ImportRule(params SyncRuleScopingCriteriaGroup[] groups)
    {
        var rule = new SyncRule
        {
            Id = 7, Name = "HR Import", Direction = SyncRuleDirection.Import,
            ConnectedSystemObjectType = _csoType, MetaverseObjectType = _mvType
        };
        rule.ObjectScopingCriteriaGroups.AddRange(groups);
        return rule;
    }

    private SyncRule ExportRule(params SyncRuleScopingCriteriaGroup[] groups)
    {
        var rule = new SyncRule
        {
            Id = 8, Name = "Finance App Users", Direction = SyncRuleDirection.Export,
            ConnectedSystemObjectType = _csoType, MetaverseObjectType = _mvType
        };
        rule.ObjectScopingCriteriaGroups.AddRange(groups);
        return rule;
    }

    private static SyncRuleScopingCriteriaGroup All(params object[] children)
    {
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        foreach (var child in children)
        {
            if (child is SyncRuleScopingCriteria criterion)
                group.Criteria.Add(criterion);
            else
                group.ChildGroups.Add((SyncRuleScopingCriteriaGroup)child);
        }
        return group;
    }

    private SyncRuleScopingCriteria Cs(string name, SearchComparisonType comparison, string? text = null, int? number = null) => new()
    {
        ConnectedSystemAttribute = _csoType.Attributes.Single(a => a.Name == name),
        ComparisonType = comparison,
        StringValue = text,
        IntValue = number
    };

    private SyncRuleScopingCriteria Relative(string name, SearchComparisonType comparison, int count, RelativeDateUnit unit,
        RelativeDateDirection direction) => new()
    {
        ConnectedSystemAttribute = _csoType.Attributes.Single(a => a.Name == name),
        ComparisonType = comparison,
        ValueMode = DateCriteriaValueMode.Relative,
        RelativeCount = count,
        RelativeUnit = unit,
        RelativeDirection = direction
    };

    private SyncRuleScopingCriteria Mv(string name, SearchComparisonType comparison, string text) => new()
    {
        MetaverseAttribute = _mvType.Attributes.Single(a => a.Name == name),
        ComparisonType = comparison,
        StringValue = text
    };

    private static AttributeSample Input(string name, params object[] values) => new(name, values);

    #region DescribeInputs

    [Test]
    public void DescribeInputs_ImportRule_ListsEachComparedAttributeOnceInCriteriaOrder()
    {
        var rule = ImportRule(
            All(Cs("department", SearchComparisonType.Equals, "Finance"),
                All(Cs("grade", SearchComparisonType.GreaterThan, number: 3), Cs("department", SearchComparisonType.NotEquals, "Audit"))));

        var inputs = ScopeTester.DescribeInputs(rule);

        Assert.That(inputs, Is.EqualTo(new[]
        {
            new AttributeSampleDescriptor("department", AttributeDataType.Text, AttributePlurality.SingleValued),
            new AttributeSampleDescriptor("grade", AttributeDataType.Number, AttributePlurality.SingleValued)
        }));
    }

    [Test]
    public void DescribeInputs_ExportRule_DescribesMetaverseAttributes()
    {
        var rule = ExportRule(All(Mv("Roles", SearchComparisonType.Equals, "Finance")));

        var inputs = ScopeTester.DescribeInputs(rule);

        Assert.That(inputs.Single(), Is.EqualTo(new AttributeSampleDescriptor("Roles", AttributeDataType.Text, AttributePlurality.MultiValued)));
    }

    #endregion

    #region Test: fidelity and inputs

    [Test]
    public void Test_TypedSamples_ExplainAsARealConnectedSystemObjectHoldingThem()
    {
        var rule = ImportRule(
            All(Cs("department", SearchComparisonType.Equals, "Finance"),
                Relative("leaveDate", SearchComparisonType.GreaterThan, 30, RelativeDateUnit.Days, RelativeDateDirection.Ago)));
        var real = new ConnectedSystemObject { TypeId = _csoType.Id };
        real.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 11, Attribute = _csoType.Attributes[0], StringValue = "Finance" });
        real.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 12, Attribute = _csoType.Attributes[1], DateTimeValue = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) });
        var expected = new ScopingEvaluationServer().ExplainCsoForImportRule(real, rule, AsOf);

        var result = ScopeTester.Test(rule, [Input("department", "Finance"), Input("leaveDate", "2026-11-01")], AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.InputErrors, Is.Empty);
            Assert.That(result.Explanation!.Outcome, Is.EqualTo(expected.Outcome).And.EqualTo(ScopingRuleOutcome.InScope));
            Assert.That(result.Explanation.Groups.Single().Criteria.Select(c => (c.Outcome, c.ActualDisplay)),
                Is.EqualTo(expected.Groups.Single().Criteria.Select(c => (c.Outcome, c.ActualDisplay))));
            Assert.That(result.Explanation.EvaluatedAt, Is.EqualTo(AsOf));
        }
    }

    [Test]
    public void Test_InputWithNoValues_IsAnAttributeTheObjectHoldsNoValueFor()
    {
        var rule = ImportRule(All(Cs("department", SearchComparisonType.HasNoValue)));

        var result = ScopeTester.Test(rule, [Input("department")], AsOf);

        Assert.That(result.Explanation!.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
    }

    [Test]
    public void Test_SeveralValuesOnAMultiValuedMetaverseAttribute_AnyValueMeetsAPositiveCriterion()
    {
        var rule = ExportRule(All(Mv("Roles", SearchComparisonType.Equals, "Finance")));

        var result = ScopeTester.Test(rule, [Input("Roles", "Sales", "Finance")], AsOf);

        Assert.That(result.Explanation!.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
    }

    [Test]
    public void Test_TextThatIsNotADate_ReportsAnInputErrorAndDoesNotEvaluate()
    {
        var rule = ImportRule(All(Relative("leaveDate", SearchComparisonType.GreaterThan, 30, RelativeDateUnit.Days, RelativeDateDirection.Ago)));

        var result = ScopeTester.Test(rule, [Input("leaveDate", "next Tuesday")], AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Explanation, Is.Null);
            Assert.That(result.InputErrors.Single(), Does.Contain("next Tuesday").And.Contain("leaveDate").And.Contain("ISO 8601"));
        }
    }

    [Test]
    public void Test_SeveralValuesForASingleValuedAttribute_ReportsAnInputError()
    {
        var rule = ImportRule(All(Cs("department", SearchComparisonType.Equals, "Finance")));

        var result = ScopeTester.Test(rule, [Input("department", "Finance", "Audit")], AsOf);

        Assert.That(result.InputErrors.Single(), Does.Contain("single-valued"));
    }

    [Test]
    public void Test_AttributeNotInTheSchema_ReportsAnInputError()
    {
        var rule = ImportRule(All(Cs("department", SearchComparisonType.Equals, "Finance")));

        var result = ScopeTester.Test(rule, [Input("costCentre", "C100")], AsOf);

        Assert.That(result.InputErrors.Single(), Does.Contain("costCentre").And.Contain("person"));
    }

    [Test]
    public void Test_AsOf_ResolvesRelativeDatesAgainstThatInstant()
    {
        var rule = ImportRule(All(Relative("leaveDate", SearchComparisonType.GreaterThan, 30, RelativeDateUnit.Days, RelativeDateDirection.Ago)));
        var inputs = new[] { Input("leaveDate", "2026-11-01") };

        var before = ScopeTester.Test(rule, inputs, AsOf);
        var after = ScopeTester.Test(rule, inputs, new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before.Explanation!.Outcome, Is.EqualTo(ScopingRuleOutcome.InScope));
            Assert.That(after.Explanation!.Outcome, Is.EqualTo(ScopingRuleOutcome.OutOfScope));
        }
    }

    #endregion

    #region Test: next change

    [Test]
    public void Test_LeaverRule_NextChangeIsTheMidnightTheLeaveDateFallsOutOfTheWindow()
    {
        // In scope while the leave date is later than 30 days ago: "30 days ago" passes 1 November at midnight on
        // 1 December, after which the object is out of scope.
        var rule = ImportRule(All(Relative("leaveDate", SearchComparisonType.GreaterThan, 30, RelativeDateUnit.Days, RelativeDateDirection.Ago)));

        var result = ScopeTester.Test(rule, [Input("leaveDate", "2026-11-01")], AsOf);

        Assert.That(result.NextChange, Is.EqualTo(new ScopeChange(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), ScopingRuleOutcome.OutOfScope)));
    }

    [Test]
    public void Test_JoinerRuleInHours_NextChangeIsTheExactInstantTheObjectEntersScope()
    {
        // In scope from two hours before the start date.
        var rule = ImportRule(All(Relative("startDate", SearchComparisonType.LessThanOrEquals, 2, RelativeDateUnit.Hours, RelativeDateDirection.FromNow)));

        var result = ScopeTester.Test(rule, [Input("startDate", "2026-11-10T12:00:00Z")], AsOf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Explanation!.Outcome, Is.EqualTo(ScopingRuleOutcome.OutOfScope));
            Assert.That(result.NextChange, Is.EqualTo(new ScopeChange(new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc), ScopingRuleOutcome.InScope)));
        }
    }

    [Test]
    public void Test_MonthsUnit_NextChangeIsTheMidnightTheOutcomeFlips()
    {
        // In scope while the start date is at or before one month from now. One month from 28 February is 28 March,
        // and no day of February reaches further, so a start date of 31 March enters scope on 1 March (1 April).
        var rule = ImportRule(All(Relative("startDate", SearchComparisonType.LessThanOrEquals, 1, RelativeDateUnit.Months, RelativeDateDirection.FromNow)));
        var asOf = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var result = ScopeTester.Test(rule, [Input("startDate", "2027-03-31")], asOf);

        Assert.That(result.NextChange, Is.EqualTo(new ScopeChange(new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc), ScopingRuleOutcome.InScope)));
    }

    [Test]
    public void Test_OtherCriteriaDecide_NextChangeIsOnlyWhereTheRuleOutcomeChanges()
    {
        // The leave date leaves its window on 1 December, but the department never matches, so the rule's outcome
        // never changes.
        var rule = ImportRule(All(Cs("department", SearchComparisonType.Equals, "Finance"),
            Relative("leaveDate", SearchComparisonType.GreaterThan, 30, RelativeDateUnit.Days, RelativeDateDirection.Ago)));

        var result = ScopeTester.Test(rule, [Input("department", "Sales"), Input("leaveDate", "2026-11-01")], AsOf);

        Assert.That(result.NextChange, Is.Null);
    }

    [Test]
    public void Test_NoRelativeCriteria_HasNoNextChange()
    {
        var rule = ImportRule(All(Cs("department", SearchComparisonType.Equals, "Finance")));

        var result = ScopeTester.Test(rule, [Input("department", "Finance")], AsOf);

        Assert.That(result.NextChange, Is.Null);
    }

    #endregion

    #region Inputs from an object

    [Test]
    public void InputsFromConnectedSystemObject_TakesEachComparedAttributesValues()
    {
        var rule = ImportRule(All(Cs("department", SearchComparisonType.Equals, "Finance"), Cs("grade", SearchComparisonType.GreaterThan, number: 3)));
        var cso = new ConnectedSystemObject { TypeId = _csoType.Id };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 11, Attribute = _csoType.Attributes[0], StringValue = "Finance" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = 13, Attribute = _csoType.Attributes[2], DateTimeValue = AsOf });

        var inputs = ScopeTester.InputsFromConnectedSystemObject(cso, rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inputs.Select(i => i.AttributeName), Is.EqualTo(new[] { "department", "grade" }));
            Assert.That(inputs[0].Values, Is.EqualTo(new object[] { "Finance" }));
            Assert.That(inputs[1].Values, Is.Empty);
        }
    }

    [Test]
    public void InputsFromMetaverseObject_TakesEveryValueAndSkipsAssertedNullMarkers()
    {
        var rule = ExportRule(All(Mv("Roles", SearchComparisonType.Equals, "Finance"), Mv("Department", SearchComparisonType.Equals, "Finance")));
        var roles = _mvType.Attributes[0];
        var mvo = new MetaverseObject { Type = _mvType };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { AttributeId = roles.Id, Attribute = roles, StringValue = "Sales" });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { AttributeId = roles.Id, Attribute = roles, StringValue = "Finance" });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { AttributeId = 22, Attribute = _mvType.Attributes[1], NullValue = true });

        var inputs = ScopeTester.InputsFromMetaverseObject(mvo, rule);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inputs[0].Values, Is.EqualTo(new object[] { "Sales", "Finance" }));
            Assert.That(inputs[1].Values, Is.Empty);
        }
    }

    #endregion
}

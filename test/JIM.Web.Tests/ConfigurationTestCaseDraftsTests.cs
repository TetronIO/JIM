// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers.Scoping;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Web;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// "Save as test case" in the portal's testers (#2055 Phase 4): the case a test becomes, with the values tested as its
/// inputs and today's result as its expectation, ready for the administrator to edit before saving.
/// </summary>
[TestFixture]
public class ConfigurationTestCaseDraftsTests
{
    private static readonly DateTime RanAt = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private DynamicExpressoEvaluator _evaluator = null!;
    private ConnectedSystemObjectType _csoType = null!;
    private MetaverseObjectType _mvType = null!;

    [SetUp]
    public void SetUp()
    {
        _evaluator = new DynamicExpressoEvaluator();
        _csoType = new ConnectedSystemObjectType
        {
            Id = 1, Name = "person",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "sn", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "hireDate", Type = AttributeDataType.DateTime },
                new ConnectedSystemObjectTypeAttribute { Id = 13, Name = "department", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 14, Name = "leaveDate", Type = AttributeDataType.DateTime }
            ]
        };
        _mvType = new MetaverseObjectType
        {
            Id = 1, Name = "Person",
            Attributes =
            [
                new MetaverseAttribute { Id = 21, Name = "Display Name", Type = AttributeDataType.Text },
                new MetaverseAttribute { Id = 22, Name = "Hire Date", Type = AttributeDataType.DateTime },
                new MetaverseAttribute { Id = 23, Name = "Account Name", Type = AttributeDataType.Text },
                new MetaverseAttribute { Id = 24, Name = "Employee ID", Type = AttributeDataType.Text }
            ]
        };
    }

    #region Attribute Flow

    [Test]
    public void FromAttributeFlowTest_Values_ExpectsThemAsCanonicalTextWithTheTestedInputs()
    {
        var (rule, mapping) = Flow("cs[\"hireDate\"]", "Hire Date");
        var inputs = Inputs(rule, mapping, ("hireDate", ["2026-03-02"]));

        var draft = Draft(rule, mapping, inputs, asOfUtc: null)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(draft.Kind, Is.EqualTo(ConfigurationTestCaseKind.AttributeFlow));
            Assert.That(draft.SyncRuleMappingId, Is.EqualTo(mapping.Id));
            Assert.That(draft.Inputs.Single(), Is.EqualTo(new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "hireDate", ["2026-03-02"])).Using<ConfigurationTestInput>(InputComparer));
            Assert.That(draft.Expectation.Kind, Is.EqualTo(ConfigurationTestExpectationKind.Values));
            Assert.That(draft.Expectation.Values, Is.EqualTo(new[] { "2026-03-02T00:00:00Z" }));
            Assert.That(draft.RunAsOfUtc, Is.Null, "an Expression that does not read the clock needs no run-as-of time");
            Assert.That(draft.DescribeInvalidity(), Is.Null);
        }
    }

    [Test]
    public void FromAttributeFlowTest_InputMarkedNoValue_IsAnInputWithNoValues()
    {
        var (rule, mapping) = Flow("cs[\"sn\"]", "Display Name");
        var inputs = Inputs(rule, mapping, ("sn", null));

        var draft = Draft(rule, mapping, inputs, asOfUtc: null)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(draft.Inputs.Single().Values, Is.Empty);
            Assert.That(draft.Expectation.Kind, Is.EqualTo(ConfigurationTestExpectationKind.NoValue));
        }
    }

    [Test]
    public void FromAttributeFlowTest_Error_ExpectsAnError()
    {
        var (rule, mapping) = Flow("cs[\"sn\"]", "Hire Date");
        var inputs = Inputs(rule, mapping, ("sn", ["Bloggs"]));

        Assert.That(Draft(rule, mapping, inputs, asOfUtc: null)!.Expectation.Kind, Is.EqualTo(ConfigurationTestExpectationKind.Error));
    }

    [Test]
    public void FromAttributeFlowTest_ExpressionReadingTheClock_RunsAsOfTheInstantItWasTested()
    {
        var (rule, mapping) = Flow("FormatDate(Now(), \"yyyy\")", "Display Name");

        var draft = Draft(rule, mapping, [], asOfUtc: null)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(draft.RunAsOfUtc, Is.EqualTo(RanAt));
            Assert.That(draft.Expectation.Values, Is.EqualTo(new[] { "2026" }));
        }
    }

    [Test]
    public void FromAttributeFlowTest_TestedAsOfAnInstant_RunsAsOfIt()
    {
        var (rule, mapping) = Flow("cs[\"sn\"]", "Display Name");
        var asOf = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.That(Draft(rule, mapping, Inputs(rule, mapping, ("sn", ["Bloggs"])), asOf)!.RunAsOfUtc, Is.EqualTo(asOf));
    }

    [Test]
    public void FromAttributeFlowTest_GeneratedValue_ExpectsItsBase()
    {
        var (rule, mapping) = Flow("Lower(cs[\"sn\"])", "Account Name");
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken };

        var draft = Draft(rule, mapping, Inputs(rule, mapping, ("sn", ["Bloggs"])), asOfUtc: null)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(draft.Kind, Is.EqualTo(ConfigurationTestCaseKind.GeneratedValue));
            Assert.That(draft.Expectation.Values, Is.EqualTo(new[] { "bloggs" }));
        }
    }

    [Test]
    public void FromAttributeFlowTest_InputErrors_IsNothingToSave()
    {
        var (rule, mapping) = Flow("cs[\"hireDate\"]", "Hire Date");

        Assert.That(Draft(rule, mapping, Inputs(rule, mapping, ("hireDate", ["not a date"])), asOfUtc: null), Is.Null);
    }

    [Test]
    public void FromAttributeFlowTest_UnsavedAttributeFlow_IsNothingToSave()
    {
        var (rule, mapping) = Flow("cs[\"sn\"]", "Display Name");
        mapping.Id = 0;

        Assert.That(Draft(rule, mapping, Inputs(rule, mapping, ("sn", ["Bloggs"])), asOfUtc: null), Is.Null);
    }

    [Test]
    public void FromAttributeFlowTest_Name_NeverQuotesAnInputValue()
    {
        // The name is written to Activities, which never hold a case's values.
        var (rule, mapping) = Flow("cs[\"sn\"]", "Display Name");

        var draft = Draft(rule, mapping, Inputs(rule, mapping, ("sn", ["Bloggs-value"])), asOfUtc: null)!;

        Assert.That(draft.Name, Does.Not.Contain("Bloggs").And.Contain("Display Name"));
    }

    #endregion

    #region Scope

    [Test]
    public void FromScopeTest_InScope_ExpectsInScopeWithTheTestedInputs()
    {
        var rule = ScopeRule(Criterion("department", SearchComparisonType.Equals, "Sales"));
        var inputs = new List<(string, TestInputState)> { ("department", State(["Sales"])) };
        var result = ScopeTester.Test(rule, inputs.Select(i => new AttributeSample(i.Item1, i.Item2.TestValues)).ToList(), RanAt);

        var draft = ConfigurationTestCaseDrafts.FromScopeTest(rule, inputs, result, asOfUtc: null, RanAt)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(draft.Kind, Is.EqualTo(ConfigurationTestCaseKind.Scope));
            Assert.That(draft.SyncRuleId, Is.EqualTo(rule.Id));
            Assert.That(draft.Inputs.Single(), Is.EqualTo(new ConfigurationTestInput(null, "department", ["Sales"])).Using<ConfigurationTestInput>(InputComparer));
            Assert.That(draft.Expectation.Kind, Is.EqualTo(ConfigurationTestExpectationKind.InScope));
            Assert.That(draft.RunAsOfUtc, Is.Null);
            Assert.That(draft.DescribeInvalidity(), Is.Null);
        }
    }

    [Test]
    public void FromScopeTest_RelativeDateCriterion_RunsAsOfTheInstantItWasTested()
    {
        var rule = ScopeRule(new SyncRuleScopingCriteria
        {
            ConnectedSystemAttribute = _csoType.Attributes.Single(a => a.Name == "leaveDate"),
            ComparisonType = SearchComparisonType.GreaterThan,
            ValueMode = DateCriteriaValueMode.Relative,
            RelativeCount = 30,
            RelativeUnit = RelativeDateUnit.Days,
            RelativeDirection = RelativeDateDirection.Ago
        });
        var inputs = new List<(string, TestInputState)> { ("leaveDate", State(["2026-11-01"])) };
        var result = ScopeTester.Test(rule, inputs.Select(i => new AttributeSample(i.Item1, i.Item2.TestValues)).ToList(), RanAt);

        Assert.That(ConfigurationTestCaseDrafts.FromScopeTest(rule, inputs, result, asOfUtc: null, RanAt)!.RunAsOfUtc, Is.EqualTo(RanAt));
    }

    [Test]
    public void FromScopeTest_Undetermined_IsNothingToSave()
    {
        var rule = ScopeRule(Criterion("department", SearchComparisonType.MatchesPattern, "("));
        var inputs = new List<(string, TestInputState)> { ("department", State(["Sales"])) };
        var result = ScopeTester.Test(rule, inputs.Select(i => new AttributeSample(i.Item1, i.Item2.TestValues)).ToList(), RanAt);

        Assert.That(ConfigurationTestCaseDrafts.FromScopeTest(rule, inputs, result, asOfUtc: null, RanAt), Is.Null);
    }

    #endregion

    #region Helpers

    private ConfigurationTestCase? Draft(SyncRule rule, SyncRuleMapping mapping,
        IReadOnlyList<(AttributeFlowTestInputDescriptor Input, TestInputState State)> inputs, DateTime? asOfUtc)
    {
        var result = AttributeFlowTester.Test(rule, mapping, mapping.Sources[0],
            inputs.Select(i => new AttributeFlowTestInput(i.Input.Source, i.Input.AttributeName, i.State.TestValues)).ToList(),
            _evaluator, asOfUtc ?? RanAt);
        return ConfigurationTestCaseDrafts.FromAttributeFlowTest(mapping, mapping.Sources[0], inputs, result, asOfUtc, RanAt);
    }

    private (SyncRule, SyncRuleMapping) Flow(string expression, string target)
    {
        var rule = new SyncRule { Id = 7, Direction = SyncRuleDirection.Import, ConnectedSystemObjectType = _csoType, MetaverseObjectType = _mvType };
        var mapping = new SyncRuleMapping { Id = 3, TargetMetaverseAttribute = _mvType.Attributes.Single(a => a.Name == target) };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = expression });
        rule.AttributeFlowRules.Add(mapping);
        return (rule, mapping);
    }

    private static List<(AttributeFlowTestInputDescriptor, TestInputState)> Inputs(SyncRule rule, SyncRuleMapping mapping,
        params (string Name, string[]? Values)[] values) =>
        AttributeFlowTester.DescribeInputs(rule, mapping.Sources[0].Expression)
            .Select(descriptor => (descriptor, State(values.Single(v => v.Name == descriptor.AttributeName).Values)))
            .ToList();

    private static TestInputState State(string[]? values)
    {
        var state = new TestInputState();
        if (values != null)
            state.Load(values);
        return state;
    }

    private SyncRule ScopeRule(SyncRuleScopingCriteria criterion)
    {
        var rule = new SyncRule { Id = 7, Direction = SyncRuleDirection.Import, ConnectedSystemObjectType = _csoType, MetaverseObjectType = _mvType };
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.Add(criterion);
        rule.ObjectScopingCriteriaGroups.Add(group);
        return rule;
    }

    private SyncRuleScopingCriteria Criterion(string name, SearchComparisonType comparison, string text) => new()
    {
        ConnectedSystemAttribute = _csoType.Attributes.Single(a => a.Name == name),
        ComparisonType = comparison,
        StringValue = text
    };

    private static readonly Func<ConfigurationTestInput, ConfigurationTestInput, bool> InputComparer = (a, b) =>
        a.Source == b.Source && a.AttributeName == b.AttributeName && a.Values.SequenceEqual(b.Values);

    #endregion
}

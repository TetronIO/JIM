// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers.ConfigurationTests;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.ConfigurationTests;

/// <summary>
/// The save gate (#2055 Phase 4b): a case gates a save only when it passed against the configuration as saved and does
/// not pass against what the save proposes. A case already failing never stops a save, and a save that removes what a
/// case covers takes the case with it, so the gate cannot lock an administrator out of their own configuration.
/// </summary>
[TestFixture]
public class ConfigurationTestGateTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private ConfigurationTestGate _gate = null!;
    private ConfigurationTestRunner _runner = null!;
    private ConnectedSystemObjectType _csoType = null!;
    private MetaverseObjectType _mvType = null!;

    [SetUp]
    public void SetUp()
    {
        _runner = new ConfigurationTestRunner(new DynamicExpressoEvaluator());
        _gate = new ConfigurationTestGate(_runner);
        _csoType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "person",
            ConnectedSystemId = 5,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "hireDate", Type = AttributeDataType.DateTime },
                new ConnectedSystemObjectTypeAttribute { Id = 13, Name = "sn", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 14, Name = "mail", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued },
                new ConnectedSystemObjectTypeAttribute { Id = 15, Name = "department", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 16, Name = "employeeNumber", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 17, Name = "leaveDate", Type = AttributeDataType.DateTime }
            ]
        };
        _mvType = new MetaverseObjectType
        {
            Id = 1,
            Name = "Person",
            Attributes =
            [
                new MetaverseAttribute { Id = 21, Name = "Display Name", Type = AttributeDataType.Text },
                new MetaverseAttribute { Id = 23, Name = "Hire Date", Type = AttributeDataType.DateTime },
                new MetaverseAttribute { Id = 24, Name = "Email Addresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued },
                new MetaverseAttribute { Id = 27, Name = "Employee ID", Type = AttributeDataType.Text }
            ]
        };
    }

    #region Which cases gate

    [Test]
    public void FindRegressions_CasePassedAndNoLongerDoes_IsARegressionWhoseNewResultCanBeAccepted()
    {
        var testCase = FlowCase(Values("Bloggs"), Cs("sn", "Bloggs"));

        var regressions = _gate.FindRegressions([testCase], Subject(FlowRule("cs[\"sn\"]")), Subject(FlowRule("Upper(cs[\"sn\"])")), Now);

        Assert.That(regressions, Has.Count.EqualTo(1));
        var regression = regressions[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(regression.TestCase, Is.SameAs(testCase));
            Assert.That(regression.Before.Status, Is.EqualTo(ConfigurationTestStatus.Passed));
            Assert.That(regression.After.Status, Is.EqualTo(ConfigurationTestStatus.Failed));
            Assert.That(regression.CanAccept, Is.True);
            Assert.That(regression.AcceptedExpectation, Is.EqualTo(Values("BLOGGS")));
        }
    }

    [Test]
    public void FindRegressions_CaseStillPasses_IsNoRegression()
    {
        var testCase = FlowCase(Values("BLOGGS"), Cs("sn", "Bloggs"));

        var regressions = _gate.FindRegressions([testCase], Subject(FlowRule("Upper(cs[\"sn\"])")), Subject(FlowRule("Upper(Trim(cs[\"sn\"]))")), Now);

        Assert.That(regressions, Is.Empty);
    }

    [Test]
    public void FindRegressions_CaseAlreadyFailingBeforeTheSave_NeverGatesIt()
    {
        // What makes the gate lockout-proof: a case broken by an earlier change, or by a schema change, never stops the
        // next save, however that save changes what the case covers.
        var testCase = FlowCase(Values("Smith"), Cs("sn", "Bloggs"));

        var regressions = _gate.FindRegressions([testCase], Subject(FlowRule("cs[\"sn\"]")), Subject(FlowRule("Upper(cs[\"sn\"])")), Now);

        Assert.That(regressions, Is.Empty);
    }

    [Test]
    public void FindRegressions_SaveRemovesTheAttributeFlowACaseCovers_IsNoRegression()
    {
        // The case is deleted with what it covers, so there is nothing left for it to stop.
        var testCase = FlowCase(Values("Bloggs"), Cs("sn", "Bloggs"));
        var proposed = FlowRule("cs[\"sn\"]");
        proposed.AttributeFlowRules.Clear();

        var regressions = _gate.FindRegressions([testCase], Subject(FlowRule("cs[\"sn\"]")), Subject(proposed), Now);

        Assert.That(regressions, Is.Empty);
    }

    [Test]
    public void FindRegressions_ProposedConfigurationReadsAnInputTheCaseDoesNotGive_IsARegressionThatCannotBeAccepted()
    {
        var testCase = FlowCase(Values("Bloggs"), Cs("sn", "Bloggs"));

        var regressions = _gate.FindRegressions([testCase], Subject(FlowRule("cs[\"sn\"]")),
            Subject(FlowRule("cs[\"sn\"] + cs[\"department\"]")), Now);

        Assert.That(regressions, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(regressions[0].After.Status, Is.EqualTo(ConfigurationTestStatus.NeedsReview));
            Assert.That(regressions[0].CanAccept, Is.False);
        }
    }

    [Test]
    public void FindRegressions_ProposedConfigurationGivesAnError_CanBeAcceptedAsExpectingAnError()
    {
        var testCase = FlowCase(Values("2026-03-02T00:00:00Z"), "Hire Date", Cs("hireDate", "2026-03-02"));

        var regressions = _gate.FindRegressions([testCase], Subject(FlowRule("cs[\"hireDate\"]", "Hire Date")),
            Subject(FlowRule("\"not a date\"", "Hire Date")), Now);

        Assert.That(regressions, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(regressions[0].After.Actual!.Outcome, Is.EqualTo(ConfigurationTestOutcome.Error));
            Assert.That(regressions[0].AcceptedExpectation, Is.EqualTo(Expect(ConfigurationTestExpectationKind.Error)));
        }
    }

    #endregion

    #region Accepting the new result

    [Test]
    public void FindRegressions_AcceptedExpectation_KeepsTheCasesOrderAndPassesWhenRunAgainstTheProposal()
    {
        var testCase = FlowCase(new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["a", "b"], InOrder: true),
            "Email Addresses", Cs("sn", "a,b"));
        var proposed = FlowRule("Split(Upper(cs[\"sn\"]), \",\")", "Email Addresses");

        var regressions = _gate.FindRegressions([testCase], Subject(FlowRule("Split(cs[\"sn\"], \",\")", "Email Addresses")),
            Subject(proposed), Now);

        Assert.That(regressions, Has.Count.EqualTo(1));
        var accepted = regressions[0].AcceptedExpectation!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted.Kind, Is.EqualTo(ConfigurationTestExpectationKind.Values));
            Assert.That(accepted.Values, Is.EqualTo(new[] { "A", "B" }));
            Assert.That(accepted.InOrder, Is.True);
        }

        testCase.Expectation = accepted;
        Assert.That(_runner.RunAttributeFlow(testCase, proposed, Now).Status, Is.EqualTo(ConfigurationTestStatus.Passed));
    }

    [Test]
    public void FindRegressions_AcceptedDateValue_PassesWhenRunAgainstTheProposal()
    {
        // The accepted values are the canonical form the comparison reads back, so accepting is always a pass.
        var testCase = FlowCase(Values("2026-03-02"), "Hire Date", Cs("hireDate", "2026-03-02"), Cs("leaveDate", "2026-04-01"));
        var proposed = FlowRule("cs[\"leaveDate\"]", "Hire Date");

        var regressions = _gate.FindRegressions([testCase], Subject(FlowRule("cs[\"hireDate\"]", "Hire Date")), Subject(proposed), Now);

        Assert.That(regressions, Has.Count.EqualTo(1));
        testCase.Expectation = regressions[0].AcceptedExpectation!;
        Assert.That(_runner.RunAttributeFlow(testCase, proposed, Now).Status, Is.EqualTo(ConfigurationTestStatus.Passed));
    }

    #endregion

    #region Scope

    [Test]
    public void FindRegressions_ScopeChangeTakesTheObjectOutOfScope_IsARegressionAcceptedAsOutOfScope()
    {
        var testCase = ScopeCase(ConfigurationTestExpectationKind.InScope, Input("department", "Sales"));

        var regressions = _gate.FindRegressions([testCase],
            Subject(ScopeRule(Criterion("department", SearchComparisonType.Equals, "Sales"))),
            Subject(ScopeRule(Criterion("department", SearchComparisonType.Equals, "Finance"))), Now);

        Assert.That(regressions, Has.Count.EqualTo(1));
        Assert.That(regressions[0].AcceptedExpectation, Is.EqualTo(Expect(ConfigurationTestExpectationKind.OutOfScope)));
    }

    [Test]
    public void FindRegressions_ScopeChangeThatGroupLogicAloneMakes_IsARegression()
    {
        // Group logic is not a criterion's state: the regression comparison sees it because it compares results.
        var testCase = ScopeCase(ConfigurationTestExpectationKind.InScope, Input("department", "Sales"), Input("sn", "Bloggs"));
        var stored = ScopeRule(Criterion("department", SearchComparisonType.Equals, "Sales"), Criterion("sn", SearchComparisonType.Equals, "Smith"));
        stored.ObjectScopingCriteriaGroups[0].Type = SearchGroupType.Any;
        var proposed = ScopeRule(Criterion("department", SearchComparisonType.Equals, "Sales"), Criterion("sn", SearchComparisonType.Equals, "Smith"));

        var regressions = _gate.FindRegressions([testCase], Subject(stored), Subject(proposed), Now);

        Assert.That(regressions, Has.Count.EqualTo(1));
    }

    [Test]
    public void FindRegressions_ScopeBecomesUndetermined_IsARegressionThatCannotBeAccepted()
    {
        var testCase = ScopeCase(ConfigurationTestExpectationKind.InScope, Input("department", "Sales"));

        var regressions = _gate.FindRegressions([testCase],
            Subject(ScopeRule(Criterion("department", SearchComparisonType.Equals, "Sales"))),
            Subject(ScopeRule(Criterion("department", SearchComparisonType.MatchesPattern, "("))), Now);

        Assert.That(regressions, Has.Count.EqualTo(1));
        Assert.That(regressions[0].CanAccept, Is.False);
    }

    #endregion

    #region Object Matching Rules

    [Test]
    public void FindRegressions_SyncRuleObjectMatchingRuleChange_IsARegression()
    {
        var testCase = MatchingCase(Values("48213"), Input("employeeNumber", "48213"));

        var regressions = _gate.FindRegressions([testCase], Subject(MatchingRuleRule("cs[\"employeeNumber\"]")),
            Subject(MatchingRuleRule("\"E\" + cs[\"employeeNumber\"]")), Now);

        Assert.That(regressions, Has.Count.EqualTo(1));
        Assert.That(regressions[0].AcceptedExpectation, Is.EqualTo(Values("E48213")));
    }

    [Test]
    public void FindRegressions_SimpleModeObjectMatchingRuleChange_IsARegression()
    {
        var testCase = MatchingCase(Values("48213"), Input("employeeNumber", "48213"));

        var regressions = _gate.FindRegressions([testCase],
            ConfigurationTestSubject.Of(MatchingRule("cs[\"employeeNumber\"]"), _csoType),
            ConfigurationTestSubject.Of(MatchingRule("\"E\" + cs[\"employeeNumber\"]"), _csoType), Now);

        Assert.That(regressions, Has.Count.EqualTo(1));
    }

    [Test]
    public void FindRegressions_CaseCoveringSomethingElse_IsNotRun()
    {
        // A Synchronisation Rule save gates only the cases covering that rule; a case for another rule's flow is left.
        var otherRulesCase = FlowCase(Values("Bloggs"), Cs("sn", "Bloggs"));
        otherRulesCase.SyncRuleMappingId = 999;

        var regressions = _gate.FindRegressions([otherRulesCase], Subject(FlowRule("cs[\"sn\"]")), Subject(FlowRule("Upper(cs[\"sn\"])")), Now);

        Assert.That(regressions, Is.Empty);
    }

    #endregion

    #region Helpers

    private static ConfigurationTestSubject Subject(SyncRule rule) => ConfigurationTestSubject.Of(rule);

    private SyncRule Rule() => new()
    {
        Id = 7, Name = "HR Import", Direction = SyncRuleDirection.Import,
        ConnectedSystemObjectType = _csoType, ConnectedSystemObjectTypeId = _csoType.Id,
        MetaverseObjectType = _mvType, MetaverseObjectTypeId = _mvType.Id
    };

    private SyncRule FlowRule(string expression, string target = "Display Name")
    {
        var rule = Rule();
        var mapping = new SyncRuleMapping { Id = 3, SyncRuleId = rule.Id, TargetMetaverseAttribute = _mvType.Attributes.Single(a => a.Name == target) };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = expression });
        rule.AttributeFlowRules.Add(mapping);
        return rule;
    }

    private SyncRule ScopeRule(params SyncRuleScopingCriteria[] criteria)
    {
        var rule = Rule();
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.AddRange(criteria);
        rule.ObjectScopingCriteriaGroups.Add(group);
        return rule;
    }

    private SyncRule MatchingRuleRule(string expression)
    {
        var rule = Rule();
        // As stored in Synchronisation Rule mode: it belongs to the rule, and takes both object types from it.
        var matchingRule = MatchingRule(expression);
        matchingRule.SyncRuleId = rule.Id;
        matchingRule.ConnectedSystemObjectTypeId = null;
        matchingRule.MetaverseObjectType = null;
        rule.ObjectMatchingRules.Add(matchingRule);
        return rule;
    }

    private ObjectMatchingRule MatchingRule(string expression) => new()
    {
        Id = 9,
        ConnectedSystemObjectTypeId = _csoType.Id,
        MetaverseObjectType = _mvType,
        TargetMetaverseAttribute = _mvType.Attributes.Single(a => a.Name == "Employee ID"),
        Sources = [new ObjectMatchingRuleSource { Order = 0, Expression = expression }]
    };

    private SyncRuleScopingCriteria Criterion(string name, SearchComparisonType comparison, string? text = null) => new()
    {
        ConnectedSystemAttribute = _csoType.Attributes.Single(a => a.Name == name),
        ComparisonType = comparison,
        StringValue = text
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

    private static ConfigurationTestCase FlowCase(ConfigurationTestExpectation expectation, ConfigurationTestInput input) =>
        FlowCase(expectation, "Display Name", input);

    private static ConfigurationTestCase FlowCase(ConfigurationTestExpectation expectation, string target, params ConfigurationTestInput[] inputs) => new()
    {
        Id = 1,
        Name = $"{target} case",
        Kind = ConfigurationTestCaseKind.AttributeFlow,
        SyncRuleMappingId = 3,
        Inputs = inputs,
        Expectation = expectation
    };

    private static ConfigurationTestCase ScopeCase(ConfigurationTestExpectationKind expected, params ConfigurationTestInput[] inputs) => new()
    {
        Id = 2,
        Name = "Scope case",
        Kind = ConfigurationTestCaseKind.Scope,
        SyncRuleId = 7,
        Inputs = inputs,
        Expectation = new ConfigurationTestExpectation(expected)
    };

    private static ConfigurationTestCase MatchingCase(ConfigurationTestExpectation expectation, ConfigurationTestInput input) => new()
    {
        Id = 4,
        Name = "Matching case",
        Kind = ConfigurationTestCaseKind.ObjectMatchingRule,
        ObjectMatchingRuleId = 9,
        Inputs = [input],
        Expectation = expectation
    };

    private static ConfigurationTestInput Cs(string name, params string[] values) => new(ExpressionInputSource.ConnectedSystem, name, values);

    private static ConfigurationTestInput Input(string name, params string[] values) => new(null, name, values);

    private static ConfigurationTestExpectation Values(params string[] values) => new(ConfigurationTestExpectationKind.Values, values);

    private static ConfigurationTestExpectation Expect(ConfigurationTestExpectationKind kind) => new(kind);

    #endregion
}

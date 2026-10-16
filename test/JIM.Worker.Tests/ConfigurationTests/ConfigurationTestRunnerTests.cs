// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
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
/// Running a saved configuration test case (#2055 Phase 4): each kind through the tester for it, the result compared
/// with the expectation on a culture-invariant value form, as an unordered set of values unless the case asks for
/// order, and a case whose inputs no longer fit the configuration marked for review rather than failed.
/// </summary>
[TestFixture]
public class ConfigurationTestRunnerTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private ConfigurationTestRunner _runner = null!;
    private ConnectedSystemObjectType _csoType = null!;
    private MetaverseObjectType _mvType = null!;

    [SetUp]
    public void SetUp()
    {
        _runner = new ConfigurationTestRunner(new DynamicExpressoEvaluator());
        _csoType = new ConnectedSystemObjectType
        {
            Id = 1,
            Name = "person",
            ConnectedSystemId = 5,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "hireDate", Type = AttributeDataType.DateTime },
                new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "givenName", Type = AttributeDataType.Text },
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
                new MetaverseAttribute { Id = 22, Name = "Employee Number", Type = AttributeDataType.Number },
                new MetaverseAttribute { Id = 23, Name = "Hire Date", Type = AttributeDataType.DateTime },
                new MetaverseAttribute { Id = 24, Name = "Email Addresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued },
                new MetaverseAttribute { Id = 25, Name = "Salary", Type = AttributeDataType.Decimal },
                new MetaverseAttribute { Id = 26, Name = "Account Name", Type = AttributeDataType.Text },
                new MetaverseAttribute { Id = 27, Name = "Employee ID", Type = AttributeDataType.Text }
            ]
        };
    }

    #region Attribute Flow: comparison

    [Test]
    public void RunAttributeFlow_ResultIsTheExpectedValue_Passes()
    {
        var (rule, testCase) = FlowCase("FormatDate((DateTime)cs[\"hireDate\"], \"yyyy-MM-dd\")", "Display Name",
            [Cs("hireDate", "2026-03-02")], Values("2026-03-02"));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
            Assert.That(result.Actual!.Outcome, Is.EqualTo(ConfigurationTestOutcome.Values));
            Assert.That(result.Actual.Values, Is.EqualTo(new[] { "2026-03-02" }));
        }
    }

    [Test]
    public void RunAttributeFlow_ResultDiffers_FailsSayingWhatWasExpectedAndWhatCame()
    {
        var (rule, testCase) = FlowCase("Upper(cs[\"sn\"])", "Display Name", [Cs("sn", "Bloggs")], Values("Bloggs"));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Failed));
            Assert.That(result.Summary, Does.Contain("Bloggs").And.Contain("BLOGGS"));
            Assert.That(result.Actual!.Values, Is.EqualTo(new[] { "BLOGGS" }));
        }
    }

    [Test]
    public void RunAttributeFlow_TextComparison_IsCaseSensitive()
    {
        var (rule, testCase) = FlowCase("cs[\"sn\"]", "Display Name", [Cs("sn", "Bloggs")], Values("bloggs"));

        Assert.That(_runner.RunAttributeFlow(testCase, rule, Now).Status, Is.EqualTo(ConfigurationTestStatus.Failed));
    }

    [TestCase("2026-03-02")]
    [TestCase("2026-03-02T00:00:00Z")]
    [TestCase("2026-03-02T01:00:00+01:00")]
    public void RunAttributeFlow_DateTarget_ComparesTheDateWhateverIsoFormItIsWrittenIn(string expected)
    {
        var (rule, testCase) = FlowCase("cs[\"hireDate\"]", "Hire Date", [Cs("hireDate", "2026-03-02")], Values(expected));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
            Assert.That(result.Actual!.Values, Is.EqualTo(new[] { "2026-03-02T00:00:00Z" }));
        }
    }

    [TestCase("de-DE")]
    [TestCase("fr-FR")]
    [TestCase("en-US")]
    public void RunAttributeFlow_WhateverTheServerCulture_ComparesDatesAndNumbersTheSame(string culture)
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);

            var (dateRule, dateCase) = FlowCase("cs[\"hireDate\"]", "Hire Date", [Cs("hireDate", "2026-03-02")], Values("2026-03-02"));
            var (decimalRule, decimalCase) = FlowCase("ToDecimal(\"1234.5\")", "Salary", [], Values("1234.50"));
            var (numberRule, numberCase) = FlowCase("ToInt(cs[\"employeeNumber\"])", "Employee Number", [Cs("employeeNumber", "48213")], Values("48213"));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_runner.RunAttributeFlow(dateCase, dateRule, Now).Status, Is.EqualTo(ConfigurationTestStatus.Passed));
                var decimalResult = _runner.RunAttributeFlow(decimalCase, decimalRule, Now);
                Assert.That(decimalResult.Status, Is.EqualTo(ConfigurationTestStatus.Passed), decimalResult.Summary);
                Assert.That(decimalResult.Actual!.Values, Is.EqualTo(new[] { "1234.5" }));
                Assert.That(_runner.RunAttributeFlow(numberCase, numberRule, Now).Status, Is.EqualTo(ConfigurationTestStatus.Passed));
            }
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous;
        }
    }

    [Test]
    public void RunAttributeFlow_ExpectedValueTheTargetCannotHold_FailsSayingSo()
    {
        var (rule, testCase) = FlowCase("cs[\"hireDate\"]", "Hire Date", [Cs("hireDate", "2026-03-02")], Values("02/03/2026"));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Failed));
            Assert.That(result.Summary, Does.Contain("02/03/2026").And.Contain("DateTime"));
        }
    }

    [Test]
    public void RunAttributeFlow_SeveralValuesInAnotherOrder_PassesByDefault()
    {
        var (rule, testCase) = FlowCase("cs[\"mail\"]", "Email Addresses", [Cs("mail", "b@corp.example", "a@corp.example")],
            Values("a@corp.example", "b@corp.example"));

        Assert.That(_runner.RunAttributeFlow(testCase, rule, Now).Status, Is.EqualTo(ConfigurationTestStatus.Passed));
    }

    [Test]
    public void RunAttributeFlow_SeveralValuesInAnotherOrder_FailsWhenTheCaseAsksForOrder()
    {
        // Split keeps the order of its text, unlike a multi-valued attribute, which synchronisation reads in a fixed order.
        var (rule, testCase) = FlowCase("Split(cs[\"sn\"], \",\")", "Email Addresses", [Cs("sn", "b,a")],
            new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["a", "b"], InOrder: true));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Failed));
            Assert.That(result.Summary, Does.Contain("order"));
        }
    }

    [Test]
    public void RunAttributeFlow_ARepeatedValue_CountsEachRepeat()
    {
        // An unordered comparison is of a multiset: two of a value is not one of it.
        var (rule, testCase) = FlowCase("Split(cs[\"sn\"], \",\")", "Email Addresses", [Cs("sn", "a,b")], Values("a", "a", "b"));

        Assert.That(_runner.RunAttributeFlow(testCase, rule, Now).Status, Is.EqualTo(ConfigurationTestStatus.Failed));
    }

    #endregion

    #region Attribute Flow: other expectations

    [Test]
    public void RunAttributeFlow_ExpectingNoValueAndThereIsNone_Passes()
    {
        var (rule, testCase) = FlowCase("cs[\"sn\"]", "Display Name", [Cs("sn")], Expect(ConfigurationTestExpectationKind.NoValue));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
            Assert.That(result.Actual!.Outcome, Is.EqualTo(ConfigurationTestOutcome.NoValue));
        }
    }

    [Test]
    public void RunAttributeFlow_ExpectingAnErrorAndOneComes_Passes()
    {
        // A Hire Date cannot hold "Bloggs", so synchronisation records an error.
        var (rule, testCase) = FlowCase("cs[\"sn\"]", "Hire Date", [Cs("sn", "Bloggs")], Expect(ConfigurationTestExpectationKind.Error));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
            Assert.That(result.Actual!.Outcome, Is.EqualTo(ConfigurationTestOutcome.Error));
        }
    }

    [Test]
    public void RunAttributeFlow_ExpectingValuesButAnErrorComes_FailsWithTheReason()
    {
        var (rule, testCase) = FlowCase("cs[\"sn\"]", "Hire Date", [Cs("sn", "Bloggs")], Values("2026-03-02"));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Failed));
            Assert.That(result.Actual!.Outcome, Is.EqualTo(ConfigurationTestOutcome.Error));
            Assert.That(result.Actual.ErrorMessage, Is.Not.Empty);
        }
    }

    [Test]
    public void RunAttributeFlow_ExpectingAnyValue_PassesOnAValueAndFailsOnNone()
    {
        var (rule, withValue) = FlowCase("cs[\"sn\"]", "Display Name", [Cs("sn", "Bloggs")], Expect(ConfigurationTestExpectationKind.AnyValue));
        var (_, withNone) = FlowCase("cs[\"sn\"]", "Display Name", [Cs("sn")], Expect(ConfigurationTestExpectationKind.AnyValue));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_runner.RunAttributeFlow(withValue, rule, Now).Status, Is.EqualTo(ConfigurationTestStatus.Passed));
            Assert.That(_runner.RunAttributeFlow(withNone, rule, Now).Status, Is.EqualTo(ConfigurationTestStatus.Failed));
        }
    }

    #endregion

    #region Attribute Flow: needs review

    [Test]
    public void RunAttributeFlow_ExpressionReadsAnInputTheCaseDoesNotGive_NeedsReviewNamingIt()
    {
        var (rule, testCase) = FlowCase("cs[\"givenName\"] + \" \" + cs[\"sn\"]", "Display Name", [Cs("givenName", "Joe")], Values("Joe Bloggs"));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.NeedsReview));
            Assert.That(result.Problems, Has.Some.Contains("cs[\"sn\"]"));
            Assert.That(result.Actual, Is.Null, "a case needing review is not run");
        }
    }

    [Test]
    public void RunAttributeFlow_InputGivenWithNoValues_CountsAsGiven()
    {
        var (rule, testCase) = FlowCase("cs[\"givenName\"] + cs[\"sn\"]", "Display Name", [Cs("givenName", "Joe"), Cs("sn")],
            Expect(ConfigurationTestExpectationKind.AnyValue));

        Assert.That(_runner.RunAttributeFlow(testCase, rule, Now).Status, Is.Not.EqualTo(ConfigurationTestStatus.NeedsReview));
    }

    [Test]
    public void RunAttributeFlow_InputThatLeftTheSchema_NeedsReview()
    {
        var (rule, testCase) = FlowCase("cs[\"sn\"]", "Display Name", [Cs("sn", "Bloggs"), Cs("costCentre", "CC1")], Values("Bloggs"));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.NeedsReview));
            Assert.That(result.Problems, Has.Some.Contains("costCentre"));
        }
    }

    [Test]
    public void RunAttributeFlow_InputThatNoLongerFitsItsAttributesType_NeedsReview()
    {
        var (rule, testCase) = FlowCase("cs[\"hireDate\"]", "Hire Date", [Cs("hireDate", "not a date")], Values("2026-03-02"));

        Assert.That(_runner.RunAttributeFlow(testCase, rule, Now).Status, Is.EqualTo(ConfigurationTestStatus.NeedsReview));
    }

    [Test]
    public void RunAttributeFlow_AttributeFlowCaseOnAGeneratedValue_NeedsReview()
    {
        var (rule, testCase) = FlowCase("cs[\"sn\"]", "Account Name", [Cs("sn", "Bloggs")], Values("Bloggs"));
        rule.AttributeFlowRules[0].Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken };

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.NeedsReview));
            Assert.That(result.Problems, Has.Some.Contains("Generated Value"));
        }
    }

    [Test]
    public void RunAttributeFlow_AttributeFlowWithNoExpression_NeedsReview()
    {
        var (rule, testCase) = FlowCase("cs[\"sn\"]", "Display Name", [Cs("sn", "Bloggs")], Values("Bloggs"));
        rule.AttributeFlowRules[0].Sources[0].Expression = null;
        rule.AttributeFlowRules[0].Sources[0].ConnectedSystemAttribute = _csoType.Attributes.Single(a => a.Name == "sn");

        Assert.That(_runner.RunAttributeFlow(testCase, rule, Now).Status, Is.EqualTo(ConfigurationTestStatus.NeedsReview));
    }

    #endregion

    #region Run as of

    [Test]
    public void RunAttributeFlow_RunAsOfSet_ExpressionReadsThatInstant()
    {
        var (rule, testCase) = FlowCase("FormatDate(Now(), \"yyyy-MM-dd\")", "Display Name", [], Values("2020-01-31"));
        testCase.RunAsOfUtc = new DateTime(2020, 1, 31, 12, 0, 0, DateTimeKind.Utc);

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
            Assert.That(result.RanAsOfUtc, Is.EqualTo(testCase.RunAsOfUtc));
            Assert.That(result.RanAsOfNow, Is.False);
        }
    }

    [Test]
    public void RunAttributeFlow_NoRunAsOfAndTheExpressionReadsTheClock_SaysItRanAsOfNow()
    {
        var (rule, testCase) = FlowCase("FormatDate(Now(), \"yyyy-MM-dd\")", "Display Name", [], Values("2026-11-10"));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
            Assert.That(result.RanAsOfUtc, Is.EqualTo(Now));
            Assert.That(result.RanAsOfNow, Is.True);
        }
    }

    [Test]
    public void RunAttributeFlow_NoRunAsOfAndTheExpressionDoesNotReadTheClock_DoesNotSayItRanAsOfNow()
    {
        var (rule, testCase) = FlowCase("cs[\"sn\"]", "Display Name", [Cs("sn", "Bloggs")], Values("Bloggs"));

        Assert.That(_runner.RunAttributeFlow(testCase, rule, Now).RanAsOfNow, Is.False);
    }

    #endregion

    #region Generated Value

    [Test]
    public void RunAttributeFlow_GeneratedValueBase_ComparesTheBaseBeforeTheToken()
    {
        var (rule, testCase) = GeneratedCase("Lower(Left(cs[\"givenName\"], 1) + cs[\"sn\"])", [Cs("givenName", "Joe"), Cs("sn", "Bloggs")], Values("jbloggs"));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
            Assert.That(result.Actual!.Values, Is.EqualTo(new[] { "jbloggs" }));
        }
    }

    [Test]
    public void RunAttributeFlow_GeneratedValueThatWaits_IsNoValue()
    {
        var (rule, testCase) = GeneratedCase("cs[\"givenName\"] + cs[\"sn\"]", [Cs("givenName", "Joe"), Cs("sn")], Expect(ConfigurationTestExpectationKind.NoValue));

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
            Assert.That(result.Actual!.Outcome, Is.EqualTo(ConfigurationTestOutcome.NoValue));
        }
    }

    [Test]
    public void RunAttributeFlow_GeneratedValueCaseOnAnOrdinaryAttributeFlow_NeedsReview()
    {
        var (rule, testCase) = FlowCase("cs[\"sn\"]", "Display Name", [Cs("sn", "Bloggs")], Values("Bloggs"));
        testCase.Kind = ConfigurationTestCaseKind.GeneratedValue;

        Assert.That(_runner.RunAttributeFlow(testCase, rule, Now).Status, Is.EqualTo(ConfigurationTestStatus.NeedsReview));
    }

    [Test]
    public void RunAttributeFlow_GeneratedValueWithNoBaseExpression_NeedsReview()
    {
        var (rule, testCase) = GeneratedCase("cs[\"sn\"]", [Cs("sn", "Bloggs")], Values("Bloggs"));
        rule.AttributeFlowRules[0].Sources.Clear();

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.NeedsReview));
            Assert.That(result.Problems, Has.Some.Contains("base expression"));
        }
    }

    #endregion

    #region Scope

    [Test]
    public void RunScope_ExpectingInScopeAndItIs_Passes()
    {
        var rule = ImportRule(All(Criterion("department", SearchComparisonType.Equals, "Sales")));
        var testCase = ScopeCase([Input("department", "Sales")], ConfigurationTestExpectationKind.InScope);

        var result = _runner.RunScope(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
            Assert.That(result.Actual!.Outcome, Is.EqualTo(ConfigurationTestOutcome.InScope));
        }
    }

    [Test]
    public void RunScope_ExpectingOutOfScopeButItIsIn_Fails()
    {
        var rule = ImportRule(All(Criterion("department", SearchComparisonType.Equals, "Sales")));
        var testCase = ScopeCase([Input("department", "Sales")], ConfigurationTestExpectationKind.OutOfScope);

        var result = _runner.RunScope(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Failed));
            Assert.That(result.Summary, Does.Contain("in scope"));
        }
    }

    [TestCase(ConfigurationTestExpectationKind.InScope)]
    [TestCase(ConfigurationTestExpectationKind.OutOfScope)]
    public void RunScope_Undetermined_FailsWhateverWasExpected(ConfigurationTestExpectationKind expected)
    {
        // A criterion synchronisation cannot evaluate: the pattern does not compile.
        var rule = ImportRule(All(Criterion("department", SearchComparisonType.MatchesPattern, "(")));
        var testCase = ScopeCase([Input("department", "Sales")], expected);

        var result = _runner.RunScope(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Failed));
            Assert.That(result.Actual!.Outcome, Is.EqualTo(ConfigurationTestOutcome.Undetermined));
        }
    }

    [Test]
    public void RunScope_CriteriaCompareAnAttributeTheCaseDoesNotGive_NeedsReview()
    {
        var rule = ImportRule(All(
            Criterion("department", SearchComparisonType.Equals, "Sales"),
            Criterion("givenName", SearchComparisonType.HasValue)));
        var testCase = ScopeCase([Input("department", "Sales")], ConfigurationTestExpectationKind.InScope);

        var result = _runner.RunScope(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.NeedsReview));
            Assert.That(result.Problems, Has.Some.Contains("givenName"));
        }
    }

    [Test]
    public void RunScope_RelativeDateCriterion_RunsAsOfTheCasesInstantOrSaysItRanAsOfNow()
    {
        // In scope while the leave date is less than 30 days ago.
        var rule = ImportRule(All(Relative("leaveDate", SearchComparisonType.GreaterThan, 30, RelativeDateUnit.Days, RelativeDateDirection.Ago)));
        var pinned = ScopeCase([Input("leaveDate", "2020-01-15")], ConfigurationTestExpectationKind.InScope);
        pinned.RunAsOfUtc = new DateTime(2020, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        var unpinned = ScopeCase([Input("leaveDate", "2020-01-15")], ConfigurationTestExpectationKind.OutOfScope);

        var pinnedResult = _runner.RunScope(pinned, rule, Now);
        var unpinnedResult = _runner.RunScope(unpinned, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pinnedResult.Status, Is.EqualTo(ConfigurationTestStatus.Passed), pinnedResult.Summary);
            Assert.That(pinnedResult.RanAsOfNow, Is.False);
            Assert.That(unpinnedResult.Status, Is.EqualTo(ConfigurationTestStatus.Passed), unpinnedResult.Summary);
            Assert.That(unpinnedResult.RanAsOfNow, Is.True);
        }
    }

    #endregion

    #region Object Matching Rule

    [Test]
    public void RunObjectMatching_MatchValuesAsExpected_Passes()
    {
        var rule = MatchingRule("Upper(cs[\"employeeNumber\"])");
        var testCase = MatchingCase([Input("employeeNumber", "ab123")], Values("AB123"));

        var result = _runner.RunObjectMatching(testCase, rule, _csoType, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
            Assert.That(result.Actual!.Values, Is.EqualTo(new[] { "AB123" }));
        }
    }

    [Test]
    public void RunObjectMatching_MatchValuesDiffer_Fails()
    {
        var rule = MatchingRule("cs[\"employeeNumber\"]");
        var testCase = MatchingCase([Input("employeeNumber", "ab123")], Values("AB123"));

        Assert.That(_runner.RunObjectMatching(testCase, rule, _csoType, Now).Status, Is.EqualTo(ConfigurationTestStatus.Failed));
    }

    [Test]
    public void RunObjectMatching_NoMatchValue_IsNoValue()
    {
        var rule = MatchingRule("cs[\"employeeNumber\"]");
        var testCase = MatchingCase([Input("employeeNumber")], Expect(ConfigurationTestExpectationKind.NoValue));

        var result = _runner.RunObjectMatching(testCase, rule, _csoType, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.Passed), result.Summary);
            Assert.That(result.Actual!.Outcome, Is.EqualTo(ConfigurationTestOutcome.NoValue));
        }
    }

    [Test]
    public void RunObjectMatching_RuleReadsAnAttributeTheCaseDoesNotGive_NeedsReview()
    {
        var rule = MatchingRule("cs[\"employeeNumber\"] + cs[\"sn\"]");
        var testCase = MatchingCase([Input("employeeNumber", "ab123")], Values("ab123Bloggs"));

        var result = _runner.RunObjectMatching(testCase, rule, _csoType, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ConfigurationTestStatus.NeedsReview));
            Assert.That(result.Problems, Has.Some.Contains("sn"));
        }
    }

    #endregion

    #region Result identity

    [Test]
    public void Run_Result_NamesTheCaseItRan()
    {
        var (rule, testCase) = FlowCase("cs[\"sn\"]", "Display Name", [Cs("sn", "Bloggs")], Values("Bloggs"));
        testCase.Id = 42;
        testCase.Name = "Surname flows unchanged";

        var result = _runner.RunAttributeFlow(testCase, rule, Now);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TestCaseId, Is.EqualTo(42));
            Assert.That(result.TestCaseName, Is.EqualTo("Surname flows unchanged"));
            Assert.That(result.Kind, Is.EqualTo(ConfigurationTestCaseKind.AttributeFlow));
            Assert.That(result.Expected, Is.EqualTo(testCase.Expectation));
        }
    }

    #endregion

    #region Helpers

    private (SyncRule Rule, ConfigurationTestCase Case) FlowCase(string expression, string targetName,
        IReadOnlyList<ConfigurationTestInput> inputs, ConfigurationTestExpectation expectation)
    {
        var rule = new SyncRule
        {
            Id = 7, Name = "HR Import", Direction = SyncRuleDirection.Import,
            ConnectedSystemObjectType = _csoType, MetaverseObjectType = _mvType
        };
        var mapping = new SyncRuleMapping { Id = 3, TargetMetaverseAttribute = _mvType.Attributes.Single(a => a.Name == targetName) };
        mapping.Sources.Add(new SyncRuleMappingSource { Expression = expression });
        rule.AttributeFlowRules.Add(mapping);

        return (rule, new ConfigurationTestCase
        {
            Name = "case",
            Kind = ConfigurationTestCaseKind.AttributeFlow,
            SyncRuleMappingId = mapping.Id,
            Inputs = inputs,
            Expectation = expectation
        });
    }

    private (SyncRule Rule, ConfigurationTestCase Case) GeneratedCase(string baseExpression,
        IReadOnlyList<ConfigurationTestInput> inputs, ConfigurationTestExpectation expectation)
    {
        var (rule, testCase) = FlowCase(baseExpression, "Account Name", inputs, expectation);
        var mapping = rule.AttributeFlowRules[0];
        mapping.Generation = new SyncRuleMappingGeneration { TokenKind = GeneratedValueTokenKind.OnlyIfTaken };
        mapping.Sources[0].MissingInputBehaviour = MissingInputBehaviour.ContributeNoValue;
        testCase.Kind = ConfigurationTestCaseKind.GeneratedValue;
        return (rule, testCase);
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

    private static SyncRuleScopingCriteriaGroup All(params SyncRuleScopingCriteria[] criteria)
    {
        var group = new SyncRuleScopingCriteriaGroup { Type = SearchGroupType.All };
        group.Criteria.AddRange(criteria);
        return group;
    }

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

    private static ConfigurationTestCase ScopeCase(IReadOnlyList<ConfigurationTestInput> inputs, ConfigurationTestExpectationKind expected) => new()
    {
        Name = "case",
        Kind = ConfigurationTestCaseKind.Scope,
        SyncRuleId = 7,
        Inputs = inputs,
        Expectation = new ConfigurationTestExpectation(expected)
    };

    private ObjectMatchingRule MatchingRule(string expression) => new()
    {
        Id = 9,
        ConnectedSystemObjectTypeId = _csoType.Id,
        MetaverseObjectType = _mvType,
        TargetMetaverseAttribute = _mvType.Attributes.Single(a => a.Name == "Employee ID"),
        Sources = [new ObjectMatchingRuleSource { Order = 0, Expression = expression }]
    };

    private static ConfigurationTestCase MatchingCase(IReadOnlyList<ConfigurationTestInput> inputs, ConfigurationTestExpectation expectation) => new()
    {
        Name = "case",
        Kind = ConfigurationTestCaseKind.ObjectMatchingRule,
        ObjectMatchingRuleId = 9,
        Inputs = inputs,
        Expectation = expectation
    };

    private static ConfigurationTestInput Cs(string name, params string[] values) => new(ExpressionInputSource.ConnectedSystem, name, values);

    private static ConfigurationTestInput Input(string name, params string[] values) => new(null, name, values);

    private static ConfigurationTestExpectation Values(params string[] values) => new(ConfigurationTestExpectationKind.Values, values);

    private static ConfigurationTestExpectation Expect(ConfigurationTestExpectationKind kind) => new(kind);

    #endregion
}

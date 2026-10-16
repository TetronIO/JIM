// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Logic;
using JIM.Web;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// How the portal shows and edits a saved configuration test case (#2055 Phase 4): what it covers, named from the
/// configuration, and its inputs laid against the inputs that configuration reads now, so one it no longer reads, or
/// one it reads that the case does not give, is plain to see.
/// </summary>
[TestFixture]
public class ConfigurationTestCaseDisplayTests
{
    private static readonly SyncRule Rule = BuildRule();

    [Test]
    public void DescribeCoverage_AttributeFlowCase_NamesItsTarget()
    {
        var testCase = new ConfigurationTestCase { Kind = ConfigurationTestCaseKind.AttributeFlow, SyncRuleMappingId = 3 };

        Assert.That(ConfigurationTestCaseDisplay.DescribeCoverage(testCase, Rule, []), Is.EqualTo("Attribute Flow to Display Name"));
    }

    [Test]
    public void DescribeCoverage_GeneratedValueCase_NamesItsTarget()
    {
        var testCase = new ConfigurationTestCase { Kind = ConfigurationTestCaseKind.GeneratedValue, SyncRuleMappingId = 3 };

        Assert.That(ConfigurationTestCaseDisplay.DescribeCoverage(testCase, Rule, []), Is.EqualTo("Generated Value for Display Name"));
    }

    [Test]
    public void DescribeCoverage_ScopeCase_IsTheScope()
    {
        var testCase = new ConfigurationTestCase { Kind = ConfigurationTestCaseKind.Scope, SyncRuleId = 7 };

        Assert.That(ConfigurationTestCaseDisplay.DescribeCoverage(testCase, Rule, []), Is.EqualTo("Scope"));
    }

    [Test]
    public void DescribeCoverage_MatchingCase_NamesWhatTheRuleMatchesOn()
    {
        var testCase = new ConfigurationTestCase { Kind = ConfigurationTestCaseKind.ObjectMatchingRule, ObjectMatchingRuleId = 9 };
        var rule = new ObjectMatchingRule { Id = 9, TargetMetaverseAttribute = new MetaverseAttribute { Name = "Employee ID" } };

        Assert.That(ConfigurationTestCaseDisplay.DescribeCoverage(testCase, null, [rule]), Is.EqualTo("Object Matching Rule on Employee ID"));
    }

    [Test]
    public void DescribeCoverage_AttributeFlowNotOnTheRule_FallsBackToItsId()
    {
        var testCase = new ConfigurationTestCase { Kind = ConfigurationTestCaseKind.AttributeFlow, SyncRuleMappingId = 99 };

        Assert.That(ConfigurationTestCaseDisplay.DescribeCoverage(testCase, Rule, []), Is.EqualTo("Attribute Flow 99"));
    }

    [TestCase(ConfigurationTestExpectationKind.NoValue, "No value")]
    [TestCase(ConfigurationTestExpectationKind.Error, "An error")]
    [TestCase(ConfigurationTestExpectationKind.AnyValue, "Any value")]
    [TestCase(ConfigurationTestExpectationKind.InScope, "In scope")]
    [TestCase(ConfigurationTestExpectationKind.OutOfScope, "Out of scope")]
    public void DescribeExpectation_EachOutcome_SaysWhatIsExpected(ConfigurationTestExpectationKind kind, string expected)
    {
        Assert.That(ConfigurationTestCaseDisplay.DescribeExpectation(new ConfigurationTestExpectation(kind)), Is.EqualTo(expected));
    }

    [Test]
    public void DescribeExpectation_Values_QuotesEachAndSaysWhenOrderMatters()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ConfigurationTestCaseDisplay.DescribeExpectation(new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["a", "b"])),
                Is.EqualTo("'a', 'b'"));
            Assert.That(ConfigurationTestCaseDisplay.DescribeExpectation(new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["a", "b"], InOrder: true)),
                Is.EqualTo("'a', 'b', in this order"));
        }
    }

    [Test]
    public void InputRows_EachInputReadComesFirstWithTheCasesValues()
    {
        var testCase = new ConfigurationTestCase
        {
            Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "SN", ["Bloggs"])]
        };

        var rows = ConfigurationTestCaseDisplay.InputRows(testCase,
        [
            new ConfigurationTestCaseDisplay.InputSlot(ExpressionInputSource.ConnectedSystem, "givenName", AttributeDataType.Text, AttributePlurality.SingleValued),
            new ConfigurationTestCaseDisplay.InputSlot(ExpressionInputSource.ConnectedSystem, "sn", AttributeDataType.Text, AttributePlurality.SingleValued)
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows.Select(r => r.AttributeName), Is.EqualTo(new[] { "givenName", "sn" }));
            Assert.That(rows[0].Given, Is.False, "the case gives no input for givenName, so it needs review");
            Assert.That(rows[1].Given, Is.True, "names match case-insensitively, as a run does");
            Assert.That(rows[1].State.Values, Is.EqualTo(new[] { "Bloggs" }));
            Assert.That(rows.All(r => r.IsRead), Is.True);
        }
    }

    [Test]
    public void InputRows_InputNoLongerRead_FollowsMarkedAsNotRead()
    {
        var testCase = new ConfigurationTestCase
        {
            Inputs = [new ConfigurationTestInput(null, "costCentre", ["CC1"]), new ConfigurationTestInput(null, "department", ["Sales"])]
        };

        var rows = ConfigurationTestCaseDisplay.InputRows(testCase,
            [new ConfigurationTestCaseDisplay.InputSlot(null, "department", AttributeDataType.Text, AttributePlurality.SingleValued)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows.Select(r => r.AttributeName), Is.EqualTo(new[] { "department", "costCentre" }));
            Assert.That(rows[1].IsRead, Is.False);
            Assert.That(rows[1].State.Values, Is.EqualTo(new[] { "CC1" }));
        }
    }

    [Test]
    public void InputRows_InputGivenWithNoValues_IsGivenWithNoValue()
    {
        var testCase = new ConfigurationTestCase { Inputs = [new ConfigurationTestInput(null, "department", [])] };

        var row = ConfigurationTestCaseDisplay.InputRows(testCase,
            [new ConfigurationTestCaseDisplay.InputSlot(null, "department", AttributeDataType.Text, AttributePlurality.SingleValued)]).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(row.Given, Is.True);
            Assert.That(row.State.NoValue, Is.True);
        }
    }

    [Test]
    public void ToInputs_EveryReadRowAndTheNotReadRowsKept_BecomeTheCasesInputs()
    {
        var testCase = new ConfigurationTestCase
        {
            Inputs = [new ConfigurationTestInput(null, "costCentre", ["CC1"]), new ConfigurationTestInput(null, "legacy", ["x"])]
        };
        var rows = ConfigurationTestCaseDisplay.InputRows(testCase,
            [new ConfigurationTestCaseDisplay.InputSlot(null, "department", AttributeDataType.Text, AttributePlurality.SingleValued)]);
        rows[0].State.SetSingle("Sales");
        rows.RemoveAll(r => r.AttributeName == "legacy");

        var inputs = ConfigurationTestCaseDisplay.ToInputs(rows);

        Assert.That(inputs.Select(i => $"{i.AttributeName}={string.Join("|", i.Values)}"), Is.EqualTo(new[] { "department=Sales", "costCentre=CC1" }));
    }

    [Test]
    public void ToInputs_ReadRowLeftAtNoValue_IsGivenWithNoValues()
    {
        // Saving from the dialog gives every input the configuration reads, so a case saved there never needs review
        // for a missing input.
        var rows = ConfigurationTestCaseDisplay.InputRows(new ConfigurationTestCase(),
            [new ConfigurationTestCaseDisplay.InputSlot(null, "department", AttributeDataType.Text, AttributePlurality.SingleValued)]);

        var input = ConfigurationTestCaseDisplay.ToInputs(rows).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(input.AttributeName, Is.EqualTo("department"));
            Assert.That(input.Values, Is.Empty);
        }
    }

    private static SyncRule BuildRule()
    {
        var rule = new SyncRule { Id = 7, Name = "HR Import" };
        rule.AttributeFlowRules.Add(new SyncRuleMapping { Id = 3, TargetMetaverseAttribute = new MetaverseAttribute { Name = "Display Name" } });
        return rule;
    }
}

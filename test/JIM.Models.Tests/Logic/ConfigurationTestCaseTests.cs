// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Expressions;
using JIM.Models.Logic;
using NUnit.Framework;

namespace JIM.Models.Tests.Logic;

/// <summary>
/// The shape of a saved configuration test case (#2055 Phase 4): what it covers, its inputs and its expectation,
/// stored as JSON on the row and refused on save when they do not fit together.
/// </summary>
[TestFixture]
public class ConfigurationTestCaseTests
{
    [Test]
    public void Inputs_SetThenRead_RoundTripsThroughTheStoredJson()
    {
        var testCase = AttributeFlowCase();
        testCase.Inputs =
        [
            new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "hireDate", ["2026-03-02"]),
            new ConfigurationTestInput(ExpressionInputSource.Metaverse, "Display Name", ["Joe", "Bloggs"]),
            new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "manager", [])
        ];

        var reread = new ConfigurationTestCase { InputsJson = testCase.InputsJson }.Inputs;

        Assert.That(reread, Has.Count.EqualTo(3));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reread[0].Source, Is.EqualTo(ExpressionInputSource.ConnectedSystem));
            Assert.That(reread[0].AttributeName, Is.EqualTo("hireDate"));
            Assert.That(reread[0].Values, Is.EqualTo(new[] { "2026-03-02" }));
            Assert.That(reread[1].Source, Is.EqualTo(ExpressionInputSource.Metaverse));
            Assert.That(reread[1].Values, Is.EqualTo(new[] { "Joe", "Bloggs" }));
            Assert.That(reread[2].Values, Is.Empty);
        }
    }

    [Test]
    public void Inputs_StoredJson_NamesTheSourceAsText()
    {
        // The document is configuration an administrator may read in a snapshot or a backup, so it says what it means.
        var testCase = AttributeFlowCase();
        testCase.Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", ["Bloggs"])];

        Assert.That(testCase.InputsJson, Does.Contain("\"ConnectedSystem\"").And.Contain("\"sn\""));
    }

    [Test]
    public void Inputs_NoSource_RoundTripsAsNoSource()
    {
        var testCase = ScopeCase();
        testCase.Inputs = [new ConfigurationTestInput(null, "department", ["Sales"])];

        var reread = new ConfigurationTestCase { InputsJson = testCase.InputsJson }.Inputs;

        Assert.That(reread[0].Source, Is.Null);
    }

    [Test]
    public void Expectation_SetThenRead_RoundTripsThroughTheStoredJson()
    {
        var testCase = AttributeFlowCase();
        testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["b", "a"], InOrder: true);

        var reread = new ConfigurationTestCase { ExpectationJson = testCase.ExpectationJson }.Expectation;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reread.Kind, Is.EqualTo(ConfigurationTestExpectationKind.Values));
            Assert.That(reread.Values, Is.EqualTo(new[] { "b", "a" }));
            Assert.That(reread.InOrder, Is.True);
        }
    }

    [Test]
    public void Expectation_NothingStored_ReadsAsAnyValue()
    {
        Assert.That(new ConfigurationTestCase().Expectation.Kind, Is.EqualTo(ConfigurationTestExpectationKind.AnyValue));
    }

    [Test]
    public void DescribeInvalidity_WellFormedCaseOfEachKind_IsNull()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(AttributeFlowCase().DescribeInvalidity(), Is.Null);
            Assert.That(GeneratedValueCase().DescribeInvalidity(), Is.Null);
            Assert.That(ScopeCase().DescribeInvalidity(), Is.Null);
            Assert.That(MatchingCase().DescribeInvalidity(), Is.Null);
        }
    }

    [Test]
    public void DescribeInvalidity_NoName_SaysANameIsNeeded()
    {
        var testCase = AttributeFlowCase();
        testCase.Name = "  ";

        Assert.That(testCase.DescribeInvalidity(), Does.Contain("name"));
    }

    [Test]
    public void DescribeInvalidity_NameTooLong_SaysHowLongItMayBe()
    {
        var testCase = AttributeFlowCase();
        testCase.Name = new string('x', ConfigurationTestCase.MaxNameLength + 1);

        Assert.That(testCase.DescribeInvalidity(), Does.Contain(ConfigurationTestCase.MaxNameLength.ToString()));
    }

    [TestCase(ConfigurationTestCaseKind.AttributeFlow)]
    [TestCase(ConfigurationTestCaseKind.GeneratedValue)]
    public void DescribeInvalidity_FlowCaseCoveringARule_SaysItCoversAnAttributeFlow(ConfigurationTestCaseKind kind)
    {
        var testCase = AttributeFlowCase();
        testCase.Kind = kind;
        testCase.SyncRuleMappingId = null;
        testCase.SyncRuleId = 7;

        Assert.That(testCase.DescribeInvalidity(), Does.Contain("Attribute Flow"));
    }

    [Test]
    public void DescribeInvalidity_ScopeCaseCoveringAnAttributeFlow_SaysItCoversASynchronisationRule()
    {
        var testCase = ScopeCase();
        testCase.SyncRuleId = null;
        testCase.SyncRuleMappingId = 3;

        Assert.That(testCase.DescribeInvalidity(), Does.Contain("Synchronisation Rule"));
    }

    [Test]
    public void DescribeInvalidity_MatchingCaseCoveringNothing_SaysItCoversAnObjectMatchingRule()
    {
        var testCase = MatchingCase();
        testCase.ObjectMatchingRuleId = null;

        Assert.That(testCase.DescribeInvalidity(), Does.Contain("Object Matching Rule"));
    }

    [Test]
    public void DescribeInvalidity_CaseCoveringTwoThings_SaysItCoversExactlyOne()
    {
        var testCase = AttributeFlowCase();
        testCase.SyncRuleId = 7;

        Assert.That(testCase.DescribeInvalidity(), Does.Contain("exactly one"));
    }

    [TestCase(ConfigurationTestExpectationKind.InScope)]
    [TestCase(ConfigurationTestExpectationKind.OutOfScope)]
    public void DescribeInvalidity_AttributeFlowExpectingAScopeOutcome_IsRefused(ConfigurationTestExpectationKind expectation)
    {
        var testCase = AttributeFlowCase();
        testCase.Expectation = new ConfigurationTestExpectation(expectation);

        Assert.That(testCase.DescribeInvalidity(), Does.Contain(expectation.ToString()));
    }

    [TestCase(ConfigurationTestExpectationKind.Values)]
    [TestCase(ConfigurationTestExpectationKind.NoValue)]
    [TestCase(ConfigurationTestExpectationKind.Error)]
    [TestCase(ConfigurationTestExpectationKind.AnyValue)]
    public void DescribeInvalidity_ScopeExpectingAValueOutcome_IsRefused(ConfigurationTestExpectationKind expectation)
    {
        var testCase = ScopeCase();
        testCase.Expectation = new ConfigurationTestExpectation(expectation, expectation == ConfigurationTestExpectationKind.Values ? ["x"] : []);

        Assert.That(testCase.DescribeInvalidity(), Does.Contain(expectation.ToString()));
    }

    [Test]
    public void DescribeInvalidity_ExpectingValuesButNoneGiven_SaysToExpectNoValueInstead()
    {
        var testCase = AttributeFlowCase();
        testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, []);

        Assert.That(testCase.DescribeInvalidity(), Does.Contain("NoValue"));
    }

    [Test]
    public void DescribeInvalidity_ValuesGivenWithAnotherExpectation_IsRefused()
    {
        var testCase = AttributeFlowCase();
        testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.NoValue, ["x"]);

        Assert.That(testCase.DescribeInvalidity(), Does.Contain("only"));
    }

    [Test]
    public void DescribeInvalidity_InOrderWithAnotherExpectation_IsRefused()
    {
        var testCase = AttributeFlowCase();
        testCase.Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.AnyValue, [], InOrder: true);

        Assert.That(testCase.DescribeInvalidity(), Does.Contain("order"));
    }

    [Test]
    public void DescribeInvalidity_FlowInputWithNoSource_SaysWhichSideToRead()
    {
        var testCase = AttributeFlowCase();
        testCase.Inputs = [new ConfigurationTestInput(null, "sn", ["Bloggs"])];

        Assert.That(testCase.DescribeInvalidity(), Does.Contain("sn").And.Contain("cs").And.Contain("mv"));
    }

    [Test]
    public void DescribeInvalidity_ScopeInputWithASource_IsRefused()
    {
        var testCase = ScopeCase();
        testCase.Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "department", ["Sales"])];

        Assert.That(testCase.DescribeInvalidity(), Does.Contain("department"));
    }

    [Test]
    public void DescribeInvalidity_SameInputTwiceInDifferentCase_IsRefused()
    {
        var testCase = AttributeFlowCase();
        testCase.Inputs =
        [
            new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", ["Bloggs"]),
            new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "SN", ["Smith"])
        ];

        Assert.That(testCase.DescribeInvalidity(), Does.Contain("more than once"));
    }

    [Test]
    public void DescribeInvalidity_SameNameOnBothSides_IsWellFormed()
    {
        var testCase = AttributeFlowCase();
        testCase.Inputs =
        [
            new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "sn", ["Bloggs"]),
            new ConfigurationTestInput(ExpressionInputSource.Metaverse, "sn", ["Smith"])
        ];

        Assert.That(testCase.DescribeInvalidity(), Is.Null);
    }

    [Test]
    public void DescribeInvalidity_InputWithNoAttributeName_IsRefused()
    {
        var testCase = AttributeFlowCase();
        testCase.Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, " ", ["x"])];

        Assert.That(testCase.DescribeInvalidity(), Does.Contain("attribute name"));
    }

    [TestCase(ConfigurationTestCaseKind.AttributeFlow, true, false, false)]
    [TestCase(ConfigurationTestCaseKind.GeneratedValue, true, false, false)]
    [TestCase(ConfigurationTestCaseKind.Scope, false, true, false)]
    [TestCase(ConfigurationTestCaseKind.ObjectMatchingRule, false, false, true)]
    public void Covers_EachKind_NamesTheOneReferenceItUses(ConfigurationTestCaseKind kind, bool mapping, bool rule, bool matching)
    {
        var covered = ConfigurationTestCase.CoveredReference(kind);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(covered == ConfigurationTestCaseCoverage.AttributeFlow, Is.EqualTo(mapping));
            Assert.That(covered == ConfigurationTestCaseCoverage.SynchronisationRule, Is.EqualTo(rule));
            Assert.That(covered == ConfigurationTestCaseCoverage.ObjectMatchingRule, Is.EqualTo(matching));
        }
    }

    private static ConfigurationTestCase AttributeFlowCase() => new()
    {
        Name = "Hire date formats as ISO",
        Kind = ConfigurationTestCaseKind.AttributeFlow,
        SyncRuleMappingId = 3,
        Inputs = [new ConfigurationTestInput(ExpressionInputSource.ConnectedSystem, "hireDate", ["2026-03-02"])],
        Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["2026-03-02"])
    };

    private static ConfigurationTestCase GeneratedValueCase() => new()
    {
        Name = "Account name base",
        Kind = ConfigurationTestCaseKind.GeneratedValue,
        SyncRuleMappingId = 4,
        Inputs = [new ConfigurationTestInput(ExpressionInputSource.Metaverse, "First Name", ["Joe"])],
        Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["joe"])
    };

    private static ConfigurationTestCase ScopeCase() => new()
    {
        Name = "Sales staff are in scope",
        Kind = ConfigurationTestCaseKind.Scope,
        SyncRuleId = 7,
        Inputs = [new ConfigurationTestInput(null, "department", ["Sales"])],
        Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.InScope)
    };

    private static ConfigurationTestCase MatchingCase() => new()
    {
        Name = "Matches on employee number",
        Kind = ConfigurationTestCaseKind.ObjectMatchingRule,
        ObjectMatchingRuleId = 9,
        Inputs = [new ConfigurationTestInput(null, "employeeNumber", ["00123"])],
        Expectation = new ConfigurationTestExpectation(ConfigurationTestExpectationKind.Values, ["00123"])
    };
}

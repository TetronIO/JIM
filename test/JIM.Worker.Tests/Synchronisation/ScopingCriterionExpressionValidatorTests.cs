// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using NUnit.Framework;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// What saving refuses about an expression scoping criterion (#2054): anything that would fail for every object, so an
/// administrator learns at save time rather than from a run that errors every object it meets.
/// </summary>
[TestFixture]
public class ScopingCriterionExpressionValidatorTests
{
    private static readonly string[] PersonAttributes = ["department", "startDate", "employeeType"];
    private readonly ScopingCriterionExpressionValidator _validator = new(new DynamicExpressoEvaluator());

    [Test]
    public void DescribeProblem_WellFormedImportExpression_IsNull()
    {
        var problem = Describe("Lower(Trim(cs[\"DEPARTMENT\"]))");

        Assert.That(problem, Is.Null, "attribute names in expressions ignore case");
    }

    [Test]
    public void DescribeProblem_AttributeCriterion_IsNull()
    {
        var criterion = new SyncRuleScopingCriteria { ConnectedSystemAttributeId = 1, ComparisonType = SearchComparisonType.Equals, StringValue = "x" };

        Assert.That(_validator.DescribeProblem(criterion, SyncRuleDirection.Import, PersonAttributes, "person"), Is.Null);
    }

    [Test]
    public void DescribeProblem_NoCompareAsType_IsRefused()
    {
        var problem = Describe("cs[\"department\"]", compareAs: null);

        Assert.That(problem, Does.Contain("Compare As"));
    }

    [TestCase(AttributeDataType.Reference)]
    [TestCase(AttributeDataType.Binary)]
    [TestCase(AttributeDataType.NotSet)]
    public void DescribeProblem_CompareAsATypeScopingCannotCompare_IsRefused(AttributeDataType compareAs)
    {
        var problem = Describe("cs[\"department\"]", compareAs: compareAs);

        Assert.That(problem, Does.Contain("Compare As"));
    }

    [Test]
    public void DescribeProblem_ExpressionAndAttributeBothSet_IsRefused()
    {
        var criterion = Criterion("cs[\"department\"]");
        criterion.ConnectedSystemAttributeId = 1;

        var problem = _validator.DescribeProblem(criterion, SyncRuleDirection.Import, PersonAttributes, "person");

        Assert.That(problem, Does.Contain("either an attribute or an expression"));
    }

    [Test]
    public void DescribeProblem_ExpressionThatDoesNotParse_IsRefused()
    {
        Assert.That(Describe("Lower(cs[\"department\"]"), Does.Contain("does not parse"));
    }

    [Test]
    public void DescribeProblem_ExpressionLongerThanTheLimit_IsRefused()
    {
        var expression = "cs[\"department\"] + \"" + new string('x', DynamicExpressoEvaluator.MaxExpressionLength) + "\"";

        Assert.That(Describe(expression), Does.Contain("at most"));
    }

    [Test]
    public void DescribeProblem_ImportExpressionReadingTheMetaverse_IsRefused()
    {
        var problem = Describe("mv[\"Department\"]");

        Assert.That(problem, Does.Contain("mv[\"Department\"]").And.Contain("import"));
    }

    [Test]
    public void DescribeProblem_ExportExpressionReadingTheConnectedSystem_IsRefused()
    {
        var problem = _validator.DescribeProblem(Criterion("cs[\"department\"]"), SyncRuleDirection.Export, ["Department"], "Person");

        Assert.That(problem, Does.Contain("cs[\"department\"]").And.Contain("export"));
    }

    [Test]
    public void DescribeProblem_ExpressionReadingNoAttribute_IsRefused()
    {
        Assert.That(Describe("\"Finance\""), Does.Contain("reads no attribute"));
    }

    [Test]
    public void DescribeProblem_ExpressionReadingAnAttributeTheTypeDoesNotHave_IsRefused()
    {
        Assert.That(Describe("cs[\"departmnet\"]"), Does.Contain("cs[\"departmnet\"]").And.Contain("person"));
    }

    [TestCase("Now()")]
    [TestCase("DateTime.UtcNow")]
    public void DescribeProblem_ExpressionReadingTheClock_IsRefusedAndPointsAtRelativeDates(string clock)
    {
        var problem = Describe($"ParseDate(cs[\"startDate\"], \"yyyy-MM-dd\") < {clock}", compareAs: AttributeDataType.Boolean);

        Assert.That(problem, Does.Contain("relative date"));
    }

    private string? Describe(string expression, AttributeDataType? compareAs = AttributeDataType.Text) =>
        _validator.DescribeProblem(Criterion(expression, compareAs), SyncRuleDirection.Import, PersonAttributes, "person");

    private static SyncRuleScopingCriteria Criterion(string expression, AttributeDataType? compareAs = AttributeDataType.Text) => new()
    {
        Expression = expression,
        CompareAs = compareAs,
        ComparisonType = SearchComparisonType.HasValue
    };
}

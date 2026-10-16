// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// What an Object Matching Rule compares is checked when the rule is saved (#2052), so a rule the matching engine
/// would fail for every object is refused with a reason instead. Each message names what is wrong in the terms an
/// administrator would use, and where there is a better way to get what they wanted, says so.
/// </summary>
[TestFixture]
public class ObjectMatchingRuleValidatorTests
{
    private ObjectMatchingRuleValidator _validator = null!;
    private ConnectedSystemObjectType _objectType = null!;
    private MetaverseAttribute _employeeId = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new ObjectMatchingRuleValidator(new DynamicExpressoEvaluator());
        _objectType = new ConnectedSystemObjectType
        {
            Id = 7,
            Name = "user",
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = 101, Name = "employeeId", Type = AttributeDataType.Text },
                new ConnectedSystemObjectTypeAttribute { Id = 102, Name = "employeeNumber", Type = AttributeDataType.Number },
                new ConnectedSystemObjectTypeAttribute
                {
                    Id = 103, Name = "proxyAddresses", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued
                },
                new ConnectedSystemObjectTypeAttribute { Id = 104, Name = "company", Type = AttributeDataType.Text }
            ]
        };
        _employeeId = new MetaverseAttribute { Id = 201, Name = "Employee ID", Type = AttributeDataType.Text };
    }

    [Test]
    public void DescribeProblem_AttributeOfTheSameType_IsWorkable()
    {
        Assert.That(_validator.DescribeProblem(AttributeRule(101), _objectType, _employeeId), Is.Null);
    }

    [Test]
    public void DescribeProblem_MultiValuedAttribute_IsWorkable()
    {
        // Every value is looked up now (#2052), so a multi-valued source is no longer a rule that can only ever
        // compare its first value.
        Assert.That(_validator.DescribeProblem(AttributeRule(103), _objectType, _employeeId), Is.Null);
    }

    [Test]
    public void DescribeProblem_AttributeOfADifferentType_NamesBothTypesAndOffersAnExpression()
    {
        var problem = _validator.DescribeProblem(AttributeRule(102), _objectType, _employeeId);

        Assert.That(problem, Does.Contain("'employeeNumber' is Number")
            .And.Contain("'Employee ID' is Text")
            .And.Contain("expression"));
    }

    [Test]
    public void DescribeProblem_AttributeNotOnTheObjectType_SaysSo()
    {
        var problem = _validator.DescribeProblem(AttributeRule(999), _objectType, _employeeId);

        Assert.That(problem, Does.Contain("999").And.Contain("'user'"));
    }

    [Test]
    public void DescribeProblem_AttributeLoadedAsANavigationFromAnotherObjectType_SaysSo()
    {
        var rule = AttributeRule(999);
        rule.Sources[0].ConnectedSystemAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Id = 999, Name = "groupType", Type = AttributeDataType.Text
        };

        Assert.That(_validator.DescribeProblem(rule, _objectType, _employeeId), Does.Contain("'groupType'").And.Contain("'user'"));
    }

    [TestCase(AttributeDataType.Boolean)]
    [TestCase(AttributeDataType.DateTime)]
    [TestCase(AttributeDataType.Reference)]
    [TestCase(AttributeDataType.Binary)]
    public void DescribeProblem_MetaverseAttributeOfAnUnmatchableType_NamesTheTypesThatCanBeMatched(AttributeDataType type)
    {
        var target = new MetaverseAttribute { Id = 202, Name = "Start Date", Type = type };

        var problem = _validator.DescribeProblem(ExpressionRule("cs[\"employeeId\"]"), _objectType, target);

        Assert.That(problem, Does.Contain("'Start Date'").And.Contain(type.ToString()).And.Contain("Text"));
    }

    [Test]
    public void DescribeProblem_ExpressionReadingAConnectedSystemAttribute_IsWorkable()
    {
        var rule = ExpressionRule("\"E\" + Trim(cs[\"employeeId\"])");

        Assert.That(_validator.DescribeProblem(rule, _objectType, _employeeId), Is.Null);
    }

    [Test]
    public void DescribeProblem_ExpressionNamingAnAttributeInADifferentCase_IsWorkable()
    {
        // Matching expressions read their inputs case-insensitively, exactly as Attribute Flow does.
        Assert.That(_validator.DescribeProblem(ExpressionRule("cs[\"EMPLOYEEID\"]"), _objectType, _employeeId), Is.Null);
    }

    [Test]
    public void DescribeProblem_ExpressionOverTheLengthLimit_NamesTheLimit()
    {
        var expression = "cs[\"employeeId\"] + \"" + new string('x', DynamicExpressoEvaluator.MaxExpressionLength) + "\"";

        var problem = _validator.DescribeProblem(ExpressionRule(expression), _objectType, _employeeId);

        Assert.That(problem, Does.Contain(DynamicExpressoEvaluator.MaxExpressionLength.ToString("N0")));
    }

    [Test]
    public void DescribeProblem_ExpressionThatDoesNotParse_SaysSo()
    {
        var problem = _validator.DescribeProblem(ExpressionRule("cs[\"employeeId\"] +"), _objectType, _employeeId);

        Assert.That(problem, Does.StartWith("The expression does not parse"));
    }

    [Test]
    public void DescribeProblem_ExpressionReadingTheMetaverse_NamesTheAccessorAndOffersADerivedAttribute()
    {
        var problem = _validator.DescribeProblem(
            ExpressionRule("Trim(cs[\"employeeId\"]) + Trim(mv[\"Department\"])"), _objectType, _employeeId);

        Assert.That(problem, Does.Contain("mv[\"Department\"]").And.Contain("derive"));
    }

    [Test]
    public void DescribeProblem_ExpressionReadingNoConnectedSystemAttribute_SaysEveryObjectWouldMatchTheSameIdentity()
    {
        var problem = _validator.DescribeProblem(ExpressionRule("\"E000001\""), _objectType, _employeeId);

        Assert.That(problem, Does.Contain("reads no Connected System attribute"));
    }

    [Test]
    public void DescribeProblem_ExpressionReadingAnAttributeNotOnTheObjectType_NamesItAndTheType()
    {
        var problem = _validator.DescribeProblem(ExpressionRule("Trim(cs[\"employeeId\"]) + Trim(cs[\"costCentre\"])"), _objectType, _employeeId);

        Assert.That(problem, Does.Contain("cs[\"costCentre\"]").And.Contain("'user'"));
    }

    [Test]
    public void DescribeProblem_ExpressionCallingANonRepeatableFunction_NamesTheCall()
    {
        var problem = _validator.DescribeProblem(ExpressionRule("Trim(cs[\"employeeId\"]) + Now().Year"), _objectType, _employeeId);

        Assert.That(problem, Does.Contain("Now()"));
    }

    [Test]
    public void DescribeProblem_RuleOfTheWrongShape_ReportsTheShapeFirst()
    {
        var rule = AttributeRule(101);
        rule.Sources.Add(new ObjectMatchingRuleSource { Order = 1, ConnectedSystemAttributeId = 104 });

        Assert.That(_validator.DescribeProblem(rule, _objectType, _employeeId), Does.Contain("exactly one source"));
    }

    private static ObjectMatchingRule AttributeRule(int attributeId) => new()
    {
        TargetMetaverseAttributeId = 201,
        Sources = [new ObjectMatchingRuleSource { Order = 0, ConnectedSystemAttributeId = attributeId }]
    };

    private static ObjectMatchingRule ExpressionRule(string expression) => new()
    {
        TargetMetaverseAttributeId = 201,
        Sources = [new ObjectMatchingRuleSource { Order = 0, Expression = expression }]
    };
}

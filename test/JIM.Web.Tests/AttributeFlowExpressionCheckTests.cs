// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The Attribute Flow dialog's live check (#2055 Phase 3): it refuses, and says why under the field, every expression
/// the save would refuse for a new or changed mapping, from the same validator the application layer runs. A warning
/// (a non-repeatable function) never blocks the dialog.
/// </summary>
[TestFixture]
public class AttributeFlowExpressionCheckTests
{
    private readonly DynamicExpressoEvaluator _evaluator = new();
    private SyncRule _importRule = null!;
    private SyncRuleMapping _mapping = null!;

    [SetUp]
    public void SetUp()
    {
        var hireDate = new MetaverseAttribute { Id = 2, Name = "Hire Date", Type = AttributeDataType.DateTime };
        _importRule = new SyncRule
        {
            Direction = SyncRuleDirection.Import,
            ConnectedSystemObjectType = new ConnectedSystemObjectType
            {
                Name = "user",
                Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "sn", Type = AttributeDataType.Text }]
            },
            MetaverseObjectType = new MetaverseObjectType { Name = "Person", Attributes = [hireDate] }
        };
        _mapping = new SyncRuleMapping { TargetMetaverseAttribute = hireDate, TargetMetaverseAttributeId = hireDate.Id };
    }

    private string? Check(string? expression) =>
        ExpressionSyntaxCheck.ErrorForAttributeFlow(_evaluator, expression, _mapping, _importRule);

    [Test]
    public void ErrorForAttributeFlow_NoExpression_IsNoError()
    {
        Assert.That(Check(" "), Is.Null);
    }

    [Test]
    public void ErrorForAttributeFlow_AWorkableExpression_IsNoError()
    {
        Assert.That(Check("ParseDate(cs[\"sn\"], \"yyyy-MM-dd\")"), Is.Null);
    }

    [Test]
    public void ErrorForAttributeFlow_AFunctionInTheWrongCase_KeepsTheSuggestion()
    {
        Assert.That(Check("lower(cs[\"sn\"])"), Does.Contain("did you mean Lower?"));
    }

    [Test]
    public void ErrorForAttributeFlow_AnAttributeTheObjectTypeDoesNotHave_SaysSo()
    {
        Assert.That(Check("ParseDate(cs[\"surname\"], \"yyyy-MM-dd\")"), Does.Contain("cs[\"surname\"]"));
    }

    [Test]
    public void ErrorForAttributeFlow_AResultTheTargetCanNeverHold_SaysSo()
    {
        Assert.That(Check("HasValue(cs[\"sn\"])"), Does.Contain("Hire Date"));
    }

    [Test]
    public void ErrorForAttributeFlow_ANonRepeatableFunction_DoesNotBlock()
    {
        Assert.That(Check("Now()"), Is.Null);
    }

    [Test]
    public void ErrorForAttributeFlow_TypesWithNoAttributesLoaded_DoNotMakeEveryInputUnknown()
    {
        // The page may hold a type without its attributes; that is not knowledge that the type has none.
        _importRule.ConnectedSystemObjectType!.Attributes = [];

        Assert.That(Check("ParseDate(cs[\"surname\"], \"yyyy-MM-dd\")"), Is.Null);
    }
}

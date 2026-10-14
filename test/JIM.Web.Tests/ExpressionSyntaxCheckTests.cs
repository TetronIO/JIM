// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// <see cref="ExpressionSyntaxCheck"/> (#2053): the portal refuses an expression that does not parse, an unknown function
/// included, when an Attribute Flow is saved, exactly as the REST API does, and says why under the field as it is typed.
/// </summary>
[TestFixture]
public class ExpressionSyntaxCheckTests
{
    private readonly DynamicExpressoEvaluator _evaluator = new();

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ErrorFor_NoExpression_IsNoError(string? expression)
    {
        Assert.That(ExpressionSyntaxCheck.ErrorFor(_evaluator, expression), Is.Null);
    }

    [Test]
    public void ErrorFor_AValidExpression_IsNoError()
    {
        Assert.That(ExpressionSyntaxCheck.ErrorFor(_evaluator, "Lower(cs[\"mail\"])"), Is.Null);
    }

    [Test]
    public void ErrorFor_AnUnknownFunction_SaysSo()
    {
        Assert.That(ExpressionSyntaxCheck.ErrorFor(_evaluator, "Lowercase(cs[\"mail\"])"), Does.Contain("Lowercase"));
    }

    [Test]
    public void ErrorFor_AFunctionNameInTheWrongCase_SuggestsTheRealName()
    {
        Assert.That(ExpressionSyntaxCheck.ErrorFor(_evaluator, "lower(cs[\"mail\"])"),
            Does.Contain("Function names are case-sensitive").And.Contain("Lower"));
    }

    [Test]
    public void ErrorFor_ASyntaxError_SaysSo()
    {
        Assert.That(ExpressionSyntaxCheck.ErrorFor(_evaluator, "Lower(cs[\"mail\"]"), Is.Not.Null);
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using NUnit.Framework;

namespace JIM.Worker.Tests.Expressions;

/// <summary>
/// Verifies the headline example data generation expression scenarios from issue #549 evaluate correctly through the
/// shared DynamicExpresso evaluator, using a Metaverse-attribute-only context like the one ExampleDataServer builds
/// during generation (there is no Connected System Object during generation, so the cs accessor is empty).
/// </summary>
[TestFixture]
public class ExampleDataExpressionTests
{
    private IExpressionEvaluator _evaluator = null!;

    [SetUp]
    public void SetUp()
    {
        _evaluator = new DynamicExpressoEvaluator();
    }

    [Test]
    public void CompanyDerivedEmail_TransformsAndStripsSpaces()
    {
        var context = new ExpressionContext(new Dictionary<string, object?>
        {
            { "First Name", "Ada" },
            { "Last Name", "Lovelace" },
            { "Company", "Stark Industries" }
        });

        const string expression = "Lower(mv[\"First Name\"]) + \".\" + Lower(mv[\"Last Name\"]) + \"@\" + Lower(Replace(mv[\"Company\"], \" \", \"\")) + \".io\"";

        var result = _evaluator.Test(expression, context);

        Assert.That(result.IsValid, Is.True, result.ErrorMessage);
        Assert.That(result.Result?.ToString(), Is.EqualTo("ada.lovelace@starkindustries.io"));
    }

    [Test]
    public void Expression_CanReferenceAValueDerivedFromAnotherExpression()
    {
        // Simulates the chained case: Login was generated first (by its own expression), Email derives from it.
        var context = new ExpressionContext(new Dictionary<string, object?>
        {
            { "Login", "ada.lovelace" }
        });

        var result = _evaluator.Test("mv[\"Login\"] + \"@example.io\"", context);

        Assert.That(result.IsValid, Is.True, result.ErrorMessage);
        Assert.That(result.Result?.ToString(), Is.EqualTo("ada.lovelace@example.io"));
    }

    [Test]
    public void Expression_AttributeNameLookupIsCaseInsensitive()
    {
        // ExampleDataServer keys the mv dictionary case-insensitively; confirm a differently-cased reference resolves.
        var context = new ExpressionContext(new Dictionary<string, object?>
        {
            { "First Name", "Grace" }
        });

        var result = _evaluator.Test("Upper(mv[\"first name\"])", context);

        Assert.That(result.IsValid, Is.True, result.ErrorMessage);
        Assert.That(result.Result?.ToString(), Is.EqualTo("GRACE"));
    }
}

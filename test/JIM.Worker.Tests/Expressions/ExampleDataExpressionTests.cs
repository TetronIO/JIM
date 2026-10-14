// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.ExampleData;
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

    private static ExampleDataTemplateAttribute TemplateAttribute(string expression) => new()
    {
        MetaverseAttribute = new MetaverseAttribute { Id = 1, Name = "Display Name", Type = AttributeDataType.Text },
        Expression = expression
    };

    private static MetaverseObject GeneratedObject(DateTime? hired = null)
    {
        var mvo = new MetaverseObject();
        if (hired != null)
        {
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
            {
                Attribute = new MetaverseAttribute { Id = 2, Name = "Hire Date", Type = AttributeDataType.DateTime },
                DateTimeValue = hired
            });
        }

        return mvo;
    }

    [Test]
    public void EvaluateAttributeExpression_ExpressionReturnsNull_GivesNoValue()
    {
        // No value, not an empty one (#2179): the object is generated without the attribute.
        var value = ExampleDataServer.EvaluateAttributeExpression(_evaluator, GeneratedObject(), TemplateAttribute("mv[\"Nickname\"]"));

        Assert.That(value, Is.Null);
    }

    [Test]
    public void EvaluateAttributeExpression_ExpressionReturnsSeveralValues_FailsTheGeneration()
    {
        Assert.That(() => ExampleDataServer.EvaluateAttributeExpression(_evaluator, GeneratedObject(), TemplateAttribute("Split(\"a;b\", \";\")")),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("several values"),
            "several values must never be written as the list's type name");
    }

    [Test]
    public void EvaluateAttributeExpression_ExpressionReturnsADate_WritesJimsDateTextForm()
    {
        var value = ExampleDataServer.EvaluateAttributeExpression(_evaluator,
            GeneratedObject(new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc)), TemplateAttribute("mv[\"Hire Date\"]"));

        Assert.That(value, Is.EqualTo("2026-03-15 10:30:00Z"));
    }
}

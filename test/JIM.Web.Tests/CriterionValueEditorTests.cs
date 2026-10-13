// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Bunit;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers what <see cref="CriterionValueEditor"/> asks for under each operator (#2053): nothing for "has a value" and
/// "has no value", which compare against no value, and a pattern, checked as it is typed, for the pattern operators.
/// </summary>
[TestFixture]
public class CriterionValueEditorTests : JimComponentTestContext
{
    private IRenderedComponent<CriterionValueEditor> RenderEditor(SearchComparisonType op, AttributeDataType type, string? value = null) =>
        Render<CriterionValueEditor>(p => p
            .Add(c => c.Criterion, new CriterionValues { ComparisonType = op, StringValue = value })
            .Add(c => c.AttributeType, type));

    [TestCase(SearchComparisonType.HasValue, AttributeDataType.Text)]
    [TestCase(SearchComparisonType.HasNoValue, AttributeDataType.Number)]
    [TestCase(SearchComparisonType.HasNoValue, AttributeDataType.DateTime)]
    public void PresenceOperator_AsksForNoValue(SearchComparisonType op, AttributeDataType type)
    {
        var cut = RenderEditor(op, type);

        Assert.That(cut.Markup.Trim(), Is.Empty);
    }

    [Test]
    public void NoOperatorChosenYet_StillShowsTheValueInput()
    {
        var cut = RenderEditor(SearchComparisonType.NotSet, AttributeDataType.Text);

        Assert.That(cut.FindComponent<MudTextField<string>>().Instance.Label, Is.EqualTo("Text Value"));
    }

    [Test]
    public void PatternOperator_AsksForAPattern()
    {
        var cut = RenderEditor(SearchComparisonType.MatchesPattern, AttributeDataType.Text);

        Assert.That(cut.FindComponent<MudTextField<string>>().Instance.Label, Is.EqualTo("Pattern"));
    }

    [Test]
    public void PatternOperator_InvalidPattern_ShowsWhyBeforeSaving()
    {
        var cut = RenderEditor(SearchComparisonType.NotMatchesPattern, AttributeDataType.Text, "[a-z");

        Assert.That(cut.Markup, Does.Contain("The pattern is not a valid regular expression"));
    }

    [Test]
    public void PatternOperator_ValidPattern_ShowsNoError()
    {
        var cut = RenderEditor(SearchComparisonType.MatchesPattern, AttributeDataType.Text, @"^[a-z.]+@example\.org$");

        Assert.That(cut.Markup, Does.Not.Contain("not a valid regular expression"));
    }
}

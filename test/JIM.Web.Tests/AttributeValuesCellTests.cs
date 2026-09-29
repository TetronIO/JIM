// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using Bunit;
using JIM.Web.Shared;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Pins the one decision <see cref="AttributeValuesCell{TValue}"/> exists to make the same way on every attribute
/// table: a handful of values reads inline, stacked in the cell, and anything beyond the inline limit is handed to
/// the host's nested virtualised table rather than dumped into the row or hidden behind a dialog.
/// </summary>
[TestFixture]
public class AttributeValuesCellTests : JimComponentTestContext
{
    private IRenderedComponent<AttributeValuesCell<string>> RenderCell(IReadOnlyList<string> values, int? totalCount = null) =>
        Render<AttributeValuesCell<string>>(p => p
            .Add(c => c.Values, values)
            .Add(c => c.TotalCount, totalCount)
            .Add(c => c.ValueTemplate, value => $"<span class=\"test-value\">{value}</span>")
            .Add(c => c.OverflowContent, "<div class=\"test-overflow\">nested table</div>"));

    private static List<string> Values(int count) =>
        Enumerable.Range(0, count).Select(i => $"value-{i:D3}").ToList();

    [Test]
    public void AttributeValuesCell_OneValue_RendersItWithoutAStack()
    {
        var cut = RenderCell(Values(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".test-value"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(".jim-attr-expanded"), Is.Empty,
                "a single value is plain text in the cell, as a single-valued attribute's is");
            Assert.That(cut.FindAll(".test-overflow"), Is.Empty);
        }
    }

    [Test]
    public void AttributeValuesCell_SeveralValuesWithinTheLimit_StacksEveryOneInline()
    {
        var cut = RenderCell(Values(4));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".jim-attr-expanded .test-value").Select(e => e.TextContent),
                Is.EqualTo(Values(4)), "every value reads in the cell, in the order it was given");
            Assert.That(cut.FindAll(".test-overflow"), Is.Empty,
                "a handful of values needs no table of its own, and certainly no dialog");
        }
    }

    [Test]
    public void AttributeValuesCell_ExactlyTheLimit_StillStacksInline()
    {
        var cut = RenderCell(Values(AttributeValuesCell<string>.InlineLimit));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".test-value"), Has.Count.EqualTo(AttributeValuesCell<string>.InlineLimit));
            Assert.That(cut.FindAll(".test-overflow"), Is.Empty);
        }
    }

    [Test]
    public void AttributeValuesCell_MoreThanTheLimit_HandsTheValuesToTheNestedTable()
    {
        // The page's detail load caps each multi-valued attribute at the inline limit, so a group with 500 members
        // arrives with 10 loaded and a total of 500; the nested table reads the rest from the server as it scrolls.
        var cut = RenderCell(Values(10), totalCount: 500);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".test-overflow"), Has.Count.EqualTo(1));
            Assert.That(cut.FindAll(".test-value"), Is.Empty,
                "the loaded values are a sample of the set, so stacking them would present part of it as the whole");
        }
    }

    [Test]
    public void AttributeValuesCell_FewerLoadedThanTheTotal_HandsTheValuesToTheNestedTableEvenWithinTheLimit()
    {
        var cut = RenderCell(Values(3), totalCount: 5);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".test-overflow"), Has.Count.EqualTo(1),
                "only the nested table can reach values the page did not load");
            Assert.That(cut.FindAll(".test-value"), Is.Empty);
        }
    }
}

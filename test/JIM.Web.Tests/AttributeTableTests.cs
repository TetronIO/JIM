// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers <see cref="AttributeTable{TItem}"/>, the shell every table of one object's attributes renders through.
/// Its rows are bounded by the object's schema, so it is a plain table that draws every row (which is what lets a
/// row grow to hold its values inline), and it filters and sorts the loaded rows in memory while keeping the same
/// search and sort deep links the virtualised grid it replaced wrote.
/// </summary>
[TestFixture]
public class AttributeTableTests : JimComponentTestContext
{
    private const string Prefix = "attr-";

    public sealed record Row(string Name, string Value);

    private NavigationManager _navigation = null!;

    [SetUp]
    public void SetUp()
    {
        _navigation = Services.GetRequiredService<NavigationManager>();
    }

    private static List<Row> Rows(int count) =>
        Enumerable.Range(0, count).Select(i => new Row($"row-{i:D2}", $"value-{i:D2}")).ToList();

    private static bool Matches(Row row, string search) =>
        row.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        row.Value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<Row> Sort(IEnumerable<Row> rows, string sortBy, bool descending)
    {
        Func<Row, string> key = sortBy == "value" ? r => r.Value : r => r.Name;
        return descending
            ? rows.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(key, StringComparer.OrdinalIgnoreCase);
    }

    private IRenderedComponent<AttributeTable<Row>> RenderTable(IReadOnlyList<Row> rows, string query = "")
    {
        _navigation.NavigateTo($"/test{query}");
        return Render<AttributeTable<Row>>(p => p
            .Add(c => c.Items, rows)
            .Add(c => c.Matches, Matches)
            .Add(c => c.Sort, Sort)
            .Add(c => c.DefaultSortBy, "name")
            .Add(c => c.UrlParameterPrefix, Prefix)
            .Add(c => c.SingularName, "Attribute")
            .Add(c => c.PluralName, "Attributes")
            .Add(c => c.HeaderContent, "<th>Name</th><th>Value</th>")
            .Add(c => c.RowTemplate, row => $"<td class=\"test-name\">{row.Name}</td><td>{row.Value}</td>")
            .Add(c => c.EmptyContent, "<p class=\"test-empty\">This object has no attributes</p>"));
    }

    private static IEnumerable<string> RenderedNames(IRenderedComponent<AttributeTable<Row>> cut) =>
        cut.FindAll(".test-name").Select(e => e.TextContent);

    [Test]
    public void AttributeTable_RendersEveryRow_WithNoPagerAndNoVirtualisation()
    {
        var cut = RenderTable(Rows(30));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".test-name"), Has.Count.EqualTo(30),
                "an object's attributes are bounded by its schema, so every row is drawn");
            Assert.That(cut.HasComponent<MudBlazor.MudTablePager>(), Is.False);
            Assert.That(cut.HasComponent<VirtualisedDataGrid<Row>>(), Is.False,
                "a virtualised grid fixes every row to one line, which is what kept values out of the row");
        }
    }

    [Test]
    public void AttributeTable_OpensSortedByTheDefaultColumn()
    {
        var rows = Rows(3);
        rows.Reverse();

        var cut = RenderTable(rows);

        Assert.That(RenderedNames(cut), Is.EqualTo(new[] { "row-00", "row-01", "row-02" }));
    }

    [Test]
    public void AttributeTable_ToggleSort_SortsByTheColumnThenFlipsItsDirection()
    {
        var cut = RenderTable(Rows(3));

        cut.InvokeAsync(() => cut.Instance.ToggleSortAsync("name"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Instance.SortDescending, Is.True, "sorting by the active column flips its direction");
            Assert.That(RenderedNames(cut), Is.EqualTo(new[] { "row-02", "row-01", "row-00" }));
        }

        cut.InvokeAsync(() => cut.Instance.ToggleSortAsync("value"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Instance.SortBy, Is.EqualTo("value"));
            Assert.That(cut.Instance.SortDescending, Is.False, "a newly chosen column starts ascending");
        }
    }

    [Test]
    public void AttributeTable_Search_NarrowsTheRowsAndCountsThemAgainstTheWhole()
    {
        var cut = RenderTable(Rows(12));

        cut.InvokeAsync(() => cut.Instance.SetSearchAsync("VALUE-1"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(RenderedNames(cut), Is.EqualTo(new[] { "row-10", "row-11" }),
                "the page's predicate decides what matches; here it matches values, case-insensitively");
            Assert.That(cut.Markup, Does.Contain("2 of 12"),
                "while a search narrows the table the count says how much of the whole it is showing");
        }
    }

    [Test]
    public void AttributeTable_SearchInTheUrl_IsAppliedOnArrival()
    {
        var cut = RenderTable(Rows(12), $"?{Prefix}q=row-03");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Instance.SearchText, Is.EqualTo("row-03"));
            Assert.That(RenderedNames(cut), Is.EqualTo(new[] { "row-03" }));
        }
    }

    [Test]
    public void AttributeTable_SortInTheUrl_IsAppliedOnArrival()
    {
        var cut = RenderTable(Rows(3), $"?{Prefix}sort=value&{Prefix}desc=1");

        Assert.That(RenderedNames(cut), Is.EqualTo(new[] { "row-02", "row-01", "row-00" }));
    }

    [Test]
    public void AttributeTable_SearchThatMatchesNothing_SaysSoAndClearingItBringsTheRowsBack()
    {
        var cut = RenderTable(Rows(3));
        cut.InvokeAsync(() => cut.Instance.SetSearchAsync("nothing-matches-this"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("No attributes match \"nothing-matches-this\""));
            Assert.That(cut.FindAll(".test-empty"), Is.Empty,
                "the object has attributes; saying it has none would be false");
        }

        cut.FindAll("button").Single(b => b.TextContent.Contains("Clear Search")).Click();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Instance.SearchText, Is.Empty);
            Assert.That(cut.FindAll(".test-name"), Has.Count.EqualTo(3));
        }
    }

    [Test]
    public void AttributeTable_WithNoRows_ShowsThePagesOwnEmptyStateAndNoSearchToClear()
    {
        var cut = RenderTable([]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll(".test-empty"), Has.Count.EqualTo(1));
            Assert.That(cut.Markup, Does.Not.Contain("Clear Search"));
        }
    }
}

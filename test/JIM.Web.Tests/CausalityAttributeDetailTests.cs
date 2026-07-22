// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Web.Causality;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityAttributeDetail"/>: operation filter chips with counts,
/// name-and-value search, the "n of m" indicator, previous-value strike-through and the empty state.
/// </summary>
[TestFixture]
public class CausalityAttributeDetailTests
{
    private static List<CausalityAttributeRow> SampleRows() =>
    [
        new(CausalityAttributeOperation.Set, "Display Name", "Text · Single-valued", "Liam Allen", null),
        new(CausalityAttributeOperation.Set, "Job Title", "Text · Single-valued", "Director", "Analyst"),
        new(CausalityAttributeOperation.Set, "Department", "Text · Single-valued", "Finance", null),
        new(CausalityAttributeOperation.Add, "mail", "Text · Multi-valued", "liam.allen@example.com", null),
        new(CausalityAttributeOperation.Add, "proxyAddresses", "Text · Multi-valued", "smtp:liam@example.com", null),
        new(CausalityAttributeOperation.Remove, "Location", "Text · Single-valued", "Sydney", null)
    ];

    private static IRenderedComponent<CausalityAttributeDetail> RenderDetail(
        BunitContext context, IReadOnlyList<CausalityAttributeRow>? rows = null)
    {
        return context.Render<CausalityAttributeDetail>(ps => ps
            .Add(c => c.Rows, rows ?? SampleRows()));
    }

    [Test]
    public async Task Render_Default_ShowsAllRowsAndTheFullCountAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        Assert.That(cut.FindAll(".attr-row"), Has.Count.EqualTo(6));
        Assert.That(cut.Find(".attr-meta-count").TextContent.Trim(), Is.EqualTo("6 of 6"));
    }

    [Test]
    public async Task Render_FilterChips_ShowOperationCountsAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        var chipLabels = cut.FindAll(".filter-chips button").Select(b => b.TextContent.Trim()).ToList();
        Assert.That(chipLabels, Is.EqualTo(new[] { "All", "Set · 3", "Add · 2", "Remove · 1" }));
    }

    [Test]
    public async Task Render_OperationWithNoRows_OmitsItsFilterChipAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var rows = SampleRows().Where(r => r.Operation != CausalityAttributeOperation.Remove).ToList();

        var cut = RenderDetail(context, rows);

        var chipLabels = cut.FindAll(".filter-chips button").Select(b => b.TextContent.Trim()).ToList();
        Assert.That(chipLabels, Is.EqualTo(new[] { "All", "Set · 3", "Add · 2" }));
    }

    [Test]
    public async Task FilterChip_Click_FiltersRowsAndUpdatesTheCountAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        var addChip = cut.FindAll(".filter-chips button").Single(b => b.TextContent.Trim().StartsWith("Add"));
        addChip.Click();

        Assert.That(cut.FindAll(".attr-row"), Has.Count.EqualTo(2));
        Assert.That(cut.Find(".attr-meta-count").TextContent.Trim(), Is.EqualTo("2 of 6"));
        var activeChip = cut.FindAll(".filter-chips button").Single(b => b.ClassList.Contains("on"));
        Assert.That(activeChip.TextContent.Trim(), Does.StartWith("Add"));
    }

    [Test]
    public async Task Search_NarrowsByNameAndValueAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        // Matches the "mail" attribute name and the proxyAddresses value containing "example.com"
        cut.Find(".attr-search input").Input("example.com");

        Assert.That(cut.FindAll(".attr-row"), Has.Count.EqualTo(2));
        Assert.That(cut.Find(".attr-meta-count").TextContent.Trim(), Is.EqualTo("2 of 6"));

        // Name-only match
        cut.Find(".attr-search input").Input("department");

        var rows = cut.FindAll(".attr-row");
        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].QuerySelector(".attr-name")!.TextContent, Does.Contain("Department"));
    }

    [Test]
    public async Task Search_WithNoMatches_ShowsTheEmptyStateAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        cut.Find(".attr-search input").Input("no-such-attribute");

        Assert.That(cut.FindAll(".attr-row"), Is.Empty);
        Assert.That(cut.Find(".attr-empty").TextContent, Does.Contain("No attributes match"));
        Assert.That(cut.Find(".attr-meta-count").TextContent.Trim(), Is.EqualTo("0 of 6"));
    }

    [Test]
    public async Task Render_RowWithPreviousValue_ShowsTheStruckThroughPreviousValueAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        var jobTitleRow = cut.FindAll(".attr-row")
            .Single(r => r.QuerySelector(".attr-name")!.TextContent.Contains("Job Title"));
        Assert.That(jobTitleRow.QuerySelector(".attr-value .was")!.TextContent, Is.EqualTo("Analyst"));
        Assert.That(jobTitleRow.QuerySelector(".attr-value")!.TextContent, Does.Contain("Director"));
    }

    [Test]
    public async Task Render_Rows_ShowOperationBadgesAndDemotedTypePluralityAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        var firstRow = cut.FindAll(".attr-row")[0];
        var opBadge = firstRow.QuerySelector(".op");
        Assert.That(opBadge, Is.Not.Null);
        Assert.That(opBadge!.ClassList, Does.Contain("set"));
        Assert.That(firstRow.QuerySelector(".attr-name .meta")!.TextContent, Is.EqualTo("Text · Single-valued"));
    }
}

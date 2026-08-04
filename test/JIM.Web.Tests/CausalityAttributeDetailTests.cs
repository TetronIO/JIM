// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Web.Causality;
using JIM.Web.Shared;
using JIM.Web.Shared.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityAttributeDetail"/>: operation filter chips with counts,
/// name-and-value search, the "n of m" indicator, previous-value strike-through and the empty state.
///
/// The attribute rows render through a standard MudTable, so these tests count rows via the
/// <see cref="TextValueDisplay"/> component JIM renders once per row rather than via MudBlazor's
/// generated CSS class names, which are a third party's implementation detail that churns between
/// releases (see test/CLAUDE.md > Blazor component tests).
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

    /// <summary>
    /// The rendered attribute row count: JIM renders exactly one value cell per row, so counting the
    /// value component counts the rows without depending on the table's generated markup.
    /// </summary>
    private static int RenderedRowCount(IRenderedComponent<CausalityAttributeDetail> cut)
    {
        return cut.FindComponents<TextValueDisplay>().Count;
    }

    /// <summary>
    /// A queued deprovision's rows are the target's identifying attribute values, carried on the delete
    /// Pending Export so the Connector can still find the entry; nothing is being written. The operation
    /// column and its filter chips would report every such row as "Set", which is the same claim the
    /// "Target identified by" caption exists to correct, one level further down.
    /// </summary>
    [Test]
    public async Task Render_WithoutOperations_HidesTheChangeColumnAndItsFilterChipsAsync()
    {
        await using var context = CausalityBunitContext.Create();
        var rows = new List<CausalityAttributeRow>
        {
            new(CausalityAttributeOperation.Set, "distinguishedName", "Text · Single-valued",
                "uid=erin.byrne,ou=People,dc=glitterband,dc=local", null)
        };

        var cut = context.Render<CausalityAttributeDetail>(ps => ps
            .Add(c => c.Rows, rows)
            .Add(c => c.ShowOperations, false));

        Assert.That(cut.Markup, Does.Not.Contain("Change"),
            "Nothing is being changed, so the operation column has no honest value to show");
        Assert.That(cut.FindAll(".filter-chips button"), Is.Empty);
        Assert.That(RenderedRowCount(cut), Is.EqualTo(1), "The values themselves must still be shown");
        Assert.That(cut.Markup, Does.Contain("uid=erin.byrne,ou=People,dc=glitterband,dc=local"));
    }

    [Test]
    public async Task Render_Default_ShowsTheChangeColumnAndOperationChipsAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        Assert.That(cut.Markup, Does.Contain("Change"));
        Assert.That(cut.FindAll(".filter-chips button"), Is.Not.Empty);
    }

    [Test]
    public async Task Render_Default_ShowsAllRowsAndTheFullCountAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        Assert.That(RenderedRowCount(cut), Is.EqualTo(6));
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

        Assert.That(RenderedRowCount(cut), Is.EqualTo(2));
        Assert.That(cut.Find(".attr-meta-count").TextContent.Trim(), Is.EqualTo("2 of 6"));
        var activeChip = cut.FindAll(".filter-chips button").Single(b => b.ClassList.Contains("on"));
        Assert.That(activeChip.TextContent.Trim(), Does.StartWith("Add"));

        // The surviving rows really are the Add ones, not merely the right count
        Assert.That(cut.Markup, Does.Contain("mail"));
        Assert.That(cut.Markup, Does.Not.Contain("Display Name"));
    }

    [Test]
    public async Task Search_NarrowsByNameAndValueAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        // Matches the "mail" attribute name and the proxyAddresses value containing "example.com"
        cut.Find(".attr-search input").Input("example.com");

        Assert.That(RenderedRowCount(cut), Is.EqualTo(2));
        Assert.That(cut.Find(".attr-meta-count").TextContent.Trim(), Is.EqualTo("2 of 6"));

        // Name-only match
        cut.Find(".attr-search input").Input("department");

        Assert.That(RenderedRowCount(cut), Is.EqualTo(1));
        Assert.That(cut.Markup, Does.Contain("Department"));
        Assert.That(cut.Markup, Does.Not.Contain("Job Title"));
    }

    [Test]
    public async Task Search_WithNoMatches_ShowsTheEmptyStateAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        cut.Find(".attr-search input").Input("no-such-attribute");

        Assert.That(RenderedRowCount(cut), Is.Zero);
        Assert.That(cut.Markup, Does.Contain("No attributes match"));
        Assert.That(cut.Find(".attr-meta-count").TextContent.Trim(), Is.EqualTo("0 of 6"));
    }

    [Test]
    public async Task Render_RowWithPreviousValue_ShowsTheStruckThroughPreviousValueAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        var jobTitleRow = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("Job Title"));
        Assert.That(jobTitleRow.QuerySelector(".attr-previous-value")!.TextContent, Is.EqualTo("Analyst"));
        Assert.That(jobTitleRow.TextContent, Does.Contain("Director"));

        // Only the superseded row carries a previous value
        Assert.That(cut.FindAll(".attr-previous-value"), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Render_Rows_ShowOperationBadgesAndDemotedTypePluralityAsync()
    {
        await using var context = CausalityBunitContext.Create();

        var cut = RenderDetail(context);

        var displayNameRow = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("Display Name"));
        Assert.That(displayNameRow.TextContent, Does.Contain("Set"));
        Assert.That(displayNameRow.TextContent, Does.Contain("Text · Single-valued"));

        var locationRow = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("Location"));
        Assert.That(locationRow.TextContent, Does.Contain("Remove"));
    }
}

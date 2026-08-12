// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using JIM.Web.Causality;
using JIM.Web.Models;
using JIM.Web.Shared;
using JIM.Web.Shared.Causality;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// bUnit tests for <see cref="CausalityAttributeDetail"/>: operation filter chips with counts, name-and-value
/// search, the toolbar count, sorting, previous-value strike-through and the empty states.
///
/// The rows render through a <see cref="VirtualisedDataGrid{TItem}"/>, so these tests count rows via the
/// <see cref="TextValueDisplay"/> component JIM renders once per row, and read the count off
/// <see cref="TableObjectCount"/>, rather than depending on MudBlazor's generated CSS class names, which are a
/// third party's implementation detail that churns between releases (see test/CLAUDE.md > Blazor component tests).
/// </summary>
[TestFixture]
public class CausalityAttributeDetailTests : JimComponentTestContext
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

    private IRenderedComponent<CausalityAttributeDetail> RenderDetail(IReadOnlyList<CausalityAttributeRow>? rows = null)
    {
        var cut = Render<CausalityAttributeDetail>(ps => ps
            .Add(c => c.Rows, rows ?? SampleRows()));

        // The grid loads its first window after its own first render, so every assertion below would otherwise
        // race the rows arriving.
        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<VirtualisedDataGrid<CausalityAttributeRow>>(), Is.True));
        return cut;
    }

    /// <summary>
    /// The rendered attribute row count: JIM renders exactly one value cell per row, so counting the
    /// value component counts the rows without depending on the table's generated markup.
    /// </summary>
    private static int RenderedRowCount(IRenderedComponent<CausalityAttributeDetail> cut)
    {
        return cut.FindComponents<TextValueDisplay>().Count;
    }

    /// <summary>The toolbar count: how many rows match, against the unfiltered total while a search narrows them.</summary>
    private static TableObjectCount Count(IRenderedComponent<CausalityAttributeDetail> cut) =>
        cut.FindComponent<TableObjectCount>().Instance;

    /// <summary>
    /// Types into the grid's search box. The text is committed through the field's own ValueChanged rather than
    /// by an input event, so the test does not wait out the shared search box's debounce.
    /// </summary>
    private static async Task SearchAsync(IRenderedComponent<CausalityAttributeDetail> cut, string text)
    {
        var searchBox = cut.FindComponent<SearchField>().FindComponent<MudTextField<string>>();
        await cut.InvokeAsync(() => searchBox.Instance.ValueChanged.InvokeAsync(text));
    }

    /// <summary>
    /// A queued deprovision's rows are the target's identifying attribute values, carried on the delete
    /// Pending Export so the Connector can still find the entry, and nothing is being written. The operation
    /// column and its filter chips would report every such row as "Set", which is the same claim the
    /// "Target identified by" caption exists to correct, one level further down.
    /// </summary>
    [Test]
    public void Render_WithoutOperations_HidesTheChangeColumnAndItsFilterChips()
    {
        var rows = new List<CausalityAttributeRow>
        {
            new(CausalityAttributeOperation.Set, "distinguishedName", "Text · Single-valued",
                "uid=erin.byrne,ou=People,dc=glitterband,dc=local", null)
        };

        var cut = Render<CausalityAttributeDetail>(ps => ps
            .Add(c => c.Rows, rows)
            .Add(c => c.ShowOperations, false));

        cut.WaitForAssertion(() => Assert.That(RenderedRowCount(cut), Is.EqualTo(1),
            "The values themselves must still be shown"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Not.Contain("Change"),
                "Nothing is being changed, so the operation column has no honest value to show");
            Assert.That(cut.FindAll(".filter-chips button"), Is.Empty);
            Assert.That(cut.Markup, Does.Contain("uid=erin.byrne,ou=People,dc=glitterband,dc=local"));
        }
    }

    [Test]
    public void Render_Default_ShowsTheChangeColumnAndOperationChips()
    {
        var cut = RenderDetail();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Change"));
            Assert.That(cut.FindAll(".filter-chips button"), Is.Not.Empty);
        }
    }

    [Test]
    public void Render_Default_ShowsAllRowsAndTheFullCount()
    {
        var cut = RenderDetail();

        cut.WaitForAssertion(() => Assert.That(RenderedRowCount(cut), Is.EqualTo(6)));
        Assert.That(Count(cut).Count, Is.EqualTo(6));
    }

    [Test]
    public void Render_Default_ShowsNoPagerAndNoBespokeFilterBox()
    {
        var cut = RenderDetail();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.HasComponent<MudTablePager>(), Is.False,
                "a virtualised list has no page size to choose and no page controls");
            Assert.That(cut.FindAll(".attr-search"), Is.Empty,
                "the shared grid owns the search box; a second one beside it would filter nothing");
        }
    }

    [Test]
    public void Render_FilterChips_ShowOperationCounts()
    {
        var cut = RenderDetail();

        var chipLabels = cut.FindAll(".filter-chips button").Select(b => b.TextContent.Trim()).ToList();
        Assert.That(chipLabels, Is.EqualTo(new[] { "All", "Set · 3", "Add · 2", "Remove · 1" }));
    }

    [Test]
    public void Render_OperationWithNoRows_OmitsItsFilterChip()
    {
        var rows = SampleRows().Where(r => r.Operation != CausalityAttributeOperation.Remove).ToList();

        var cut = RenderDetail(rows);

        var chipLabels = cut.FindAll(".filter-chips button").Select(b => b.TextContent.Trim()).ToList();
        Assert.That(chipLabels, Is.EqualTo(new[] { "All", "Set · 3", "Add · 2" }));
    }

    [Test]
    public void FilterChip_Click_FiltersRowsAndUpdatesTheCount()
    {
        var cut = RenderDetail();
        cut.WaitForAssertion(() => Assert.That(RenderedRowCount(cut), Is.EqualTo(6)));

        var addChip = cut.FindAll(".filter-chips button").Single(b => b.TextContent.Trim().StartsWith("Add"));
        addChip.Click();

        cut.WaitForAssertion(() => Assert.That(RenderedRowCount(cut), Is.EqualTo(2)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Count(cut).Count, Is.EqualTo(2), "the count must describe the match set the chip produced");
            Assert.That(cut.FindAll(".filter-chips button").Single(b => b.ClassList.Contains("on")).TextContent.Trim(),
                Does.StartWith("Add"));

            // The surviving rows really are the Add ones, not merely the right count
            Assert.That(cut.Markup, Does.Contain("mail"));
            Assert.That(cut.Markup, Does.Not.Contain("Display Name"));
        }
    }

    [Test]
    public async Task Search_NarrowsByNameAndValueAsync()
    {
        var cut = RenderDetail();

        // Matches the "mail" attribute name and the proxyAddresses value containing "example.com"
        await SearchAsync(cut, "example.com");

        cut.WaitForAssertion(() => Assert.That(RenderedRowCount(cut), Is.EqualTo(2)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Count(cut).Count, Is.EqualTo(2));
            Assert.That(Count(cut).Total, Is.EqualTo(6), "the unfiltered total is what the match count is read against");
        }

        // Name-only match
        await SearchAsync(cut, "department");

        cut.WaitForAssertion(() => Assert.That(RenderedRowCount(cut), Is.EqualTo(1)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Department"));
            Assert.That(cut.Markup, Does.Not.Contain("Job Title"));
        }
    }

    [Test]
    public async Task Search_WithNoMatches_ShowsTheEmptyStateAsync()
    {
        var cut = RenderDetail();

        await SearchAsync(cut, "no-such-attribute");

        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<TableEmptyState>(), Is.True));

        var emptyState = cut.FindComponent<TableEmptyState>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(RenderedRowCount(cut), Is.Zero);
            Assert.That(emptyState.PrimaryText, Does.Contain("No attributes match"));
            Assert.That(emptyState.ActionText, Is.EqualTo("Clear Filters"),
                "an empty state caused by a filter must offer the way out of it");
            Assert.That(Count(cut).Count, Is.Zero);
        }
    }

    [Test]
    public void Render_WithRows_ShowsNoEmptyState()
    {
        var cut = RenderDetail();

        cut.WaitForAssertion(() => Assert.That(RenderedRowCount(cut), Is.EqualTo(6)));
        Assert.That(cut.HasComponent<TableEmptyState>(), Is.False,
            "an empty state over rows that are merely in flight tells the reader the opposite of the truth");
    }

    [Test]
    public void Render_WithNoRowsAtAll_SaysTheEventCarriesNoValues()
    {
        var cut = RenderDetail(new List<CausalityAttributeRow>());

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindComponent<TableEmptyState>().Instance.PrimaryText,
                Is.EqualTo("This event carries no attribute values")));
    }

    /// <summary>
    /// The rows arrive in the order that tells the story of the change, so the grid opens on that order and
    /// reorders only when a header is clicked.
    /// </summary>
    [Test]
    public async Task Sort_ByAttribute_ReordersTheRowsAsync()
    {
        var cut = RenderDetail();
        var grid = cut.FindComponent<VirtualisedDataGrid<CausalityAttributeRow>>().Instance;

        var unsorted = await grid.LoadWindow(
            new VirtualisedWindowRequest(0, 10, null, grid.SortBy, grid.SortDescending, IncludeTotalCount: true),
            CancellationToken.None);
        Assert.That(unsorted.Items.First().Name, Is.EqualTo("Display Name"),
            "with no column chosen the rows must stay in the order they were built in");

        await cut.InvokeAsync(() => grid.ToggleSortAsync("attribute"));

        var ascending = await grid.LoadWindow(
            new VirtualisedWindowRequest(0, 10, null, grid.SortBy, grid.SortDescending, IncludeTotalCount: true),
            CancellationToken.None);
        Assert.That(ascending.Items.Select(r => r.Name),
            Is.EqualTo(new[] { "Department", "Display Name", "Job Title", "Location", "mail", "proxyAddresses" }));

        await cut.InvokeAsync(() => grid.ToggleSortAsync("attribute"));

        var descending = await grid.LoadWindow(
            new VirtualisedWindowRequest(0, 10, null, grid.SortBy, grid.SortDescending, IncludeTotalCount: true),
            CancellationToken.None);
        Assert.That(descending.Items.Select(r => r.Name), Is.EqualTo(ascending.Items.Select(r => r.Name).Reverse()),
            "clicking the same header again must flip the direction rather than re-sorting the same way");
    }

    [Test]
    public async Task Sort_AppliesToTheWindowRatherThanTheRowsOnScreenAsync()
    {
        var cut = RenderDetail();
        var grid = cut.FindComponent<VirtualisedDataGrid<CausalityAttributeRow>>().Instance;

        var window = await grid.LoadWindow(
            new VirtualisedWindowRequest(2, 2, null, "attribute", false, IncludeTotalCount: true), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(r => r.Name), Is.EqualTo(new[] { "Job Title", "Location" }),
                "a window must be sliced out of the sorted set, not sorted after slicing");
            Assert.That(window.TotalItems, Is.EqualTo(6));
        }
    }

    [Test]
    public void Render_RowWithPreviousValue_ShowsTheStruckThroughPreviousValue()
    {
        var cut = RenderDetail();

        cut.WaitForAssertion(() => Assert.That(RenderedRowCount(cut), Is.EqualTo(6)));

        var jobTitleRow = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("Job Title"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(jobTitleRow.QuerySelector(".attr-previous-value")!.TextContent, Is.EqualTo("Analyst"));
            Assert.That(jobTitleRow.TextContent, Does.Contain("Director"));

            // Only the superseded row carries a previous value
            Assert.That(cut.FindAll(".attr-previous-value"), Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Render_Rows_ShowOperationBadgesAndDemotedTypePlurality()
    {
        var cut = RenderDetail();

        cut.WaitForAssertion(() => Assert.That(RenderedRowCount(cut), Is.EqualTo(6)));

        var displayNameRow = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("Display Name"));
        var locationRow = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("Location"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(displayNameRow.TextContent, Does.Contain("Set"));
            Assert.That(displayNameRow.TextContent, Does.Contain("Text · Single-valued"));
            Assert.That(locationRow.TextContent, Does.Contain("Remove"));
        }
    }
}

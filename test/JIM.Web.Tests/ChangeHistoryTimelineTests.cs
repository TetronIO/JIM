// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Web.Models;
using JIM.Web.Shared;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the attribute changes table inside the Change History timeline's details dialog, now that it is a
/// <see cref="VirtualisedDataGrid{TItem}"/> like every other table in the portal. Two things are worth pinning:
/// that the grid is configured for a dialog rather than for a page (it states its own height ceiling instead of
/// measuring where the page footer lands behind the overlay, and keeps its state out of an address bar that
/// outlives the dialog), and that all three of the filters the table had survived the conversion, since the
/// grid's own search box replaced the standalone one it used to sit beside.
/// </summary>
[TestFixture]
public class ChangeHistoryTimelineTests : JimComponentTestContext
{
    private static ChangeHistoryTimeline.ChangeGroup BuildChangeGroup() => new()
    {
        ChangeType = ObjectChangeType.Updated,
        ChangeTime = new System.DateTime(2026, 5, 1, 9, 30, 0, System.DateTimeKind.Utc),
        ChangeInitiatorType = "User",
        ChangeInitiatorName = "Ada Lovelace",
        ChangeMechanismType = "User",
        AttributeChanges =
        [
            new ChangeHistoryTimeline.AttributeChange
            {
                AttributeName = "Department",
                ChangeType = ValueChangeType.Add,
                Value = "Engineering",
                AttributeType = AttributeDataType.Text
            },
            new ChangeHistoryTimeline.AttributeChange
            {
                AttributeName = "Static Members",
                ChangeType = ValueChangeType.Add,
                Value = "Grace Hopper",
                IsMultiValued = true,
                AttributeType = AttributeDataType.Text
            },
            new ChangeHistoryTimeline.AttributeChange
            {
                AttributeName = "Static Members",
                ChangeType = ValueChangeType.Remove,
                Value = "Alan Turing",
                IsMultiValued = true,
                AttributeType = AttributeDataType.Text
            }
        ]
    };

    /// <summary>
    /// Renders the timeline with one change group and opens its details dialog, which is where the grid lives.
    /// The dialog is an inline one, so its content renders inside the dialog provider rather than inside the
    /// timeline; the provider is what the returned component searches, and the timeline is only clicked.
    /// </summary>
    private IRenderedComponent<MudDialogProvider> RenderTimelineWithDialogOpen()
    {
        var provider = Render<MudDialogProvider>();

        var timeline = Render<ChangeHistoryTimeline>(p => p
            .Add(c => c.Changes, new List<ChangeHistoryTimeline.ChangeGroup> { BuildChangeGroup() }));

        timeline.Find(".cursor-pointer").Click();
        provider.WaitForAssertion(() =>
            Assert.That(provider.HasComponent<VirtualisedDataGrid<ChangeHistoryTimeline.AttributeChangeDisplay>>(), Is.True));

        return provider;
    }

    /// <summary>
    /// The dialog's grid, found as a component rather than by markup so that everything inside it (its toolbar's
    /// filters, its search box) can be reached without colliding with the timeline's own filters above it.
    /// </summary>
    private static IRenderedComponent<VirtualisedDataGrid<ChangeHistoryTimeline.AttributeChangeDisplay>> GridComponent(
        IRenderedComponent<MudDialogProvider> cut) =>
        cut.FindComponent<VirtualisedDataGrid<ChangeHistoryTimeline.AttributeChangeDisplay>>();

    private static VirtualisedDataGrid<ChangeHistoryTimeline.AttributeChangeDisplay> Grid(
        IRenderedComponent<MudDialogProvider> cut) => GridComponent(cut).Instance;

    private static IRenderedComponent<MudSelect<string>> ToolbarFilter(
        IRenderedComponent<MudDialogProvider> cut, string label) =>
        GridComponent(cut).FindComponents<MudSelect<string>>().Single(s => s.Instance.Label == label);

    private static Task<VirtualisedWindow<ChangeHistoryTimeline.AttributeChangeDisplay>> LoadWindowAsync(
        IRenderedComponent<MudDialogProvider> cut, string? searchText = null) =>
        Grid(cut).LoadWindow(
            new VirtualisedWindowRequest(0, 50, searchText, "order", false, IncludeTotalCount: true),
            CancellationToken.None);

    [Test]
    public void ChangeHistoryTimeline_ChangeDetails_ShowsTheAttributeChangesInAVirtualisedGrid()
    {
        var cut = RenderTimelineWithDialogOpen();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.Markup, Does.Contain("Engineering"));
            Assert.That(cut.HasComponent<MudTablePager>(), Is.False,
                "a virtualised list has no page size to choose and no page controls");
        }
    }

    /// <summary>
    /// The two settings that make the shared grid work inside a dialog at all. Without the stated ceiling it
    /// measures where the page footer lands behind the overlay and collapses to its floor; without the URL opt-out
    /// it writes a search and scroll position into an address bar that no deep link can reopen the dialog from.
    /// </summary>
    [Test]
    public void ChangeHistoryTimeline_ChangeDetailsGrid_StatesItsOwnHeightAndKeepsItsStateOutOfTheUrl()
    {
        var cut = RenderTimelineWithDialogOpen();

        var grid = Grid(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(grid.MaxHeight, Is.Not.Null.And.Not.Empty);
            Assert.That(grid.TrackUrlState, Is.False);
        }
    }

    [Test]
    public async Task ChangeHistoryTimeline_ChangeDetails_SearchNarrowsTheChangesAsync()
    {
        var cut = RenderTimelineWithDialogOpen();

        var window = await LoadWindowAsync(cut, searchText: "Department");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(c => c.AttributeName), Is.EqualTo(new[] { "Department" }));
            Assert.That(window.TotalItems, Is.EqualTo(1), "the count must describe the match set, not every change");
        }
    }

    [Test]
    public async Task ChangeHistoryTimeline_ChangeDetails_AttributeFilterNarrowsTheChangesAsync()
    {
        var cut = RenderTimelineWithDialogOpen();
        var attributeFilter = ToolbarFilter(cut, "Attribute");

        await cut.InvokeAsync(() => attributeFilter.Instance.ValueChanged.InvokeAsync("Static Members"));

        var window = await LoadWindowAsync(cut);

        Assert.That(window.Items.Select(c => c.AttributeName).Distinct(), Is.EqualTo(new[] { "Static Members" }));
    }

    [Test]
    public async Task ChangeHistoryTimeline_ChangeDetails_ChangeTypeFilterNarrowsTheChangesAsync()
    {
        var cut = RenderTimelineWithDialogOpen();
        var changeTypeFilter = ToolbarFilter(cut, "Change type");

        await cut.InvokeAsync(() => changeTypeFilter.Instance.ValueChanged.InvokeAsync("Remove"));

        var window = await LoadWindowAsync(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(c => c.DisplayChangeType).Distinct(), Is.EqualTo(new[] { "Remove" }));
            Assert.That(window.Items.Select(c => c.DisplayValue), Is.EqualTo(new[] { "Alan Turing" }));
        }
    }

    /// <summary>
    /// A filter that matched nothing is a different situation from an operation that recorded no changes, and
    /// only the first has a way out to offer.
    /// </summary>
    [Test]
    public async Task ChangeHistoryTimeline_ChangeDetails_FilterMatchingNothing_OffersTheWayOutOfItAsync()
    {
        var cut = RenderTimelineWithDialogOpen();
        var attributeFilter = ToolbarFilter(cut, "Attribute");

        await cut.InvokeAsync(() => attributeFilter.Instance.ValueChanged.InvokeAsync("Static Members"));

        var searchBox = GridComponent(cut).FindComponent<SearchField>().FindComponent<MudTextField<string>>();
        await cut.InvokeAsync(() => searchBox.Instance.ValueChanged.InvokeAsync("no-such-change"));

        cut.WaitForAssertion(() =>
        {
            var emptyState = cut.FindComponent<TableEmptyState>().Instance;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(emptyState.PrimaryText, Is.EqualTo("No changes match the current filters"));
                Assert.That(emptyState.ActionText, Is.EqualTo("Clear Filters"));
            }
        });
    }
}

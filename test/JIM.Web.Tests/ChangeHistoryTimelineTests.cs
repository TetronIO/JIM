// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
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
        InitiatedByType = ActivityInitiatorType.User,
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

    /// <summary>
    /// The change details can carry what a host page knows about a change that the timeline does not, such as the
    /// SCIM requests behind an imported one (#124), rendered for the change that was opened.
    /// </summary>
    [Test]
    public void ChangeHistoryTimeline_ChangeDetailExtra_RenderedForTheOpenedChange()
    {
        var provider = Render<MudDialogProvider>();
        var change = BuildChangeGroup();
        change.ChangeId = System.Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var timeline = Render<ChangeHistoryTimeline>(p => p
            .Add(c => c.Changes, new List<ChangeHistoryTimeline.ChangeGroup> { change })
            .Add(c => c.ChangeDetailExtra, opened => builder => builder.AddMarkupContent(0, $"<p class=\"extra\">{opened.ChangeId}</p>")));
        timeline.Find(".cursor-pointer").Click();

        provider.WaitForAssertion(() => Assert.That(provider.Find(".extra").TextContent, Is.EqualTo("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
    }

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

    /// <summary>
    /// The Source column (#399): a change carrying provenance renders the shared origin chip, and the caller's
    /// attribute name becomes a link into that attribute's Inspect view.
    /// </summary>
    [Test]
    public async Task ChangeHistoryTimeline_ChangeDetails_ChangeWithOrigin_CarriesOriginAndInspectHrefIntoTheGridAsync()
    {
        var origin = new ValueOrigin
        {
            Kind = ValueOriginKind.SynchronisationRule,
            ConnectedSystemId = 4,
            ConnectedSystemName = "HR",
            SyncRuleId = 9,
            SyncRuleName = "HR Import"
        };
        var changeGroup = new ChangeHistoryTimeline.ChangeGroup
        {
            ChangeType = ObjectChangeType.Updated,
            ChangeTime = new System.DateTime(2026, 5, 1, 9, 30, 0, System.DateTimeKind.Utc),
            ChangeMechanismType = "SynchronisationRule",
            AttributeChanges =
            [
                new ChangeHistoryTimeline.AttributeChange
                {
                    AttributeName = "Mail",
                    ChangeType = ValueChangeType.Add,
                    Value = "jsmith@example.com",
                    AttributeType = AttributeDataType.Text,
                    Origin = origin,
                    AttributeInspectHref = "/t/people/v/11111111-1111-1111-1111-111111111111?view=inspect&attr=5"
                }
            ]
        };

        var provider = Render<MudDialogProvider>();
        var timeline = Render<ChangeHistoryTimeline>(p => p
            .Add(c => c.Changes, new List<ChangeHistoryTimeline.ChangeGroup> { changeGroup }));
        timeline.Find(".cursor-pointer").Click();
        provider.WaitForAssertion(() =>
            Assert.That(provider.HasComponent<VirtualisedDataGrid<ChangeHistoryTimeline.AttributeChangeDisplay>>(), Is.True));

        var window = await LoadWindowAsync(provider);
        var item = window.Items.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(item.Origin, Is.Not.Null);
            Assert.That(item.Origin!.ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(item.AttributeInspectHref, Is.EqualTo("/t/people/v/11111111-1111-1111-1111-111111111111?view=inspect&attr=5"));
        }

        // Rendered markup: the shared origin chip appears in the Source column, and the attribute name is a link.
        provider.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(provider.HasComponent<ValueOriginChip>(), Is.True);
                var attributeLink = provider.FindAll("a").FirstOrDefault(a => a.TextContent == "Mail");
                Assert.That(attributeLink, Is.Not.Null);
                Assert.That(attributeLink!.GetAttribute("href"), Is.EqualTo(item.AttributeInspectHref));
            }
        });
    }

    /// <summary>
    /// Callers with no provenance of their own (Connected System Object change history, Deleted Objects) carry no
    /// <see cref="ChangeHistoryTimeline.AttributeChange.Origin"/>; the Source column must render exactly as it
    /// always has for them, never "Source not recorded".
    /// </summary>
    [Test]
    public async Task ChangeHistoryTimeline_ChangeDetails_ChangeWithNoOrigin_LeavesOriginAndHrefNullAsync()
    {
        var cut = RenderTimelineWithDialogOpen();

        var window = await LoadWindowAsync(cut);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items, Is.Not.Empty);
            Assert.That(window.Items.All(i => i.Origin == null), Is.True);
            Assert.That(window.Items.All(i => i.AttributeInspectHref == null), Is.True);
        }
    }

    // ─── Who made each change: the site's initiator chip (#2460 follow-up) ───

    [Test]
    public void ChangeHistoryTimeline_SynchronisationChange_CardNamesTheRuleWithItsConnectedSystem()
    {
        var changeGroup = BuildChangeGroup();
        changeGroup.InitiatedByType = ActivityInitiatorType.ApiKey;
        changeGroup.ChangeInitiatorName = "Infrastructure Key";
        changeGroup.ChangeMechanismType = "SynchronisationRule";
        changeGroup.SyncRuleId = 9;
        changeGroup.SyncRuleName = "HR Import";
        changeGroup.ConnectedSystemId = 4;
        changeGroup.ConnectedSystemName = "HR";

        var timeline = Render<ChangeHistoryTimeline>(p => p.Add(c => c.Changes, [changeGroup]));

        var pill = timeline.FindComponent<SystemRuleChip>().Instance;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pill.SyncRuleName, Is.EqualTo("HR Import"));
            Assert.That(pill.ConnectedSystemName, Is.EqualTo("HR"));
        }
    }

    [Test]
    public void ChangeHistoryTimeline_ChangeJimMadeItself_CardSaysSystem()
    {
        var changeGroup = BuildChangeGroup();
        changeGroup.InitiatedByType = ActivityInitiatorType.System;
        changeGroup.ChangeInitiatorName = "System";
        changeGroup.ChangeMechanismType = "System";

        var timeline = Render<ChangeHistoryTimeline>(p => p.Add(c => c.Changes, [changeGroup]));

        Assert.That(timeline.FindAll("[data-testid=jim-initiator-system]"), Has.Count.EqualTo(1));
    }

    [Test]
    public void ChangeHistoryTimeline_ChangeDetails_InitiatedByNamesThePrincipalNotTheRule()
    {
        // The dialog separates who (the principal that started the run) from how (the mechanism).
        var changeGroup = BuildChangeGroup();
        changeGroup.InitiatedByType = ActivityInitiatorType.ApiKey;
        changeGroup.ChangeInitiatorId = System.Guid.NewGuid();
        changeGroup.ChangeInitiatorName = "Infrastructure Key";
        changeGroup.ChangeMechanismType = "SynchronisationRule";
        changeGroup.SyncRuleName = "HR Import";

        var provider = Render<MudDialogProvider>();
        var timeline = Render<ChangeHistoryTimeline>(p => p.Add(c => c.Changes, [changeGroup]));
        timeline.Find(".cursor-pointer").Click();

        provider.WaitForAssertion(() =>
            Assert.That(provider.FindAll("[data-testid=jim-initiator-api-key]"), Has.Count.EqualTo(1)));
    }
}

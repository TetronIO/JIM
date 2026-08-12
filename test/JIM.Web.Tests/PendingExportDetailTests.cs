// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using JIM.Web.Models;
using JIM.Web.Pages.Admin;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Pending Export's Attribute Changes table now that it is a <see cref="VirtualisedDataGrid{TItem}"/>.
/// The rows are attributes rather than individual changes, and the grouping is done once when the page loads, so
/// what is worth pinning is the seam between that list and the grid: the window is sliced from it (search, sort
/// and slice), the count is only taken when the request asks for it, and the two empty states say different
/// things because only one of them has a way out to offer.
/// </summary>
[TestFixture]
public class PendingExportDetailTests : JimComponentTestContext
{
    private const int ConnectedSystemId = 7;

    private static readonly Guid PendingExportId = Guid.NewGuid();

    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private NavigationManager _navigation = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);

        _connectedSystems
            .Setup(r => r.GetConnectedSystemHeaderAsync(ConnectedSystemId))
            .ReturnsAsync(new ConnectedSystemHeader { Id = ConnectedSystemId, Name = "Directory" });

        SetupChanges([]);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    [SetUp]
    public void SetUp()
    {
        _navigation = Services.GetRequiredService<NavigationManager>();
    }

    private void SetupChanges(
        List<PendingExportAttributeValueChange> changes,
        Dictionary<string, int>? totalCounts = null)
    {
        _connectedSystems
            .Setup(r => r.GetPendingExportDetailAsync(PendingExportId))
            .ReturnsAsync(new PendingExportDetailResult
            {
                PendingExport = new PendingExport
                {
                    Id = PendingExportId,
                    ConnectedSystemId = ConnectedSystemId,
                    ConnectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "Directory" },
                    ChangeType = PendingExportChangeType.Update,
                    Status = PendingExportStatus.Pending,
                    AttributeValueChanges = changes
                },
                AttributeChangeTotalCounts = totalCounts ?? changes
                    .GroupBy(c => c.Attribute?.Name ?? $"Attribute {c.AttributeId}")
                    .ToDictionary(g => g.Key, g => g.Count())
            });
    }

    /// <summary>
    /// One single-valued change per named attribute, which is the shape that gives one grid row per attribute.
    /// </summary>
    private static List<PendingExportAttributeValueChange> BuildChanges(params string[] attributeNames) =>
        attributeNames
            .Select((name, i) => new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                AttributeId = i + 1,
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = i + 1, Name = name },
                ChangeType = PendingExportAttributeChangeType.Update,
                Status = PendingExportAttributeChangeStatus.Pending,
                StringValue = $"{name}-value"
            })
            .ToList();

    /// <summary>
    /// One attribute carrying <paramref name="count"/> queued changes, which is the shape that gives one grid row
    /// carrying more changes than a row can show.
    /// </summary>
    private static List<PendingExportAttributeValueChange> BuildMultiValuedChanges(string attributeName, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                AttributeId = 1,
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = 1, Name = attributeName },
                ChangeType = PendingExportAttributeChangeType.Add,
                Status = PendingExportAttributeChangeStatus.Pending,
                StringValue = $"{attributeName}-value-{i:D3}"
            })
            .ToList();

    /// <summary>
    /// Serves one attribute's queued changes as the repository's range read does, which is what the "+n more"
    /// dialog reads from once it is open.
    /// </summary>
    private void SetupChangeRange(string attributeName, IReadOnlyList<PendingExportAttributeValueChange> changes)
    {
        _connectedSystems
            .Setup(r => r.GetPendingExportAttributeChangesRangeAsync(PendingExportId, attributeName,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync((Guid _, string _, int offset, int count, string? _, bool includeTotalCount) =>
                new RangeResultSet<PendingExportAttributeValueChange>
                {
                    Results = changes.Skip(offset).Take(count).ToList(),
                    TotalResults = includeTotalCount ? changes.Count : null
                });
    }

    private IRenderedComponent<PendingExportDetail> RenderPage(string query = "")
    {
        _navigation.NavigateTo(
            $"/admin/connected-systems/{ConnectedSystemId}/pending-exports/{PendingExportId}{query}");
        return Render<PendingExportDetail>(p => p
            .Add(c => c.ConnectedSystemId, ConnectedSystemId)
            .Add(c => c.Id, PendingExportId));
    }

    private static VirtualisedDataGrid<PendingExportDetail.AttributeChangeGroup> Grid(
        IRenderedComponent<PendingExportDetail> cut) =>
        cut.FindComponent<VirtualisedDataGrid<PendingExportDetail.AttributeChangeGroup>>().Instance;

    [Test]
    public void PendingExportDetail_AttributeChanges_AreAVirtualisedGridWithNoPager()
    {
        SetupChanges(BuildChanges("department", "title"));

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.HasComponent<VirtualisedDataGrid<PendingExportDetail.AttributeChangeGroup>>(), Is.True,
                    "the Attribute Changes must be shown in the shared virtualised grid, not a hand-rolled table");
                Assert.That(cut.HasComponent<MudBlazor.MudTablePager>(), Is.False,
                    "a virtualised list has no page size to choose and no page controls");
                Assert.That(cut.Markup, Does.Contain("department"));
            }
        });
    }

    [Test]
    public async Task PendingExportDetail_Window_IsSlicedFromTheAttributesThePageAlreadyHoldsAsync()
    {
        SetupChanges(BuildChanges("alpha", "bravo", "charlie", "delta"));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<VirtualisedDataGrid<PendingExportDetail.AttributeChangeGroup>>(), Is.True));

        var window = await Grid(cut).LoadWindow(
            new VirtualisedWindowRequest(1, 2, null, "attribute", false, IncludeTotalCount: true),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(g => g.AttributeName), Is.EqualTo(new[] { "bravo", "charlie" }),
                "the window must be the requested slice of the sorted attributes, not the first rows of it");
            Assert.That(window.TotalItems, Is.EqualTo(4), "the total counts every matching attribute, not the window");
        }
    }

    [Test]
    public async Task PendingExportDetail_WindowSortedDescending_ReversesTheAttributeOrderAsync()
    {
        SetupChanges(BuildChanges("alpha", "bravo", "charlie"));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<VirtualisedDataGrid<PendingExportDetail.AttributeChangeGroup>>(), Is.True));

        var window = await Grid(cut).LoadWindow(
            new VirtualisedWindowRequest(0, 3, null, "attribute", true, IncludeTotalCount: false),
            CancellationToken.None);

        Assert.That(window.Items.Select(g => g.AttributeName), Is.EqualTo(new[] { "charlie", "bravo", "alpha" }),
            "the sort the header asks for has to reach the window, or the arrow points at an order nothing applied");
    }

    [Test]
    public async Task PendingExportDetail_WindowRequestSkippingTheCount_ReturnsANullTotalRatherThanZeroAsync()
    {
        SetupChanges(BuildChanges("alpha", "bravo"));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<VirtualisedDataGrid<PendingExportDetail.AttributeChangeGroup>>(), Is.True));

        var window = await Grid(cut).LoadWindow(
            new VirtualisedWindowRequest(0, 2, null, "attribute", false, IncludeTotalCount: false),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalItems, Is.Null,
                "null is \"not counted\"; a zero here would read as a Pending Export carrying no changes");
            Assert.That(window.Items, Has.Count.EqualTo(2), "the window itself is unaffected by skipping the count");
        }
    }

    [Test]
    public async Task PendingExportDetail_WindowWithASearch_MatchesTheAttributeNameAndItsValuesAsync()
    {
        SetupChanges(BuildChanges("department", "title"));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<VirtualisedDataGrid<PendingExportDetail.AttributeChangeGroup>>(), Is.True));

        var byName = await Grid(cut).LoadWindow(
            new VirtualisedWindowRequest(0, 25, "DEPART", "attribute", false, IncludeTotalCount: true),
            CancellationToken.None);
        var byValue = await Grid(cut).LoadWindow(
            new VirtualisedWindowRequest(0, 25, "title-value", "attribute", false, IncludeTotalCount: true),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byName.Items.Select(g => g.AttributeName), Is.EqualTo(new[] { "department" }),
                "the search matches the attribute name, case-insensitively, as the paged table's filter did");
            Assert.That(byValue.Items.Select(g => g.AttributeName), Is.EqualTo(new[] { "title" }),
                "the search also matches a change's value, as the paged table's filter did");
        }
    }

    // ─── One line per row ───

    /// <summary>
    /// The virtualiser positions every row arithmetically from one fixed row height, so a row that renders taller
    /// than that height puts the scroll position, the row index written to the URL and the space reserved for the
    /// rows below it out of step with what is on screen. An attribute carrying one change is one line already and
    /// must keep rendering exactly as it did, with nothing offered to open.
    /// </summary>
    [Test]
    public void PendingExportDetail_AttributeWithOneChange_RendersItsValueInlineWithNothingToOpen()
    {
        SetupChanges(BuildChanges("department"));

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("department-value"));
                Assert.That(cut.FindAll(".jim-attr-expand-btn"), Is.Empty,
                    "there is nothing more to reach, so an affordance would be a dead one");
            }
        });
    }

    [Test]
    public void PendingExportDetail_AttributeWithSeveralChanges_RendersOneValueAndAnAffordanceForTheRest()
    {
        SetupChanges(BuildMultiValuedChanges("member", 4));

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("member-value-000"), "the first value still reads in the row");
                Assert.That(cut.Markup, Does.Not.Contain("member-value-001"),
                    "a row is one line, so the remaining changes must not be stacked into the cell");
                Assert.That(cut.FindAll(".jim-attr-expand-btn"), Has.Count.EqualTo(1));
                Assert.That(cut.Find(".jim-attr-expand-btn").TextContent, Does.Contain("+3 more"),
                    "the affordance must account for every change the row is not showing");
            }
        });
    }

    /// <summary>
    /// The row cannot show the changes, so the affordance has to reach them, and what it opens has to be able to
    /// carry all of them: a group with half a million members is the case this whole shape exists for. The dialog's
    /// grid also has to state its own height ceiling (its container is not the page) and keep its state out of the
    /// address bar (no deep link can reopen a dialog to put it back).
    /// </summary>
    [Test]
    public void PendingExportDetail_SeveralChangesAffordance_OpensAVirtualisedDialogOverEveryChange()
    {
        var changes = BuildMultiValuedChanges("member", 500);
        SetupChanges(changes.Take(10).ToList(), new Dictionary<string, int> { ["member"] = 500 });
        SetupChangeRange("member", changes);

        var provider = Render<MudBlazor.MudDialogProvider>();
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".jim-attr-expand-btn"), Is.Not.Empty));

        cut.Find(".jim-attr-expand-btn").Click();

        provider.WaitForAssertion(() =>
            Assert.That(provider.HasComponent<VirtualisedDataGrid<PendingExportAttributeValueChange>>(), Is.True,
                "the affordance must open the changes, not merely say how many there are"));

        var grid = provider.FindComponent<VirtualisedDataGrid<PendingExportAttributeValueChange>>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.HasComponent<PendingExportMvaDialog>(), Is.True);
            Assert.That(provider.HasComponent<MudBlazor.MudTablePager>(), Is.False,
                "the dialog's list is virtualised, so every change stays reachable however many there are");
            Assert.That(grid.MaxHeight, Is.Not.Null.And.Not.Empty);
            Assert.That(grid.TrackUrlState, Is.False);
        }
    }

    [Test]
    public void PendingExportDetail_WithASearchThatMatchedNothing_OffersToClearIt()
    {
        SetupChanges(BuildChanges("department"));

        var cut = RenderPage("?q=nothing-matches-this");

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("No attributes match \"nothing-matches-this\""));
                Assert.That(cut.Markup, Does.Contain("Clear Search"),
                    "a search that matched nothing has a way out, and the empty state must offer it");
            }
        });
    }

    [Test]
    public void PendingExportDetail_WithNoAttributeChangesAtAll_SaysSoWithoutOfferingASearchToClear()
    {
        // A Delete carries no attribute changes at all, which is a different situation from a search matching
        // nothing: the panel says why rather than offering an action that would do nothing.
        SetupChanges([]);

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("No attribute changes"));
                Assert.That(cut.Markup, Does.Not.Contain("Clear Search"));
            }
        });
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}

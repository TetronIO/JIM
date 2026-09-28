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
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using JIM.Web;
using JIM.Web.Models;
using JIM.Web.Pages.Admin;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Pending Export's Attribute Changes table, an <see cref="AttributeTable{TItem}"/>. The rows are
/// attributes rather than individual changes, grouped once when the page loads, so what is worth pinning is the
/// seam between that list and the table (the search and the sort), how an attribute carrying several changes
/// reads (inline, or a nested virtualised table beyond the inline limit), and the two empty states, which say
/// different things because only one of them has a way out to offer.
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
    /// One single-valued change per named attribute, which is the shape that gives one table row per attribute.
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
    /// One attribute carrying <paramref name="count"/> queued changes: one attribute row carrying several changes.
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
    /// Serves one attribute's queued changes as the repository's range read does, which is what the nested changes
    /// table reads from as it scrolls.
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

    private static AttributeTable<PendingExportDetail.AttributeChangeGroup> Table(
        IRenderedComponent<PendingExportDetail> cut) =>
        cut.FindComponent<AttributeTable<PendingExportDetail.AttributeChangeGroup>>().Instance;

    private static IEnumerable<string> VisibleAttributeNames(IRenderedComponent<PendingExportDetail> cut) =>
        Table(cut).VisibleItems.Select(g => g.AttributeName);

    [Test]
    public void PendingExportDetail_AttributeChanges_AreAPlainAttributeTable()
    {
        SetupChanges(BuildChanges("department", "title"));

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.HasComponent<AttributeTable<PendingExportDetail.AttributeChangeGroup>>(), Is.True,
                    "one row per attribute is bounded by the object's schema, so it is a plain table whose rows " +
                    "can hold several changes inline");
                Assert.That(cut.HasComponent<VirtualisedDataGrid<PendingExportDetail.AttributeChangeGroup>>(), Is.False);
                Assert.That(cut.HasComponent<MudBlazor.MudTablePager>(), Is.False);
                Assert.That(cut.Markup, Does.Contain("department"));
            }
        });
    }

    [Test]
    public void PendingExportDetail_AttributeChanges_OpenSortedByAttributeWithEveryRowShown()
    {
        SetupChanges(BuildChanges("delta", "alpha", "charlie", "bravo"));

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
            Assert.That(VisibleAttributeNames(cut), Is.EqualTo(new[] { "alpha", "bravo", "charlie", "delta" })));
    }

    [Test]
    public async Task PendingExportDetail_AttributeSortToggled_ReversesTheAttributeOrderAsync()
    {
        SetupChanges(BuildChanges("alpha", "bravo", "charlie"));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<AttributeTable<PendingExportDetail.AttributeChangeGroup>>(), Is.True));

        await cut.InvokeAsync(() => Table(cut).ToggleSortAsync("attribute"));

        Assert.That(VisibleAttributeNames(cut), Is.EqualTo(new[] { "charlie", "bravo", "alpha" }),
            "the sort the header asks for has to reach the rows, or the arrow points at an order nothing applied");
    }

    [Test]
    public async Task PendingExportDetail_Search_MatchesTheAttributeNameAndItsValuesAsync()
    {
        SetupChanges(BuildChanges("department", "title"));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<AttributeTable<PendingExportDetail.AttributeChangeGroup>>(), Is.True));

        await cut.InvokeAsync(() => Table(cut).SetSearchAsync("DEPART"));
        var byName = VisibleAttributeNames(cut).ToList();
        await cut.InvokeAsync(() => Table(cut).SetSearchAsync("title-value"));
        var byValue = VisibleAttributeNames(cut).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byName, Is.EqualTo(new[] { "department" }),
                "the search matches the attribute name, case-insensitively");
            Assert.That(byValue, Is.EqualTo(new[] { "title" }), "the search also matches a change's value");
        }
    }

    // ─── Multi-valued attributes ───

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
                Assert.That(cut.FindAll("button").Where(b => b.TextContent.Contains("more")), Is.Empty);
            }
        });
    }

    /// <summary>
    /// A handful of changes reads in the row itself. Each is a value with its own change type and status, so each
    /// stacked line says what will happen to that value, not just what the value is.
    /// </summary>
    [Test]
    public void PendingExportDetail_AttributeWithSeveralChangesWithinTheInlineLimit_StacksEveryChangeInTheRow()
    {
        SetupChanges(BuildMultiValuedChanges("member", 4));

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            var lines = cut.FindAll(".jim-attr-expanded .jim-attr-expanded-item");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(lines, Has.Count.EqualTo(4));
                for (var i = 0; i < 4; i++)
                {
                    Assert.That(lines[i].TextContent, Does.Contain($"member-value-{i:D3}"));
                    Assert.That(lines[i].TextContent, Does.Contain("Add"),
                        "each line carries its own change type: adding a member and removing one read very differently");
                }
                Assert.That(cut.FindAll("button").Where(b => b.TextContent.Contains("more")), Is.Empty,
                    "every change is already on the page, so there is nothing more to open");
                Assert.That(cut.HasComponent<PendingExportMvaTable>(), Is.False);
            }
        });
    }

    /// <summary>
    /// A group with half a million members is the case the nested table exists for: the page loads only a sample,
    /// so the cell holds a virtualised table that reads the rest from the server as it scrolls, embedded in the
    /// row rather than behind a dialog.
    /// </summary>
    [Test]
    public void PendingExportDetail_AttributeWithChangesBeyondTheInlineLimit_HoldsANestedVirtualisedTableInTheRow()
    {
        var changes = BuildMultiValuedChanges("member", 500);
        SetupChanges(changes.Take(10).ToList(), new Dictionary<string, int> { ["member"] = 500 });
        SetupChangeRange("member", changes);

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<PendingExportMvaTable>(), Is.True));

        var grid = cut.FindComponent<VirtualisedDataGrid<PendingExportAttributeValueChange>>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.FindAll("button").Where(b => b.TextContent.Contains("more")), Is.Empty, "there is no dialog left to open");
            Assert.That(grid.Embedded, Is.True);
            Assert.That(grid.MaxHeight, Is.Not.Null.And.Not.Empty);
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

    // ─── Naming the Target and Source objects (#1669) ───

    /// <summary>
    /// The Target and Source row labels stay short: the object's type is already named once, by the
    /// chip beneath the label ("user: ...", "User: ..."), so restating "Connected System Object" /
    /// "Metaverse Object" in the row label itself would say the same thing twice and wrap the label
    /// onto three lines beside the chip (#1669 follow-up, found on a live check of the page).
    /// </summary>
    [Test]
    public void PendingExportDetail_TargetAndSourceRows_UseTheShortLabelsAsync()
    {
        SetupRelatedObjects(
            cso: new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = new ConnectedSystemObjectType { Name = "user" }
            },
            mvo: null);

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("<strong>Target</strong>"));
                Assert.That(cut.Markup, Does.Contain("<strong>Source</strong>"));
                Assert.That(cut.Markup, Does.Not.Contain("Target Connected System Object"));
                Assert.That(cut.Markup, Does.Not.Contain("Source Metaverse Object"));
            }
        });
    }

    /// <summary>
    /// The Target and Source rows' sub-lines carry only the place ("in Cross-Domain Export", "in the
    /// Metaverse"), never the type: the chip above each already names the type once ("user: ...",
    /// "User: ..."), so the sub-line repeating it would say it twice (#1669 follow-up, found on a live
    /// check of the page: the old "user in Cross-Domain Export" sub-line duplicated the chip's own
    /// "user: e6d5…").
    /// </summary>
    [Test]
    public void PendingExportDetail_TargetAndSourceObjects_SubLinesNameOnlyThePlaceAsync()
    {
        SetupRelatedObjects(
            cso: new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                Type = new ConnectedSystemObjectType { Name = "user" }
            },
            mvo: new MetaverseObject
            {
                Id = Guid.NewGuid(),
                Type = new MetaverseObjectType { Name = "User", PluralName = "Users" },
                CachedDisplayName = "Baseline User"
            });

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                var places = cut.FindAll(".jim-object-place");
                Assert.That(places, Has.Count.EqualTo(2));
                Assert.That(places[0].TextContent.Trim(), Is.EqualTo("in Directory"),
                    "the Target sub-line must not repeat the chip's own type prefix");
                Assert.That(places[1].TextContent.Trim(), Is.EqualTo("in the Metaverse"),
                    "the Source sub-line must not repeat the chip's own type prefix");
            }
        });
    }

    /// <summary>
    /// The Target sub-line never varies with the Connected System Object's type, known or not: it takes
    /// no type argument at all, because the chip beside it is the one and only place that names the
    /// type (#1669 follow-up).
    /// </summary>
    [Test]
    public void PendingExportDetail_TargetObjectSubLine_NeverNamesTheTypeRegardlessOfWhetherItIsKnownAsync()
    {
        SetupRelatedObjects(
            cso: new ConnectedSystemObject { Id = Guid.NewGuid(), Type = new ConnectedSystemObjectType() },
            mvo: null);

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Find(".jim-object-place").TextContent.Trim(), Is.EqualTo("in Directory")));
    }

    /// <summary>
    /// Sets the Target and Source objects on the Pending Export the page loads, keeping every other field
    /// (attribute changes, counts) as the empty baseline <see cref="SetupChanges"/> establishes.
    /// </summary>
    private void SetupRelatedObjects(ConnectedSystemObject? cso, MetaverseObject? mvo)
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
                    ConnectedSystemObject = cso,
                    SourceMetaverseObject = mvo,
                    AttributeValueChanges = []
                },
                AttributeChangeTotalCounts = new()
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

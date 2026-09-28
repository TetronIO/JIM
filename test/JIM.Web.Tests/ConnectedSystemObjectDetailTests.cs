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
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
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
/// Covers the two tables on a Connected System Object's page, which sit at opposite ends of the size spectrum and
/// are worth pinning for opposite reasons. The attributes are bounded by the object's schema, so they are an
/// <see cref="AttributeTable{TItem}"/> whose search and five sorts run in memory, and whose rows hold a
/// multi-valued attribute's values inline (a nested virtualised table beyond the inline limit). The Pending
/// Export's queued changes are unbounded, so they are a <see cref="VirtualisedDataGrid{TItem}"/> whose windows must
/// reach the application layer's range read by offset and count, and must pass the skip-the-count contract through
/// rather than counting the whole set on every scroll.
/// </summary>
[TestFixture]
public class ConnectedSystemObjectDetailTests : JimComponentTestContext
{
    private const int ConnectedSystemId = 4;

    private static readonly Guid ConnectedSystemObjectId = Guid.NewGuid();
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

        SetupAttributeValues([]);
        SetupPendingExport(changeCount: 0);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    [SetUp]
    public void SetUp()
    {
        _navigation = Services.GetRequiredService<NavigationManager>();
    }

    private void SetupAttributeValues(
        List<ConnectedSystemObjectAttributeValue> values,
        Dictionary<string, int>? totalCounts = null)
    {
        _connectedSystems
            .Setup(r => r.GetConnectedSystemObjectDetailAsync(
                ConnectedSystemId, ConnectedSystemObjectId, CsoAttributeLoadStrategy.CappedMva))
            .ReturnsAsync(new CsoDetailResult
            {
                ConnectedSystemObject = new ConnectedSystemObject
                {
                    Id = ConnectedSystemObjectId,
                    ConnectedSystemId = ConnectedSystemId,
                    Type = new ConnectedSystemObjectType { Id = 1, Name = "User" },
                    Status = ConnectedSystemObjectStatus.Normal,
                    Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    AttributeValues = values
                },
                AttributeValueTotalCounts = totalCounts ?? values
                    .GroupBy(v => v.Attribute.Name)
                    .ToDictionary(g => g.Key, g => g.Count()),
                ChangeCount = 0
            });
    }

    /// <summary>
    /// Serves one attribute's values as the repository's range read does, which is what the nested values table
    /// reads from as it scrolls.
    /// </summary>
    private void SetupAttributeValueRange(string attributeName, IReadOnlyList<ConnectedSystemObjectAttributeValue> values)
    {
        _connectedSystems
            .Setup(r => r.GetAttributeValuesRangeAsync(ConnectedSystemObjectId, attributeName,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync((Guid _, string _, int offset, int count, string? _, bool includeTotalCount) =>
                new RangeResultSet<ConnectedSystemObjectAttributeValue>
                {
                    Results = values.Skip(offset).Take(count).ToList(),
                    TotalResults = includeTotalCount ? values.Count : null
                });
    }

    private void SetupPendingExport(int changeCount)
    {
        _connectedSystems
            .Setup(r => r.GetPendingExportHeaderByConnectedSystemObjectIdAsync(ConnectedSystemObjectId))
            .ReturnsAsync(changeCount == 0
                ? null
                : (new PendingExport
                {
                    Id = PendingExportId,
                    ConnectedSystemId = ConnectedSystemId,
                    ChangeType = PendingExportChangeType.Update
                }, changeCount));
    }

    private void SetupPendingExportChanges(List<PendingExportAttributeValueChange> changes)
    {
        SetupPendingExport(changes.Count);

        _connectedSystems
            .Setup(r => r.GetAllPendingExportChangesRangeAsync(
                PendingExportId, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync((Guid _, int offset, int count, string? _, bool includeTotalCount) =>
                new RangeResultSet<PendingExportAttributeValueChange>
                {
                    Results = changes.Skip(offset).Take(count).ToList(),
                    TotalResults = includeTotalCount ? changes.Count : null
                });
    }

    private static List<ConnectedSystemObjectAttributeValue> BuildAttributeValues(params string[] attributeNames) =>
        attributeNames
            .Select((name, i) => new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = i + 1,
                Attribute = new ConnectedSystemObjectTypeAttribute
                {
                    Id = i + 1,
                    Name = name,
                    Type = AttributeDataType.Text,
                    AttributePlurality = AttributePlurality.SingleValued
                },
                StringValue = $"{name}-value"
            })
            .ToList();

    /// <summary>
    /// One multi-valued attribute holding <paramref name="count"/> values: one attribute row carrying several
    /// values.
    /// </summary>
    private static List<ConnectedSystemObjectAttributeValue> BuildMultiValuedAttribute(string name, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = 1,
                Attribute = new ConnectedSystemObjectTypeAttribute
                {
                    Id = 1,
                    Name = name,
                    Type = AttributeDataType.Text,
                    AttributePlurality = AttributePlurality.MultiValued
                },
                StringValue = $"{name}-value-{i:D3}"
            })
            .ToList();

    private static List<PendingExportAttributeValueChange> BuildPendingChanges(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                AttributeId = i + 1,
                Attribute = new ConnectedSystemObjectTypeAttribute { Id = i + 1, Name = $"attribute-{i:D3}" },
                ChangeType = PendingExportAttributeChangeType.Update,
                Status = PendingExportAttributeChangeStatus.Pending,
                StringValue = $"value-{i:D3}"
            })
            .ToList();

    private IRenderedComponent<ConnectedSystemObjectDetail> RenderPage(string query = "")
    {
        _navigation.NavigateTo(
            $"/admin/connected-systems/{ConnectedSystemId}/connector-space/{ConnectedSystemObjectId}{query}");
        return Render<ConnectedSystemObjectDetail>(p => p
            .Add(c => c.CsId, ConnectedSystemId)
            .Add(c => c.CsoId, ConnectedSystemObjectId.ToString()));
    }

    private static AttributeTable<ConnectedSystemObjectDetail.AttributeGroup> AttributeTable(
        IRenderedComponent<ConnectedSystemObjectDetail> cut) =>
        cut.FindComponent<AttributeTable<ConnectedSystemObjectDetail.AttributeGroup>>().Instance;

    private static VirtualisedDataGrid<PendingExportAttributeValueChange> PendingExportGrid(
        IRenderedComponent<ConnectedSystemObjectDetail> cut) =>
        cut.FindComponent<VirtualisedDataGrid<PendingExportAttributeValueChange>>().Instance;

    private static IEnumerable<string> VisibleAttributeNames(IRenderedComponent<ConnectedSystemObjectDetail> cut) =>
        AttributeTable(cut).VisibleItems.Select(g => g.AttributeName);

    [Test]
    public void CsoDetail_Attributes_AreAPlainAttributeTableAndTheQueuedChangesAVirtualisedGrid()
    {
        SetupAttributeValues(BuildAttributeValues("displayName"));
        SetupPendingExportChanges(BuildPendingChanges(3));

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.HasComponent<AttributeTable<ConnectedSystemObjectDetail.AttributeGroup>>(), Is.True,
                    "an object's attributes are bounded by its schema, so they are a plain table whose rows can " +
                    "hold a multi-valued attribute's values inline");
                Assert.That(cut.HasComponent<VirtualisedDataGrid<ConnectedSystemObjectDetail.AttributeGroup>>(), Is.False);
                Assert.That(cut.HasComponent<VirtualisedDataGrid<PendingExportAttributeValueChange>>(), Is.True,
                    "the queued Pending Export changes are unbounded, so they stay in the shared virtualised grid");
                Assert.That(cut.HasComponent<MudBlazor.MudTablePager>(), Is.False);
            }
        });
    }

    [Test]
    public async Task CsoDetail_PendingExportWindow_IsReadByOffsetAndCountAsync()
    {
        SetupAttributeValues(BuildAttributeValues("displayName"));
        SetupPendingExportChanges(BuildPendingChanges(250));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<VirtualisedDataGrid<PendingExportAttributeValueChange>>(), Is.True));

        var window = await PendingExportGrid(cut).LoadWindow(
            new VirtualisedWindowRequest(120, 40, null, "order", false, IncludeTotalCount: true),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.First().StringValue, Is.EqualTo("value-120"),
                "an arbitrary offset must be read as an offset, not rounded to a page boundary");
            Assert.That(window.Items, Has.Count.EqualTo(40));
            Assert.That(window.TotalItems, Is.EqualTo(250));
        }
    }

    [Test]
    public async Task CsoDetail_PendingExportWindowSkippingTheCount_AsksTheApplicationLayerNotToCountAsync()
    {
        SetupAttributeValues(BuildAttributeValues("displayName"));
        SetupPendingExportChanges(BuildPendingChanges(10));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<VirtualisedDataGrid<PendingExportAttributeValueChange>>(), Is.True));

        var window = await PendingExportGrid(cut).LoadWindow(
            new VirtualisedWindowRequest(0, 5, null, "order", false, IncludeTotalCount: false),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalItems, Is.Null,
                "null is \"not counted\"; a zero here would read as a Pending Export carrying no changes");
            Assert.That(window.Items, Has.Count.EqualTo(5));
        }

        _connectedSystems.Verify(r => r.GetAllPendingExportChangesRangeAsync(
            PendingExportId, 0, 5, null, false), Times.Once,
            "counting is the expensive half of a window read, so the request's false must reach the range read");
    }

    [Test]
    public void CsoDetail_Attributes_OpenSortedByNameWithEveryRowShown()
    {
        SetupAttributeValues(BuildAttributeValues("delta", "alpha", "charlie", "bravo"));

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
            Assert.That(VisibleAttributeNames(cut), Is.EqualTo(new[] { "alpha", "bravo", "charlie", "delta" })));
    }

    [Test]
    public async Task CsoDetail_AttributesSortedByValue_OrderOnTheValueRatherThanTheNameAsync()
    {
        // The table offers five sorts and its headers claim them; a header that reordered nothing, or that quietly
        // fell back to the attribute name, would be indistinguishable at a glance.
        SetupAttributeValues(BuildAttributeValues("alpha", "bravo"));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<AttributeTable<ConnectedSystemObjectDetail.AttributeGroup>>(), Is.True));

        await cut.InvokeAsync(() => AttributeTable(cut).ToggleSortAsync("value"));
        await cut.InvokeAsync(() => AttributeTable(cut).ToggleSortAsync("value"));

        Assert.That(VisibleAttributeNames(cut), Is.EqualTo(new[] { "bravo", "alpha" }));
    }

    [Test]
    public async Task CsoDetail_AttributeSearch_MatchesTheAttributeNameAndItsValuesAsync()
    {
        SetupAttributeValues(BuildAttributeValues("department", "title"));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<AttributeTable<ConnectedSystemObjectDetail.AttributeGroup>>(), Is.True));

        await cut.InvokeAsync(() => AttributeTable(cut).SetSearchAsync("DEPART"));
        var byName = VisibleAttributeNames(cut).ToList();
        await cut.InvokeAsync(() => AttributeTable(cut).SetSearchAsync("title-value"));
        var byValue = VisibleAttributeNames(cut).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byName, Is.EqualTo(new[] { "department" }));
            Assert.That(byValue, Is.EqualTo(new[] { "title" }));
        }
    }

    [Test]
    public async Task CsoDetail_AttributeSearch_MatchesAValueTheInlineStackShowsBeyondTheFirstAsync()
    {
        SetupAttributeValues([.. BuildAttributeValues("department"), .. BuildMultiValuedAttribute("member", 4)]);
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<AttributeTable<ConnectedSystemObjectDetail.AttributeGroup>>(), Is.True));

        await cut.InvokeAsync(() => AttributeTable(cut).SetSearchAsync("member-value-003"));

        Assert.That(VisibleAttributeNames(cut), Is.EqualTo(new[] { "member" }));
    }

    [Test]
    public void CsoDetail_AttributesWithASearchThatMatchedNothing_OffersToClearIt()
    {
        SetupAttributeValues(BuildAttributeValues("department"));

        var cut = RenderPage("?attr-q=nothing-matches-this");

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

    // ─── Multi-valued attributes ───

    [Test]
    public void CsoDetail_SingleValuedAttribute_RendersItsValueInlineWithNothingToOpen()
    {
        SetupAttributeValues(BuildAttributeValues("department"));

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("department-value"), "the one value is shown in the cell");
                Assert.That(cut.FindAll("button").Where(b => b.TextContent.Contains("more")), Is.Empty);
            }
        });
    }

    /// <summary>
    /// A handful of values reads in the row itself, as it does on the Metaverse Object's page: a click and a dialog
    /// just to see a second telephone number is the friction this table exists to remove.
    /// </summary>
    [Test]
    public void CsoDetail_MultiValuedAttributeWithinTheInlineLimit_StacksEveryValueInTheRow()
    {
        SetupAttributeValues(BuildMultiValuedAttribute("member", 4));

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.FindAll(".jim-attr-expanded .jim-attr-expanded-item"), Has.Count.EqualTo(4));
                for (var i = 0; i < 4; i++)
                    Assert.That(cut.Markup, Does.Contain($"member-value-{i:D3}"));
                Assert.That(cut.FindAll("button").Where(b => b.TextContent.Contains("more")), Is.Empty,
                    "every value is already on the page, so there is nothing more to open");
                Assert.That(cut.HasComponent<CsoMvaTable>(), Is.False);
            }
        });
    }

    /// <summary>
    /// A group with half a million members is the case the nested table exists for: the page loads only a sample,
    /// so the cell holds a virtualised table that reads the rest from the server as it scrolls, embedded in the
    /// row rather than behind a dialog.
    /// </summary>
    [Test]
    public void CsoDetail_MultiValuedAttributeBeyondTheInlineLimit_HoldsANestedVirtualisedTableInTheRow()
    {
        var values = BuildMultiValuedAttribute("member", 500);
        SetupAttributeValues(values.Take(10).ToList(), new Dictionary<string, int> { ["member"] = 500 });
        SetupAttributeValueRange("member", values);

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<VirtualisedDataGrid<ConnectedSystemObjectAttributeValue>>(), Is.True));

        var grid = cut.FindComponent<VirtualisedDataGrid<ConnectedSystemObjectAttributeValue>>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cut.HasComponent<CsoMvaTable>(), Is.True);
            Assert.That(cut.FindAll("button").Where(b => b.TextContent.Contains("more")), Is.Empty, "there is no dialog left to open");
            Assert.That(grid.Embedded, Is.True,
                "one table per large attribute sits on the page, so none may claim the address bar or the density toggle");
            Assert.That(grid.MaxHeight, Is.Not.Null.And.Not.Empty,
                "its container is a table cell, not the page, so it has to state its own height ceiling");
        }
    }

    [Test]
    public void CsoDetail_ObjectWithNoAttributeValues_SaysWhatWouldPutRowsThere()
    {
        SetupAttributeValues([]);

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.Markup, Does.Contain("This object has no attribute values"));
                Assert.That(cut.Markup, Does.Contain("Import brings them in"),
                    "an empty list must say what would populate it");
                Assert.That(cut.Markup, Does.Not.Contain("Clear Search"),
                    "there is no search to clear, so the button would be a dead affordance");
            }
        });
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}

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
/// Covers the two tables on a Connected System Object's page now that both are
/// <see cref="VirtualisedDataGrid{TItem}"/>s. They sit at opposite ends of the window contract and are worth
/// pinning for opposite reasons: the attributes came back with the page, so their windows are sliced in memory
/// and must honour the search and the five sorts the paged table offered; the Pending Export's queued changes are
/// unbounded, so their windows must reach the application layer's range read by offset and count, and must pass
/// the skip-the-count contract through rather than counting the whole set on every scroll.
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

    private void SetupAttributeValues(List<ConnectedSystemObjectAttributeValue> values)
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
                AttributeValueTotalCounts = values
                    .GroupBy(v => v.Attribute.Name)
                    .ToDictionary(g => g.Key, g => g.Count()),
                ChangeCount = 0
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

    private static VirtualisedDataGrid<ConnectedSystemObjectDetail.AttributeGroup> AttributeGrid(
        IRenderedComponent<ConnectedSystemObjectDetail> cut) =>
        cut.FindComponent<VirtualisedDataGrid<ConnectedSystemObjectDetail.AttributeGroup>>().Instance;

    private static VirtualisedDataGrid<PendingExportAttributeValueChange> PendingExportGrid(
        IRenderedComponent<ConnectedSystemObjectDetail> cut) =>
        cut.FindComponent<VirtualisedDataGrid<PendingExportAttributeValueChange>>().Instance;

    [Test]
    public void CsoDetail_BothTables_AreVirtualisedGridsWithNoPager()
    {
        SetupAttributeValues(BuildAttributeValues("displayName"));
        SetupPendingExportChanges(BuildPendingChanges(3));

        var cut = RenderPage();

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.HasComponent<VirtualisedDataGrid<ConnectedSystemObjectDetail.AttributeGroup>>(), Is.True,
                    "the attributes must be shown in the shared virtualised grid");
                Assert.That(cut.HasComponent<VirtualisedDataGrid<PendingExportAttributeValueChange>>(), Is.True,
                    "the queued Pending Export changes must be shown in the shared virtualised grid");
                Assert.That(cut.HasComponent<MudBlazor.MudTablePager>(), Is.False,
                    "a virtualised list has no page size to choose and no page controls");
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
    public async Task CsoDetail_AttributeWindow_IsSlicedFromTheValuesThePageAlreadyHoldsAsync()
    {
        SetupAttributeValues(BuildAttributeValues("alpha", "bravo", "charlie", "delta"));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<VirtualisedDataGrid<ConnectedSystemObjectDetail.AttributeGroup>>(), Is.True));

        var window = await AttributeGrid(cut).LoadWindow(
            new VirtualisedWindowRequest(1, 2, null, "attribute", false, IncludeTotalCount: true),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(g => g.AttributeName), Is.EqualTo(new[] { "bravo", "charlie" }),
                "the window must be the requested slice of the sorted attributes, not the first rows of it");
            Assert.That(window.TotalItems, Is.EqualTo(4));
        }
    }

    [Test]
    public async Task CsoDetail_AttributeWindowSortedByValue_OrdersOnTheValueRatherThanTheNameAsync()
    {
        // The paged table offered five sorts and the grid's headers still claim them; a header that reordered
        // nothing, or that quietly fell back to the attribute name, would be indistinguishable at a glance.
        SetupAttributeValues(BuildAttributeValues("alpha", "bravo"));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<VirtualisedDataGrid<ConnectedSystemObjectDetail.AttributeGroup>>(), Is.True));

        var window = await AttributeGrid(cut).LoadWindow(
            new VirtualisedWindowRequest(0, 25, null, "value", true, IncludeTotalCount: false),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(g => g.AttributeName), Is.EqualTo(new[] { "bravo", "alpha" }));
            Assert.That(window.TotalItems, Is.Null);
        }
    }

    [Test]
    public async Task CsoDetail_AttributeWindowWithASearch_MatchesTheAttributeNameAndItsValuesAsync()
    {
        SetupAttributeValues(BuildAttributeValues("department", "title"));
        var cut = RenderPage();
        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<VirtualisedDataGrid<ConnectedSystemObjectDetail.AttributeGroup>>(), Is.True));

        var byName = await AttributeGrid(cut).LoadWindow(
            new VirtualisedWindowRequest(0, 25, "DEPART", "attribute", false, IncludeTotalCount: true),
            CancellationToken.None);
        var byValue = await AttributeGrid(cut).LoadWindow(
            new VirtualisedWindowRequest(0, 25, "title-value", "attribute", false, IncludeTotalCount: true),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byName.Items.Select(g => g.AttributeName), Is.EqualTo(new[] { "department" }));
            Assert.That(byValue.Items.Select(g => g.AttributeName), Is.EqualTo(new[] { "title" }));
        }
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

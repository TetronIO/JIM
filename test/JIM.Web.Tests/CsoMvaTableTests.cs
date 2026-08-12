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
using JIM.Models.Utility;
using JIM.Web.Models;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the inline table of one Connected System Object attribute's values now that it is a
/// <see cref="VirtualisedDataGrid{TItem}"/>. The behaviour worth pinning is the seam between the two: the grid
/// asks for an arbitrary window (offset and count), while the application layer only serves whole pages, so the
/// component stitches the pages spanning a window. Getting that arithmetic wrong shows the reader the wrong
/// values with no error anywhere, which is exactly the class of defect a unit test can catch and a glance cannot.
/// </summary>
[TestFixture]
public class CsoMvaTableTests : JimComponentTestContext
{
    private const string AttributeName = "member";
    private const int SourcePageSize = 100;

    private static readonly Guid ConnectedSystemObjectId = Guid.NewGuid();

    private Mock<IConnectedSystemRepository> _connectedSystems = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    /// <summary>
    /// Serves the given values a page at a time, exactly as the repository does (ordered, offset by page), so a
    /// test can assert on the window the component assembles rather than on the pages it happened to ask for.
    /// </summary>
    private void SetupValues(IReadOnlyList<ConnectedSystemObjectAttributeValue> values, string? expectedSearch = null)
    {
        _connectedSystems
            .Setup(r => r.GetAttributeValuesPagedAsync(ConnectedSystemObjectId, AttributeName,
                It.IsAny<int>(), It.IsAny<int>(), expectedSearch))
            .ReturnsAsync((Guid _, string _, int page, int pageSize, string? _) => new PagedResultSet<ConnectedSystemObjectAttributeValue>
            {
                Results = values.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                TotalResults = values.Count,
                CurrentPage = page,
                PageSize = pageSize
            });
    }

    private static List<ConnectedSystemObjectAttributeValue> BuildValues(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                Attribute = new ConnectedSystemObjectTypeAttribute { Name = AttributeName, Type = AttributeDataType.Text },
                StringValue = $"value-{i:D4}"
            })
            .ToList();

    private IRenderedComponent<CsoMvaTable> RenderTable() =>
        Render<CsoMvaTable>(p => p
            .Add(c => c.AttributeName, AttributeName)
            .Add(c => c.ConnectedSystemObjectId, ConnectedSystemObjectId)
            .Add(c => c.IsReferenceAttribute, false));

    private static Func<VirtualisedWindowRequest, CancellationToken, Task<VirtualisedWindow<ConnectedSystemObjectAttributeValue>>>
        LoadWindow(IRenderedComponent<CsoMvaTable> cut) =>
        cut.FindComponent<VirtualisedDataGrid<ConnectedSystemObjectAttributeValue>>().Instance.LoadWindow;

    [Test]
    public void CsoMvaTable_RendersAVirtualisedGridWithTheValuesInIt()
    {
        SetupValues(BuildValues(3));

        var cut = RenderTable();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.HasComponent<VirtualisedDataGrid<ConnectedSystemObjectAttributeValue>>(), Is.True,
                "the values must be shown in the shared virtualised grid, not a hand-rolled table");
            Assert.That(cut.Markup, Does.Contain("value-0000"));
            Assert.That(cut.Markup, Does.Contain("3 Values"), "the toolbar count says how many values there are");
        });
    }

    [Test]
    public void CsoMvaTable_HasNoPager()
    {
        SetupValues(BuildValues(3));

        var cut = RenderTable();

        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<MudBlazor.MudTablePager>(), Is.False,
                "a virtualised list has no page size to choose and no page controls"));
    }

    [Test]
    public async Task CsoMvaTable_WindowInsideOnePage_ReturnsThatSliceAsync()
    {
        var values = BuildValues(250);
        SetupValues(values);
        var cut = RenderTable();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(10, 5, null, "order", false, IncludeTotalCount: true), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(v => v.StringValue),
                Is.EqualTo(values.Skip(10).Take(5).Select(v => v.StringValue)));
            Assert.That(window.TotalItems, Is.EqualTo(250));
        }
    }

    /// <summary>
    /// The window the virtualiser asks for is not aligned to the application layer's pages, so a window that
    /// straddles a page boundary has to be joined from both. This is the arithmetic that silently shows the
    /// wrong values if it is wrong.
    /// </summary>
    [Test]
    public async Task CsoMvaTable_WindowStraddlingAPageBoundary_IsStitchedFromBothPagesAsync()
    {
        var values = BuildValues(250);
        SetupValues(values);
        var cut = RenderTable();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(SourcePageSize - 3, 10, null, "order", false, IncludeTotalCount: true),
            CancellationToken.None);

        Assert.That(window.Items.Select(v => v.StringValue),
            Is.EqualTo(values.Skip(SourcePageSize - 3).Take(10).Select(v => v.StringValue)));
    }

    [Test]
    public async Task CsoMvaTable_WindowRunningPastTheEnd_StopsAtTheLastValueAsync()
    {
        var values = BuildValues(12);
        SetupValues(values);
        var cut = RenderTable();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(8, 30, null, "order", false, IncludeTotalCount: true), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items, Has.Count.EqualTo(4));
            Assert.That(window.TotalItems, Is.EqualTo(12));
        }
    }

    /// <summary>
    /// Counting is the expensive half of a window read, so the grid only asks for it when the filters changed;
    /// a loader that ignores that turns every scroll into a count query.
    /// </summary>
    [Test]
    public async Task CsoMvaTable_WindowNotAskingForTheCount_ReturnsANullTotalAsync()
    {
        SetupValues(BuildValues(40));
        var cut = RenderTable();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(0, 10, null, "order", false, IncludeTotalCount: false), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalItems, Is.Null, "null means not counted, and must not be read as no matches");
            Assert.That(window.Items, Has.Count.EqualTo(10));
        }
    }

    [Test]
    public async Task CsoMvaTable_WindowWithSearchText_PassesItToTheApplicationLayerAsync()
    {
        var matches = BuildValues(2);
        SetupValues(BuildValues(40));
        SetupValues(matches, expectedSearch: "value-000");
        var cut = RenderTable();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(0, 10, "value-000", "order", false, IncludeTotalCount: true), CancellationToken.None);

        Assert.That(window.TotalItems, Is.EqualTo(2),
            "the search must narrow the read rather than being applied to an already-windowed page");
    }

    [Test]
    public void CsoMvaTable_WithNoValues_SaysSoRatherThanShowingAnEmptyTable()
    {
        SetupValues([]);

        var cut = RenderTable();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.HasComponent<TableEmptyState>(), Is.True);
            Assert.That(cut.FindComponent<TableEmptyState>().Instance.PrimaryText,
                Is.EqualTo("This attribute has no values"));
        });
    }

    [Test]
    public void CsoMvaTable_WithValues_ShowsNoEmptyState()
    {
        SetupValues(BuildValues(5));

        var cut = RenderTable();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("value-0000")));
        Assert.That(cut.HasComponent<TableEmptyState>(), Is.False,
            "an empty state over rows that are merely in flight tells the reader the opposite of the truth");
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}

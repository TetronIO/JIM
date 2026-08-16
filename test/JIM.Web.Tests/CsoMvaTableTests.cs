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
/// <see cref="VirtualisedDataGrid{TItem}"/> over the application layer's range read. The behaviour worth pinning
/// is the seam between the two: the grid asks for an arbitrary window (offset and count) and the range read is
/// addressed the same way, so the component must hand the request over unchanged, in one call, and must pass the
/// total back exactly as it came, including a null one. Each of those going wrong shows the reader the wrong
/// values (or none) with no error anywhere.
/// </summary>
[TestFixture]
public class CsoMvaTableTests : JimComponentTestContext
{
    private const string AttributeName = "member";

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
    /// Serves the given values as the repository's range read does: the window at the requested offset and count,
    /// and the total only when it was asked for (null, never zero, when it was not).
    /// </summary>
    private void SetupValues(IReadOnlyList<ConnectedSystemObjectAttributeValue> values, string? expectedSearch = null)
    {
        _connectedSystems
            .Setup(r => r.GetAttributeValuesRangeAsync(ConnectedSystemObjectId, AttributeName,
                It.IsAny<int>(), It.IsAny<int>(), expectedSearch, It.IsAny<bool>()))
            .ReturnsAsync((Guid _, string _, int offset, int count, string? _, bool includeTotalCount) =>
                new RangeResultSet<ConnectedSystemObjectAttributeValue>
                {
                    Results = values.Skip(offset).Take(count).ToList(),
                    TotalResults = includeTotalCount ? values.Count : null
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

    /// <summary>
    /// The grid addresses windows by absolute offset and count, and so does the range read behind them, so the
    /// request goes over unchanged. Any arithmetic here (the page stitching this replaced) is a chance to hand
    /// the reader values from the wrong place with no error anywhere.
    /// </summary>
    [Test]
    public async Task CsoMvaTable_Window_ReadsTheOffsetAndCountItWasAskedForAsync()
    {
        var values = BuildValues(250);
        SetupValues(values);
        var cut = RenderTable();
        _connectedSystems.Invocations.Clear();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(97, 10, null, "order", false, IncludeTotalCount: true), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(v => v.StringValue),
                Is.EqualTo(values.Skip(97).Take(10).Select(v => v.StringValue)));
            Assert.That(window.TotalItems, Is.EqualTo(250));
        }

        _connectedSystems.Verify(r => r.GetAttributeValuesRangeAsync(
            ConnectedSystemObjectId, AttributeName, 97, 10, null, true), Times.Once);
    }

    /// <summary>
    /// One window is one read. It used to take two whenever the window did not start on a page boundary, which
    /// is most of them.
    /// </summary>
    [Test]
    public async Task CsoMvaTable_Window_CostsASingleReadAsync()
    {
        SetupValues(BuildValues(250));
        var cut = RenderTable();
        _connectedSystems.Invocations.Clear();

        await LoadWindow(cut)(
            new VirtualisedWindowRequest(97, 10, null, "order", false, IncludeTotalCount: true), CancellationToken.None);

        _connectedSystems.Verify(r => r.GetAttributeValuesRangeAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Once, "a window that does not align to a page boundary must not cost a second round trip");
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
    /// Counting is the expensive half of a window read, so the grid only asks for it when the filters changed.
    /// The loader must pass that through rather than counting anyway, and must report the resulting absent total
    /// as null: a zero in its place reads as "nothing matched" and empties the list.
    /// </summary>
    [Test]
    public async Task CsoMvaTable_WindowNotAskingForTheCount_DoesNotCountAndReturnsANullTotalAsync()
    {
        SetupValues(BuildValues(40));
        var cut = RenderTable();
        _connectedSystems.Invocations.Clear();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(0, 10, null, "order", false, IncludeTotalCount: false), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalItems, Is.Null, "null means not counted, and must not be read as no matches");
            Assert.That(window.Items, Has.Count.EqualTo(10));
        }

        _connectedSystems.Verify(r => r.GetAttributeValuesRangeAsync(
            ConnectedSystemObjectId, AttributeName, 0, 10, null, false), Times.Once);
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

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
using JIM.Models.Utility;
using JIM.Web.Models;
using JIM.Web.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the multi-valued attribute tables on the Metaverse Object detail surfaces (<see cref="MvoDetailsPanel"/>
/// and its tabbed sibling <see cref="MvoDetailsTabs"/>) now that both are <see cref="VirtualisedDataGrid{TItem}"/>s.
/// The two surfaces carry the same table, so both are pinned here rather than only whichever one is looked at.
///
/// The behaviour that matters is which side a window comes from: an attribute the page already holds every value
/// of is windowed in memory, and one whose values were capped by the detail load is read from the application
/// layer. Getting that wrong is invisible on screen (both render rows) and costs a database read per scroll, or
/// silently shows only the capped subset as though it were the whole set.
/// </summary>
[TestFixture]
public class MvoDetailsMvaTableTests : JimComponentTestContext
{
    private const string AttributeName = "Group Memberships";

    private static readonly Guid MetaverseObjectId = Guid.NewGuid();

    private Mock<IMetaverseRepository> _metaverse = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _metaverse = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.Metaverse).Returns(_metaverse.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    /// <summary>
    /// Serves the given values as the repository's range read does: the window at the requested offset and count,
    /// and the total only when it was asked for (null, never zero, when it was not).
    /// </summary>
    private void SetupServerValues(IReadOnlyList<MetaverseObjectAttributeValue> values)
    {
        _metaverse
            .Setup(r => r.GetAttributeValuesRangeAsync(MetaverseObjectId, AttributeName,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync((Guid _, string _, int offset, int count, string? _, bool includeTotalCount) =>
                new RangeResultSet<MetaverseObjectAttributeValue>
                {
                    Results = values.Skip(offset).Take(count).ToList(),
                    TotalResults = includeTotalCount ? values.Count : null
                });
    }

    private static MetaverseObject BuildObject(int valueCount)
    {
        var attribute = new MetaverseAttribute
        {
            Id = 1,
            Name = AttributeName,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued
        };

        return new MetaverseObject
        {
            Id = MetaverseObjectId,
            Type = new MetaverseObjectType { Id = 3, Name = "Group" },
            AttributeValues = Enumerable.Range(0, valueCount)
                .Select(i => new MetaverseObjectAttributeValue
                {
                    Id = Guid.NewGuid(),
                    Attribute = attribute,
                    StringValue = $"value-{i:D4}"
                })
                .ToList()
        };
    }

    private IRenderedComponent<MvoDetailsPanel> RenderPanel(int loadedValueCount, int? totalCount = null) =>
        Render<MvoDetailsPanel>(p => p
            .Add(c => c.MetaverseObject, BuildObject(loadedValueCount))
            .Add(c => c.ObjectTypeId, 3)
            .Add(c => c.AttributeValueTotalCounts, totalCount.HasValue
                ? new Dictionary<string, int> { [AttributeName] = totalCount.Value }
                : new Dictionary<string, int>()));

    private IRenderedComponent<MvoDetailsTabs> RenderTabs(int loadedValueCount, int? totalCount = null) =>
        Render<MvoDetailsTabs>(p => p
            .Add(c => c.MetaverseObject, BuildObject(loadedValueCount))
            .Add(c => c.ObjectTypeId, 3)
            .Add(c => c.AttributeValueTotalCounts, totalCount.HasValue
                ? new Dictionary<string, int> { [AttributeName] = totalCount.Value }
                : new Dictionary<string, int>()));

    private static Func<VirtualisedWindowRequest, CancellationToken, Task<VirtualisedWindow<MetaverseObjectAttributeValue>>>
        LoadWindow(IRenderedComponent<IComponent> cut) =>
        cut.FindComponent<VirtualisedDataGrid<MetaverseObjectAttributeValue>>().Instance.LoadWindow;

    #region Rendering

    [Test]
    public void MvoDetailsPanel_MultiValuedAttribute_RendersAVirtualisedGridWithNoPager()
    {
        var cut = RenderPanel(loadedValueCount: 12);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.HasComponent<VirtualisedDataGrid<MetaverseObjectAttributeValue>>(), Is.True);
            Assert.That(cut.Markup, Does.Contain("value-0000"));
        });
        Assert.That(cut.HasComponent<MudTablePager>(), Is.False,
            "a virtualised list has no page size to choose and no page controls");
    }

    [Test]
    public void MvoDetailsTabs_MultiValuedAttribute_RendersAVirtualisedGridWithNoPager()
    {
        var cut = RenderTabs(loadedValueCount: 12);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.HasComponent<VirtualisedDataGrid<MetaverseObjectAttributeValue>>(), Is.True);
            Assert.That(cut.Markup, Does.Contain("value-0000"));
        });
        Assert.That(cut.HasComponent<MudTablePager>(), Is.False);
    }

    #endregion

    #region Windowing

    [Test]
    public async Task MvoDetailsPanel_UncappedAttribute_WindowsTheValuesThePageAlreadyHoldsAsync()
    {
        var cut = RenderPanel(loadedValueCount: 12);

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(4, 3, null, "order", false, IncludeTotalCount: true), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(v => v.StringValue), Is.EqualTo(new[] { "value-0004", "value-0005", "value-0006" }));
            Assert.That(window.TotalItems, Is.EqualTo(12));
        }

        _metaverse.Verify(r => r.GetAttributeValuesRangeAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>()),
            Times.Never, "every value is already on the page; reading them back costs a database round trip per scroll");
    }

    [Test]
    public async Task MvoDetailsPanel_UncappedAttribute_SearchNarrowsTheWindowInMemoryAsync()
    {
        var cut = RenderPanel(loadedValueCount: 12);

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(0, 10, "value-0007", "order", false, IncludeTotalCount: true), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(v => v.StringValue), Is.EqualTo(new[] { "value-0007" }));
            Assert.That(window.TotalItems, Is.EqualTo(1), "the count must describe the match set, not the whole list");
        }
    }

    [Test]
    public async Task MvoDetailsPanel_UncappedAttribute_WindowNotAskingForTheCount_ReturnsANullTotalAsync()
    {
        var cut = RenderPanel(loadedValueCount: 12);

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(0, 5, null, "order", false, IncludeTotalCount: false), CancellationToken.None);

        Assert.That(window.TotalItems, Is.Null, "null means not counted, and must not be read as no matches");
    }

    /// <summary>
    /// A capped attribute holds more values in the database than the detail load brought back, so its windows have
    /// to come from the application layer. Windowing the loaded subset instead would quietly present the cap as
    /// the whole membership. The request goes over unchanged, and costs one read rather than the two the page
    /// stitching this replaced needed for any window not starting on a page boundary.
    /// </summary>
    [Test]
    public async Task MvoDetailsPanel_CappedAttribute_ReadsItsWindowFromTheApplicationLayerAsync()
    {
        SetupServerValues(BuildObject(250).AttributeValues);
        var cut = RenderPanel(loadedValueCount: 3, totalCount: 250);
        _metaverse.Invocations.Clear();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(98, 5, null, "order", false, IncludeTotalCount: true), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalItems, Is.EqualTo(250));
            Assert.That(window.Items.Select(v => v.StringValue),
                Is.EqualTo(new[] { "value-0098", "value-0099", "value-0100", "value-0101", "value-0102" }));
        }

        _metaverse.Verify(r => r.GetAttributeValuesRangeAsync(
            MetaverseObjectId, AttributeName, 98, 5, null, true), Times.Once,
            "the offset and count are handed over as they arrived, in one read");
    }

    /// <summary>
    /// Counting is the expensive half of a window read, so a capped attribute's loader must pass the grid's
    /// "do not count" through, and must report the resulting absent total as null: a zero in its place reads as
    /// "nothing matched" and empties the table.
    /// </summary>
    [Test]
    public async Task MvoDetailsPanel_CappedAttribute_WindowNotAskingForTheCount_DoesNotCountAndReturnsANullTotalAsync()
    {
        SetupServerValues(BuildObject(250).AttributeValues);
        var cut = RenderPanel(loadedValueCount: 3, totalCount: 250);
        _metaverse.Invocations.Clear();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(0, 5, null, "order", false, IncludeTotalCount: false), CancellationToken.None);

        Assert.That(window.TotalItems, Is.Null, "null means not counted, and must not be read as no matches");

        _metaverse.Verify(r => r.GetAttributeValuesRangeAsync(
            MetaverseObjectId, AttributeName, 0, 5, null, false), Times.Once);
    }

    [Test]
    public async Task MvoDetailsTabs_UncappedAttribute_WindowsTheValuesThePageAlreadyHoldsAsync()
    {
        var cut = RenderTabs(loadedValueCount: 12);

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(9, 5, null, "order", false, IncludeTotalCount: true), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(v => v.StringValue), Is.EqualTo(new[] { "value-0009", "value-0010", "value-0011" }));
            Assert.That(window.TotalItems, Is.EqualTo(12));
        }
    }

    [Test]
    public async Task MvoDetailsTabs_CappedAttribute_ReadsItsWindowFromTheApplicationLayerAsync()
    {
        SetupServerValues(BuildObject(120).AttributeValues);
        var cut = RenderTabs(loadedValueCount: 3, totalCount: 120);
        _metaverse.Invocations.Clear();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(7, 4, null, "order", false, IncludeTotalCount: true), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.TotalItems, Is.EqualTo(120));
            Assert.That(window.Items.Select(v => v.StringValue),
                Is.EqualTo(new[] { "value-0007", "value-0008", "value-0009", "value-0010" }));
        }

        _metaverse.Verify(r => r.GetAttributeValuesRangeAsync(
            MetaverseObjectId, AttributeName, 7, 4, null, true), Times.Once);
    }

    #endregion

    #region Empty states

    [Test]
    public void MvoDetailsPanel_WithValues_ShowsNoEmptyState()
    {
        var cut = RenderPanel(loadedValueCount: 12);

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("value-0011")));
        Assert.That(cut.HasComponent<TableEmptyState>(), Is.False,
            "an empty state over rows that are merely in flight tells the reader the opposite of the truth");
    }

    [Test]
    public async Task MvoDetailsPanel_SearchMatchingNothing_OffersTheWayOutOfItAsync()
    {
        var cut = RenderPanel(loadedValueCount: 12);
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("value-0000")));

        var searchBox = cut.FindComponent<SearchField>().FindComponent<MudTextField<string>>();
        await cut.InvokeAsync(() => searchBox.Instance.ValueChanged.InvokeAsync("no-such-value"));

        cut.WaitForAssertion(() =>
        {
            var emptyState = cut.FindComponent<TableEmptyState>().Instance;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(emptyState.PrimaryText, Is.EqualTo("No values match \"no-such-value\""));
                Assert.That(emptyState.ActionText, Is.EqualTo("Clear Search"));
            }
        });
    }

    [Test]
    public void MvoDetailsTabs_WithValues_ShowsNoEmptyState()
    {
        var cut = RenderTabs(loadedValueCount: 12);

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("value-0011")));
        Assert.That(cut.HasComponent<TableEmptyState>(), Is.False);
    }

    #endregion

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}

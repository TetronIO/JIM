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
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the inline table of one Metaverse Object attribute's values now that it is a
/// <see cref="VirtualisedDataGrid{TItem}"/> over the application layer's range read: that the grid is what renders
/// the values, that a window is one read at exactly the offset and count that was asked for, that an uncounted
/// window comes back with a null total rather than a zero, and that an attribute with nothing in it says so
/// rather than showing an empty table.
/// </summary>
[TestFixture]
public class MvoMvaTableTests : JimComponentTestContext
{
    private const string AttributeName = "Static Members";

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
    private void SetupValues(IReadOnlyList<MetaverseObjectAttributeValue> values)
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

    private static List<MetaverseObjectAttributeValue> BuildValues(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                Attribute = new MetaverseAttribute { Name = AttributeName, Type = AttributeDataType.Text },
                StringValue = $"value-{i:D4}"
            })
            .ToList();

    private IRenderedComponent<MvoMvaTable> RenderTable(bool isReferenceAttribute = false) =>
        Render<MvoMvaTable>(p => p
            .Add(c => c.AttributeName, AttributeName)
            .Add(c => c.MetaverseObjectId, MetaverseObjectId)
            .Add(c => c.IsReferenceAttribute, isReferenceAttribute));

    private static Func<VirtualisedWindowRequest, CancellationToken, Task<VirtualisedWindow<MetaverseObjectAttributeValue>>>
        LoadWindow(IRenderedComponent<MvoMvaTable> cut) =>
        cut.FindComponent<VirtualisedDataGrid<MetaverseObjectAttributeValue>>().Instance.LoadWindow;

    [Test]
    public void MvoMvaTable_RendersAVirtualisedGridWithTheValuesInIt()
    {
        SetupValues(BuildValues(4));

        var cut = RenderTable();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.HasComponent<VirtualisedDataGrid<MetaverseObjectAttributeValue>>(), Is.True);
            Assert.That(cut.Markup, Does.Contain("value-0000"));
            Assert.That(cut.Markup, Does.Contain("4 Values"));
        });
    }

    [Test]
    public void MvoMvaTable_HasNoPager()
    {
        SetupValues(BuildValues(4));

        var cut = RenderTable();

        cut.WaitForAssertion(() =>
            Assert.That(cut.HasComponent<MudBlazor.MudTablePager>(), Is.False));
    }

    [Test]
    public void MvoMvaTable_ForAReferenceAttribute_KeepsTheDisplayNameAndTypeColumns()
    {
        SetupValues(BuildValues(2));

        var cut = RenderTable(isReferenceAttribute: true);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Display Name"));
            Assert.That(cut.Markup, Does.Contain("Type"));
        });
    }

    /// <summary>
    /// The grid addresses windows by absolute offset and count, and so does the range read behind them, so the
    /// request goes over unchanged and in a single read; the page stitching this replaced took two whenever the
    /// window did not start on a page boundary.
    /// </summary>
    [Test]
    public async Task MvoMvaTable_Window_ReadsTheOffsetAndCountItWasAskedForInOneReadAsync()
    {
        var values = BuildValues(250);
        SetupValues(values);
        var cut = RenderTable();
        _metaverse.Invocations.Clear();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(96, 12, null, "order", false, IncludeTotalCount: true),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(v => v.StringValue),
                Is.EqualTo(values.Skip(96).Take(12).Select(v => v.StringValue)));
            Assert.That(window.TotalItems, Is.EqualTo(250));
        }

        _metaverse.Verify(r => r.GetAttributeValuesRangeAsync(
            MetaverseObjectId, AttributeName, 96, 12, null, true), Times.Once);
    }

    /// <summary>
    /// Counting is the expensive half of a window read, so a request that does not ask for it must not trigger
    /// one, and the absent total must stay null: a zero in its place reads as "nothing matched".
    /// </summary>
    [Test]
    public async Task MvoMvaTable_WindowNotAskingForTheCount_DoesNotCountAndReturnsANullTotalAsync()
    {
        SetupValues(BuildValues(30));
        var cut = RenderTable();
        _metaverse.Invocations.Clear();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(0, 5, null, "order", false, IncludeTotalCount: false), CancellationToken.None);

        Assert.That(window.TotalItems, Is.Null, "null means not counted, and must not be read as no matches");

        _metaverse.Verify(r => r.GetAttributeValuesRangeAsync(
            MetaverseObjectId, AttributeName, 0, 5, null, false), Times.Once);
    }

    [Test]
    public void MvoMvaTable_WithNoValues_SaysSoRatherThanShowingAnEmptyTable()
    {
        SetupValues([]);

        var cut = RenderTable();

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindComponent<TableEmptyState>().Instance.PrimaryText,
                Is.EqualTo("This attribute has no values")));
    }

    [Test]
    public void MvoMvaTable_WithValues_ShowsNoEmptyState()
    {
        SetupValues(BuildValues(6));

        var cut = RenderTable();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("value-0005")));
        Assert.That(cut.HasComponent<TableEmptyState>(), Is.False);
    }

    private sealed class FakeJimApplicationFactory(IRepository repository) : IJimApplicationFactory
    {
        public JimApplication Create() => new(repository);
    }
}

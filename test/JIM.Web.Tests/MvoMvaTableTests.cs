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
/// <see cref="VirtualisedDataGrid{TItem}"/>: that the grid is what renders the values, that an arbitrary window
/// is stitched correctly from the whole pages the application layer serves, and that an attribute with nothing
/// in it says so rather than showing an empty table.
/// </summary>
[TestFixture]
public class MvoMvaTableTests : JimComponentTestContext
{
    private const string AttributeName = "Static Members";
    private const int SourcePageSize = 100;

    private static readonly Guid MetaverseObjectId = Guid.NewGuid();

    private Mock<IMetaverseRepository> _metaverse = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _metaverse = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.Metaverse).Returns(_metaverse.Object);

        Services.AddSingleton<IJimApplicationFactory>(new FakeJimApplicationFactory(repository.Object));
    }

    private void SetupValues(IReadOnlyList<MetaverseObjectAttributeValue> values)
    {
        _metaverse
            .Setup(r => r.GetAttributeValuesPagedAsync(MetaverseObjectId, AttributeName,
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid _, string _, int page, int pageSize, string? _) => new PagedResultSet<MetaverseObjectAttributeValue>
            {
                Results = values.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                TotalResults = values.Count,
                CurrentPage = page,
                PageSize = pageSize
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

    [Test]
    public async Task MvoMvaTable_WindowStraddlingAPageBoundary_IsStitchedFromBothPagesAsync()
    {
        var values = BuildValues(250);
        SetupValues(values);
        var cut = RenderTable();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(SourcePageSize - 4, 12, null, "order", false, IncludeTotalCount: true),
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(window.Items.Select(v => v.StringValue),
                Is.EqualTo(values.Skip(SourcePageSize - 4).Take(12).Select(v => v.StringValue)));
            Assert.That(window.TotalItems, Is.EqualTo(250));
        }
    }

    [Test]
    public async Task MvoMvaTable_WindowNotAskingForTheCount_ReturnsANullTotalAsync()
    {
        SetupValues(BuildValues(30));
        var cut = RenderTable();

        var window = await LoadWindow(cut)(
            new VirtualisedWindowRequest(0, 5, null, "order", false, IncludeTotalCount: false), CancellationToken.None);

        Assert.That(window.TotalItems, Is.Null, "null means not counted, and must not be read as no matches");
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

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Utility;
using JIM.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Covers the Metaverse Object's Table view, which renders through the same <see cref="AttributeTable{TItem}"/> as
/// the Connected System Object's and Pending Export's attribute tables, so it offers the same search and sorts and
/// shows a multi-valued attribute the same way: stacked inline up to the limit, a nested virtualised table beyond.
/// </summary>
[TestFixture]
public class MvoDetailsTableTests : JimComponentTestContext
{
    private static readonly Guid MetaverseObjectId = Guid.NewGuid();

    private Mock<IMetaverseRepository> _metaverse = null!;

    protected override void ConfigureAdditionalServices()
    {
        var repository = new Mock<IRepository>();
        _metaverse = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.Metaverse).Returns(_metaverse.Object);

        var syncRepository = new Mock<ISyncRepository>();
        syncRepository
            .Setup(r => r.GetGeneratedValueAssignmentHeadersForMetaverseObjectAsync(It.IsAny<Guid>()))
            .ReturnsAsync([]);

        Services.AddSingleton<IJimApplicationFactory>(
            new FakeJimApplicationFactory(new JimApplication(repository.Object, syncRepository: syncRepository.Object)));
    }

    private static MetaverseAttribute Attribute(string name, AttributePlurality plurality = AttributePlurality.SingleValued) =>
        new() { Name = name, Type = AttributeDataType.Text, AttributePlurality = plurality };

    private static List<MetaverseObjectAttributeValue> SingleValues(params string[] names) =>
        names.Select(name => new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = Attribute(name),
            StringValue = $"{name}-value"
        }).ToList();

    private static List<MetaverseObjectAttributeValue> MultiValues(string name, int count)
    {
        var attribute = Attribute(name, AttributePlurality.MultiValued);
        return Enumerable.Range(0, count).Select(i => new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = attribute,
            StringValue = $"{name}-value-{i:D3}"
        }).ToList();
    }

    private IRenderedComponent<MvoDetailsTable> RenderTable(
        List<MetaverseObjectAttributeValue> values, Dictionary<string, int>? totalCounts = null) =>
        Render<MvoDetailsTable>(p => p
            .Add(c => c.MetaverseObject, new MetaverseObject { Id = MetaverseObjectId, AttributeValues = values })
            .Add(c => c.AttributeValueTotalCounts, totalCounts ?? new Dictionary<string, int>()));

    private static AttributeTable<MvoDetailsTable.MvoAttributeTableGroup> Table(IRenderedComponent<MvoDetailsTable> cut) =>
        cut.FindComponent<AttributeTable<MvoDetailsTable.MvoAttributeTableGroup>>().Instance;

    private static IEnumerable<string> VisibleAttributeNames(IRenderedComponent<MvoDetailsTable> cut) =>
        Table(cut).VisibleItems.Select(g => g.AttributeName);

    [Test]
    public void MvoDetailsTable_RendersThroughTheSharedAttributeTableSortedByName()
    {
        var cut = RenderTable(SingleValues("Job Title", "Display Name", "Employee ID"));

        cut.WaitForAssertion(() =>
            Assert.That(VisibleAttributeNames(cut), Is.EqualTo(new[] { "Display Name", "Employee ID", "Job Title" })));
    }

    [Test]
    public async Task MvoDetailsTable_Search_MatchesTheAttributeNameAndItsValuesAsync()
    {
        var cut = RenderTable([.. SingleValues("Display Name", "Job Title"), .. MultiValues("Other Telephones", 2)]);
        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<AttributeTable<MvoDetailsTable.MvoAttributeTableGroup>>(), Is.True));

        await cut.InvokeAsync(() => Table(cut).SetSearchAsync("job"));
        var byName = VisibleAttributeNames(cut).ToList();
        await cut.InvokeAsync(() => Table(cut).SetSearchAsync("Other Telephones-value-001"));
        var byValue = VisibleAttributeNames(cut).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byName, Is.EqualTo(new[] { "Job Title" }));
            Assert.That(byValue, Is.EqualTo(new[] { "Other Telephones" }),
                "a value beyond the first is searchable too, since it is on the page");
        }
    }

    [Test]
    public async Task MvoDetailsTable_SortByPlurality_GroupsSingleAndMultiValuedAttributesAsync()
    {
        var cut = RenderTable([.. MultiValues("Other Telephones", 2), .. SingleValues("Display Name", "Job Title")]);
        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<AttributeTable<MvoDetailsTable.MvoAttributeTableGroup>>(), Is.True));

        await cut.InvokeAsync(() => Table(cut).ToggleSortAsync("plurality"));

        Assert.That(VisibleAttributeNames(cut).Last(), Is.EqualTo("Other Telephones"));
    }

    [Test]
    public void MvoDetailsTable_MultiValuedAttributeWithinTheInlineLimit_StacksEveryValueInTheRow()
    {
        var cut = RenderTable(MultiValues("Other Telephones", 3));

        cut.WaitForAssertion(() =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(cut.FindAll(".jim-attr-expanded .jim-attr-expanded-item"), Has.Count.EqualTo(3));
                Assert.That(cut.HasComponent<MvoMvaTable>(), Is.False);
            }
        });
    }

    [Test]
    public void MvoDetailsTable_MultiValuedAttributeBeyondTheInlineLimit_HoldsAnEmbeddedNestedTable()
    {
        var values = MultiValues("Static Members", 500);
        _metaverse
            .Setup(r => r.GetAttributeValuesRangeAsync(MetaverseObjectId, "Static Members",
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .ReturnsAsync((Guid _, string _, int offset, int count, string? _, bool includeTotalCount) =>
                new RangeResultSet<MetaverseObjectAttributeValue>
                {
                    Results = values.Skip(offset).Take(count).ToList(),
                    TotalResults = includeTotalCount ? values.Count : null
                });

        var cut = RenderTable(values.Take(10).ToList(), new Dictionary<string, int> { ["Static Members"] = 500 });

        cut.WaitForAssertion(() => Assert.That(cut.HasComponent<MvoMvaTable>(), Is.True));
        var grid = cut.FindComponent<VirtualisedDataGrid<MetaverseObjectAttributeValue>>().Instance;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(grid.Embedded, Is.True,
                "one table sits on the page per large attribute, so none may claim the address bar or the density toggle");
            Assert.That(grid.MaxHeight, Is.Not.Null.And.Not.Empty,
                "its container is a table cell, not the page, so it has to state its own height ceiling");
        }
    }

    private sealed class FakeJimApplicationFactory(JimApplication jim) : IJimApplicationFactory
    {
        public JimApplication Create() => jim;
    }
}

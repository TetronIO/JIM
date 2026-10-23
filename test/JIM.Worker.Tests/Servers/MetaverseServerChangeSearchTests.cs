// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Servers;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.History;
using JIM.Models.Utility;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The validation <see cref="MetaverseServer.SearchMetaverseObjectChangesRangeAsync"/> performs before querying: each
/// attribute condition is resolved to its attribute's type and refused with a reason when it cannot be searched.
/// The query itself is covered against real PostgreSQL by <c>MetaverseObjectChangeSearchDatabaseTests</c>.
/// </summary>
[TestFixture]
public class MetaverseServerChangeSearchTests
{
    private Mock<IMetaverseRepository> _metaverse = null!;
    private JimApplication _jim = null!;
    private MetaverseObjectChangeSearchQuery? _queried;

    [SetUp]
    public void SetUp()
    {
        _queried = null;
        var repository = new Mock<IRepository>();
        _metaverse = new Mock<IMetaverseRepository>();
        repository.Setup(r => r.Metaverse).Returns(_metaverse.Object);
        _jim = new JimApplication(repository.Object);

        _metaverse
            .Setup(r => r.GetMetaverseAttributeAsync(7, false))
            .ReturnsAsync(new MetaverseAttribute { Id = 7, Name = "Employee Number", Type = AttributeDataType.Number });
        _metaverse
            .Setup(r => r.SearchMetaverseObjectChangesRangeAsync(It.IsAny<MetaverseObjectChangeSearchQuery>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Callback<MetaverseObjectChangeSearchQuery, int, int, bool>((q, _, _, _) => _queried = q)
            .ReturnsAsync(new RangeResultSet<MetaverseObjectChangeSearchResult>());
    }

    [TearDown]
    public void TearDown()
    {
        _jim.Dispose();
    }

    [Test]
    public async Task SearchMetaverseObjectChangesRangeAsync_ValidCondition_QueriesWithTheParsedTypedValueAsync()
    {
        var criteria = new MetaverseObjectChangeSearchCriteria
        {
            DisplayNameSearch = "  Bob ",
            AttributeConditions = [new() { AttributeId = 7, Operator = AttributeChangeOperator.ChangedTo, Value = "42" }]
        };

        await _jim.Metaverse.SearchMetaverseObjectChangesRangeAsync(criteria, 0, 50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_queried!.DisplayNameSearch, Is.EqualTo("Bob"));
            Assert.That(_queried!.Conditions.Single().AttributeType, Is.EqualTo(AttributeDataType.Number));
            Assert.That(_queried!.Conditions.Single().Match!.IntValue, Is.EqualTo(42));
            Assert.That(_queried!.Conditions.Single().Operator, Is.EqualTo(AttributeChangeOperator.ChangedTo));
        }
    }

    [Test]
    public void SearchMetaverseObjectChangesRangeAsync_UnknownAttribute_IsRefusedWithoutQuerying()
    {
        var criteria = new MetaverseObjectChangeSearchCriteria { AttributeConditions = [new() { AttributeId = 8 }] };

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _jim.Metaverse.SearchMetaverseObjectChangesRangeAsync(criteria, 0, 50));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain("8"));
            Assert.That(_queried, Is.Null);
        }
    }

    [Test]
    public void SearchMetaverseObjectChangesRangeAsync_ValueNotOfTheAttributesType_IsRefusedNamingTheAttribute()
    {
        var criteria = new MetaverseObjectChangeSearchCriteria
        {
            AttributeConditions = [new() { AttributeId = 7, Value = "forty-two" }]
        };

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _jim.Metaverse.SearchMetaverseObjectChangesRangeAsync(criteria, 0, 50));

        Assert.That(ex!.Message, Does.StartWith("Employee Number:").And.Contain("forty-two"));
    }

    [Test]
    public void SearchMetaverseObjectChangesRangeAsync_TimeRangeEndingBeforeItStarts_IsRefused()
    {
        var criteria = new MetaverseObjectChangeSearchCriteria
        {
            FromDate = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
            ToDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        Assert.ThrowsAsync<ArgumentException>(() => _jim.Metaverse.SearchMetaverseObjectChangesRangeAsync(criteria, 0, 50));
    }

    [Test]
    public void SearchMetaverseObjectChangesRangeAsync_TooManyConditions_IsRefused()
    {
        var criteria = new MetaverseObjectChangeSearchCriteria
        {
            AttributeConditions = Enumerable.Range(0, MetaverseServer.MaxChangeSearchConditions + 1)
                .Select(_ => new MetaverseObjectChangeAttributeCondition { AttributeId = 7 })
                .ToList()
        };

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _jim.Metaverse.SearchMetaverseObjectChangesRangeAsync(criteria, 0, 50));

        Assert.That(ex!.Message, Does.Contain(MetaverseServer.MaxChangeSearchConditions.ToString()));
    }

    [Test]
    public void SearchMetaverseObjectChangesRangeAsync_UndefinedOperator_IsRefused()
    {
        var criteria = new MetaverseObjectChangeSearchCriteria
        {
            AttributeConditions = [new() { AttributeId = 7, Operator = (AttributeChangeOperator)99 }]
        };

        Assert.ThrowsAsync<ArgumentException>(() => _jim.Metaverse.SearchMetaverseObjectChangesRangeAsync(criteria, 0, 50));
    }
}

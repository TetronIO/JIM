using System;
using System.Linq;
using JIM.Web.Extensions.Api;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Api.Tests;

[TestFixture]
public class QueryableExtensionsTests
{
    private class TestItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public Guid ExternalId { get; set; }
    }

    #region ToPaginatedResponse Tests

    [Test]
    public void ToPaginatedResponse_ReturnsCorrectItemsForFirstPage()
    {
        var items = Enumerable.Range(1, 50).Select(i => new TestItem { Id = i }).AsQueryable();
        var request = new PaginationRequest { Page = 1, PageSize = 10 };

        var result = items.ToPaginatedResponse(request);

        Assert.That(result.Items.Count(), Is.EqualTo(10));
        Assert.That(result.Items.First().Id, Is.EqualTo(1));
        Assert.That(result.Items.Last().Id, Is.EqualTo(10));
    }

    [Test]
    public void ToPaginatedResponse_ReturnsCorrectItemsForSecondPage()
    {
        var items = Enumerable.Range(1, 50).Select(i => new TestItem { Id = i }).AsQueryable();
        var request = new PaginationRequest { Page = 2, PageSize = 10 };

        var result = items.ToPaginatedResponse(request);

        Assert.That(result.Items.Count(), Is.EqualTo(10));
        Assert.That(result.Items.First().Id, Is.EqualTo(11));
        Assert.That(result.Items.Last().Id, Is.EqualTo(20));
    }

    [Test]
    public void ToPaginatedResponse_ReturnsCorrectTotalCount()
    {
        var items = Enumerable.Range(1, 47).Select(i => new TestItem { Id = i }).AsQueryable();
        var request = new PaginationRequest { Page = 1, PageSize = 10 };

        var result = items.ToPaginatedResponse(request);

        Assert.That(result.TotalCount, Is.EqualTo(47));
    }

    [Test]
    public void ToPaginatedResponse_ReturnsPartialLastPage()
    {
        var items = Enumerable.Range(1, 25).Select(i => new TestItem { Id = i }).AsQueryable();
        var request = new PaginationRequest { Page = 3, PageSize = 10 };

        var result = items.ToPaginatedResponse(request);

        Assert.That(result.Items.Count(), Is.EqualTo(5));
        Assert.That(result.Items.First().Id, Is.EqualTo(21));
    }

    [Test]
    public void ToPaginatedResponse_ReturnsEmptyForPageBeyondData()
    {
        var items = Enumerable.Range(1, 10).Select(i => new TestItem { Id = i }).AsQueryable();
        var request = new PaginationRequest { Page = 5, PageSize = 10 };

        var result = items.ToPaginatedResponse(request);

        Assert.That(result.Items, Is.Empty);
        Assert.That(result.TotalCount, Is.EqualTo(10));
    }

    #endregion

    #region ApplySort Tests

    [Test]
    public void ApplySort_SortsAscendingByName()
    {
        var items = new[]
        {
            new TestItem { Id = 1, Name = "Charlie" },
            new TestItem { Id = 2, Name = "Alice" },
            new TestItem { Id = 3, Name = "Bob" }
        }.AsQueryable();

        var result = items.ApplySort("Name", false).ToList();

        Assert.That(result[0].Name, Is.EqualTo("Alice"));
        Assert.That(result[1].Name, Is.EqualTo("Bob"));
        Assert.That(result[2].Name, Is.EqualTo("Charlie"));
    }

    [Test]
    public void ApplySort_SortsDescendingByName()
    {
        var items = new[]
        {
            new TestItem { Id = 1, Name = "Charlie" },
            new TestItem { Id = 2, Name = "Alice" },
            new TestItem { Id = 3, Name = "Bob" }
        }.AsQueryable();

        var result = items.ApplySort("Name", true).ToList();

        Assert.That(result[0].Name, Is.EqualTo("Charlie"));
        Assert.That(result[1].Name, Is.EqualTo("Bob"));
        Assert.That(result[2].Name, Is.EqualTo("Alice"));
    }

    [Test]
    public void ApplySort_SortsByIdAscending()
    {
        var items = new[]
        {
            new TestItem { Id = 3 },
            new TestItem { Id = 1 },
            new TestItem { Id = 2 }
        }.AsQueryable();

        var result = items.ApplySort("Id", false).ToList();

        Assert.That(result[0].Id, Is.EqualTo(1));
        Assert.That(result[1].Id, Is.EqualTo(2));
        Assert.That(result[2].Id, Is.EqualTo(3));
    }

    [Test]
    public void ApplySort_IsCaseInsensitive()
    {
        var items = new[]
        {
            new TestItem { Id = 3 },
            new TestItem { Id = 1 },
            new TestItem { Id = 2 }
        }.AsQueryable();

        var result = items.ApplySort("id", false).ToList();

        Assert.That(result[0].Id, Is.EqualTo(1));
    }

    [Test]
    public void ApplySort_ReturnsUnsortedWhenPropertyNotFound()
    {
        var items = new[]
        {
            new TestItem { Id = 3 },
            new TestItem { Id = 1 },
            new TestItem { Id = 2 }
        }.AsQueryable();

        var result = items.ApplySort("NonExistent", false).ToList();

        // Should maintain original order
        Assert.That(result[0].Id, Is.EqualTo(3));
        Assert.That(result[1].Id, Is.EqualTo(1));
        Assert.That(result[2].Id, Is.EqualTo(2));
    }

    [Test]
    public void ApplySort_ReturnsUnsortedWhenPropertyNameIsNull()
    {
        var items = new[]
        {
            new TestItem { Id = 3 },
            new TestItem { Id = 1 }
        }.AsQueryable();

        var result = items.ApplySort(null, false).ToList();

        Assert.That(result[0].Id, Is.EqualTo(3));
    }

    [Test]
    public void ApplySort_ReturnsUnsortedWhenPropertyNameIsEmpty()
    {
        var items = new[]
        {
            new TestItem { Id = 3 },
            new TestItem { Id = 1 }
        }.AsQueryable();

        var result = items.ApplySort("", false).ToList();

        Assert.That(result[0].Id, Is.EqualTo(3));
    }

    #endregion

    #region ApplyFilter Tests

    [Test]
    public void ApplyFilter_FiltersStringEquality()
    {
        var items = new[]
        {
            new TestItem { Id = 1, Name = "Alice" },
            new TestItem { Id = 2, Name = "Bob" },
            new TestItem { Id = 3, Name = "Alice" }
        }.AsQueryable();

        var result = items.ApplyFilter("Name:eq:Alice").ToList();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.All(x => x.Name == "Alice"), Is.True);
    }

    [Test]
    public void ApplyFilter_FiltersStringNotEqual()
    {
        var items = new[]
        {
            new TestItem { Id = 1, Name = "Alice" },
            new TestItem { Id = 2, Name = "Bob" },
            new TestItem { Id = 3, Name = "Charlie" }
        }.AsQueryable();

        var result = items.ApplyFilter("Name:ne:Alice").ToList();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Any(x => x.Name == "Alice"), Is.False);
    }

    [Test]
    public void ApplyFilter_FiltersStringContains()
    {
        var items = new[]
        {
            new TestItem { Id = 1, Name = "Alice" },
            new TestItem { Id = 2, Name = "Bob" },
            new TestItem { Id = 3, Name = "Alicia" }
        }.AsQueryable();

        var result = items.ApplyFilter("Name:contains:lic").ToList();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(x => x.Name), Does.Contain("Alice"));
        Assert.That(result.Select(x => x.Name), Does.Contain("Alicia"));
    }

    [Test]
    public void ApplyFilter_FiltersStringStartsWith()
    {
        var items = new[]
        {
            new TestItem { Id = 1, Name = "Alice" },
            new TestItem { Id = 2, Name = "Bob" },
            new TestItem { Id = 3, Name = "Alicia" }
        }.AsQueryable();

        var result = items.ApplyFilter("Name:startswith:Ali").ToList();

        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void ApplyFilter_FiltersStringEndsWith()
    {
        var items = new[]
        {
            new TestItem { Id = 1, Name = "Alice" },
            new TestItem { Id = 2, Name = "Bob" },
            new TestItem { Id = 3, Name = "Grace" }
        }.AsQueryable();

        var result = items.ApplyFilter("Name:endswith:ce").ToList();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(x => x.Name), Does.Contain("Alice"));
        Assert.That(result.Select(x => x.Name), Does.Contain("Grace"));
    }

    [Test]
    public void ApplyFilter_FiltersIntegerEquality()
    {
        var items = new[]
        {
            new TestItem { Id = 1 },
            new TestItem { Id = 2 },
            new TestItem { Id = 3 }
        }.AsQueryable();

        var result = items.ApplyFilter("Id:eq:2").ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(2));
    }

    [Test]
    public void ApplyFilter_FiltersIntegerGreaterThan()
    {
        var items = new[]
        {
            new TestItem { Id = 1 },
            new TestItem { Id = 2 },
            new TestItem { Id = 3 }
        }.AsQueryable();

        var result = items.ApplyFilter("Id:gt:1").ToList();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.All(x => x.Id > 1), Is.True);
    }

    [Test]
    public void ApplyFilter_FiltersIntegerLessThan()
    {
        var items = new[]
        {
            new TestItem { Id = 1 },
            new TestItem { Id = 2 },
            new TestItem { Id = 3 }
        }.AsQueryable();

        var result = items.ApplyFilter("Id:lt:3").ToList();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.All(x => x.Id < 3), Is.True);
    }

    [Test]
    public void ApplyFilter_FiltersBooleanEquality()
    {
        var items = new[]
        {
            new TestItem { Id = 1, IsActive = true },
            new TestItem { Id = 2, IsActive = false },
            new TestItem { Id = 3, IsActive = true }
        }.AsQueryable();

        var result = items.ApplyFilter("IsActive:eq:true").ToList();

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.All(x => x.IsActive), Is.True);
    }

    [Test]
    public void ApplyFilter_FiltersGuidEquality()
    {
        var guid1 = Guid.NewGuid();
        var guid2 = Guid.NewGuid();
        var items = new[]
        {
            new TestItem { Id = 1, ExternalId = guid1 },
            new TestItem { Id = 2, ExternalId = guid2 }
        }.AsQueryable();

        var result = items.ApplyFilter($"ExternalId:eq:{guid1}").ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ExternalId, Is.EqualTo(guid1));
    }

    [Test]
    public void ApplyFilter_ReturnsAllWhenFilterIsNull()
    {
        var items = new[]
        {
            new TestItem { Id = 1 },
            new TestItem { Id = 2 }
        }.AsQueryable();

        var result = items.ApplyFilter(null).ToList();

        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void ApplyFilter_ReturnsAllWhenFilterIsEmpty()
    {
        var items = new[]
        {
            new TestItem { Id = 1 },
            new TestItem { Id = 2 }
        }.AsQueryable();

        var result = items.ApplyFilter("").ToList();

        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void ApplyFilter_ReturnsAllWhenFilterFormatInvalid()
    {
        var items = new[]
        {
            new TestItem { Id = 1 },
            new TestItem { Id = 2 }
        }.AsQueryable();

        var result = items.ApplyFilter("InvalidFormat").ToList();

        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void ApplyFilter_ReturnsAllWhenPropertyNotFound()
    {
        var items = new[]
        {
            new TestItem { Id = 1 },
            new TestItem { Id = 2 }
        }.AsQueryable();

        var result = items.ApplyFilter("NonExistent:eq:value").ToList();

        Assert.That(result, Has.Count.EqualTo(2));
    }

    [Test]
    public void ApplyFilter_IsCaseInsensitiveForPropertyName()
    {
        var items = new[]
        {
            new TestItem { Id = 1, Name = "Alice" },
            new TestItem { Id = 2, Name = "Bob" }
        }.AsQueryable();

        var result = items.ApplyFilter("name:eq:Alice").ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("Alice"));
    }

    #endregion

    #region ApplySortAndFilter Tests

    [Test]
    public void ApplySortAndFilter_AppliesBothSortingAndFiltering()
    {
        var items = new[]
        {
            new TestItem { Id = 3, Name = "Charlie", IsActive = true },
            new TestItem { Id = 1, Name = "Alice", IsActive = true },
            new TestItem { Id = 2, Name = "Bob", IsActive = false },
            new TestItem { Id = 4, Name = "David", IsActive = true }
        }.AsQueryable();

        var request = new PaginationRequest
        {
            Filter = "IsActive:eq:true",
            SortBy = "Name",
            SortDirection = "asc"
        };

        var result = items.ApplySortAndFilter(request).ToList();

        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0].Name, Is.EqualTo("Alice"));
        Assert.That(result[1].Name, Is.EqualTo("Charlie"));
        Assert.That(result[2].Name, Is.EqualTo("David"));
    }

    #endregion
}

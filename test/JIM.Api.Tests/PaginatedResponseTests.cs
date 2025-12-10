using System;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Api.Tests;

[TestFixture]
public class PaginatedResponseTests
{
    [Test]
    public void Create_WithItems_SetsItemsCorrectly()
    {
        var items = new[] { "a", "b", "c" };
        var response = PaginatedResponse<string>.Create(items, 100, 1, 25);

        Assert.That(response.Items, Is.EqualTo(items));
    }

    [Test]
    public void Create_SetsTotalCountCorrectly()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 42, 1, 25);

        Assert.That(response.TotalCount, Is.EqualTo(42));
    }

    [Test]
    public void Create_SetsPageCorrectly()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 100, 3, 25);

        Assert.That(response.Page, Is.EqualTo(3));
    }

    [Test]
    public void Create_SetsPageSizeCorrectly()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 100, 1, 50);

        Assert.That(response.PageSize, Is.EqualTo(50));
    }

    [Test]
    public void TotalPages_CalculatesCorrectly_WhenExactlyDivisible()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 100, 1, 25);

        Assert.That(response.TotalPages, Is.EqualTo(4));
    }

    [Test]
    public void TotalPages_CalculatesCorrectly_WhenNotExactlyDivisible()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 101, 1, 25);

        Assert.That(response.TotalPages, Is.EqualTo(5));
    }

    [Test]
    public void TotalPages_ReturnsOne_WhenTotalCountIsZero()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 0, 1, 25);

        Assert.That(response.TotalPages, Is.EqualTo(0));
    }

    [Test]
    public void TotalPages_ReturnsOne_WhenTotalCountLessThanPageSize()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 10, 1, 25);

        Assert.That(response.TotalPages, Is.EqualTo(1));
    }

    [Test]
    public void HasNextPage_ReturnsTrue_WhenNotOnLastPage()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 100, 1, 25);

        Assert.That(response.HasNextPage, Is.True);
    }

    [Test]
    public void HasNextPage_ReturnsFalse_WhenOnLastPage()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 100, 4, 25);

        Assert.That(response.HasNextPage, Is.False);
    }

    [Test]
    public void HasNextPage_ReturnsFalse_WhenOnlyOnePage()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 10, 1, 25);

        Assert.That(response.HasNextPage, Is.False);
    }

    [Test]
    public void HasPreviousPage_ReturnsFalse_WhenOnFirstPage()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 100, 1, 25);

        Assert.That(response.HasPreviousPage, Is.False);
    }

    [Test]
    public void HasPreviousPage_ReturnsTrue_WhenOnSecondPage()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 100, 2, 25);

        Assert.That(response.HasPreviousPage, Is.True);
    }

    [Test]
    public void HasPreviousPage_ReturnsTrue_WhenOnLastPage()
    {
        var response = PaginatedResponse<string>.Create(Array.Empty<string>(), 100, 4, 25);

        Assert.That(response.HasPreviousPage, Is.True);
    }
}

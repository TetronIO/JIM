using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Api.Tests;

[TestFixture]
public class PaginationRequestTests
{
    [Test]
    public void Page_DefaultsToOne()
    {
        var request = new PaginationRequest();
        Assert.That(request.Page, Is.EqualTo(1));
    }

    [Test]
    public void PageSize_DefaultsTo25()
    {
        var request = new PaginationRequest();
        Assert.That(request.PageSize, Is.EqualTo(25));
    }

    [Test]
    public void Page_WhenSetToZero_ReturnsOne()
    {
        var request = new PaginationRequest { Page = 0 };
        Assert.That(request.Page, Is.EqualTo(1));
    }

    [Test]
    public void Page_WhenSetToNegative_ReturnsOne()
    {
        var request = new PaginationRequest { Page = -5 };
        Assert.That(request.Page, Is.EqualTo(1));
    }

    [Test]
    public void PageSize_WhenSetToZero_ReturnsDefault()
    {
        var request = new PaginationRequest { PageSize = 0 };
        Assert.That(request.PageSize, Is.EqualTo(25));
    }

    [Test]
    public void PageSize_WhenSetToNegative_ReturnsDefault()
    {
        var request = new PaginationRequest { PageSize = -10 };
        Assert.That(request.PageSize, Is.EqualTo(25));
    }

    [Test]
    public void PageSize_WhenExceedsMax_ReturnsCappedAt100()
    {
        var request = new PaginationRequest { PageSize = 500 };
        Assert.That(request.PageSize, Is.EqualTo(100));
    }

    [Test]
    public void PageSize_WhenWithinRange_ReturnsSetValue()
    {
        var request = new PaginationRequest { PageSize = 50 };
        Assert.That(request.PageSize, Is.EqualTo(50));
    }

    [Test]
    public void SortDirection_DefaultsToAsc()
    {
        var request = new PaginationRequest();
        Assert.That(request.SortDirection, Is.EqualTo("asc"));
    }

    [Test]
    public void IsDescending_WhenSortDirectionIsDesc_ReturnsTrue()
    {
        var request = new PaginationRequest { SortDirection = "desc" };
        Assert.That(request.IsDescending, Is.True);
    }

    [Test]
    public void IsDescending_WhenSortDirectionIsDescUpperCase_ReturnsTrue()
    {
        var request = new PaginationRequest { SortDirection = "DESC" };
        Assert.That(request.IsDescending, Is.True);
    }

    [Test]
    public void IsDescending_WhenSortDirectionIsAsc_ReturnsFalse()
    {
        var request = new PaginationRequest { SortDirection = "asc" };
        Assert.That(request.IsDescending, Is.False);
    }

    [Test]
    public void Skip_CalculatesCorrectlyForFirstPage()
    {
        var request = new PaginationRequest { Page = 1, PageSize = 25 };
        Assert.That(request.Skip, Is.EqualTo(0));
    }

    [Test]
    public void Skip_CalculatesCorrectlyForSecondPage()
    {
        var request = new PaginationRequest { Page = 2, PageSize = 25 };
        Assert.That(request.Skip, Is.EqualTo(25));
    }

    [Test]
    public void Skip_CalculatesCorrectlyForThirdPageWith10Items()
    {
        var request = new PaginationRequest { Page = 3, PageSize = 10 };
        Assert.That(request.Skip, Is.EqualTo(20));
    }
}

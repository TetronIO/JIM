// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

[TestFixture]
public class PaginationRequestTests
{
    // Runs the DataAnnotations validation that [ApiController] uses to auto-generate a 400 for a
    // bound PaginationRequest, so these tests exercise the exact mechanism behind the HTTP 400.
    private static List<ValidationResult> Validate(PaginationRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

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

    [Test]
    public void MaxPage_IsOneThousand()
    {
        // The page-depth cap is a single named constant so it can be tuned in one place.
        Assert.That(PaginationRequest.MaxPage, Is.EqualTo(1000));
    }

    [Test]
    public void Page_AtMaxPage_PassesValidation()
    {
        // The boundary itself is allowed; only requests beyond it are rejected.
        var request = new PaginationRequest { Page = PaginationRequest.MaxPage };
        var results = Validate(request);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Page_JustUnderMaxPage_PassesValidation()
    {
        var request = new PaginationRequest { Page = PaginationRequest.MaxPage - 1 };
        var results = Validate(request);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Page_JustOverMaxPage_FailsValidationWithHelpfulMessage()
    {
        // Beyond the cap must fail validation, which [ApiController] surfaces as a 400 rather than
        // silently clamping, so the caller learns they have over-paged.
        var request = new PaginationRequest { Page = PaginationRequest.MaxPage + 1 };
        var results = Validate(request);
        Assert.That(results, Is.Not.Empty);
        Assert.That(results[0].MemberNames, Does.Contain(nameof(PaginationRequest.Page)));
        Assert.That(results[0].ErrorMessage, Does.Contain("1000"));
    }

    [Test]
    public void Page_FarBeyondMaxPage_FailsValidation()
    {
        var request = new PaginationRequest { Page = int.MaxValue };
        var results = Validate(request);
        Assert.That(results, Is.Not.Empty);
        Assert.That(results.Exists(r => r.MemberNames.Contains(nameof(PaginationRequest.Page))), Is.True);
    }
}

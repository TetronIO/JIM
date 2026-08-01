// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the shared pagination depth rule (issue #487). The rule bounds how far into a result set a
/// caller may page, so PostgreSQL is never asked to grind through an absurd OFFSET scan.
/// </summary>
[TestFixture]
public class PaginationLimitsTests
{
    [Test]
    public void MaxSkip_IsOneMillion()
    {
        // A single named constant so the ceiling can be tuned in one place. Sized above the largest
        // validated deployment scale (500,000 objects) so a legitimate full enumeration is possible.
        Assert.That(PaginationLimits.MaxSkip, Is.EqualTo(1_000_000));
    }

    [Test]
    public void IsWithinDepth_FirstPage_IsAllowed()
    {
        Assert.That(PaginationLimits.IsWithinDepth(1, 100), Is.True);
    }

    [Test]
    public void IsWithinDepth_AtTheCeiling_IsAllowed()
    {
        // Offset of exactly MaxSkip is the boundary and is permitted; only beyond it is rejected.
        Assert.That(PaginationLimits.IsWithinDepth(10_001, 100), Is.True);
    }

    [Test]
    public void IsWithinDepth_JustBeyondTheCeiling_IsRejected()
    {
        Assert.That(PaginationLimits.IsWithinDepth(10_002, 100), Is.False);
    }

    [Test]
    public void IsWithinDepth_ScalesWithPageSize_NotPageNumber()
    {
        // The database cost is the OFFSET, not the page number, so the same page number is allowed at a
        // small page size and rejected at a large one. A page-number-only cap gets this wrong in both
        // directions, which is why the rule is expressed as a maximum offset.
        Assert.That(PaginationLimits.IsWithinDepth(20_000, 10), Is.True);
        Assert.That(PaginationLimits.IsWithinDepth(20_000, 100), Is.False);
    }

    [Test]
    public void IsWithinDepth_AtIntMaxValue_IsRejectedWithoutOverflowing()
    {
        // (page - 1) * pageSize overflows a 32-bit int well before int.MaxValue pages; the rule must
        // evaluate in 64-bit arithmetic or a huge page number wraps negative and slips through.
        Assert.That(PaginationLimits.IsWithinDepth(int.MaxValue, 100), Is.False);
    }

    [Test]
    public void IsWithinDepth_ZeroOrNegativePage_IsTreatedAsFirstPage()
    {
        // Controllers clamp a sub-1 page to 1 rather than rejecting it; the depth rule must agree, or a
        // request the action would happily serve gets a 400 instead.
        Assert.That(PaginationLimits.IsWithinDepth(0, 100), Is.True);
        Assert.That(PaginationLimits.IsWithinDepth(-5, 100), Is.True);
    }

    [Test]
    public void IsWithinDepth_OversizedPageSize_IsEvaluatedAgainstTheClampedPageSize()
    {
        // Page size is clamped to 100 before the query runs, so the depth rule must evaluate the page size
        // the query will actually use; otherwise an oversized pageSize is rejected for a cost never incurred.
        Assert.That(PaginationLimits.IsWithinDepth(10_001, 100_000), Is.True);
    }

    [Test]
    public void MaxPageFor_ReturnsTheDeepestAllowedPageForAPageSize()
    {
        Assert.That(PaginationLimits.MaxPageFor(100), Is.EqualTo(10_001));
        Assert.That(PaginationLimits.MaxPageFor(50), Is.EqualTo(20_001));
    }

    [Test]
    public void DepthExceededMessage_NamesTheLimitAndHowToAvoidIt()
    {
        var message = PaginationLimits.DepthExceededMessage(50_000, 100);

        Assert.That(message, Does.Contain("1000000").Or.Contain("1,000,000"));
        Assert.That(message, Does.Contain("10001").Or.Contain("10,001"), "The message should tell the caller the deepest page they may request at their page size.");
    }
}

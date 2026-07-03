// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Search;
using NUnit.Framework;

namespace JIM.Models.Tests.Search;

/// <summary>
/// Tests for the Temporal Scope Reconciler's candidate value window (issue #892). The window is
/// (B(afterUtc), B(nowUtc)] where B is the relative boundary; these tests pin the Hours-exact case,
/// the day-rounding case (empty window between midnights), and the bootstrap (null lower bound) case.
/// </summary>
[TestFixture]
public class RelativeDateScopeWindowTests
{
    [Test]
    public void Resolve_HoursExact_ZeroOffset_WindowSpansAfterToNow()
    {
        // "value >= now" style criterion (count 0). B(t) == t for Hours, so the window is exactly (after, now].
        var after = new DateTime(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var (lower, upper) = RelativeDateScopeWindow.Resolve(0, RelativeDateUnit.Hours, RelativeDateDirection.FromNow, after, now);

        Assert.That(lower, Is.EqualTo(after));
        Assert.That(upper, Is.EqualTo(now));
    }

    [Test]
    public void Resolve_HoursExact_WithOffset_ShiftsBothBoundsByTheOffset()
    {
        // "startDate <= 24 hours from now": provision 24h early. B(t) = t + 24h, so the value window shifts +24h.
        var after = new DateTime(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var (lower, upper) = RelativeDateScopeWindow.Resolve(24, RelativeDateUnit.Hours, RelativeDateDirection.FromNow, after, now);

        Assert.That(lower, Is.EqualTo(after.AddHours(24)));
        Assert.That(upper, Is.EqualTo(now.AddHours(24)));
    }

    [Test]
    public void Resolve_Days_SameCalendarDay_EmptyWindow()
    {
        // Day-granularity boundaries only move at midnight. Two instants on the same day (after any offset)
        // resolve to the same midnight, so the window collapses (lower == upper) and admits nothing new.
        var after = new DateTime(2026, 6, 15, 1, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 6, 15, 23, 0, 0, DateTimeKind.Utc);

        var (lower, upper) = RelativeDateScopeWindow.Resolve(7, RelativeDateUnit.Days, RelativeDateDirection.Ago, after, now);

        Assert.That(upper, Is.EqualTo(new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc)), "B(now) = (now - 7 days) truncated to midnight");
        Assert.That(lower, Is.EqualTo(upper), "same day either side of the offset resolves to the same midnight boundary");
    }

    [Test]
    public void Resolve_Days_CrossingMidnight_WindowIsOneMidnightStep()
    {
        // When the sweep crosses a midnight, the boundary advances one whole day: the window is exactly the
        // one day whose objects the boundary crossed.
        var after = new DateTime(2026, 6, 14, 23, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 6, 15, 1, 0, 0, DateTimeKind.Utc);

        var (lower, upper) = RelativeDateScopeWindow.Resolve(7, RelativeDateUnit.Days, RelativeDateDirection.Ago, after, now);

        Assert.That(lower, Is.EqualTo(new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(upper, Is.EqualTo(new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void Resolve_NullAfter_Bootstrap_HasNoLowerBound()
    {
        var now = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var (lower, upper) = RelativeDateScopeWindow.Resolve(0, RelativeDateUnit.Hours, RelativeDateDirection.FromNow, null, now);

        Assert.That(lower, Is.Null);
        Assert.That(upper, Is.EqualTo(now));
    }

    [Test]
    public void Resolve_UpperIsNeverBeforeLower_ForAfterBeforeNow()
    {
        // Monotonicity guard: the boundary is non-decreasing in t, so for after <= now the window is well-formed.
        var after = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 12, 31, 23, 59, 0, DateTimeKind.Utc);

        var (lower, upper) = RelativeDateScopeWindow.Resolve(3, RelativeDateUnit.Months, RelativeDateDirection.FromNow, after, now);

        Assert.That(lower, Is.Not.Null);
        Assert.That(lower!.Value, Is.LessThanOrEqualTo(upper));
    }
}

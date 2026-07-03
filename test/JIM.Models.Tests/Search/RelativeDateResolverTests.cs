// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Search;
using NUnit.Framework;

namespace JIM.Models.Tests.Search;

[TestFixture]
public class RelativeDateResolverTests
{
    // A fixed "now" with a non-midnight time component so truncation is observable.
    private static readonly DateTime NowUtc = new(2026, 6, 15, 14, 30, 45, DateTimeKind.Utc);

    [Test]
    public void Resolve_DaysAgo_SubtractsAndTruncatesToMidnightUtc()
    {
        var result = RelativeDateResolver.Resolve(7, RelativeDateUnit.Days, RelativeDateDirection.Ago, NowUtc);
        Assert.That(result, Is.EqualTo(new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc)));
        Assert.That(result.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void Resolve_DaysFromNow_AddsAndTruncatesToMidnightUtc()
    {
        var result = RelativeDateResolver.Resolve(7, RelativeDateUnit.Days, RelativeDateDirection.FromNow, NowUtc);
        Assert.That(result, Is.EqualTo(new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void Resolve_Hours_KeepsExactInstant_NotTruncated()
    {
        var ago = RelativeDateResolver.Resolve(6, RelativeDateUnit.Hours, RelativeDateDirection.Ago, NowUtc);
        Assert.That(ago, Is.EqualTo(new DateTime(2026, 6, 15, 8, 30, 45, DateTimeKind.Utc)));
        Assert.That(ago.Kind, Is.EqualTo(DateTimeKind.Utc));

        var fromNow = RelativeDateResolver.Resolve(2, RelativeDateUnit.Hours, RelativeDateDirection.FromNow, NowUtc);
        Assert.That(fromNow, Is.EqualTo(new DateTime(2026, 6, 15, 16, 30, 45, DateTimeKind.Utc)));
    }

    [Test]
    public void Resolve_Weeks_MultipliesBySevenDays()
    {
        var result = RelativeDateResolver.Resolve(2, RelativeDateUnit.Weeks, RelativeDateDirection.Ago, NowUtc);
        Assert.That(result, Is.EqualTo(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void Resolve_Months_IsCalendarCorrect_ClampsShortMonths()
    {
        // 31 March minus 1 month clamps to the last day of February (.NET AddMonths behaviour).
        var march31 = new DateTime(2026, 3, 31, 9, 0, 0, DateTimeKind.Utc);
        var result = RelativeDateResolver.Resolve(1, RelativeDateUnit.Months, RelativeDateDirection.Ago, march31);
        Assert.That(result, Is.EqualTo(new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void Resolve_Years_IsCalendarCorrect_ClampsLeapDay()
    {
        // 29 Feb 2024 plus 1 year clamps to 28 Feb 2025.
        var leapDay = new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Utc);
        var result = RelativeDateResolver.Resolve(1, RelativeDateUnit.Years, RelativeDateDirection.FromNow, leapDay);
        Assert.That(result, Is.EqualTo(new DateTime(2025, 2, 28, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void Resolve_ZeroDays_IsTodayMidnightUtc()
    {
        var result = RelativeDateResolver.Resolve(0, RelativeDateUnit.Days, RelativeDateDirection.Ago, NowUtc);
        Assert.That(result, Is.EqualTo(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void Resolve_ZeroHours_IsNowExactly()
    {
        var result = RelativeDateResolver.Resolve(0, RelativeDateUnit.Hours, RelativeDateDirection.FromNow, NowUtc);
        Assert.That(result, Is.EqualTo(NowUtc));
    }

    [Test]
    public void Resolve_NegativeCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RelativeDateResolver.Resolve(-1, RelativeDateUnit.Days, RelativeDateDirection.Ago, NowUtc));
    }

    [Test]
    public void Resolve_NormalisesUnspecifiedNowToUtc()
    {
        // A caller passing an Unspecified-kind "now" still gets a UTC-kinded result.
        var unspecified = new DateTime(2026, 6, 15, 14, 30, 45, DateTimeKind.Unspecified);
        var result = RelativeDateResolver.Resolve(1, RelativeDateUnit.Hours, RelativeDateDirection.FromNow, unspecified);
        Assert.That(result.Kind, Is.EqualTo(DateTimeKind.Utc));
    }
}

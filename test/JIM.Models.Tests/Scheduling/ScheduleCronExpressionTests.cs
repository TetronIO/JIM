// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Scheduling;
using NUnit.Framework;

namespace JIM.Models.Tests.Scheduling;

/// <summary>
/// The cron expression a Schedule's guided pattern stands for. The portal's Schedule editor and the REST API (and so
/// New-JIMSchedule and Set-JIMSchedule) both build it here, so a Schedule means the same thing whichever surface
/// created it. Before this existed only the editor could build one, and the API rejected a patterned Schedule outright.
/// </summary>
[TestFixture]
public class ScheduleCronExpressionTests
{
    [TestCase(new[] { 1, 2, 3, 4, 5 }, "0 6 * * 1,2,3,4,5")]
    [TestCase(new[] { 0, 1, 2, 3, 4, 5, 6 }, "0 6 * * *")]
    [TestCase(new[] { 5, 1, 3, 1 }, "0 6 * * 1,3,5")]
    public void BuildSpecificTimes_Days_AreListedInOrderOnceOrAsEveryDay(int[] days, string expected)
    {
        Assert.That(ScheduleCronExpression.BuildSpecificTimes(days, [new TimeSpan(6, 0, 0)]), Is.EqualTo(expected));
    }

    [Test]
    public void BuildSpecificTimes_SeveralTimesInTheSameMinute_ListsEachHour()
    {
        var expression = ScheduleCronExpression.BuildSpecificTimes([0, 6], [new TimeSpan(18, 30, 0), new TimeSpan(6, 30, 0)]);

        Assert.That(expression, Is.EqualTo("30 6,18 * * 0,6"));
    }

    [Test]
    public void BuildInterval_EveryNHoursAllDay_UsesAStep()
    {
        Assert.That(ScheduleCronExpression.BuildInterval([1, 2, 3, 4, 5, 6], 1, ScheduleIntervalUnit.Hours, null, null),
            Is.EqualTo("0 */1 * * 1,2,3,4,5,6"));
    }

    [Test]
    public void BuildInterval_EveryNHoursWithinAWindow_ListsTheHoursFromStartToEnd()
    {
        var expression = ScheduleCronExpression.BuildInterval([1, 2, 3, 4, 5], 2, ScheduleIntervalUnit.Hours,
            new TimeSpan(8, 0, 0), new TimeSpan(18, 0, 0));

        Assert.That(expression, Is.EqualTo("0 8,10,12,14,16,18 * * 1,2,3,4,5"));
    }

    [Test]
    public void BuildInterval_EveryNMinutes_UsesAMinuteStep()
    {
        Assert.That(ScheduleCronExpression.BuildInterval([0, 1, 2, 3, 4, 5, 6], 15, ScheduleIntervalUnit.Minutes, null, null),
            Is.EqualTo("*/15 * * * *"));
    }

    [Test]
    public void TryFromPattern_SpecificTimesAsTheApiSendsThem_BuildsTheExpression()
    {
        var built = ScheduleCronExpression.TryFromPattern(SchedulePatternType.SpecificTimes, "0", "01:00", null, null, null, null,
            out var expression, out var error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built, Is.True, error);
            Assert.That(expression, Is.EqualTo("0 1 * * 0"));
        }
    }

    [Test]
    public void TryFromPattern_IntervalAsTheApiSendsIt_BuildsTheExpression()
    {
        var built = ScheduleCronExpression.TryFromPattern(SchedulePatternType.Interval, "1,2,3,4,5", null, 2, ScheduleIntervalUnit.Hours,
            "08:00", "18:00", out var expression, out var error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built, Is.True, error);
            Assert.That(expression, Is.EqualTo("0 8,10,12,14,16,18 * * 1,2,3,4,5"));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    public void TryFromPattern_NoDaysGiven_RunsEveryDay(string? days)
    {
        ScheduleCronExpression.TryFromPattern(SchedulePatternType.SpecificTimes, days, "06:00", null, null, null, null,
            out var expression, out _);

        Assert.That(expression, Is.EqualTo("0 6 * * *"));
    }

    [TestCase(SchedulePatternType.SpecificTimes, "1,7", "06:00", null, null, null, null, "daysOfWeek")]
    [TestCase(SchedulePatternType.SpecificTimes, "Monday", "06:00", null, null, null, null, "daysOfWeek")]
    [TestCase(SchedulePatternType.SpecificTimes, "1", null, null, null, null, null, "runTimes")]
    [TestCase(SchedulePatternType.SpecificTimes, "1", "25:00", null, null, null, null, "runTimes")]
    [TestCase(SchedulePatternType.SpecificTimes, "1", "6am", null, null, null, null, "runTimes")]
    [TestCase(SchedulePatternType.SpecificTimes, "1", "06:00,18:30", null, null, null, null, "same minute")]
    [TestCase(SchedulePatternType.Interval, "1", null, null, ScheduleIntervalUnit.Hours, null, null, "intervalValue")]
    [TestCase(SchedulePatternType.Interval, "1", null, 0, ScheduleIntervalUnit.Hours, null, null, "intervalValue")]
    [TestCase(SchedulePatternType.Interval, "1", null, 24, ScheduleIntervalUnit.Hours, null, null, "intervalValue")]
    [TestCase(SchedulePatternType.Interval, "1", null, 60, ScheduleIntervalUnit.Minutes, null, null, "intervalValue")]
    [TestCase(SchedulePatternType.Interval, "1", null, 2, null, null, null, "intervalUnit")]
    [TestCase(SchedulePatternType.Interval, "1", null, 2, ScheduleIntervalUnit.Hours, "08:00", null, "intervalWindowStart and intervalWindowEnd")]
    [TestCase(SchedulePatternType.Interval, "1", null, 2, ScheduleIntervalUnit.Hours, "18:00", "08:00", "before")]
    [TestCase(SchedulePatternType.Interval, "1", null, 15, ScheduleIntervalUnit.Minutes, "08:00", "18:00", "hourly")]
    [TestCase(SchedulePatternType.Custom, "1", null, null, null, null, null, "Custom")]
    public void TryFromPattern_IncompleteOrContradictoryPattern_RefusesAndSaysWhy(
        SchedulePatternType pattern, string? days, string? runTimes, int? intervalValue, ScheduleIntervalUnit? unit,
        string? windowStart, string? windowEnd, string mentioned)
    {
        var built = ScheduleCronExpression.TryFromPattern(pattern, days, runTimes, intervalValue, unit, windowStart, windowEnd,
            out var expression, out var error);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built, Is.False);
            Assert.That(expression, Is.Null);
            Assert.That(error, Does.Contain(mentioned));
        }
    }
}

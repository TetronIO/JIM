// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// The one-line trigger description on a Schedule's chip, shown on the Home page and the Operations Schedules tab.
/// Both pages used to carry their own copy of this logic, and each copy wrote a time two ways depending on how the
/// Schedule was stored ("Sun at 1am" beside "Daily at 2:30 AM"); one describer now writes every time as the Schedule
/// editor does.
/// </summary>
[TestFixture]
public class ScheduleTriggerDisplayTests
{
    private static ScheduleHeader Header(
        SchedulePatternType pattern,
        string? daysOfWeek = null,
        string? runTimes = null,
        int? intervalValue = null,
        ScheduleIntervalUnit? intervalUnit = null,
        string? windowStart = null,
        string? windowEnd = null,
        string? cronExpression = null,
        ScheduleTriggerType trigger = ScheduleTriggerType.Cron) => new()
    {
        Name = "Test Schedule",
        TriggerType = trigger,
        PatternType = pattern,
        DaysOfWeek = daysOfWeek,
        RunTimes = runTimes,
        IntervalValue = intervalValue,
        IntervalUnit = intervalUnit,
        IntervalWindowStart = windowStart,
        IntervalWindowEnd = windowEnd,
        CronExpression = cronExpression
    };

    [Test]
    public void Describe_SpecificTimeOnOneDay_WritesTheTimeAsTheEditorDoes()
    {
        var header = Header(SchedulePatternType.SpecificTimes, daysOfWeek: "0", runTimes: "01:00", cronExpression: "0 1 * * 0");

        Assert.That(ScheduleTriggerDisplay.Describe(header), Is.EqualTo("Sun at 1:00 AM"));
    }

    [Test]
    public void Describe_SpecificTimeAndTheSameTimeFromAnExpression_ReadAlike()
    {
        var fromPattern = Header(SchedulePatternType.SpecificTimes, runTimes: "02:30", cronExpression: "30 2 * * *");
        var fromExpression = Header(SchedulePatternType.Custom, cronExpression: "30 2 * * *");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScheduleTriggerDisplay.Describe(fromPattern), Is.EqualTo("Daily at 2:30 AM"));
            Assert.That(ScheduleTriggerDisplay.Describe(fromExpression), Is.EqualTo("Daily at 2:30 AM"));
        }
    }

    [Test]
    public void Describe_SeveralSpecificTimes_ListsEachTime()
    {
        var header = Header(SchedulePatternType.SpecificTimes, daysOfWeek: "1,2,3,4,5", runTimes: "09:00,17:30");

        Assert.That(ScheduleTriggerDisplay.Describe(header), Is.EqualTo("Weekdays at 9:00 AM, 5:30 PM"));
    }

    [Test]
    public void Describe_MoreThanThreeSpecificTimes_CountsThem()
    {
        var header = Header(SchedulePatternType.SpecificTimes, runTimes: "06:00,10:00,14:00,18:00");

        Assert.That(ScheduleTriggerDisplay.Describe(header), Is.EqualTo("Daily, 4 times/day"));
    }

    [TestCase("00:00", "Daily at 12:00 AM")]
    [TestCase("12:00", "Daily at 12:00 PM")]
    [TestCase("23:45", "Daily at 11:45 PM")]
    public void Describe_MidnightNoonAndLateEvening_UseTheTwelveHourClock(string runTime, string expected)
    {
        var header = Header(SchedulePatternType.SpecificTimes, runTimes: runTime);

        Assert.That(ScheduleTriggerDisplay.Describe(header), Is.EqualTo(expected));
    }

    [Test]
    public void Describe_IntervalAllButOneDay_NamesTheMissingDay()
    {
        var header = Header(SchedulePatternType.Interval, daysOfWeek: "1,2,3,4,5,6", intervalValue: 1, intervalUnit: ScheduleIntervalUnit.Hours);

        Assert.That(ScheduleTriggerDisplay.Describe(header), Is.EqualTo("Daily exc. Sun, Every 1 hr"));
    }

    [Test]
    public void Describe_IntervalWithinAWindow_WritesTheWindowTimesAsTheEditorDoes()
    {
        var header = Header(SchedulePatternType.Interval, daysOfWeek: "1,2,3,4,5", intervalValue: 2, intervalUnit: ScheduleIntervalUnit.Hours,
            windowStart: "08:00", windowEnd: "18:00");

        Assert.That(ScheduleTriggerDisplay.Describe(header), Is.EqualTo("Weekdays, Every 2 hr (8:00 AM to 6:00 PM)"));
    }

    [Test]
    public void Describe_GuidedPatternWithoutItsTimes_FallsBackToTheExpression()
    {
        var header = Header(SchedulePatternType.SpecificTimes, cronExpression: "0 9 * * 1-5");

        Assert.That(ScheduleTriggerDisplay.Describe(header), Is.EqualTo("Weekdays at 9:00 AM"));
    }

    [Test]
    public void Describe_ManualTrigger_SaysManual()
    {
        var header = Header(SchedulePatternType.SpecificTimes, runTimes: "01:00", trigger: ScheduleTriggerType.Manual);

        Assert.That(ScheduleTriggerDisplay.Describe(header), Is.EqualTo("Manual"));
    }

    [Test]
    public void Describe_ScheduleAndItsHeader_ReadAlike()
    {
        var schedule = new Schedule
        {
            Name = "Test Schedule",
            TriggerType = ScheduleTriggerType.Cron,
            PatternType = SchedulePatternType.SpecificTimes,
            DaysOfWeek = "0",
            RunTimes = "01:00",
            CronExpression = "0 1 * * 0"
        };
        var header = Header(SchedulePatternType.SpecificTimes, daysOfWeek: "0", runTimes: "01:00", cronExpression: "0 1 * * 0");

        Assert.That(ScheduleTriggerDisplay.Describe(schedule), Is.EqualTo(ScheduleTriggerDisplay.Describe(header)));
    }

    [TestCase(1, 0, "1:00 AM")]
    [TestCase(13, 5, "1:05 PM")]
    [TestCase(0, 30, "12:30 AM")]
    public void FormatTime_WritesHoursAndMinutesOnTheTwelveHourClock(int hours, int minutes, string expected)
    {
        Assert.That(ScheduleTriggerDisplay.FormatTime(new TimeSpan(hours, minutes, 0)), Is.EqualTo(expected));
    }
}

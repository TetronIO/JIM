using System;
using System.Linq;
using JIM.Models.Scheduling;
using NUnit.Framework;

namespace JIM.Models.Tests.Scheduling;

[TestFixture]
public class SchedulePatternTests
{
    #region SchedulePatternType enum tests

    [Test]
    public void SchedulePatternType_SpecificTimes_HasCorrectValue()
    {
        Assert.That((int)SchedulePatternType.SpecificTimes, Is.EqualTo(0));
    }

    [Test]
    public void SchedulePatternType_Interval_HasCorrectValue()
    {
        Assert.That((int)SchedulePatternType.Interval, Is.EqualTo(1));
    }

    [Test]
    public void SchedulePatternType_Custom_HasCorrectValue()
    {
        Assert.That((int)SchedulePatternType.Custom, Is.EqualTo(2));
    }

    #endregion

    #region ScheduleIntervalUnit enum tests

    [Test]
    public void ScheduleIntervalUnit_Minutes_HasCorrectValue()
    {
        Assert.That((int)ScheduleIntervalUnit.Minutes, Is.EqualTo(0));
    }

    [Test]
    public void ScheduleIntervalUnit_Hours_HasCorrectValue()
    {
        Assert.That((int)ScheduleIntervalUnit.Hours, Is.EqualTo(1));
    }

    #endregion

    #region Schedule model pattern configuration tests

    [Test]
    public void Schedule_DefaultPatternType_IsSpecificTimes()
    {
        var schedule = new Schedule();

        Assert.That(schedule.PatternType, Is.EqualTo(SchedulePatternType.SpecificTimes));
    }

    [Test]
    public void Schedule_CanSetSpecificTimesConfiguration()
    {
        var schedule = new Schedule
        {
            PatternType = SchedulePatternType.SpecificTimes,
            DaysOfWeek = "1,2,3,4,5",
            RunTimes = "09:00,12:00,15:00,18:00"
        };

        Assert.That(schedule.PatternType, Is.EqualTo(SchedulePatternType.SpecificTimes));
        Assert.That(schedule.DaysOfWeek, Is.EqualTo("1,2,3,4,5"));
        Assert.That(schedule.RunTimes, Is.EqualTo("09:00,12:00,15:00,18:00"));
    }

    [Test]
    public void Schedule_CanSetIntervalConfiguration()
    {
        var schedule = new Schedule
        {
            PatternType = SchedulePatternType.Interval,
            DaysOfWeek = "1,2,3,4,5",
            IntervalValue = 2,
            IntervalUnit = ScheduleIntervalUnit.Hours,
            IntervalWindowStart = "06:00",
            IntervalWindowEnd = "18:00"
        };

        Assert.That(schedule.PatternType, Is.EqualTo(SchedulePatternType.Interval));
        Assert.That(schedule.IntervalValue, Is.EqualTo(2));
        Assert.That(schedule.IntervalUnit, Is.EqualTo(ScheduleIntervalUnit.Hours));
        Assert.That(schedule.IntervalWindowStart, Is.EqualTo("06:00"));
        Assert.That(schedule.IntervalWindowEnd, Is.EqualTo("18:00"));
    }

    [Test]
    public void Schedule_CanSetIntervalWithoutWindow()
    {
        var schedule = new Schedule
        {
            PatternType = SchedulePatternType.Interval,
            DaysOfWeek = "0,1,2,3,4,5,6",
            IntervalValue = 30,
            IntervalUnit = ScheduleIntervalUnit.Minutes
        };

        Assert.That(schedule.IntervalWindowStart, Is.Null);
        Assert.That(schedule.IntervalWindowEnd, Is.Null);
    }

    [Test]
    public void Schedule_CanSetCustomPattern()
    {
        var schedule = new Schedule
        {
            PatternType = SchedulePatternType.Custom,
            CronExpression = "0 9,12,15,18 * * 1-5"
        };

        Assert.That(schedule.PatternType, Is.EqualTo(SchedulePatternType.Custom));
        Assert.That(schedule.CronExpression, Is.EqualTo("0 9,12,15,18 * * 1-5"));
    }

    [Test]
    public void Schedule_AllDays_CanBeRepresented()
    {
        var schedule = new Schedule
        {
            DaysOfWeek = "0,1,2,3,4,5,6"
        };

        var days = schedule.DaysOfWeek!.Split(',').Select(int.Parse).ToHashSet();

        Assert.That(days.Count, Is.EqualTo(7));
        Assert.That(days.Contains(0), Is.True); // Sunday
        Assert.That(days.Contains(6), Is.True); // Saturday
    }

    [Test]
    public void Schedule_Weekdays_CanBeRepresented()
    {
        var schedule = new Schedule
        {
            DaysOfWeek = "1,2,3,4,5"
        };

        var days = schedule.DaysOfWeek!.Split(',').Select(int.Parse).ToHashSet();

        Assert.That(days.Count, Is.EqualTo(5));
        Assert.That(days.Contains(0), Is.False); // No Sunday
        Assert.That(days.Contains(6), Is.False); // No Saturday
    }

    [Test]
    public void Schedule_Weekends_CanBeRepresented()
    {
        var schedule = new Schedule
        {
            DaysOfWeek = "0,6"
        };

        var days = schedule.DaysOfWeek!.Split(',').Select(int.Parse).ToHashSet();

        Assert.That(days.Count, Is.EqualTo(2));
        Assert.That(days.Contains(0), Is.True);  // Sunday
        Assert.That(days.Contains(6), Is.True);  // Saturday
    }

    [Test]
    public void Schedule_MultipleTimes_CanBeParsed()
    {
        var schedule = new Schedule
        {
            RunTimes = "09:00,12:00,15:00,18:00"
        };

        var times = schedule.RunTimes!.Split(',')
            .Select(t => TimeSpan.Parse(t.Trim()))
            .ToList();

        Assert.That(times.Count, Is.EqualTo(4));
        Assert.That(times[0], Is.EqualTo(new TimeSpan(9, 0, 0)));
        Assert.That(times[1], Is.EqualTo(new TimeSpan(12, 0, 0)));
        Assert.That(times[2], Is.EqualTo(new TimeSpan(15, 0, 0)));
        Assert.That(times[3], Is.EqualTo(new TimeSpan(18, 0, 0)));
    }

    [Test]
    public void Schedule_IntervalWindowTimes_CanBeParsed()
    {
        var schedule = new Schedule
        {
            IntervalWindowStart = "06:00",
            IntervalWindowEnd = "18:00"
        };

        var start = TimeSpan.Parse(schedule.IntervalWindowStart!);
        var end = TimeSpan.Parse(schedule.IntervalWindowEnd!);

        Assert.That(start, Is.EqualTo(new TimeSpan(6, 0, 0)));
        Assert.That(end, Is.EqualTo(new TimeSpan(18, 0, 0)));
    }

    #endregion
}

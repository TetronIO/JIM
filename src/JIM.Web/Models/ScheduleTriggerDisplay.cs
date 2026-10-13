// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// The one-line description of when a Schedule runs, shown on its trigger chip on the Home page and the Operations
/// Schedules tab ("Weekdays at 9:00 AM", "Daily exc. Sun, Every 1 hr").
/// <para>
/// A guided pattern is described from the pattern the Schedule stores, anything else from its cron expression. Both
/// paths write a time the way the Schedule editor's summary does, through <see cref="FormatTime(TimeSpan)"/>, so a Schedule
/// reads the same however it was created.
/// </para>
/// </summary>
public static class ScheduleTriggerDisplay
{
    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>
    /// When the Schedule listed by this header runs.
    /// </summary>
    public static string Describe(ScheduleHeader schedule) => Describe(
        schedule.TriggerType, schedule.PatternType, schedule.DaysOfWeek, schedule.RunTimes, schedule.IntervalValue,
        schedule.IntervalUnit, schedule.IntervalWindowStart, schedule.IntervalWindowEnd, schedule.CronExpression);

    /// <summary>
    /// When this Schedule runs.
    /// </summary>
    public static string Describe(Schedule schedule) => Describe(
        schedule.TriggerType, schedule.PatternType, schedule.DaysOfWeek, schedule.RunTimes, schedule.IntervalValue,
        schedule.IntervalUnit, schedule.IntervalWindowStart, schedule.IntervalWindowEnd, schedule.CronExpression);

    /// <summary>
    /// A time of day on the twelve-hour clock, with minutes always shown ("1:00 AM", "5:30 PM").
    /// </summary>
    public static string FormatTime(TimeSpan time) => FormatTime(time.Hours, time.Minutes);

    private static string Describe(
        ScheduleTriggerType triggerType,
        SchedulePatternType patternType,
        string? daysOfWeek,
        string? runTimes,
        int? intervalValue,
        ScheduleIntervalUnit? intervalUnit,
        string? intervalWindowStart,
        string? intervalWindowEnd,
        string? cronExpression)
    {
        if (triggerType != ScheduleTriggerType.Cron)
            return "Manual";

        var dayDesc = DescribePatternDays(daysOfWeek);

        switch (patternType)
        {
            case SchedulePatternType.Interval:
                var unit = intervalUnit == ScheduleIntervalUnit.Hours ? "hr" : "min";
                var intervalDesc = $"Every {intervalValue ?? 1} {unit}";

                if (!string.IsNullOrEmpty(intervalWindowStart) && !string.IsNullOrEmpty(intervalWindowEnd))
                    return $"{dayDesc}, {intervalDesc} ({FormatTimeString(intervalWindowStart)} to {FormatTimeString(intervalWindowEnd)})";
                return $"{dayDesc}, {intervalDesc}";

            case SchedulePatternType.SpecificTimes when !string.IsNullOrEmpty(runTimes):
                var times = runTimes.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (times.Length <= 3)
                    return $"{dayDesc} at {string.Join(", ", times.Select(t => FormatTimeString(t.Trim())))}";
                return $"{dayDesc}, {times.Length} times/day";
        }

        // A Custom pattern, or a guided one stored without its times, is described from its expression.
        return DescribeCronExpression(cronExpression);
    }

    /// <summary>
    /// The days of a guided pattern, from its comma-separated day numbers (0 is Sunday).
    /// </summary>
    private static string DescribePatternDays(string? daysOfWeek)
    {
        if (string.IsNullOrEmpty(daysOfWeek))
            return "Daily";

        var days = daysOfWeek.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => int.TryParse(d.Trim(), out var day) ? day : -1)
            .Where(d => d is >= 0 and <= 6)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        if (days.Count == 0 || days.Count == 7)
            return "Daily";
        if (days.SequenceEqual([1, 2, 3, 4, 5]))
            return "Weekdays";
        if (days.SequenceEqual([0, 6]))
            return "Weekends";

        // Six days read best as the one day left out.
        if (days.Count == 6)
            return $"Daily exc. {DayNames[Enumerable.Range(0, 7).Except(days).First()]}";

        return string.Join(", ", days.Select(d => DayNames[d]));
    }

    /// <summary>
    /// A stored HH:mm time, as <see cref="FormatTime(TimeSpan)"/> writes it; anything unparseable is shown as stored.
    /// </summary>
    private static string FormatTimeString(string time) =>
        TimeSpan.TryParse(time.Trim(), out var parsed) ? FormatTime(parsed) : time;

    private static string FormatTime(int hour, int minute)
    {
        var period = hour >= 12 ? "PM" : "AM";
        var displayHour = hour > 12 ? hour - 12 : (hour == 0 ? 12 : hour);
        return $"{displayHour}:{minute:D2} {period}";
    }

    /// <summary>
    /// The common shapes of a five-field cron expression; anything else is shown as the expression itself.
    /// </summary>
    private static string DescribeCronExpression(string? cronExpression)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
            return "Not configured";

        var parts = cronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
            return cronExpression;

        var minute = parts[0];
        var hour = parts[1];
        var dayOfMonth = parts[2];
        var month = parts[3];
        var dayOfWeek = parts[4];
        var dayDesc = DescribeCronDays(dayOfWeek);

        // Several specific hours, e.g. "0 9,12,15,18 * * 1-5".
        if (dayOfMonth == "*" && month == "*" && hour.Contains(',') && !hour.Contains('/'))
        {
            var hours = hour.Split(',')
                .Select(h => int.TryParse(h.Trim(), out var value) ? value : -1)
                .Where(h => h >= 0)
                .ToList();

            if (hours.Count > 0)
            {
                var minuteValue = int.TryParse(minute, out var m) ? m : 0;
                return $"{dayDesc} at {string.Join(", ", hours.Select(h => FormatTime(h, minuteValue)))}";
            }
        }

        if (dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            if (hour.StartsWith("*/", StringComparison.Ordinal))
                return $"Every {hour[2..]} hours";
            return $"Daily at {FormatCronTime(hour, minute)}";
        }

        if (dayOfMonth == "*" && month == "*" && int.TryParse(hour, out _))
            return $"{dayDesc} at {FormatCronTime(hour, minute)}";

        if (hour == "*" && dayOfMonth == "*" && month == "*")
        {
            var hourlyDesc = minute == "0" ? "Hourly" : $"Hourly at :{minute.PadLeft(2, '0')}";
            return dayOfWeek != "*" ? $"{dayDesc}, {hourlyDesc.ToLowerInvariant()}" : hourlyDesc;
        }

        if (minute.StartsWith("*/", StringComparison.Ordinal) && hour == "*")
        {
            var interval = minute[2..];
            return dayOfWeek != "*" ? $"{dayDesc}, every {interval} min" : $"Every {interval} minutes";
        }

        if (hour.StartsWith("*/", StringComparison.Ordinal) && minute == "0")
        {
            var interval = hour[2..];
            return dayOfWeek != "*" ? $"{dayDesc}, every {interval} hrs" : $"Every {interval} hours";
        }

        return cronExpression;
    }

    /// <summary>
    /// The day-of-week field of a cron expression.
    /// </summary>
    private static string DescribeCronDays(string dayOfWeek)
    {
        if (dayOfWeek == "*")
            return "Daily";
        if (dayOfWeek is "1-5" or "MON-FRI")
            return "Weekdays";
        if (dayOfWeek is "0,6" or "SAT,SUN")
            return "Weekends";

        var dayNumbers = dayOfWeek.Split(',')
            .Select(d => int.TryParse(d.Trim(), out var day) ? day : -1)
            .Where(d => d is >= 0 and <= 6)
            .ToList();

        if (dayNumbers.Count == 0)
            return dayOfWeek;
        if (dayNumbers.Count == 7)
            return "Daily";

        var ordered = dayNumbers.OrderBy(d => d).ToList();
        if (ordered.SequenceEqual([1, 2, 3, 4, 5]))
            return "Weekdays";
        if (ordered.SequenceEqual([0, 6]))
            return "Weekends";

        return string.Join(", ", dayNumbers.Select(d => DayNames[d]));
    }

    private static string FormatCronTime(string hour, string minute) =>
        int.TryParse(hour, out var h) && int.TryParse(minute, out var m) ? FormatTime(h, m) : $"{hour}:{minute}";
}

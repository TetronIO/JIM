// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// The one-line description of when a Schedule runs, shown on its trigger chip on the Home page and the Operations
/// Schedules tab ("Weekdays at 9:00 AM", "Daily exc. Sun, Every 1 hr").
/// <para>
/// A guided pattern is described from the pattern the Schedule stores, anything else from its cron expression. Both
/// paths write a time the way the Schedule editor's summary does, through <see cref="FormatTime(TimeSpan)"/>, so a Schedule
/// reads the same however it was created.
/// </para>
/// <para>
/// Written in the viewer's language (#3030): the words come from <c>ScheduleText</c>, day names from the culture's own
/// abbreviations, and a time from <c>ScheduleText.TimeOfDayFormat</c> (twelve-hour in English, twenty-four-hour in
/// German and French).
/// </para>
/// </summary>
public static class ScheduleTriggerDisplay
{
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
    /// A time of day as the viewer's language writes it: on the twelve-hour clock in English, with minutes always
    /// shown ("1:00 AM", "5:30 PM"), and on the twenty-four-hour clock in German and French ("17:30").
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
            return ScheduleText.TriggerManual;

        var dayDesc = DescribePatternDays(daysOfWeek);

        switch (patternType)
        {
            case SchedulePatternType.Interval:
                var intervalDesc = Format(
                    intervalUnit == ScheduleIntervalUnit.Hours ? ScheduleText.EveryHoursShort : ScheduleText.EveryMinutesShort,
                    intervalValue ?? 1);

                if (!string.IsNullOrEmpty(intervalWindowStart) && !string.IsNullOrEmpty(intervalWindowEnd))
                {
                    return Format(ScheduleText.IntervalWindow, $"{dayDesc}, {intervalDesc}",
                        FormatTimeString(intervalWindowStart), FormatTimeString(intervalWindowEnd));
                }

                return $"{dayDesc}, {intervalDesc}";

            case SchedulePatternType.SpecificTimes when !string.IsNullOrEmpty(runTimes):
                var times = runTimes.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (times.Length <= 3)
                    return Format(ScheduleText.DaysAtTimes, dayDesc, string.Join(", ", times.Select(t => FormatTimeString(t.Trim()))));
                return Format(ScheduleText.TimesPerDay, dayDesc, times.Length);
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
            return ScheduleText.Daily;

        var days = daysOfWeek.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => int.TryParse(d.Trim(), out var day) ? day : -1)
            .Where(d => d is >= 0 and <= 6)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        if (days.Count == 0 || days.Count == 7)
            return ScheduleText.Daily;
        if (days.SequenceEqual([1, 2, 3, 4, 5]))
            return ScheduleText.Weekdays;
        if (days.SequenceEqual([0, 6]))
            return ScheduleText.Weekends;

        // Six days read best as the one day left out.
        if (days.Count == 6)
            return Format(ScheduleText.DailyExcept, DayName(Enumerable.Range(0, 7).Except(days).First()));

        return string.Join(", ", days.Select(DayName));
    }

    /// <summary>
    /// A stored HH:mm time, as <see cref="FormatTime(TimeSpan)"/> writes it; anything unparseable is shown as stored.
    /// </summary>
    private static string FormatTimeString(string time) =>
        TimeSpan.TryParse(time.Trim(), out var parsed) ? FormatTime(parsed) : time;

    /// <summary>
    /// A time of day in <c>ScheduleText.TimeOfDayFormat</c>. Formatted with the invariant culture, whose AM and PM
    /// designators are the capitals the English format wants (en-GB's own are "am" and "pm"); a twenty-four-hour
    /// format has no designator, so the language makes no other difference.
    /// </summary>
    private static string FormatTime(int hour, int minute) =>
        DateTime.MinValue.AddHours(hour % 24).AddMinutes(minute % 60)
            .ToString(ScheduleText.TimeOfDayFormat, CultureInfo.InvariantCulture);

    /// <summary>A day's abbreviated name in the viewer's language (0 is Sunday): "Mon", "Mo", "lun.".</summary>
    private static string DayName(int day) => CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[day];

    private static string Format(string template, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, template, arguments);

    /// <summary>
    /// The common shapes of a five-field cron expression; anything else is shown as the expression itself.
    /// </summary>
    private static string DescribeCronExpression(string? cronExpression)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
            return ScheduleText.NotConfigured;

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
                return Format(ScheduleText.DaysAtTimes, dayDesc, string.Join(", ", hours.Select(h => FormatTime(h, minuteValue))));
            }
        }

        if (dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            if (hour.StartsWith("*/", StringComparison.Ordinal))
                return Format(ScheduleText.CronEveryHours, hour[2..]);
            return Format(ScheduleText.CronDailyAt, FormatCronTime(hour, minute));
        }

        if (dayOfMonth == "*" && month == "*" && int.TryParse(hour, out _))
            return Format(ScheduleText.DaysAtTimes, dayDesc, FormatCronTime(hour, minute));

        if (hour == "*" && dayOfMonth == "*" && month == "*")
        {
            var hourlyDesc = minute == "0" ? ScheduleText.Hourly : Format(ScheduleText.HourlyAt, minute.PadLeft(2, '0'));
            return dayOfWeek != "*" ? $"{dayDesc}, {hourlyDesc.ToLower(CultureInfo.CurrentCulture)}" : hourlyDesc;
        }

        if (minute.StartsWith("*/", StringComparison.Ordinal) && hour == "*")
        {
            var interval = minute[2..];
            return dayOfWeek != "*" ? Format(ScheduleText.DaysEveryMinutes, dayDesc, interval) : Format(ScheduleText.CronEveryMinutes, interval);
        }

        if (hour.StartsWith("*/", StringComparison.Ordinal) && minute == "0")
        {
            var interval = hour[2..];
            return dayOfWeek != "*" ? Format(ScheduleText.DaysEveryHours, dayDesc, interval) : Format(ScheduleText.CronEveryHours, interval);
        }

        return cronExpression;
    }

    /// <summary>
    /// The day-of-week field of a cron expression.
    /// </summary>
    private static string DescribeCronDays(string dayOfWeek)
    {
        if (dayOfWeek == "*")
            return ScheduleText.Daily;
        if (dayOfWeek is "1-5" or "MON-FRI")
            return ScheduleText.Weekdays;
        if (dayOfWeek is "0,6" or "SAT,SUN")
            return ScheduleText.Weekends;

        var dayNumbers = dayOfWeek.Split(',')
            .Select(d => int.TryParse(d.Trim(), out var day) ? day : -1)
            .Where(d => d is >= 0 and <= 6)
            .ToList();

        if (dayNumbers.Count == 0)
            return dayOfWeek;
        if (dayNumbers.Count == 7)
            return ScheduleText.Daily;

        var ordered = dayNumbers.OrderBy(d => d).ToList();
        if (ordered.SequenceEqual([1, 2, 3, 4, 5]))
            return ScheduleText.Weekdays;
        if (ordered.SequenceEqual([0, 6]))
            return ScheduleText.Weekends;

        return string.Join(", ", dayNumbers.Select(DayName));
    }

    private static string FormatCronTime(string hour, string minute) =>
        int.TryParse(hour, out var h) && int.TryParse(minute, out var m) ? FormatTime(h, m) : $"{hour}:{minute}";
}

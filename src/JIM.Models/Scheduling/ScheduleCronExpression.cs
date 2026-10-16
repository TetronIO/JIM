// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Models.Scheduling;

/// <summary>
/// The cron expression a Schedule's guided pattern stands for. A Cron-triggered Schedule is fired from its
/// <see cref="Schedule.CronExpression"/> alone; the pattern fields (<see cref="Schedule.PatternType"/>,
/// <see cref="Schedule.DaysOfWeek"/>, <see cref="Schedule.RunTimes"/>, the interval fields) are how a person describes it.
/// </summary>
/// <remarks>
/// The portal's Schedule editor and the REST API (and so New-JIMSchedule and Set-JIMSchedule) both build the expression
/// here, so a Schedule means the same whichever surface created it. Before this existed only the editor could build one,
/// and the API dropped every pattern field and refused a Cron trigger without a raw expression.
/// </remarks>
public static class ScheduleCronExpression
{
    private const int DaysInWeek = 7;
    private static readonly string[] TimeFormats = [@"h\:mm", @"hh\:mm"];

    /// <summary>
    /// The expression for running at each of <paramref name="runTimes"/> on each of <paramref name="daysOfWeek"/>
    /// (0 = Sunday to 6 = Saturday). One expression holds one minute past the hour, so the times must share one
    /// (see <see cref="RunTimesShareAMinute"/>); building from times that do not throws rather than quietly running
    /// every time at the first time's minute.
    /// </summary>
    public static string BuildSpecificTimes(IEnumerable<int> daysOfWeek, IReadOnlyCollection<TimeSpan> runTimes)
    {
        if (runTimes.Count == 0)
            throw new ArgumentException("At least one run time is needed.", nameof(runTimes));
        if (!RunTimesShareAMinute(runTimes, out var error))
            throw new ArgumentException(error, nameof(runTimes));

        var minute = runTimes.First().Minutes;
        var hours = string.Join(",", runTimes.Select(t => t.Hours).Distinct().OrderBy(h => h));
        return $"{minute} {hours} * * {DaysField(daysOfWeek)}";
    }

    /// <summary>
    /// Whether every one of <paramref name="runTimes"/> is at the same minute past the hour, which one expression needs.
    /// When they are not, <paramref name="error"/> says why in the words both the portal's Schedule editor and the REST
    /// API show.
    /// </summary>
    public static bool RunTimesShareAMinute(IEnumerable<TimeSpan> runTimes, out string? error)
    {
        if (runTimes.Select(t => t.Minutes).Distinct().Count() > 1)
        {
            error = "Every run time must be at the same minute past the hour (06:30 and 18:30, not 06:00 and 18:30); " +
                    "one Schedule holds one cron expression. Use a Custom pattern, or a second Schedule, for times that differ.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// The expression for running every <paramref name="intervalValue"/> hours or minutes on each of
    /// <paramref name="daysOfWeek"/>. An hourly interval with a window runs on the hour from the window's start hour to
    /// its end hour; without one it runs all day.
    /// </summary>
    public static string BuildInterval(IEnumerable<int> daysOfWeek, int intervalValue, ScheduleIntervalUnit intervalUnit,
        TimeSpan? windowStart, TimeSpan? windowEnd)
    {
        var days = DaysField(daysOfWeek);
        if (intervalUnit == ScheduleIntervalUnit.Minutes)
            return $"*/{intervalValue} * * * {days}";

        if (windowStart.HasValue && windowEnd.HasValue)
        {
            var hours = new List<int>();
            for (var hour = windowStart.Value.Hours; hour <= windowEnd.Value.Hours; hour += intervalValue)
                hours.Add(hour);
            return $"0 {string.Join(",", hours)} * * {days}";
        }

        return $"0 */{intervalValue} * * {days}";
    }

    /// <summary>
    /// Builds the expression from a pattern in the form the REST API carries it: days as "1,2,3", times as "06:00,18:00".
    /// Returns false, with an error naming the field at fault, when the pattern is incomplete or cannot be held in one
    /// expression. A Custom pattern has no timing to build from; its expression is supplied as is.
    /// </summary>
    public static bool TryFromPattern(SchedulePatternType patternType, string? daysOfWeek, string? runTimes, int? intervalValue,
        ScheduleIntervalUnit? intervalUnit, string? intervalWindowStart, string? intervalWindowEnd,
        out string? expression, out string? error)
    {
        expression = null;
        if (patternType == SchedulePatternType.Custom)
        {
            error = "A Custom pattern has no timing to build an expression from; supply cronExpression.";
            return false;
        }

        if (!TryParseDays(daysOfWeek, out var days, out error))
            return false;

        if (patternType == SchedulePatternType.SpecificTimes)
        {
            if (!TryParseRunTimes(runTimes, out var times, out error))
                return false;
            expression = BuildSpecificTimes(days, times);
            return true;
        }

        if (intervalUnit is not { } unit)
        {
            error = "An Interval pattern needs intervalUnit (Hours or Minutes).";
            return false;
        }
        var limit = unit == ScheduleIntervalUnit.Hours ? 23 : 59;
        if (intervalValue is not { } value || value < 1 || value > limit)
        {
            error = $"An Interval pattern needs intervalValue between 1 and {limit} for {unit}.";
            return false;
        }

        TimeSpan? start = null, end = null;
        var hasStart = !string.IsNullOrWhiteSpace(intervalWindowStart);
        var hasEnd = !string.IsNullOrWhiteSpace(intervalWindowEnd);
        if (hasStart || hasEnd)
        {
            if (!hasStart || !hasEnd)
            {
                error = "A window needs both intervalWindowStart and intervalWindowEnd.";
                return false;
            }
            if (unit != ScheduleIntervalUnit.Hours)
            {
                error = "A window applies to an hourly interval only; an interval in minutes runs all day.";
                return false;
            }
            if (!TryParseTime(intervalWindowStart!, out var windowStart) || !TryParseTime(intervalWindowEnd!, out var windowEnd))
            {
                error = "intervalWindowStart and intervalWindowEnd are times of day, HH:mm.";
                return false;
            }
            if (windowStart.Hours > windowEnd.Hours)
            {
                error = "intervalWindowStart must come before intervalWindowEnd, or be in the same hour.";
                return false;
            }
            (start, end) = (windowStart, windowEnd);
        }

        expression = BuildInterval(days, value, unit, start, end);
        error = null;
        return true;
    }

    private static string DaysField(IEnumerable<int> daysOfWeek)
    {
        var days = daysOfWeek.Distinct().OrderBy(d => d).ToList();
        return days.Count == DaysInWeek ? "*" : string.Join(",", days);
    }

    private static bool TryParseDays(string? daysOfWeek, out List<int> days, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(daysOfWeek))
        {
            days = Enumerable.Range(0, DaysInWeek).ToList();
            return true;
        }

        days = [];
        foreach (var part in daysOfWeek.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var day) || day >= DaysInWeek)
            {
                error = $"daysOfWeek lists days as numbers from 0 (Sunday) to 6 (Saturday), separated by commas; '{part}' is not one.";
                return false;
            }
            days.Add(day);
        }
        return true;
    }

    private static bool TryParseRunTimes(string? runTimes, out List<TimeSpan> times, out string? error)
    {
        times = [];
        error = null;
        foreach (var part in (runTimes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseTime(part, out var time))
            {
                error = $"runTimes lists times of day as HH:mm, separated by commas; '{part}' is not one.";
                return false;
            }
            times.Add(time);
        }

        if (times.Count == 0)
        {
            error = "A SpecificTimes pattern needs at least one time in runTimes.";
            return false;
        }
        return RunTimesShareAMinute(times, out error);
    }

    private static bool TryParseTime(string text, out TimeSpan time) =>
        TimeSpan.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);
}

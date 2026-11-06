// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;

namespace JIM.Web.Pages.Admin.Components;

/// <summary>
/// The Schedules the Schedules tab shows in demo mode (<c>?d=true</c>, Development only): every trigger pattern, every
/// last outcome, one never run, one disabled, and descriptions from none to long, so the list can be judged fully
/// populated. Held to that by <c>OperationsDemoDataTests</c>.
/// </summary>
public static class OperationsSchedulesDemoData
{
    /// <summary>
    /// The demo Schedule due next, which the Service Health strip names in demo mode.
    /// </summary>
    public static NextDueSchedule NextDue(DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Business Hours Delta Cycle",
        NextRunTime = now.AddMinutes(14)
    };

    public static List<ScheduleHeader> Build(DateTime now) =>
    [
        Schedule("Nightly Full Sync", now, steps: 5, description: "Full import and synchronisation of every system, then exports.",
            pattern: SchedulePatternType.SpecificTimes, runTimes: "02:00", nextRun: now.Date.AddDays(1).AddHours(2),
            last: ScheduleExecutionStatus.Complete, lastRun: now.Date.AddHours(2)),
        Schedule("Business Hours Delta Cycle", now, steps: 8,
            description: "Delta imports from HR and both directories every 15 minutes during the working day, synchronising each "
                + "and exporting the results, so joiners can sign in within the hour and leavers lose access before they reach the car park.",
            pattern: SchedulePatternType.Interval, daysOfWeek: "1,2,3,4,5", interval: 15, unit: ScheduleIntervalUnit.Minutes,
            windowStart: "07:00", windowEnd: "19:00", nextRun: now.AddMinutes(14),
            last: ScheduleExecutionStatus.InProgress, lastRun: now.AddMinutes(-2), currentStep: 3),
        Schedule("Weekend Reconciliation", now, steps: 3, description: null,
            pattern: SchedulePatternType.SpecificTimes, daysOfWeek: "0,6", runTimes: "06:00,12:00,18:00", nextRun: now.Date.AddDays(1).AddHours(6),
            last: ScheduleExecutionStatus.CompleteWithError, lastRun: now.AddDays(-5), failedSteps: [1]),
        Schedule("Cloud Provisioning", now, steps: 3, description: "Exports to the cloud directory.",
            pattern: SchedulePatternType.Interval, interval: 2, unit: ScheduleIntervalUnit.Hours, nextRun: now.AddMinutes(48),
            last: ScheduleExecutionStatus.Failed, lastRun: now.AddMinutes(-72), currentStep: 1,
            error: "Step 2 failed: the service refused the connection."),
        Schedule("Month-End Audit Export", now, steps: 2, description: "Runs at 23:00 on the last day of each month.",
            pattern: SchedulePatternType.Custom, cron: "0 23 L * *", nextRun: now.Date.AddDays(24).AddHours(23),
            last: ScheduleExecutionStatus.Cancelled, lastRun: now.AddDays(-6)),
        Schedule("Directory Restructure", now, steps: 4, description: "Moves accounts into the new OU structure.",
            pattern: SchedulePatternType.SpecificTimes, runTimes: "22:00", nextRun: null, enabled: false,
            last: ScheduleExecutionStatus.Paused, lastRun: now.AddDays(-1), currentStep: 2),
        Schedule("Ad Hoc HR Catch-Up", now, steps: 2, description: "Started by hand when HR sends a late batch.",
            trigger: ScheduleTriggerType.Manual, nextRun: null,
            last: ScheduleExecutionStatus.Queued, lastRun: now.AddMinutes(-1)),
        Schedule("New Starter Mailboxes", now, steps: 1, description: "Created this week; has not run yet.",
            pattern: SchedulePatternType.SpecificTimes, daysOfWeek: "1,2,3,4,5", runTimes: "08:30", nextRun: now.Date.AddDays(1).AddHours(8).AddMinutes(30)),
    ];

    private static ScheduleHeader Schedule(string name, DateTime now, int steps, string? description,
        SchedulePatternType pattern = SchedulePatternType.SpecificTimes, ScheduleTriggerType trigger = ScheduleTriggerType.Cron,
        string? daysOfWeek = null, string? runTimes = null, int? interval = null, ScheduleIntervalUnit? unit = null,
        string? windowStart = null, string? windowEnd = null, string? cron = null, DateTime? nextRun = null, bool enabled = true,
        ScheduleExecutionStatus? last = null, DateTime? lastRun = null, int? currentStep = null, int[]? failedSteps = null,
        string? error = null) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            IsEnabled = enabled,
            TriggerType = trigger,
            PatternType = pattern,
            DaysOfWeek = daysOfWeek,
            RunTimes = runTimes,
            IntervalValue = interval,
            IntervalUnit = unit,
            IntervalWindowStart = windowStart,
            IntervalWindowEnd = windowEnd,
            CronExpression = cron,
            NextRunTime = nextRun,
            LastRunTime = lastRun,
            Created = now.AddDays(-30),
            StepCount = steps,
            LastExecutionId = last.HasValue ? Guid.NewGuid() : null,
            LastExecutionStatus = last,
            LastExecutionCurrentStepIndex = currentStep,
            LastExecutionTotalSteps = last.HasValue ? steps : null,
            LastExecutionFailedStepIndices = failedSteps ?? [],
            LastExecutionCompletedAt = last is ScheduleExecutionStatus.InProgress or ScheduleExecutionStatus.Queued or null ? null : lastRun,
            LastExecutionErrorMessage = error
        };
}

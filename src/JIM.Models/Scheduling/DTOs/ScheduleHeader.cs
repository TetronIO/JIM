// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scheduling.DTOs;

/// <summary>
/// A lightweight projection of a Schedule for list views, carrying its step count and the outcome of its most recent
/// execution. The "Header" tier of the entity retrieval taxonomy.
/// </summary>
/// <remarks>
/// The last-execution fields are what let the Schedules list show whether a run succeeded rather than only when it
/// happened. They are projected in the same query as the Schedule itself, so a page of schedules costs one round
/// trip rather than one query per row.
/// </remarks>
public class ScheduleHeader
{
    /// <summary>
    /// The unique identifier of the Schedule.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The name of the Schedule.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The optional description of the Schedule.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether this is a built-in Schedule, which cannot be deleted.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// Whether the Schedule is enabled and will run on its trigger.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// How the Schedule is triggered.
    /// </summary>
    public ScheduleTriggerType TriggerType { get; set; }

    /// <summary>
    /// The recurrence pattern the Schedule was configured with.
    /// </summary>
    public SchedulePatternType PatternType { get; set; }

    /// <summary>
    /// The cron expression driving the Schedule, where one applies.
    /// </summary>
    public string? CronExpression { get; set; }

    /// <summary>
    /// The days of the week the Schedule runs on, as a comma-separated list of day numbers.
    /// </summary>
    public string? DaysOfWeek { get; set; }

    /// <summary>
    /// The times of day the Schedule runs at, as a comma-separated list.
    /// </summary>
    public string? RunTimes { get; set; }

    /// <summary>
    /// The interval between runs, for interval patterns.
    /// </summary>
    public int? IntervalValue { get; set; }

    /// <summary>
    /// The unit the interval is expressed in.
    /// </summary>
    public ScheduleIntervalUnit? IntervalUnit { get; set; }

    /// <summary>
    /// The start of the daily window an interval pattern runs within.
    /// </summary>
    public string? IntervalWindowStart { get; set; }

    /// <summary>
    /// The end of the daily window an interval pattern runs within.
    /// </summary>
    public string? IntervalWindowEnd { get; set; }

    /// <summary>
    /// When the Schedule is next due to run (UTC).
    /// </summary>
    public DateTime? NextRunTime { get; set; }

    /// <summary>
    /// When the Schedule last ran (UTC).
    /// </summary>
    public DateTime? LastRunTime { get; set; }

    /// <summary>
    /// When the Schedule was created (UTC). Used as the default sort.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// How many steps the Schedule has.
    /// </summary>
    public int StepCount { get; set; }

    /// <summary>
    /// The most recent execution of this Schedule, if it has ever run.
    /// </summary>
    public Guid? LastExecutionId { get; set; }

    /// <summary>
    /// The outcome of the most recent execution. Null when the Schedule has never run.
    /// </summary>
    public ScheduleExecutionStatus? LastExecutionStatus { get; set; }

    /// <summary>
    /// The step the most recent execution reached (0-based). Read with <see cref="LastExecutionTotalSteps"/> to show
    /// how far a failed run got before it stopped.
    /// </summary>
    public int? LastExecutionCurrentStepIndex { get; set; }

    /// <summary>
    /// How many steps the most recent execution set out to run.
    /// </summary>
    public int? LastExecutionTotalSteps { get; set; }

    /// <summary>
    /// When the most recent execution finished (UTC). Null while it is still running.
    /// </summary>
    public DateTime? LastExecutionCompletedAt { get; set; }

    /// <summary>
    /// The error reported by the most recent execution, if it failed.
    /// </summary>
    public string? LastExecutionErrorMessage { get; set; }
}

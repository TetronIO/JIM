using JIM.Models.Activities;
using JIM.Models.Interfaces;

namespace JIM.Models.Scheduling;

/// <summary>
/// Represents a scheduled plan containing multiple steps that execute in sequence or parallel.
/// A Schedule defines WHAT to run and WHEN to run it.
/// </summary>
public class Schedule : IAuditable
{
    public Guid Id { get; set; }

    /// <summary>
    /// Display name for the schedule (e.g., "Delta Sync Schedule", "Nightly Full Sync").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description explaining the schedule's purpose.
    /// </summary>
    public string? Description { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Timing Configuration
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// How the schedule is triggered (Cron or Manual).
    /// </summary>
    public ScheduleTriggerType TriggerType { get; set; } = ScheduleTriggerType.Manual;

    /// <summary>
    /// Cron expression for scheduling (e.g., "0 6 * * 1-5" for 6am Mon-Fri).
    /// Only used when TriggerType is Cron.
    /// This is generated from the pattern configuration fields below, or entered directly when PatternType is Custom.
    /// </summary>
    public string? CronExpression { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule Pattern Configuration (used to build CronExpression)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The type of schedule pattern: SpecificTimes, Interval, or Custom.
    /// Determines which other configuration fields are used.
    /// </summary>
    public SchedulePatternType PatternType { get; set; } = SchedulePatternType.SpecificTimes;

    /// <summary>
    /// Days of week to run (0=Sunday, 6=Saturday). Only used when PatternType != Custom.
    /// Stored as comma-separated values: "1,2,3,4,5" for Mon-Fri.
    /// </summary>
    public string? DaysOfWeek { get; set; }

    /// <summary>
    /// Times to run when PatternType is SpecificTimes.
    /// Stored as comma-separated 24h times: "09:00,12:00,15:00,18:00".
    /// </summary>
    public string? RunTimes { get; set; }

    /// <summary>
    /// Interval value when PatternType is Interval (e.g., 2 for "every 2 hours").
    /// </summary>
    public int? IntervalValue { get; set; }

    /// <summary>
    /// Interval unit when PatternType is Interval.
    /// </summary>
    public ScheduleIntervalUnit? IntervalUnit { get; set; }

    /// <summary>
    /// Optional start time for interval window (e.g., "06:00"). Only used when PatternType is Interval.
    /// If null, interval runs all day.
    /// </summary>
    public string? IntervalWindowStart { get; set; }

    /// <summary>
    /// Optional end time for interval window (e.g., "18:00"). Only used when PatternType is Interval.
    /// If null, interval runs all day.
    /// </summary>
    public string? IntervalWindowEnd { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the schedule is enabled and will be triggered automatically.
    /// Disabled schedules can still be triggered manually.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// When the schedule last ran (UTC).
    /// </summary>
    public DateTime? LastRunTime { get; set; }

    /// <summary>
    /// When the schedule will next run (UTC). Calculated by the scheduler service.
    /// </summary>
    public DateTime? NextRunTime { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Steps
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The steps that make up this schedule, ordered by StepIndex.
    /// </summary>
    public List<ScheduleStep> Steps { get; set; } = new();

    /// <summary>
    /// History of executions for this schedule.
    /// </summary>
    public List<ScheduleExecution> Executions { get; set; } = new();

    // -----------------------------------------------------------------------------------------------------------------
    // Audit (IAuditable)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// When the schedule was created (UTC).
    /// </summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The type of security principal that created this schedule.
    /// </summary>
    public ActivityInitiatorType CreatedByType { get; set; }

    /// <summary>
    /// The ID of the principal that created this schedule.
    /// </summary>
    public Guid? CreatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of creation.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the schedule was last modified (UTC).
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The type of security principal that last modified this schedule.
    /// </summary>
    public ActivityInitiatorType LastUpdatedByType { get; set; }

    /// <summary>
    /// The ID of the principal that last modified this schedule.
    /// </summary>
    public Guid? LastUpdatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of the last modification.
    /// </summary>
    public string? LastUpdatedByName { get; set; }
}

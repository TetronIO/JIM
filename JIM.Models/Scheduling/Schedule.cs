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
    /// Note: Users configure schedules via a friendly UI; cron syntax is generated automatically.
    /// </summary>
    public string? CronExpression { get; set; }

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

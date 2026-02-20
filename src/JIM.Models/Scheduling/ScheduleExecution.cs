using JIM.Models.Activities;
namespace JIM.Models.Scheduling;

/// <summary>
/// Represents a single execution instance of a schedule. Tracks progress through steps
/// and links to the WorkerTasks created for each step.
/// </summary>
public class ScheduleExecution
{
    public Guid Id { get; set; }

    /// <summary>
    /// The schedule being executed.
    /// </summary>
    public Guid ScheduleId { get; set; }
    public Schedule Schedule { get; set; } = null!;

    /// <summary>
    /// Snapshot of the schedule name at execution time (in case schedule is renamed/deleted).
    /// </summary>
    public string ScheduleName { get; set; } = string.Empty;

    // -----------------------------------------------------------------------------------------------------------------
    // Progress
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Current status of this execution.
    /// </summary>
    public ScheduleExecutionStatus Status { get; set; } = ScheduleExecutionStatus.Queued;

    /// <summary>
    /// The current step index being executed (0-based).
    /// </summary>
    public int CurrentStepIndex { get; set; }

    /// <summary>
    /// Total number of steps in this execution (snapshot from schedule at execution time).
    /// </summary>
    public int TotalSteps { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Timing
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// When the execution was queued (UTC).
    /// </summary>
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the execution actually started (UTC).
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the execution completed (success, failure, or cancellation) (UTC).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Initiator tracking - uses the standard triad pattern (Type + Id + Name) to survive principal deletion.
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The type of security principal that initiated this execution.
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; } = ActivityInitiatorType.NotSet;

    /// <summary>
    /// The unique identifier of the security principal (MetaverseObject or ApiKey) that initiated this execution.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// The name of the security principal at the time of execution, retained for audit trail.
    /// </summary>
    public string? InitiatedByName { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Results
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Error message if the execution failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Stack trace if the execution failed due to an exception.
    /// </summary>
    public string? ErrorStackTrace { get; set; }
}

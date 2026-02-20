using JIM.Models.Activities;
using JIM.Models.Scheduling;

namespace JIM.Web.Models.Api;

/// <summary>
/// DTO for a schedule execution in list views.
/// </summary>
public class ScheduleExecutionDto
{
    /// <summary>
    /// The unique identifier of the execution.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The schedule ID this execution belongs to.
    /// </summary>
    public Guid ScheduleId { get; set; }

    /// <summary>
    /// The schedule name for display purposes.
    /// </summary>
    public string ScheduleName { get; set; } = null!;

    /// <summary>
    /// The current status of the execution.
    /// </summary>
    public ScheduleExecutionStatus Status { get; set; }

    /// <summary>
    /// The current step being executed (0-based).
    /// </summary>
    public int CurrentStepIndex { get; set; }

    /// <summary>
    /// The total number of steps in the schedule.
    /// </summary>
    public int TotalSteps { get; set; }

    /// <summary>
    /// When the execution was queued (UTC).
    /// </summary>
    public DateTime QueuedAt { get; set; }

    /// <summary>
    /// When the execution started (UTC).
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the execution completed (UTC).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// The type of principal that initiated the execution.
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; }

    /// <summary>
    /// The ID of the principal that initiated the execution.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// The display name of the principal that initiated the execution.
    /// </summary>
    public string? InitiatedByName { get; set; }

    /// <summary>
    /// Error message if the execution failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Creates a DTO from a ScheduleExecution entity.
    /// </summary>
    public static ScheduleExecutionDto FromEntity(ScheduleExecution execution)
    {
        return new ScheduleExecutionDto
        {
            Id = execution.Id,
            ScheduleId = execution.ScheduleId,
            ScheduleName = execution.Schedule?.Name ?? "Unknown",
            Status = execution.Status,
            CurrentStepIndex = execution.CurrentStepIndex,
            TotalSteps = execution.TotalSteps,
            QueuedAt = execution.QueuedAt,
            StartedAt = execution.StartedAt,
            CompletedAt = execution.CompletedAt,
            InitiatedByType = execution.InitiatedByType,
            InitiatedById = execution.InitiatedById,
            InitiatedByName = execution.InitiatedByName,
            ErrorMessage = execution.ErrorMessage
        };
    }
}

/// <summary>
/// DTO for a schedule execution with step details.
/// </summary>
public class ScheduleExecutionDetailDto : ScheduleExecutionDto
{
    /// <summary>
    /// The worker tasks associated with this execution, showing step progress.
    /// </summary>
    public List<ScheduleExecutionStepDto> Steps { get; set; } = new();

    /// <summary>
    /// Creates a detail DTO from a ScheduleExecution entity.
    /// </summary>
    public static new ScheduleExecutionDetailDto FromEntity(ScheduleExecution execution)
    {
        return new ScheduleExecutionDetailDto
        {
            Id = execution.Id,
            ScheduleId = execution.ScheduleId,
            ScheduleName = execution.Schedule?.Name ?? "Unknown",
            Status = execution.Status,
            CurrentStepIndex = execution.CurrentStepIndex,
            TotalSteps = execution.TotalSteps,
            QueuedAt = execution.QueuedAt,
            StartedAt = execution.StartedAt,
            CompletedAt = execution.CompletedAt,
            InitiatedByType = execution.InitiatedByType,
            InitiatedById = execution.InitiatedById,
            InitiatedByName = execution.InitiatedByName,
            ErrorMessage = execution.ErrorMessage,
            // Steps will be populated separately from worker tasks
            Steps = new()
        };
    }
}

/// <summary>
/// DTO for an individual step's status within an execution.
/// </summary>
public class ScheduleExecutionStepDto
{
    /// <summary>
    /// The step index (0-based).
    /// </summary>
    public int StepIndex { get; set; }

    /// <summary>
    /// The step name.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The step type.
    /// </summary>
    public ScheduleStepType StepType { get; set; }

    /// <summary>
    /// How this step executes relative to other steps at the same index.
    /// Sequential means it runs alone; ParallelWithPrevious means it runs concurrently with siblings.
    /// </summary>
    public StepExecutionMode ExecutionMode { get; set; }

    /// <summary>
    /// The connected system ID for RunProfile steps.
    /// Useful for distinguishing parallel sub-steps at the same StepIndex.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The current status of this step.
    /// </summary>
    public string Status { get; set; } = null!;

    /// <summary>
    /// The worker task ID if this step has been queued.
    /// </summary>
    public Guid? TaskId { get; set; }

    /// <summary>
    /// When the step started (UTC).
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the step completed (UTC).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Error message if the step failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The Activity ID associated with this step's worker task (if available).
    /// </summary>
    public Guid? ActivityId { get; set; }

    /// <summary>
    /// The Activity status for this step (e.g. Complete, CompleteWithWarning, FailedWithError).
    /// Only populated when the step has completed and an activity exists.
    /// </summary>
    public string? ActivityStatus { get; set; }
}

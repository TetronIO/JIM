using JIM.Models.Activities;
namespace JIM.Models.Tasking.DTOs;

public class WorkerTaskHeader
{
    public Guid Id { get; set; }

    public string Name { get; set; } = null!;

    public string Type { get; set; } = null!;

    public DateTime Timestamp { get; set; }

    public WorkerTaskStatus Status { get; set; }

    /// <summary>
    /// The type of security principal that initiated this task.
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; } = ActivityInitiatorType.NotSet;

    /// <summary>
    /// The unique identifier of the security principal (MetaverseObject or ApiKey) that initiated this task.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// The name of the security principal at the time of task creation.
    /// </summary>
    public string? InitiatedByName { get; set; }

    /// <summary>
    /// The total number of objects to process (from the associated Activity).
    /// </summary>
    public int? ObjectsToProcess { get; set; }

    /// <summary>
    /// The number of objects processed so far (from the associated Activity).
    /// </summary>
    public int? ObjectsProcessed { get; set; }

    /// <summary>
    /// The current progress message (from the associated Activity).
    /// </summary>
    public string? ProgressMessage { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule execution context - for grouping tasks by schedule in the queue UI
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// If this task is part of a schedule execution, the execution ID for UI grouping.
    /// </summary>
    public Guid? ScheduleExecutionId { get; set; }

    /// <summary>
    /// The schedule name at execution time, for display in the queue UI.
    /// </summary>
    public string? ScheduleExecutionName { get; set; }

    /// <summary>
    /// The step index within the schedule, for ordering within the group.
    /// </summary>
    public int? ScheduleStepIndex { get; set; }
}
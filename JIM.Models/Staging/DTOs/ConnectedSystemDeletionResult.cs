namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Result of a Connected System deletion request.
/// </summary>
public class ConnectedSystemDeletionResult
{
    /// <summary>
    /// Whether the deletion request was accepted.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The outcome of the deletion request.
    /// </summary>
    public DeletionOutcome Outcome { get; set; }

    /// <summary>
    /// Error message if the deletion failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The ID of the worker task if deletion was queued.
    /// </summary>
    public Guid? WorkerTaskId { get; set; }

    /// <summary>
    /// The ID of the activity tracking this deletion.
    /// </summary>
    public Guid? ActivityId { get; set; }

    public static ConnectedSystemDeletionResult CompletedImmediately(Guid activityId)
    {
        return new ConnectedSystemDeletionResult
        {
            Success = true,
            Outcome = DeletionOutcome.CompletedImmediately,
            ActivityId = activityId
        };
    }

    public static ConnectedSystemDeletionResult QueuedAsBackgroundJob(Guid workerTaskId, Guid activityId)
    {
        return new ConnectedSystemDeletionResult
        {
            Success = true,
            Outcome = DeletionOutcome.QueuedAsBackgroundJob,
            WorkerTaskId = workerTaskId,
            ActivityId = activityId
        };
    }

    public static ConnectedSystemDeletionResult QueuedAfterSync(Guid workerTaskId, Guid activityId)
    {
        return new ConnectedSystemDeletionResult
        {
            Success = true,
            Outcome = DeletionOutcome.QueuedAfterSync,
            WorkerTaskId = workerTaskId,
            ActivityId = activityId
        };
    }

    public static ConnectedSystemDeletionResult Failed(string errorMessage)
    {
        return new ConnectedSystemDeletionResult
        {
            Success = false,
            Outcome = DeletionOutcome.Failed,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// The outcome of a deletion request.
/// </summary>
public enum DeletionOutcome
{
    /// <summary>
    /// Deletion completed synchronously.
    /// </summary>
    CompletedImmediately,

    /// <summary>
    /// Deletion was queued as a background job (large system).
    /// </summary>
    QueuedAsBackgroundJob,

    /// <summary>
    /// Deletion was queued to run after a currently running sync completes.
    /// </summary>
    QueuedAfterSync,

    /// <summary>
    /// Deletion failed.
    /// </summary>
    Failed
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Result of a Connected System deletion request.
/// </summary>
public class ConnectedSystemDeletionResult
{
    /// <summary>
    /// Whether JIM accepted the deletion; false when it failed, and ErrorMessage says why.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// How the deletion runs: at once, as a background job, or once a running synchronisation finishes; or Failed.
    /// </summary>
    public DeletionOutcome Outcome { get; set; }

    /// <summary>
    /// Why the deletion failed; null unless the Outcome is Failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The ID of the worker task carrying out the deletion; null when it completed at once.
    /// </summary>
    public Guid? WorkerTaskId { get; set; }

    /// <summary>
    /// The ID of the Activity recording the deletion; null when it failed.
    /// </summary>
    public Guid? ActivityId { get; set; }

    /// <summary>
    /// What the caller should know about a deletion that went ahead, such as the preview it cites having been out
    /// of date. Empty when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// #2022.
    /// </remarks>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Adds what the caller should be told, and returns this result.</summary>
    public ConnectedSystemDeletionResult WithWarnings(IEnumerable<string> warnings)
    {
        Warnings.AddRange(warnings);
        return this;
    }

    /// <summary>Adds a warning, where there is one, and returns this result.</summary>
    public ConnectedSystemDeletionResult WithWarning(string? warning) => warning is null ? this : WithWarnings([warning]);

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

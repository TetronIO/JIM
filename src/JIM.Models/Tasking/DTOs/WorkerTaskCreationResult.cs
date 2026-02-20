namespace JIM.Models.Tasking.DTOs;

/// <summary>
/// Result of creating a worker task, including any validation warnings.
/// </summary>
public class WorkerTaskCreationResult
{
    /// <summary>
    /// Whether the worker task was created successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The ID of the created worker task, if successful.
    /// </summary>
    public Guid? WorkerTaskId { get; set; }

    /// <summary>
    /// Warning messages that should be communicated to the caller.
    /// The task may still have been created despite warnings.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Error message if the task was not created.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Creates a successful result with no warnings.
    /// </summary>
    public static WorkerTaskCreationResult Succeeded(Guid workerTaskId)
    {
        return new WorkerTaskCreationResult
        {
            Success = true,
            WorkerTaskId = workerTaskId
        };
    }

    /// <summary>
    /// Creates a successful result with warnings.
    /// </summary>
    public static WorkerTaskCreationResult SucceededWithWarnings(Guid workerTaskId, params string[] warnings)
    {
        return new WorkerTaskCreationResult
        {
            Success = true,
            WorkerTaskId = workerTaskId,
            Warnings = warnings.ToList()
        };
    }

    /// <summary>
    /// Creates a failed result with an error message.
    /// </summary>
    public static WorkerTaskCreationResult Failed(string errorMessage)
    {
        return new WorkerTaskCreationResult
        {
            Success = false,
            ErrorMessage = errorMessage
        };
    }
}

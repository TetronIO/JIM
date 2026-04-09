// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Tasking;

public class TaskTask
{
    /// <summary>
    /// The identifier for the WorkerTask being processed.
    /// </summary>
    public Guid TaskId { get; set; }

    /// <summary>
    /// The Task where the WorkerTask is being executed within.
    /// </summary>
    public Task Task { get; set; }

    /// <summary>
    /// The cancellation token source that will cancel the task.
    /// </summary>
    public CancellationTokenSource CancellationTokenSource { get; set; }

    public TaskTask(Guid taskId, Task task, CancellationTokenSource cancellationTokenSource)
    {
        TaskId = taskId;
        Task = task;
        CancellationTokenSource = cancellationTokenSource;
    }
}
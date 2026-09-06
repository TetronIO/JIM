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

    /// <summary>
    /// What the task is, in the words the Worker's heartbeat reports to administrators (for example
    /// "Full Import: Corporate Directory"). Captured when the task is dispatched so the main loop can describe its
    /// in-flight work without another database read per heartbeat. Null when the dispatcher had nothing to say.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// When the Worker dispatched the task (UTC). The earliest of these across the in-flight tasks is the
    /// heartbeat's CurrentWorkStartedAt.
    /// </summary>
    public DateTime StartedAt { get; }

    public TaskTask(Guid taskId, Task task, CancellationTokenSource cancellationTokenSource)
        : this(taskId, task, cancellationTokenSource, null, DateTime.UtcNow)
    {
    }

    public TaskTask(Guid taskId, Task task, CancellationTokenSource cancellationTokenSource, string? description, DateTime startedAt)
    {
        TaskId = taskId;
        Task = task;
        CancellationTokenSource = cancellationTokenSource;
        Description = description;
        StartedAt = startedAt;
    }
}
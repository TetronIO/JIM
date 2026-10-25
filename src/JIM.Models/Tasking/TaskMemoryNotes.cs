// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Tasking;

/// <summary>
/// What the Worker's memory guard has said about one in-flight task, for the task to record on its Activity when it
/// ends. The guard writes from its own thread and the task reads from its own; the first warning and the first stop
/// reason are kept, and a later one is ignored.
/// </summary>
public sealed class TaskMemoryNotes
{
    private string? _warning;
    private string? _stopReason;

    /// <summary>Why memory was low while the task ran, or null when it was not.</summary>
    public string? Warning => Volatile.Read(ref _warning);

    /// <summary>Why the guard stopped the task, or null when it did not.</summary>
    public string? StopReason => Volatile.Read(ref _stopReason);

    public void RecordWarning(string warning) => Interlocked.CompareExchange(ref _warning, warning, null);

    public void RecordStop(string reason) => Interlocked.CompareExchange(ref _stopReason, reason, null);
}

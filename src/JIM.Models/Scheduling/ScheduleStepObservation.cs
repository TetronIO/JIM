// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Tasking;

namespace JIM.Models.Scheduling;

/// <summary>
/// One thing known about one task of a Schedule Execution: which step group it belongs to, what it is
/// called, and whatever its Worker Task and Activity currently say about it (#1162).
/// </summary>
/// <remarks>
/// A Schedule Execution is watched through two records that overlap in time. The Worker Task is
/// deleted the moment its task finishes, and the Activity is not created until the task starts, so
/// neither on its own can describe a Schedule mid-flight: tasks alone can never show a completed or
/// failed step, and Activities alone can never show one still waiting to run. An observation carries
/// whichever exists, so a caller assembles the pair it has rather than the reader having to know
/// where each half came from.
/// </remarks>
public sealed class ScheduleStepObservation
{
    /// <summary>
    /// The step group this task belongs to (0-based). Tasks sharing an index run concurrently.
    /// </summary>
    public required int StepIndex { get; init; }

    /// <summary>
    /// What to call this task where it is the only one in its step group.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The Activity this observation describes, where it has one. Carried so that a caller holding
    /// both records of the same task can tell they are the same task: the two are read at slightly
    /// different moments, and counting one task twice would put a phantom wedge in a parallel step.
    /// </summary>
    public Guid? ActivityId { get; init; }

    /// <summary>
    /// The task's status while it is still in the queue, or null once it has been deleted.
    /// </summary>
    public WorkerTaskStatus? TaskStatus { get; init; }

    /// <summary>
    /// The Activity's status once the task has started, or null before it has.
    /// </summary>
    public ActivityStatus? ActivityStatus { get; init; }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// One start of the worker: the task, or the tasks of a parallel Schedule step, that it will begin together.
/// </summary>
/// <remarks>
/// Mirrors how the worker takes work (<c>TaskingRepository.GetNextWorkerTasksToProcessAsync</c>): queued tasks in
/// Schedule step order, tasks outside a Schedule last, each by age; a parallel step's tasks together. Tasks waiting
/// for a previous step are placed by the same rule, since that is where they will sort once their step is queued.
/// The Activity tab's "Next" and its estimated start times are both read from this, so they are promises about the
/// worker's behaviour rather than about the order rows happen to arrive in.
/// </remarks>
public sealed class QueueRunSlot
{
    /// <summary>Where this slot comes in the queue: 1 is the next thing the worker will start.</summary>
    public required int Position { get; init; }

    /// <summary>The task or tasks the worker will start together, in step then age order.</summary>
    public required IReadOnlyList<WorkerTaskHeader> Tasks { get; init; }

    /// <summary>Whether this slot is a parallel Schedule step running several tasks at once.</summary>
    public bool IsParallel => Tasks.Count > 1;

    /// <summary>
    /// The queued and waiting tasks, divided into the slots the worker will start, in the order it will start them.
    /// Running tasks are not part of the plan.
    /// </summary>
    public static List<QueueRunSlot> Plan(IEnumerable<WorkerTaskHeader> tasks) =>
        tasks
            .Where(t => t.Status is WorkerTaskStatus.Queued or WorkerTaskStatus.WaitingForPreviousStep)
            .OrderBy(t => t.ScheduleStepIndex ?? int.MaxValue)
            .ThenBy(t => t.Timestamp)
            .GroupBy(SlotKey)
            .Select((group, index) => new QueueRunSlot { Position = index + 1, Tasks = group.ToList() })
            .ToList();

    /// <summary>
    /// A parallel task shares its slot with the rest of its Schedule step; anything else is a slot of its own.
    /// GroupBy keeps first-appearance order, so slots come out in the order their first task sorts.
    /// </summary>
    private static string SlotKey(WorkerTaskHeader task) =>
        task is { ExecutionMode: WorkerTaskExecutionMode.Parallel, ScheduleExecutionId: { } execution }
            ? $"{execution}:{task.ScheduleStepIndex}"
            : task.Id.ToString();
}

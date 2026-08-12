// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// One block of the Operations queue: the Worker Tasks of a single Schedule Execution, or the tasks that belong
/// to no Schedule at all.
/// </summary>
/// <remarks>
/// The queue is drawn as one grid per group rather than as a single grouped table because MudBlazor's grouping
/// and its virtualisation are mutually exclusive branches of the same component: a grid cannot both group and
/// stream. Grouping therefore lives here, and each group's grid windows only its own rows.
/// </remarks>
public sealed class WorkerTaskQueueGroup
{
    /// <summary>
    /// The key of the group holding everything the queue is running outside a Schedule. All such tasks share one
    /// key, so they form one block rather than one per task.
    /// </summary>
    public const string StandaloneKey = "standalone";

    /// <summary>Identity of this group in the render tree, and the key its grid is held under.</summary>
    public required string Key { get; init; }

    /// <summary>The Schedule Execution whose steps these tasks are, or null for the standalone group.</summary>
    public Guid? ScheduleExecutionId { get; init; }

    /// <summary>The Schedule's name as at execution time, for the group's header.</summary>
    public string? ScheduleName { get; init; }

    /// <summary>This group's tasks, in the order the queue shows them.</summary>
    public required IReadOnlyList<WorkerTaskHeader> Tasks { get; init; }

    /// <summary>
    /// Divides the queue into the blocks it is drawn as, preserving the order the queue has always shown: the
    /// task the worker is running leads, and a group takes its position from its first task, so a Schedule with
    /// something processing sits above one that is merely queued. Within a group the tasks are ordered by their
    /// step, which is the sequence the Schedule will actually run them in.
    /// </summary>
    public static List<WorkerTaskQueueGroup> Build(IEnumerable<WorkerTaskHeader> tasks) =>
        tasks
            .OrderBy(t => StatusSortOrder(t.Status))
            .ThenBy(t => t.ScheduleStepIndex ?? 0)
            .ThenBy(t => t.Timestamp)
            .GroupBy(t => t.ScheduleExecutionId?.ToString() ?? StandaloneKey)
            .Select(group =>
            {
                var first = group.First();
                return new WorkerTaskQueueGroup
                {
                    Key = group.Key,
                    ScheduleExecutionId = first.ScheduleExecutionId,
                    ScheduleName = first.ScheduleExecutionName,
                    Tasks = group.ToList()
                };
            })
            .ToList();

    /// <summary>
    /// What the worker is doing now comes first, then what it will pick up next, then what is waiting on a step
    /// before it. A status the queue does not otherwise show sorts last rather than being dropped.
    /// </summary>
    private static int StatusSortOrder(WorkerTaskStatus status) => status switch
    {
        WorkerTaskStatus.Processing => 0,
        WorkerTaskStatus.CancellationRequested => 1,
        WorkerTaskStatus.Queued => 2,
        WorkerTaskStatus.WaitingForPreviousStep => 3,
        _ => 4
    };
}

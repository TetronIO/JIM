// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Tasking.DTOs;

namespace JIM.Web.Models;

/// <summary>
/// One row of the Activity tab's Queued section (#2549): a task, or the header over a Schedule Execution's tasks.
/// </summary>
/// <remarks>
/// The tab reads in time order with the latest at the top, so Queued is laid out furthest-out first and the next run
/// last, directly above In progress.
/// </remarks>
public sealed record QueueDisplayRow
{
    /// <summary>Whether this row is a Schedule Execution header rather than a task.</summary>
    public bool IsHeader { get; init; }

    /// <summary>The task this row shows; null for a header.</summary>
    public WorkerTaskHeader? Task { get; init; }

    /// <summary>Where the task comes in the queue: "Next", "2nd", "3rd", and so on; null for a header.</summary>
    public string? PositionLabel { get; init; }

    /// <summary>Whether the task starts together with others in a parallel Schedule step.</summary>
    public bool IsParallel { get; init; }

    /// <summary>The Schedule Execution the task belongs to, or that the header introduces.</summary>
    public Guid? ScheduleExecutionId { get; init; }

    /// <summary>The Schedule's name, for a header.</summary>
    public string? ScheduleName { get; init; }

    /// <summary>
    /// The rows for the queue, furthest-out first and the next run last, with a header above each unbroken run of one
    /// Schedule Execution's tasks.
    /// </summary>
    public static List<QueueDisplayRow> Build(IReadOnlyList<QueueRunSlot> slots)
    {
        var rows = new List<QueueDisplayRow>();
        Guid? currentExecution = null;

        foreach (var (slot, task) in slots
                     .Reverse()
                     .SelectMany(slot => slot.Tasks.Select(task => (slot, task))))
        {
            if (task.ScheduleExecutionId is { } execution && execution != currentExecution)
                rows.Add(new QueueDisplayRow { IsHeader = true, ScheduleExecutionId = execution, ScheduleName = task.ScheduleExecutionName });

            currentExecution = task.ScheduleExecutionId;
            rows.Add(new QueueDisplayRow
            {
                Task = task,
                PositionLabel = PositionLabelFor(slot.Position),
                IsParallel = slot.IsParallel,
                ScheduleExecutionId = task.ScheduleExecutionId
            });
        }

        return rows;
    }

    /// <summary>
    /// The last <paramref name="maxRows"/> rows, which are those nearest In progress, and how many tasks were left out.
    /// When the cut falls inside a Schedule Execution, its header is kept above the tasks still shown.
    /// </summary>
    public static (List<QueueDisplayRow> Visible, int Hidden) Limit(List<QueueDisplayRow> rows, int maxRows)
    {
        if (rows.Count <= maxRows)
            return (rows, 0);

        var cut = rows.Count - maxRows;
        var visible = rows.Skip(cut).ToList();
        var hidden = rows.Take(cut).Count(r => !r.IsHeader);

        if (visible[0] is { IsHeader: false, ScheduleExecutionId: { } execution })
        {
            var header = rows.Take(cut).Last(r => r.IsHeader && r.ScheduleExecutionId == execution);
            visible.Insert(0, header);
        }

        return (visible, hidden);
    }

    private static string PositionLabelFor(int position) => position == 1 ? "Next" : Ordinal(position);

    private static string Ordinal(int number)
    {
        var suffix = (number % 100) is 11 or 12 or 13
            ? "th"
            : (number % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{number}{suffix}";
    }
}

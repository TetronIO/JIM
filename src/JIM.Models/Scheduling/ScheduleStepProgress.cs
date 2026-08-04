// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scheduling;

/// <summary>
/// One step group of a Schedule Execution as a list view draws it (#1162): where it has got to, and,
/// where several tasks run concurrently, where each of them has got to.
/// </summary>
public class ScheduleStepProgress
{
    /// <summary>
    /// The step group's position in the Schedule (0-based).
    /// </summary>
    public int StepIndex { get; set; }

    /// <summary>
    /// What to call this step: the task's name where it runs one, or how many run concurrently where
    /// it runs several, since no one of their names describes the step.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Where the step group as a whole has got to.
    /// </summary>
    public ScheduleStepStatus Status { get; set; }

    /// <summary>
    /// Each concurrent task's own status, ordered for drawing rather than by task: see
    /// <see cref="ScheduleStepReading.OrderWedges"/> for why the order is load-bearing.
    /// </summary>
    public IReadOnlyList<ScheduleStepStatus> TaskStatuses { get; set; } = [];

    /// <summary>
    /// Whether this step runs several tasks concurrently, and so is drawn divided rather than whole.
    /// </summary>
    public bool IsParallel => TaskStatuses.Count > 1;
}

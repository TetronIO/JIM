// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scheduling.DTOs;

/// <summary>
/// A Schedule Execution with its Schedule loaded and the derived state of every step. The "Detail" tier of the
/// entity retrieval taxonomy: the entity plus the metadata a detail view needs alongside it.
/// </summary>
public class ScheduleExecutionDetail
{
    /// <summary>
    /// The execution itself, with its Schedule loaded.
    /// </summary>
    public ScheduleExecution Execution { get; set; } = null!;

    /// <summary>
    /// One entry per Schedule Step, ordered by step index and then by Connected System so that parallel siblings
    /// appear in a stable order. Empty when the execution's Schedule has since been deleted.
    /// </summary>
    public List<ScheduleExecutionStepState> Steps { get; set; } = [];
}

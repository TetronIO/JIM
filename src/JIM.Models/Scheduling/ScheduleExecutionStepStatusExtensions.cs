// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scheduling;

/// <summary>
/// Display helpers for <see cref="ScheduleExecutionStepStatus"/>.
/// </summary>
public static class ScheduleExecutionStepStatusExtensions
{
    /// <summary>
    /// Gets the human-readable label for a step status.
    /// </summary>
    /// <remarks>
    /// This is also the exact wire value of ScheduleExecutionStepDto.Status on
    /// GET /api/v1/schedule-executions/{id}, so every string here is a published REST contract; changing one is a
    /// breaking API change. The labels cannot be derived from the enum member names because "Completed with Warning"
    /// and "Completed with Error" carry a lowercase "with". ScheduleExecutionStepStatusExtensionsTests asserts each
    /// label literally.
    /// </remarks>
    /// <param name="status">The step status to label.</param>
    public static string ToDisplayString(this ScheduleExecutionStepStatus status)
    {
        return status switch
        {
            ScheduleExecutionStepStatus.Pending => "Pending",
            ScheduleExecutionStepStatus.Waiting => "Waiting",
            ScheduleExecutionStepStatus.Queued => "Queued",
            ScheduleExecutionStepStatus.Processing => "Processing",
            ScheduleExecutionStepStatus.Cancelling => "Cancelling",
            ScheduleExecutionStepStatus.Completed => "Completed",
            ScheduleExecutionStepStatus.CompletedWithWarning => "Completed with Warning",
            ScheduleExecutionStepStatus.CompletedWithError => "Completed with Error",
            ScheduleExecutionStepStatus.Failed => "Failed",
            ScheduleExecutionStepStatus.Cancelled => "Cancelled",
            _ => "Unknown"
        };
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scheduling;

/// <summary>
/// Why a Schedule Step that was waiting to run was cancelled instead (#1768). Written onto the step's Activity as
/// its message when JIM cancels it, and read back by the Schedule Execution view, the REST API and PowerShell, so an
/// administrator can see why a step shows as Cancelled without cross-referencing the execution.
/// </summary>
/// <remarks>
/// Only these exact texts are surfaced as a reason. An Activity's message otherwise carries progress text written
/// while it ran ("Importing objects", say), which would read as nonsense under a cancelled step; matching the known
/// reasons exactly keeps the two apart without another column.
/// </remarks>
public static class ScheduleStepNotRunReasons
{
    /// <summary>
    /// An earlier step failed, and it was set to stop the Schedule when it fails.
    /// </summary>
    public const string EarlierStepStoppedSchedule = "Not run: an earlier step stopped the Schedule.";

    /// <summary>
    /// The Schedule could not finish starting, so none of its steps ran.
    /// </summary>
    public const string ScheduleCouldNotStart = "Not run: the Schedule could not start.";

    /// <summary>
    /// Someone cancelled the Schedule Execution before the step's turn came.
    /// </summary>
    public const string ExecutionCancelled = "Not run: the Schedule Execution was cancelled.";

    /// <summary>
    /// Whether an Activity's message is one of the reasons above, rather than progress text it wrote while running.
    /// </summary>
    public static bool IsNotRunReason(string? message) =>
        message is EarlierStepStoppedSchedule or ScheduleCouldNotStart or ExecutionCancelled;
}

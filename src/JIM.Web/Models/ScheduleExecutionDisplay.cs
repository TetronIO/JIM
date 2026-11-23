// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
using JIM.Utilities;
using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// How a Schedule Execution's outcome reads on screen: the last-run chip on the Schedules list, and the explanation
/// on the Schedule Execution page when a run continued past a failed step (#1787).
/// </summary>
public static class ScheduleExecutionDisplay
{
    /// <summary>
    /// Shown when an execution is Complete With Error but recorded no message of its own.
    /// </summary>
    public static string CompleteWithErrorFallback => OperationsText.ScheduleExecCompleteWithErrorFallback;

    /// <summary>
    /// The label for a Schedule's last-run outcome chip. A failed run names the step it stopped on, and a run that
    /// continued past failures names the steps that failed, which saves the administrator a click through to the
    /// Schedule Execution. Stored step indices are 0-based and are shown 1-based.
    /// </summary>
    public static string LastOutcomeLabel(ScheduleHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        if (!header.LastExecutionStatus.HasValue)
            return string.Empty;

        return header.LastExecutionStatus.Value switch
        {
            ScheduleExecutionStatus.Failed => FailedLabel(header),
            ScheduleExecutionStatus.CompleteWithError => CompleteWithErrorLabel(header),
            var status => StatusLabel(status)
        };
    }

    /// <summary>
    /// The status of a Schedule Execution as a person reads it: one key per status, so a new status is a compile-time gap.
    /// </summary>
    public static string StatusLabel(ScheduleExecutionStatus status) => status switch
    {
        ScheduleExecutionStatus.Queued => OperationsText.ScheduleExecQueued,
        ScheduleExecutionStatus.InProgress => OperationsText.CompletedStatusInProgress,
        ScheduleExecutionStatus.Complete => OperationsText.CompletedStatusComplete,
        ScheduleExecutionStatus.Failed => OperationsText.ScheduleExecFailed,
        ScheduleExecutionStatus.Cancelled => OperationsText.CompletedStatusCancelled,
        ScheduleExecutionStatus.Paused => OperationsText.ScheduleExecPaused,
        ScheduleExecutionStatus.CompleteWithError => OperationsText.CompletedStatusCompleteWithError,
        _ => status.ToString().SplitOnCapitalLetters()
    };

    /// <summary>
    /// The warning shown on a Complete With Error execution: the execution's own message naming the failed steps, or
    /// a general explanation when none was recorded.
    /// </summary>
    public static string CompleteWithErrorMessage(string? errorMessage) =>
        string.IsNullOrWhiteSpace(errorMessage) ? CompleteWithErrorFallback : errorMessage;

    /// <summary>
    /// "Failed on step 3": the step the run stopped on, clamped to the total in case the execution failed after
    /// advancing past its last step.
    /// </summary>
    private static string FailedLabel(ScheduleHeader header)
    {
        if (!header.LastExecutionCurrentStepIndex.HasValue || !header.LastExecutionTotalSteps.HasValue)
            return OperationsText.ScheduleExecFailed;

        var totalSteps = header.LastExecutionTotalSteps.Value;
        if (totalSteps < 1)
            return OperationsText.ScheduleExecFailed;

        var stepNumber = Math.Min(header.LastExecutionCurrentStepIndex.Value + 1, totalSteps);
        return string.Format(CultureInfo.CurrentCulture, OperationsText.ScheduleExecFailedOnStep, stepNumber);
    }

    /// <summary>
    /// "Complete With Error on steps 2 and 3". Steps in one parallel group share a step index, so each index is named
    /// once however many of its steps failed.
    /// </summary>
    private static string CompleteWithErrorLabel(ScheduleHeader header)
    {
        var stepNumbers = header.LastExecutionFailedStepIndices
            .Distinct()
            .Order()
            .Select(i => (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        return stepNumbers.Count switch
        {
            0 => OperationsText.CompletedStatusCompleteWithError,
            1 => string.Format(CultureInfo.CurrentCulture, OperationsText.ScheduleExecCompleteWithErrorOnStep, stepNumbers[0]),
            _ => string.Format(CultureInfo.CurrentCulture, OperationsText.ScheduleExecCompleteWithErrorOnSteps, string.Join(", ", stepNumbers.Take(stepNumbers.Count - 1)), stepNumbers[^1])
        };
    }
}

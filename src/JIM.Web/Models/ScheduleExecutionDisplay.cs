// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;
using JIM.Utilities;

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
    public const string CompleteWithErrorFallback =
        "The Schedule finished, but one or more steps failed. They are set to let the Schedule continue; the step statuses below show which.";

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
            var status => status.ToString().SplitOnCapitalLetters()
        };
    }

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
            return "Failed";

        var totalSteps = header.LastExecutionTotalSteps.Value;
        if (totalSteps < 1)
            return "Failed";

        var stepNumber = Math.Min(header.LastExecutionCurrentStepIndex.Value + 1, totalSteps);
        return $"Failed on step {stepNumber}";
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
            0 => "Complete With Error",
            1 => $"Complete With Error on step {stepNumbers[0]}",
            _ => $"Complete With Error on steps {string.Join(", ", stepNumbers.Take(stepNumbers.Count - 1))} and {stepNumbers[^1]}"
        };
    }
}

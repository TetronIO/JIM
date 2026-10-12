// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Worker;

/// <summary>
/// Decides how a finished Run Profile execution's Activity is completed, from the errors its execution items recorded.
/// </summary>
internal static class ActivityOutcomeEvaluator
{
    /// <summary>
    /// Every item errored fails the run; an unhandled error (a fault in JIM rather than in the data) completes it with
    /// an error; any other item error, or a warning carried on the Activity, completes it with a warning.
    /// </summary>
    /// <remarks>
    /// An Object Type change refused at import (#2049) is left out of the every-item-errored test. It is a safe,
    /// deliberate outcome that changed nothing, the same in kind as deletions a Run Profile limit withheld, which
    /// complete with a warning. Counted in, a run would fail or merely warn depending on whether anything else changed
    /// alongside the refused objects, and a failed run stops the Schedule it belongs to by default.
    /// </remarks>
    internal static ActivityStatus Evaluate(ActivityRpeiErrorCounts counts, bool hasWarningMessage)
    {
        var itemsOtherThanRefusals = counts.TotalRpeis - counts.TotalObjectTypeChangeRefusals;
        var errorsOtherThanRefusals = counts.TotalWithErrors - counts.TotalObjectTypeChangeRefusals;

        if (itemsOtherThanRefusals > 0 && errorsOtherThanRefusals == itemsOtherThanRefusals)
            return ActivityStatus.FailedWithError;

        if (counts.TotalUnhandledErrors > 0)
            return ActivityStatus.CompleteWithError;

        if (counts.TotalWithErrors > 0 || hasWarningMessage)
            return ActivityStatus.CompleteWithWarning;

        return ActivityStatus.Complete;
    }
}

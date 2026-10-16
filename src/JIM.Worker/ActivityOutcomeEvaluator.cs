// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;

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
    /// <para>
    /// Every item erroring is every object failing only where every object gets an item. An import gives none to an
    /// object it found unchanged, so one bad value among a hundred unchanged objects would read as a failed run (#2066):
    /// an import fails only when it read no more objects than it recorded errors for. Its object count is the whole
    /// run's, set as the import finishes; other run types' counters describe their last step, so they, and an import
    /// that recorded no count, keep the item rule.
    /// </para>
    /// </remarks>
    /// <param name="counts">What the run's execution items recorded.</param>
    /// <param name="hasWarningMessage">Whether the run left a warning on the Activity itself.</param>
    /// <param name="runType">The Run Profile's type, where known.</param>
    /// <param name="objectsProcessed">The Activity's final object count.</param>
    internal static ActivityStatus Evaluate(ActivityRpeiErrorCounts counts, bool hasWarningMessage,
        ConnectedSystemRunType? runType = null, int objectsProcessed = 0)
    {
        var itemsOtherThanRefusals = counts.TotalRpeis - counts.TotalObjectTypeChangeRefusals;
        var errorsOtherThanRefusals = counts.TotalWithErrors - counts.TotalObjectTypeChangeRefusals;
        var importReadObjectsBeyondTheErrors = runType is ConnectedSystemRunType.FullImport or ConnectedSystemRunType.DeltaImport
                                               && objectsProcessed > errorsOtherThanRefusals;

        if (itemsOtherThanRefusals > 0 && errorsOtherThanRefusals == itemsOtherThanRefusals && !importReadObjectsBeyondTheErrors)
            return ActivityStatus.FailedWithError;

        if (counts.TotalUnhandledErrors > 0)
            return ActivityStatus.CompleteWithError;

        if (counts.TotalWithErrors > 0 || hasWarningMessage)
            return ActivityStatus.CompleteWithWarning;

        return ActivityStatus.Complete;
    }
}

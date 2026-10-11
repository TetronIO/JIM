// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Worker;

/// <summary>
/// How a finished Run Profile's Activity ends, decided from what its execution items recorded.
/// </summary>
internal enum RunCompletionOutcome
{
    /// <summary>Nothing went wrong.</summary>
    Complete,

    /// <summary>Some objects or the run itself have something to look at, but the run did its work.</summary>
    CompleteWithWarning,

    /// <summary>An unhandled error, which points at JIM rather than the data, so it is escalated for investigation.</summary>
    CompleteWithError,

    /// <summary>Every object the run handled failed.</summary>
    Failed
}

/// <summary>
/// Decides how a finished Run Profile's Activity ends. Pure, so the rule is tested without driving a run.
/// </summary>
internal static class RunCompletionEvaluator
{
    /// <param name="itemsWithErrors">Execution items that recorded an error.</param>
    /// <param name="items">Every execution item the run recorded.</param>
    /// <param name="unhandledErrors">Execution items whose error was unhandled.</param>
    /// <param name="hasWarningMessage">Whether the run left a warning on the Activity itself.</param>
    /// <param name="runType">The Run Profile's type.</param>
    /// <param name="objectsProcessed">The Activity's final object count.</param>
    internal static RunCompletionOutcome Evaluate(int itemsWithErrors, int items, int unhandledErrors, bool hasWarningMessage,
        ConnectedSystemRunType? runType, int objectsProcessed)
    {
        // Every item errored. That is every object failing only where every object gets an item: an import gives none
        // to an object it found unchanged, so an import fails only when it read no more objects than it recorded
        // errors for (#2066). Its object count is the whole run's, set as the import finishes; other run types'
        // counters describe their last step, so they, and an import that recorded no count, keep the item rule.
        var everyItemErrored = items > 0 && itemsWithErrors == items;
        var objectsOutsideTheItems = runType is ConnectedSystemRunType.FullImport or ConnectedSystemRunType.DeltaImport
                                     && objectsProcessed > itemsWithErrors;
        if (everyItemErrored && !objectsOutsideTheItems)
            return RunCompletionOutcome.Failed;

        if (unhandledErrors > 0)
            return RunCompletionOutcome.CompleteWithError;

        if (itemsWithErrors > 0 || hasWarningMessage)
            return RunCompletionOutcome.CompleteWithWarning;

        return RunCompletionOutcome.Complete;
    }
}

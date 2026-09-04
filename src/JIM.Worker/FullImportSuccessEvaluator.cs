// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Worker;

/// <summary>
/// Decides whether a completed Full Import run's Activity counts as "successful" for the purposes of the
/// #1605 stranded-value sweep gate: only a genuinely successful Full Import may stamp
/// <see cref="JIM.Models.Staging.ConnectedSystem.LastSuccessfulFullImportCompletedAt"/>, because the gate's
/// whole job is telling a re-imported Connector Space apart from an empty or half-rebuilt one.
/// </summary>
internal static class FullImportSuccessEvaluator
{
    /// <summary>
    /// Complete always counts. CompleteWithWarning counts only when no object-level errors were recorded
    /// and deletion detection withheld nothing (#1618 Run Profile Safeguards): an object that failed to
    /// import was never staged, so counting a CompleteWithWarning caused by object-level errors would let
    /// the sweep treat those un-staged objects as departed; a run whose deletion detection refused because
    /// it would have exceeded the Run Profile's limits marked nothing as deleted, so it must not count
    /// either, or the sweep would treat the Connector Space as fully re-imported when it was not checked
    /// for departures at all. CompleteWithError, FailedWithError and Cancelled never count.
    /// </summary>
    /// <param name="status">The Activity's final status.</param>
    /// <param name="objectLevelErrorCount">How many Run Profile Execution Items recorded an error.</param>
    /// <param name="detectedDeletionsWithheld">
    /// How many Connected System Objects deletion detection would have newly marked as deleted but
    /// withheld because a Run Profile Safeguards limit was exceeded. Zero (or the Activity's default when
    /// the caller has nothing better) when nothing was withheld.
    /// </param>
    internal static bool IsSuccessfulFullImport(ActivityStatus status, int objectLevelErrorCount, int detectedDeletionsWithheld)
    {
        if (detectedDeletionsWithheld > 0)
            return false;

        return status switch
        {
            ActivityStatus.Complete => true,
            ActivityStatus.CompleteWithWarning => objectLevelErrorCount == 0,
            _ => false
        };
    }
}

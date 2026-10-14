// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Transactional;

/// <summary>
/// Summary of a reference recall staging pass (membership-removal Pending Exports staged for
/// Metaverse Objects that referenced deleted Metaverse Objects). Logged by callers per the
/// batch-summary logging requirements.
/// </summary>
public class ReferenceRecallResult
{
    /// <summary>
    /// Distinct referencing Metaverse Objects that were re-evaluated for export.
    /// </summary>
    public int ReferencingObjectsEvaluated { get; set; }

    /// <summary>
    /// Pending Exports staged (created or merged into) carrying reference-removal changes.
    /// </summary>
    public int PendingExportsStaged { get; set; }

    /// <summary>
    /// Individual reference-removal attribute changes staged across all Pending Exports.
    /// </summary>
    public int RemovalChangesStaged { get; set; }

    /// <summary>
    /// Removal changes dropped because the deleted object had no resolvable presence in the
    /// target Connected System (it was never provisioned there, so there is nothing to remove).
    /// </summary>
    public int UnresolvableChangesDropped { get; set; }

    /// <summary>
    /// Referencing objects handled by the set-based fast path (#1003).
    /// </summary>
    public int FastPathReferencingObjects { get; set; }

    /// <summary>
    /// Referencing objects routed through the full-evaluation fallback because an applicable
    /// export rule sources a candidate reference attribute non-directly.
    /// </summary>
    public int FallbackReferencingObjects { get; set; }

    /// <summary>
    /// Referencing CSOs skipped because an existing Delete Pending Export supersedes any
    /// membership update (the object is being deprovisioned from the target).
    /// </summary>
    public int SkippedDueToExistingDeletePendingExport { get; set; }

    /// <summary>
    /// The Pending Exports persisted by this staging pass, for the caller to fold into Activity
    /// reporting (RPEIs and PendingExportCreated outcomes). Bounded by removals per flush.
    /// </summary>
    public List<PendingExport> StagedPendingExports { get; } = [];

    /// <summary>
    /// Referencing objects whose scope for an export rule could not be decided, so nothing was staged for them. The
    /// caller records each as that object's error; the rest of the recall is unaffected.
    /// </summary>
    public List<ReferenceRecallScopingFailure> ScopingFailures { get; } = [];

    /// <summary>
    /// One error execution item per <see cref="ScopingFailures"/> entry, for callers that collect items to persist
    /// themselves rather than preparing them on an Activity.
    /// </summary>
    public IEnumerable<ActivityRunProfileExecutionItem> ScopingFailureExecutionItems() =>
        ScopingFailures.Select(failure => new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ErrorType = ActivityRunProfileExecutionItemErrorType.ScopingCriterionFailed,
            ErrorMessage = failure.DescribeForAdministrator(),
            DisplayNameSnapshot = ReferencingObjectDisplayNames.GetValueOrDefault(failure.MetaverseObjectId)
        });

    /// <summary>
    /// Referencing Metaverse Object display names keyed by MVO id, for Activity reporting
    /// snapshots without reloading the objects.
    /// </summary>
    public Dictionary<Guid, string?> ReferencingObjectDisplayNames { get; } = [];
}

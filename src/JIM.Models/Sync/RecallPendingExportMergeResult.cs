// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// What merging reference recall changes with a CSO's existing Pending Export decided (#288 outbound
/// extraction; returned by <c>ISyncEngine.MergeRecallChangesWithExistingPendingExport</c>).
/// </summary>
public readonly struct RecallPendingExportMergeResult : IEquatable<RecallPendingExportMergeResult>
{
    /// <summary>
    /// The verdict: stage the merged changes, or skip because an existing Delete or Create export wins.
    /// </summary>
    public RecallPendingExportMergeOutcome Outcome { get; init; }

    /// <summary>
    /// How many of the existing export's changes were purged because their unresolved reference is a deleted
    /// Metaverse Object: they can never resolve, and merged in they would wedge the export in
    /// deferred-resolution limbo.
    /// </summary>
    public int PurgedChangeCount { get; init; }

    /// <inheritdoc />
    public bool Equals(RecallPendingExportMergeResult other) =>
        Outcome == other.Outcome && PurgedChangeCount == other.PurgedChangeCount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RecallPendingExportMergeResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Outcome, PurgedChangeCount);
}

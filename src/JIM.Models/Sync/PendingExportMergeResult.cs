// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Sync;

/// <summary>
/// What merging newly evaluated attribute changes into an already-staged Pending Export did (#288 outbound
/// extraction; returned by <c>ISyncEngine.MergeAttributeChangesIntoPendingExport</c>). Carried so the
/// orchestrator can log the merge exactly as the braided implementation did.
/// </summary>
public readonly struct PendingExportMergeResult : IEquatable<PendingExportMergeResult>
{
    /// <summary>
    /// How many incoming changes replaced a staged change with the same merge key (export evaluation wins;
    /// it derives from the latest Metaverse Object state).
    /// </summary>
    public int ReplacedCount { get; init; }

    /// <summary>
    /// How many incoming changes were new to the staged Pending Export and were added.
    /// </summary>
    public int AddedCount { get; init; }

    /// <inheritdoc />
    public bool Equals(PendingExportMergeResult other) =>
        ReplacedCount == other.ReplacedCount && AddedCount == other.AddedCount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PendingExportMergeResult other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ReplacedCount, AddedCount);
}

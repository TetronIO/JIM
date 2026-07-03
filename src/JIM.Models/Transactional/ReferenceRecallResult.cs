// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// The outcome of previewing a full synchronisation of one Connected System (#288, PRD decision D2): the
/// whole-population count tier, a bounded per-category sample of full trees, and an explicit statement of
/// whether the work budget truncated the evaluation (requirement 14). Nothing here is persisted, and the
/// preview persisted nothing to produce it.
/// </summary>
public class FullSyncPreviewResult
{
    /// <summary>
    /// The Connected System that was previewed.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// How many objects the Connected System holds; always the full population size, whether or not the
    /// budget allowed evaluating them all.
    /// </summary>
    public int TotalObjectCount { get; set; }

    /// <summary>
    /// How many objects were actually evaluated before the preview completed or its budget ran out.
    /// </summary>
    public int EvaluatedObjectCount { get; set; }

    /// <summary>
    /// How many loaded objects were skipped without evaluation (obsolete objects awaiting cleanup).
    /// </summary>
    public int SkippedObjectCount { get; set; }

    /// <summary>
    /// True when the work budget stopped the preview before the whole population was evaluated; the counts
    /// and samples then describe the evaluated subset, not the system (PRD requirement 14).
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// Which budget stopped the preview, when one did.
    /// </summary>
    public FullSyncPreviewTruncationReason TruncationReason { get; set; }

    /// <summary>
    /// The whole-population count tier (PRD requirement 12).
    /// </summary>
    public FullSyncPreviewCounts Counts { get; set; } = new();

    /// <summary>
    /// The bounded per-category full-tree samples (PRD requirement 12's sampled tier).
    /// </summary>
    public List<FullSyncPreviewSample> Samples { get; set; } = [];

    /// <summary>
    /// Run-level blocking conditions (the system could not be previewed at all); per-object blockers live
    /// on each object's preview and are counted in <see cref="FullSyncPreviewCounts.BlockedByErrors"/>.
    /// </summary>
    public List<SyncPreviewMessage> Errors { get; set; } = [];

    /// <summary>
    /// Run-level advisory conditions.
    /// </summary>
    public List<SyncPreviewMessage> Warnings { get; set; } = [];
}

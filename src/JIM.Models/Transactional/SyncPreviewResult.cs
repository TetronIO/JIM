// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Sync;

namespace JIM.Models.Transactional;

/// <summary>
/// The speculative outcome of previewing a synchronisation for one object (#288, PRD requirement 1): the
/// causal outcome tree, the inbound summary (CSO previews only), the outbound summary composed from
/// <see cref="ExportEvaluationPreviewResult"/> (requirement 2: one definition of the create/update/delete
/// counters), and blocking Errors held programmatically apart from advisory Warnings (requirement 16).
/// Nothing here is persisted; a preview that could not complete for an expected reason still returns, with
/// the blocker in <see cref="Errors"/>.
/// </summary>
public class SyncPreviewResult
{
    /// <summary>
    /// The speculative causal outcome tree, in the same shape a real Run Profile Execution Item's outcomes
    /// render from (PRD decision D4; the shared mapping is <see cref="SyncOutcomeNode.FromSyncOutcome"/>).
    /// </summary>
    public List<SyncOutcomeNode> OutcomeTree { get; set; } = [];

    /// <summary>
    /// The inbound summary: project or join, and the attribute flows. Null for an MVO preview, which has no
    /// inbound chain.
    /// </summary>
    public SyncPreviewInboundSummary? Inbound { get; set; }

    /// <summary>
    /// The outbound summary: the Pending Exports that would be staged per target Connected System, none of
    /// them persisted. Composes the existing preview result so the create/update/delete counters have one
    /// definition (PRD requirement 2).
    /// </summary>
    public ExportEvaluationPreviewResult Outbound { get; set; } = new();

    /// <summary>
    /// The per-(Metaverse Object, export Synchronisation Rule) outbound decision records behind
    /// <see cref="Outbound"/>, as the Phase 2 evaluation-only path produced them.
    /// </summary>
    public OutboundPreviewResult OutboundDecisions { get; set; } = new();

    /// <summary>
    /// Conditions that would prevent the real sync (PRD requirement 16); distinct from
    /// <see cref="Warnings"/> so a consumer renders blockers and advisories differently without string
    /// parsing.
    /// </summary>
    public List<SyncPreviewMessage> Errors { get; set; } = [];

    /// <summary>
    /// Advisory conditions that would not prevent the real sync.
    /// </summary>
    public List<SyncPreviewMessage> Warnings { get; set; } = [];

    /// <summary>
    /// True when the preview surfaced at least one blocking condition.
    /// </summary>
    public bool HasBlockingErrors => Errors.Count > 0;

    /// <summary>
    /// The Synchronisation Rules that participated at any step of the previewed chain.
    /// </summary>
    public List<SyncPreviewSyncRuleReference> AffectedSyncRules { get; set; } = [];
}

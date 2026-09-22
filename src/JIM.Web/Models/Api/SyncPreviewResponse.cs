// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Web.Models.Api;

/// <summary>
/// The speculative outcome of previewing a synchronisation for one object: what would happen were the
/// real synchronisation to run now. Nothing here is persisted; a preview that could not complete for an
/// expected reason (for example, the object does not exist) still returns, with the blocker in
/// <see cref="Errors"/> rather than the endpoint failing.
/// </summary>
public class SyncPreviewResponse
{
    /// <summary>
    /// The speculative causal outcome tree, in the same shape a really-recorded Run Profile Execution
    /// Item's outcomes render from.
    /// </summary>
    public List<SyncOutcomeNodeDto> OutcomeTree { get; set; } = [];

    /// <summary>
    /// The inbound summary: would the object project or join, and what attribute flows would result. Null
    /// for a Metaverse Object preview, which has no inbound chain.
    /// </summary>
    public SyncPreviewInboundSummaryDto? Inbound { get; set; }

    /// <summary>
    /// The Pending Exports that would be staged per target Connected System, were the real synchronisation
    /// to run. None of these are persisted.
    /// </summary>
    public List<SyncPreviewProposedExportDto> ProposedExports { get; set; } = [];

    /// <summary>
    /// Conditions that would prevent the real synchronisation. Distinct from <see cref="Warnings"/> so a
    /// caller can render blockers and advisories differently without parsing message text.
    /// </summary>
    public List<SyncPreviewMessageDto> Errors { get; set; } = [];

    /// <summary>
    /// Advisory conditions that would not prevent the real synchronisation.
    /// </summary>
    public List<SyncPreviewMessageDto> Warnings { get; set; } = [];

    /// <summary>
    /// True when the preview surfaced at least one blocking condition (that is, <see cref="Errors"/> is
    /// non-empty).
    /// </summary>
    public bool HasBlockingErrors { get; set; }

    /// <summary>
    /// The Synchronisation Rules that participated at any step of the previewed chain.
    /// </summary>
    public List<SyncRuleReferenceDto> AffectedSyncRules { get; set; } = [];

    public static SyncPreviewResponse FromModel(SyncPreviewResult result) => new()
    {
        OutcomeTree = [.. result.OutcomeTree.Select(SyncOutcomeNodeDto.FromModel)],
        Inbound = result.Inbound == null ? null : SyncPreviewInboundSummaryDto.FromModel(result.Inbound),
        ProposedExports = [.. result.Outbound.ProposedExports.Select(SyncPreviewProposedExportDto.FromModel)],
        Errors = [.. result.Errors.Select(SyncPreviewMessageDto.FromModel)],
        Warnings = [.. result.Warnings.Select(SyncPreviewMessageDto.FromModel)],
        HasBlockingErrors = result.HasBlockingErrors,
        AffectedSyncRules = [.. result.AffectedSyncRules.Select(SyncRuleReferenceDto.FromModel)]
    };
}

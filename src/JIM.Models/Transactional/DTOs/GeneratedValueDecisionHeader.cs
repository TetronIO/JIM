// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// One generated value waiting on, or released from, an administrator's decision (Unique Value Generation, #242,
/// release 4, Phase 9): a row of the Generated Values tab, the Metaverse Object banner, the REST decision list and
/// <c>Get-JIMGeneratedValueDecision</c>. Denormalised so no row needs a second read: the object, the attribute, the
/// systems involved and the Synchronisation Rule are all named.
/// </summary>
public class GeneratedValueDecisionHeader
{
    /// <summary>The generated value's assignment id: what the actions take.</summary>
    public Guid AssignmentId { get; set; }

    /// <summary>Where the decision stands.</summary>
    public GeneratedValueDecisionStatus Status { get; set; }

    /// <summary>
    /// The Metaverse Object the value belongs to (import mode), or the one the value's Connected System Object is joined
    /// to (export mode, where it may be null).
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>The display name of <see cref="MetaverseObjectId"/>, when it has one.</summary>
    public string? MetaverseObjectDisplayName { get; set; }

    /// <summary>The name of <see cref="MetaverseObjectId"/>'s Metaverse Object Type.</summary>
    public string? MetaverseObjectTypeName { get; set; }

    /// <summary>The plural name of <see cref="MetaverseObjectId"/>'s Metaverse Object Type, for its link.</summary>
    public string? MetaverseObjectTypePluralName { get; set; }

    /// <summary>Export mode: the Connected System Object the value belongs to. Null for an import-mode value.</summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>Export mode: the Connected System holding <see cref="ConnectedSystemObjectId"/>.</summary>
    public int? ConnectedSystemObjectConnectedSystemId { get; set; }

    /// <summary>The attribute the value was generated for: a Metaverse attribute, or (export mode) a Connected System attribute.</summary>
    public string AttributeName { get; set; } = null!;

    /// <summary>The value the target rejected, which is still the value JIM holds.</summary>
    public string Value { get; set; } = null!;

    /// <summary>
    /// Why the value is held; null when the reason was not recorded (a decision older than the reason column) or when the
    /// value is not held.
    /// </summary>
    public GeneratedValueNeedsDecisionReason? Reason { get; set; }

    /// <summary>How many times Collision Remediation has already corrected this value.</summary>
    public int RemediationCount { get; set; }

    /// <summary>The Connected System whose export rejected the value.</summary>
    public int? RejectedByConnectedSystemId { get; set; }

    /// <summary>The name of <see cref="RejectedByConnectedSystemId"/>; null when that system has since been deleted.</summary>
    public string? RejectedByConnectedSystemName { get; set; }

    /// <summary>
    /// The Connected System that anchors the value (<see cref="GeneratedValueNeedsDecisionReason.AnchoredElsewhere"/>) or
    /// cannot say whether it does (<see cref="GeneratedValueNeedsDecisionReason.CannotTell"/>).
    /// </summary>
    public int? AnchoredByConnectedSystemId { get; set; }

    /// <summary>The name of <see cref="AnchoredByConnectedSystemId"/>; null when that system has since been deleted.</summary>
    public string? AnchoredByConnectedSystemName { get; set; }

    /// <summary>When the value began waiting on a decision (UTC).</summary>
    public DateTime? Since { get; set; }

    /// <summary>When an administrator allowed the rename (UTC); null unless <see cref="Status"/> is RenameAllowed.</summary>
    public DateTime? RenameAllowedAt { get; set; }

    /// <summary>Who allowed the rename; null unless <see cref="Status"/> is RenameAllowed.</summary>
    public string? RenameAllowedBy { get; set; }

    /// <summary>The Synchronisation Rule whose Attribute Flow generated the value.</summary>
    public int SyncRuleId { get; set; }

    /// <summary>The name of <see cref="SyncRuleId"/>.</summary>
    public string? SyncRuleName { get; set; }

    /// <summary>The Attribute Flow (mapping) that generated the value.</summary>
    public int SyncRuleMappingId { get; set; }
}

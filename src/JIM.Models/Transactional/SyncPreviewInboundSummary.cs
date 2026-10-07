// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// The inbound half of a CSO preview (#288, PRD requirement 1): would the CSO project a new Metaverse Object
/// or join an existing one, and what Metaverse attribute values would flow. Absent from an MVO preview, which
/// has no inbound chain.
/// </summary>
public class SyncPreviewInboundSummary
{
    /// <summary>
    /// True when the CSO would project a new Metaverse Object.
    /// </summary>
    public bool WouldProject { get; set; }

    /// <summary>
    /// The Metaverse Object Type a projection would create, when <see cref="WouldProject"/>.
    /// </summary>
    public int? ProjectedMetaverseObjectTypeId { get; set; }

    /// <summary>
    /// Snapshot of the projected type's name.
    /// </summary>
    public string? ProjectedMetaverseObjectTypeName { get; set; }

    /// <summary>
    /// The existing Metaverse Object the CSO would join via Object Matching Rules, when one matched.
    /// </summary>
    public Guid? WouldJoinMetaverseObjectId { get; set; }

    /// <summary>
    /// The Metaverse Object the CSO is already joined to, when it is; the preview then evaluates flows and
    /// the outbound chain against it.
    /// </summary>
    public Guid? AlreadyJoinedMetaverseObjectId { get; set; }

    /// <summary>
    /// The Metaverse Object Type of the object the CSO is, or would be, connected to: the joined one, the one it would
    /// join, or the one it would project (#1530).
    /// </summary>
    public int? MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// Snapshot of that type's name.
    /// </summary>
    public string? MetaverseObjectTypeName { get; set; }

    /// <summary>
    /// The connected Metaverse Object's name as it stands now, or a projection's name as Attribute Flow would give it
    /// (#1530). Snapshotted because a preview's rows name objects its consequences may delete or rename.
    /// </summary>
    public string? MetaverseObjectDisplayName { get; set; }

    /// <summary>
    /// The Metaverse attribute changes synchronising the CSO would make, one entry per attribute value added or
    /// removed: what inbound Attribute Flow writes, or, for a CSO leaving (out of scope or obsolete), the values it
    /// contributed being withdrawn and any surviving contributor's taking their place.
    /// </summary>
    public List<SyncPreviewAttributeFlowChange> AttributeFlowChanges { get; set; } = [];
}

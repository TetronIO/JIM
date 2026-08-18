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
    /// The Metaverse attribute changes inbound Attribute Flow would make, one entry per attribute value
    /// added or removed.
    /// </summary>
    public List<SyncPreviewAttributeFlowChange> AttributeFlowChanges { get; set; } = [];
}

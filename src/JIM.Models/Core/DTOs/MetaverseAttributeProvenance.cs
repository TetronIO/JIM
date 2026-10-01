// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// Everything the attribute inspector shows for one attribute on one Metaverse Object (#399): the current value(s)
/// and their origin, the contributing Connected System Object, when and by what the value was last set, every
/// contributing source in priority order with the value it would supply, and the attribute's change history.
/// </summary>
public class MetaverseAttributeProvenance
{
    public Guid MetaverseObjectId { get; set; }

    public int MetaverseObjectTypeId { get; set; }

    public int AttributeId { get; set; }

    public string AttributeName { get; set; } = null!;

    public AttributeDataType AttributeType { get; set; }

    public AttributePlurality AttributePlurality { get; set; }

    /// <summary>Current values, capped for multi-valued attributes; see <see cref="CurrentValueTotalCount"/>.</summary>
    public List<ProvenanceValue> CurrentValues { get; set; } = new();

    public int CurrentValueTotalCount { get; set; }

    /// <summary>The joined Connected System Object the current value came from, where one can be identified.</summary>
    public ProvenanceConnectedSystemObject? ContributingConnectedSystemObject { get; set; }

    /// <summary>The most recent recorded change that set the current value; null when change tracking recorded none.</summary>
    public ProvenanceChange? LastSet { get; set; }

    /// <summary>Every import mapping targeting this attribute on this object type, in priority order (rank 1 first).</summary>
    public List<AttributeSourceCandidate> Sources { get; set; } = new();

    /// <summary>The attribute's change history, newest first, capped; see <see cref="HistoryTruncated"/>.</summary>
    public List<AttributeHistoryEntry> History { get; set; } = new();

    public bool HistoryTruncated { get; set; }
}

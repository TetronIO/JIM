// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Where the value queued for one Connected System attribute on a Pending Export originated (#399's "Value from"
/// column), resolved from the export mapping that the staging Synchronisation Rule used: either a single
/// Metaverse attribute (with the origin of the source Metaverse Object's current value for it), or a computed
/// source (an expression, an advanced/chained mapping, or Unique Value Generation). One entry per Connected
/// System attribute the Pending Export carries changes for; there is no entry when the export mapping cannot be
/// resolved (the staging rule or mapping has since been deleted) or when the Pending Export has no source
/// Metaverse Object (a delete).
/// </summary>
public class PendingExportValueSource
{
    public int ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// True when the mapping computes the value (an expression, an advanced/chained mapping, or generation)
    /// rather than passing a single Metaverse attribute through unchanged. <see cref="Expression"/> describes it;
    /// <see cref="SourceMetaverseAttributeId"/>, <see cref="Origin"/> and <see cref="HasSeveralOrigins"/> are
    /// meaningless when this is true.
    /// </summary>
    public bool IsComputed { get; set; }

    /// <summary>The text to show for a computed source, when <see cref="IsComputed"/> is true.</summary>
    public string? Expression { get; set; }

    /// <summary>The single Metaverse attribute the value came from, when <see cref="IsComputed"/> is false.</summary>
    public int? SourceMetaverseAttributeId { get; set; }

    public string? SourceMetaverseAttributeName { get; set; }

    /// <summary>
    /// The origin of the source Metaverse Object's current value for <see cref="SourceMetaverseAttributeId"/>,
    /// when not computed. Defaults to <see cref="ValueOrigin.NotRecorded"/> when the source Metaverse Object holds
    /// no current value for the attribute.
    /// </summary>
    public ValueOrigin Origin { get; set; } = ValueOrigin.NotRecorded;

    /// <summary>
    /// True when the source Metaverse attribute is multi-valued and its current values came from more than one
    /// distinct origin, in which case <see cref="Origin"/> is only the first and the caller should render
    /// "Several sources" instead (matching <c>ValueOriginChip.HasSeveralOrigins</c>).
    /// </summary>
    public bool HasSeveralOrigins { get; set; }
}

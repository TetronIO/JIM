// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The origin of every attribute on one Metaverse Object (#399): what the Inspect view's Source column, contribution
/// bar, source filter and Group by Source are built from.
/// </summary>
public class MetaverseObjectProvenance
{
    public Guid MetaverseObjectId { get; set; }

    /// <summary>One entry per attribute that holds at least one value (asserted-null rows included).</summary>
    public List<MetaverseAttributeOriginSummary> Attributes { get; set; } = new();
}

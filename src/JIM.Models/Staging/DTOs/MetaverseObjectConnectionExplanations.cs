// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Why a Metaverse Object is connected where it is, and why not elsewhere (#348). Every explanation in it was
/// evaluated at <see cref="EvaluatedAt"/>.
/// </summary>
public sealed class MetaverseObjectConnectionExplanations
{
    public Guid MetaverseObjectId { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>The one instant, in UTC, every scoping evaluation in this result resolved relative dates against.</summary>
    public DateTime EvaluatedAt { get; set; }

    public List<MetaverseObjectConnectionExplanation> Connections { get; set; } = [];

    /// <summary>
    /// Enabled export Synchronisation Rules whose Connected System holds no object joined to this one, ordered by
    /// Connected System then rule name; null unless requested.
    /// </summary>
    public List<NotConnectedEntry>? NotConnected { get; set; }
}

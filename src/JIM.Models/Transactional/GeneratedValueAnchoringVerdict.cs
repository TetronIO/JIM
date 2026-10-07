// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// Whether a rejected generated value is anchored (Unique Value Generation, #242, release 4; plan decision 10), and
/// by which Connected System: the one that has accepted the value for the same object, or the one that cannot say.
/// </summary>
/// <param name="Anchoring">The verdict.</param>
/// <param name="ConnectedSystemId">The Connected System that anchors the value or cannot tell; null when unanchored.</param>
public sealed record GeneratedValueAnchoringVerdict(GeneratedValueAnchoring Anchoring, int? ConnectedSystemId)
{
    /// <summary>
    /// No other participating Connected System holds the value for the object.
    /// </summary>
    public static GeneratedValueAnchoringVerdict Unanchored { get; } = new(GeneratedValueAnchoring.Unanchored, null);
}

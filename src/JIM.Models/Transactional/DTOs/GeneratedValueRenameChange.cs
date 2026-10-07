// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// One Connected System that allowing a held generated value's rename will change, and how (Unique Value Generation,
/// #242, release 4, Phase 9).
/// </summary>
public class GeneratedValueRenameChange
{
    /// <summary>The Connected System the value is exported to.</summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>The Connected System's name.</summary>
    public string ConnectedSystemName { get; set; } = null!;

    /// <summary>What happens to the object's account there.</summary>
    public GeneratedValueRenameChangeKind Kind { get; set; }
}

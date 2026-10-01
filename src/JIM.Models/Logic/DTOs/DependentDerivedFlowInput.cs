// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.DTOs;

/// <summary>
/// One input a <see cref="DependentDerivedFlow"/> is left without: a Metaverse attribute that lost its last enabled
/// contributor, and how the flow reaches it.
/// </summary>
public class DependentDerivedFlowInput
{
    /// <summary>
    /// The Metaverse attribute that lost its last enabled contributor.
    /// </summary>
    public string MetaverseAttributeName { get; set; } = null!;

    /// <summary>
    /// True when the flow reaches the attribute through other derived attributes (listed in <see cref="Via"/>)
    /// rather than reading it directly.
    /// </summary>
    public bool Indirect { get; set; }

    /// <summary>
    /// The derived attributes in between, from the flow's own input towards <see cref="MetaverseAttributeName"/>.
    /// Empty when the flow reads the attribute directly.
    /// </summary>
    public List<string> Via { get; set; } = new();
}

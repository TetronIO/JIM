// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// The root cause of a <see cref="DerivedFlowDependent"/> (#1750, FR 3): a Metaverse attribute the change leaves with
/// no enabled contributor at all, and how the dependant reaches it.
/// </summary>
/// <param name="MetaverseAttributeId">The attribute that lost its last enabled contributor.</param>
/// <param name="MetaverseAttributeName">Its name, for messages.</param>
/// <param name="Via">The derived attributes the dependant reaches it through, read from the dependant's own input
/// towards the attribute that lost its contributor. Empty when the dependant reads the attribute directly.</param>
public sealed record DerivedFlowLostInput(int MetaverseAttributeId, string MetaverseAttributeName, IReadOnlyList<string> Via)
{
    /// <summary>
    /// Whether the dependant reaches the attribute through other derived attributes rather than reading it directly.
    /// </summary>
    public bool Indirect => Via.Count > 0;
}

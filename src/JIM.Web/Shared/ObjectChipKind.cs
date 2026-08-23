// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Shared;

/// <summary>
/// Which side of the Metaverse an object sits on, which is what an <see cref="ObjectChip"/>'s avatar says.
/// The object counterpart of <see cref="AttributeChipKind"/>.
/// </summary>
public enum ObjectChipKind
{
    /// <summary>
    /// A Connected System Object: a record as it exists in a Connected System, shown with a <c>CS</c> avatar.
    /// </summary>
    ConnectedSystem,

    /// <summary>
    /// A Metaverse Object: the Identity JIM holds, shown with an <c>MV</c> avatar.
    /// </summary>
    Metaverse
}

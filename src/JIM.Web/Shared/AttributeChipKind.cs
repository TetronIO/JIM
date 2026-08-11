// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Shared;

/// <summary>
/// Which side of the Metaverse an attribute rendered by <c>AttributeChip</c> belongs to, or that its value is
/// computed rather than read from an attribute at all.
/// </summary>
public enum AttributeChipKind
{
    /// <summary>
    /// An attribute on a Connected System, shown with a <c>CS</c> avatar.
    /// </summary>
    ConnectedSystem,

    /// <summary>
    /// A Metaverse Attribute, shown with an <c>MV</c> avatar.
    /// </summary>
    Metaverse,

    /// <summary>
    /// A computed value, shown with an <c>Ex</c> avatar and the expression text.
    /// </summary>
    Expression
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// Which side of the Metaverse an Expression reads an input from.
/// </summary>
public enum ExpressionInputSource
{
    /// <summary>
    /// A Metaverse Object attribute, written as mv["Attribute Name"].
    /// </summary>
    Metaverse,

    /// <summary>
    /// A Connected System Object attribute, written as cs["Attribute Name"].
    /// </summary>
    ConnectedSystem
}

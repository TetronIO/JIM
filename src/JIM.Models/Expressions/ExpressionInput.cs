// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// An attribute an Expression reads, resolved from the Expression's own text.
/// </summary>
/// <param name="Source">Which side of the Metaverse the attribute is read from.</param>
/// <param name="AttributeName">The attribute name as written in the Expression.</param>
public record ExpressionInput(ExpressionInputSource Source, string AttributeName)
{
    /// <summary>
    /// The input as it appears in the Expression, for example <c>mv["Display Name"]</c>. Used as a
    /// display label and as the key a caller supplies a sample value against.
    /// </summary>
    public string Accessor => Source == ExpressionInputSource.Metaverse
        ? $"mv[\"{AttributeName}\"]"
        : $"cs[\"{AttributeName}\"]";
}

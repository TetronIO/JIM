// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Expressions;

/// <summary>
/// One input an Attribute Flow's Expression reads, described from the schema (#2055), so the tester can offer an
/// editor of the right type and as many values as the attribute holds.
/// </summary>
/// <param name="Source">Which side the attribute is read from.</param>
/// <param name="AttributeName">The attribute's name, as the Expression reads it.</param>
/// <param name="Type">The attribute's type, or null when the object type has no such attribute, or the Attribute Flow
/// cannot read this side (a <c>cs[...]</c> input in an export Attribute Flow).</param>
/// <param name="Plurality">Whether the attribute holds one value or several; null when <paramref name="Type"/> is.</param>
public sealed record AttributeFlowTestInputDescriptor(
    ExpressionInputSource Source,
    string AttributeName,
    AttributeDataType? Type,
    AttributePlurality? Plurality)
{
    /// <summary>
    /// The input as an Expression writes it, for example <c>cs["sn"]</c>.
    /// </summary>
    public string Accessor => Source == ExpressionInputSource.Metaverse ? $"mv[\"{AttributeName}\"]" : $"cs[\"{AttributeName}\"]";

    /// <summary>
    /// Whether the object being synchronised can hold a value for this input.
    /// </summary>
    public bool InSchema => Type != null;
}

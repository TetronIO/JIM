// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Expressions;

/// <summary>
/// One input to the Attribute Flow tester (#2055): the sample values for one attribute an Expression reads, before they
/// are converted to the attribute's type.
/// </summary>
/// <param name="Source">Which side the attribute is read from.</param>
/// <param name="AttributeName">The attribute's name, as the Expression reads it.</param>
/// <param name="Values">The sample values: text, or a typed value (a number, a date and time, a Boolean, a GUID, or
/// a Binary value). Empty means the object has no value for the attribute, which is distinct from empty text. Several
/// values are for a multi-valued attribute.</param>
public sealed record AttributeFlowTestInput(ExpressionInputSource Source, string AttributeName, IReadOnlyList<object> Values)
{
    /// <summary>
    /// The input as an Expression writes it, for example <c>cs["sn"]</c>.
    /// </summary>
    public string Accessor => Source == ExpressionInputSource.Metaverse ? $"mv[\"{AttributeName}\"]" : $"cs[\"{AttributeName}\"]";
}

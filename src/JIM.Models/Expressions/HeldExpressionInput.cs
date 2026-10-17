// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Expressions;

/// <summary>
/// An input an Expression reads that the object holds only under another data type (#2319): a schema refresh changed
/// the attribute's type and no import has read it since.
/// </summary>
/// <param name="Accessor">The input as the Expression addresses it, for example <c>cs["employeeNumber"]</c>.</param>
/// <param name="HeldDataType">The data type the value is held as.</param>
public sealed record HeldExpressionInput(string Accessor, AttributeDataType HeldDataType)
{
    /// <summary>The input and the type it is held as, as an administrator reads them: <c>cs["employeeNumber"] (held as Text)</c>.</summary>
    public override string ToString() => $"{Accessor} (held as {HeldDataType})";
}

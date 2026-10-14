// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// One suggestion the expression editor offers at the caret (#2053).
/// </summary>
/// <param name="Kind">What it inserts.</param>
/// <param name="Label">What the list shows: the function or attribute name.</param>
/// <param name="InsertText">What replaces the text being typed when it is chosen.</param>
/// <param name="Detail">A function's signature; null for an attribute.</param>
/// <param name="Description">A function's description, as plain text; null for an attribute.</param>
public sealed record ExpressionSuggestion(ExpressionSuggestionKind Kind, string Label, string InsertText, string? Detail, string? Description);

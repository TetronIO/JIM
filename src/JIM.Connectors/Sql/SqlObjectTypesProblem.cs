// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql;

/// <summary>
/// One thing wrong with an Object Types document, with as much as is known about where it is, so an editor
/// can show it beside the Object Type or line it concerns.
/// </summary>
/// <param name="Message">The administrator-facing account of the problem; the same text saving the document would refuse it with.</param>
/// <param name="ObjectTypeIndex">The zero-based position of the Object Type it concerns in the 'objectTypes' list, or null for the document as a whole.</param>
/// <param name="ObjectTypeName">The name of that Object Type, where it has one.</param>
/// <param name="Field">The field it concerns, where one can be named (a misspelt field's own name, for one).</param>
/// <param name="LineNumber">The one-based line the problem is on, where the problem is in the JSON itself rather than in what it says.</param>
/// <param name="Column">The one-based position within <paramref name="LineNumber"/>, in bytes, where known.</param>
public sealed record SqlObjectTypesProblem(
    string Message,
    int? ObjectTypeIndex = null,
    string? ObjectTypeName = null,
    string? Field = null,
    long? LineNumber = null,
    long? Column = null);

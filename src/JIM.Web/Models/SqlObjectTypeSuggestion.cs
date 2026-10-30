// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Something the SQL Object Types editor offers because of a foreign key the database declares on an Object
/// Type's source. Only ever offered: nothing changes until the administrator accepts it.
/// </summary>
/// <param name="Kind">What accepting it does.</param>
/// <param name="ConstraintName">The foreign key it comes from.</param>
/// <param name="ColumnName">The source column the key is on.</param>
/// <param name="ReferencedSchemaName">The schema of the table the key points at.</param>
/// <param name="ReferencedObjectName">The table the key points at.</param>
/// <param name="ReferencedColumnName">The column of that table the key points at.</param>
/// <param name="ReferencedObjectTypeName">For a <see cref="SqlObjectTypeSuggestionKind.Reference"/>, the Object Type whose anchor that column is.</param>
public sealed record SqlObjectTypeSuggestion(
    SqlObjectTypeSuggestionKind Kind,
    string ConstraintName,
    string ColumnName,
    string? ReferencedSchemaName,
    string ReferencedObjectName,
    string ReferencedColumnName,
    string? ReferencedObjectTypeName);

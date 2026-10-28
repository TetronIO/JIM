// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A foreign key a table declares: the columns whose values hold another table's key.
/// </summary>
public class ConnectorCatalogueForeignKey
{
    /// <summary>
    /// The constraint's name.
    /// </summary>
    public string ConstraintName { get; set; } = null!;

    /// <summary>
    /// This table's columns, in key order.
    /// </summary>
    public List<string> Columns { get; set; } = [];

    /// <summary>
    /// The schema of the table the key points at; null where the source has no schemas.
    /// </summary>
    public string? ReferencedSchemaName { get; set; }

    /// <summary>
    /// The table the key points at.
    /// </summary>
    public string ReferencedObjectName { get; set; } = null!;

    /// <summary>
    /// The columns of that table each of <see cref="Columns"/> pairs with, in the same order.
    /// </summary>
    public List<string> ReferencedColumns { get; set; } = [];
}

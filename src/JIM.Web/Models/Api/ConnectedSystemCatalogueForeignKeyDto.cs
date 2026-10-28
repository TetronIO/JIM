// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// A foreign key a table declares: the columns whose values hold another table's key.
/// </summary>
/// <remarks>Issue #2570.</remarks>
public class ConnectedSystemCatalogueForeignKeyDto
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
    /// The schema of the table the key points at. Null where the source has no schemas.
    /// </summary>
    public string? ReferencedSchemaName { get; set; }

    /// <summary>
    /// The table the key points at.
    /// </summary>
    public string ReferencedObjectName { get; set; } = null!;

    /// <summary>
    /// The referenced table's columns, paired in order with Columns.
    /// </summary>
    public List<string> ReferencedColumns { get; set; } = [];

    /// <param name="model">The foreign key.</param>
    public static ConnectedSystemCatalogueForeignKeyDto FromModel(ConnectorCatalogueForeignKey model) => new()
    {
        ConstraintName = model.ConstraintName,
        Columns = [.. model.Columns],
        ReferencedSchemaName = model.ReferencedSchemaName,
        ReferencedObjectName = model.ReferencedObjectName,
        ReferencedColumns = [.. model.ReferencedColumns]
    };
}

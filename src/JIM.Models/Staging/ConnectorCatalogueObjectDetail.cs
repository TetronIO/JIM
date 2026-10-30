// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What a Connector's catalogue says about one table, view or query: its columns, and the keys it declares.
/// </summary>
public class ConnectorCatalogueObjectDetail
{
    /// <summary>
    /// The schema the object is in; null for a query, or where the source has no schemas.
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    /// The object's name; null for a query.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Whether it is a table, a view or a query.
    /// </summary>
    public ConnectorCatalogueObjectKind Kind { get; set; }

    /// <summary>
    /// Every column, in the order the source reports them.
    /// </summary>
    public List<ConnectorCatalogueColumn> Columns { get; set; } = [];

    /// <summary>
    /// The primary key's columns, in key order; empty where none is declared, which is always so for a view or a query.
    /// </summary>
    public List<string> PrimaryKey { get; set; } = [];

    /// <summary>
    /// The foreign keys the object declares, one entry per constraint; empty for a view or a query.
    /// </summary>
    public List<ConnectorCatalogueForeignKey> ForeignKeys { get; set; } = [];
}

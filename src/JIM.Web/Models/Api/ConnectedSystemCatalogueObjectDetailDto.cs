// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// What a Connected System's source says about one table, view or query: its columns and the keys it declares.
/// </summary>
/// <remarks>Issue #2570.</remarks>
public class ConnectedSystemCatalogueObjectDetailDto
{
    /// <summary>
    /// The schema the object is in. Null for a query, or where the source has no schemas.
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    /// The object's name, exactly as the source spells it. Null for a query.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Whether it is a "Table", a "View" or a "Statement" (a query).
    /// </summary>
    public string Kind { get; set; } = null!;

    /// <summary>
    /// Every column, in the order the source reports them.
    /// </summary>
    public List<ConnectedSystemCatalogueColumnDto> Columns { get; set; } = [];

    /// <summary>
    /// The primary key's columns, in key order. Empty where none is declared, which is always so for a view or a query.
    /// </summary>
    public List<string> PrimaryKey { get; set; } = [];

    /// <summary>
    /// The foreign keys the table declares, one entry per constraint. Empty for a view or a query.
    /// </summary>
    public List<ConnectedSystemCatalogueForeignKeyDto> ForeignKeys { get; set; } = [];

    /// <param name="model">The catalogue's description.</param>
    public static ConnectedSystemCatalogueObjectDetailDto FromModel(ConnectorCatalogueObjectDetail model) => new()
    {
        SchemaName = model.SchemaName,
        Name = model.Name,
        Kind = model.Kind.ToString(),
        Columns = [.. model.Columns.Select(ConnectedSystemCatalogueColumnDto.FromModel)],
        PrimaryKey = [.. model.PrimaryKey],
        ForeignKeys = [.. model.ForeignKeys.Select(ConnectedSystemCatalogueForeignKeyDto.FromModel)]
    };
}

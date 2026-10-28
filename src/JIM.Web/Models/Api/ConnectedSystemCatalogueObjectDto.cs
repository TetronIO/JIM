// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web.Models.Api;

/// <summary>
/// A table or view a Connected System's source contains, as its catalogue reports it.
/// </summary>
/// <remarks>Issue #2570.</remarks>
public class ConnectedSystemCatalogueObjectDto
{
    /// <summary>
    /// The schema the object is in, e.g. "HR". Null where the source has no schemas.
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    /// The object's name, exactly as the source spells it, e.g. "EMPLOYEES".
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Whether it is a "Table" or a "View".
    /// </summary>
    public string Kind { get; set; } = null!;

    /// <param name="model">The catalogue entry.</param>
    public static ConnectedSystemCatalogueObjectDto FromModel(ConnectorCatalogueObject model) => new()
    {
        SchemaName = model.SchemaName,
        Name = model.Name,
        Kind = model.Kind.ToString()
    };
}

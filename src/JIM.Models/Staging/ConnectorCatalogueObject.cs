// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A table or view a Connector's source contains, as its catalogue reports it.
/// </summary>
public class ConnectorCatalogueObject
{
    /// <summary>
    /// The schema the object is in; null where the source has no schemas.
    /// </summary>
    public string? SchemaName { get; set; }

    /// <summary>
    /// The object's name, exactly as the source spells it.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Whether it is a table or a view.
    /// </summary>
    public ConnectorCatalogueObjectKind Kind { get; set; }
}

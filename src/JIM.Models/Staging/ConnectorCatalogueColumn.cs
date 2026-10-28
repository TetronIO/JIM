// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// A column of a table, view or query, as the source describes it.
/// </summary>
public class ConnectorCatalogueColumn
{
    /// <summary>
    /// The column's name, exactly as the source spells it.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The source's own name for the column's type, e.g. "nvarchar" or "NUMBER".
    /// </summary>
    public string DataTypeName { get; set; } = null!;

    /// <summary>
    /// The longest value the column holds, in the source's units; null where the type has no length, and -1 where it is unbounded.
    /// </summary>
    public int? MaxLength { get; set; }

    /// <summary>
    /// The number of digits a numeric column holds; null where the source does not say.
    /// </summary>
    public int? Precision { get; set; }

    /// <summary>
    /// The number of those digits after the decimal point; null where the source does not say.
    /// </summary>
    public int? Scale { get; set; }

    /// <summary>
    /// Whether the column may hold no value.
    /// </summary>
    public bool IsNullable { get; set; }
}

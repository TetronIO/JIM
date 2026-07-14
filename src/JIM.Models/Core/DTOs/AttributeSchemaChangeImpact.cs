// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// The evaluated impact of changing a Metaverse Attribute's data type or plurality. Any stored value across the
/// Metaverse hard-blocks the change (a type/plurality change would invalidate existing values). Returned by both the
/// preview and the execute method so REST and PowerShell callers get the same block/allow decision.
/// </summary>
public class AttributeSchemaChangeImpact
{
    public int AttributeId { get; set; }

    public string AttributeName { get; set; } = string.Empty;

    /// <summary>
    /// True if the attribute is built-in. Built-in attributes can never be changed.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// The total number of Metaverse Objects that hold a stored value for the attribute.
    /// </summary>
    public int TotalObjectsWithValues { get; set; }

    /// <summary>
    /// Set true by the execute method when the change was actually applied.
    /// </summary>
    public bool Applied { get; set; }

    /// <summary>
    /// True when stored values exist: the type/plurality change is a hard block.
    /// </summary>
    public bool BlockedByValues => TotalObjectsWithValues > 0;
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Connectors.Sql;

/// <summary>
/// A related table in the Object Types document, as written: a child table of one row per value, read as a
/// multi-valued attribute of its parent Object Type.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SqlRelatedTableDocument
{
    /// <summary>What the multi-valued attribute is called on the parent Object Type.</summary>
    public string? AttributeName { get; set; }

    /// <summary>The schema the child table is in, or null to find it by name alone.</summary>
    public string? Schema { get; set; }

    /// <summary>The child table.</summary>
    public string? Table { get; set; }

    /// <summary>The column holding each value.</summary>
    public string? ValueColumn { get; set; }

    /// <summary>The child table's columns holding the parent's anchor, one per anchor column, in the same order.</summary>
    public List<string?>? JoinColumns { get; set; }

    /// <summary>The Object Type the values reference, when they are references.</summary>
    public string? ReferencesObjectType { get; set; }

    /// <summary>The last-modified column on the child table, for Watermark Column Delta Import.</summary>
    public string? WatermarkColumn { get; set; }
}

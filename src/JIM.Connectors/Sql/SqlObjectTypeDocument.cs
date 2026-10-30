// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Connectors.Sql;

/// <summary>
/// One Object Type in the Object Types document, exactly as written: where its objects come from, what
/// identifies them, and what the catalogue cannot say about its columns. Unvalidated; see
/// <see cref="SqlObjectTypesDocument"/>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SqlObjectTypeDocument
{
    /// <summary>What JIM calls the Object Type.</summary>
    public string? Name { get; set; }

    /// <summary>The schema the table or view is in; null to find the table by name alone, and always null for a <see cref="Select"/>.</summary>
    public string? Schema { get; set; }

    /// <summary>The table or view the objects are read from; exactly one of this and <see cref="Select"/>.</summary>
    public string? Table { get; set; }

    /// <summary>A SELECT statement the objects are read from instead of a table or view.</summary>
    public string? Select { get; set; }

    /// <summary>The column or columns whose values identify a row, in key order.</summary>
    public List<string?>? AnchorColumns { get; set; }

    /// <summary>Columns configured to say what their type cannot: today, the Object Type they reference.</summary>
    public List<SqlColumnDocument>? Columns { get; set; }

    /// <summary>Child tables holding one row per value of a multi-valued attribute.</summary>
    public List<SqlRelatedTableDocument>? RelatedTables { get; set; }

    /// <summary>The last-modified or version column a Watermark Column Delta Import reads.</summary>
    public string? WatermarkColumn { get; set; }

    /// <summary>The change-log table a Change-Log Table Delta Import reads.</summary>
    public SqlChangeLogDocument? ChangeLog { get; set; }
}

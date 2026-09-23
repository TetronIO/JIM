// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Sql;

/// <summary>
/// Where a column a statement compares against lives: a table or view the catalogue describes, or an
/// administrator-supplied SELECT statement, which only the statement itself can describe.
/// </summary>
/// <remarks>
/// A record, so that two references to the same source are the same key: each source's columns are
/// read once per call however many values are compared against them.
/// </remarks>
internal sealed record SqlColumnSource
{
    private SqlColumnSource(string? schemaName, string? objectName, string? selectStatement)
    {
        SchemaName = schemaName;
        ObjectName = objectName;
        SelectStatement = selectStatement;
    }

    internal string? SchemaName { get; }

    /// <summary>
    /// The table or view, or null where <see cref="SelectStatement"/> is the source instead.
    /// </summary>
    internal string? ObjectName { get; }

    /// <summary>
    /// The statement standing in for a table, or null where <see cref="ObjectName"/> is the source.
    /// </summary>
    internal string? SelectStatement { get; }

    internal bool IsStatement => SelectStatement != null;

    internal static SqlColumnSource Table(string? schemaName, string objectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        return new SqlColumnSource(schemaName, objectName, null);
    }

    internal static SqlColumnSource Statement(string selectStatement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectStatement);
        return new SqlColumnSource(null, null, selectStatement);
    }

    /// <summary>
    /// A name for the source that an administrator would recognise, for a log line rather than a
    /// statement.
    /// </summary>
    internal string Describe() =>
        IsStatement ? "the Object Type's 'select' statement" : SqlConnectorExport.QualifiedName(SchemaName, ObjectName!);
}

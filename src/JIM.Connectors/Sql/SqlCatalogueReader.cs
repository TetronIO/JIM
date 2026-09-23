// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace JIM.Connectors.Sql;

/// <summary>
/// Reads what a database's own catalogue says a table's columns are.
/// </summary>
/// <remarks>
/// <para>
/// Shared by schema discovery, which turns the answer into a Connector Schema, and by import and
/// export, which need it to bind a value the way the column it meets expects. All three ask the same
/// question of the same catalogue through the same provider seam, so they ask it in one place: two
/// parsers of the same rows would eventually disagree about what a column is, and the disagreement
/// would surface as an export writing something an import could not read back.
/// </para>
/// <para>
/// The catalogue is the authority on types rather than the Object Types document, because the document
/// is administrator-authored configuration that no database update touches: a column retyped in the
/// table would leave a recorded type stale and silently wrong, while a catalogue read is always current.
/// </para>
/// </remarks>
internal static class SqlCatalogueReader
{
    /// <summary>
    /// The columns of one table or view, in the order the catalogue reports them.
    /// </summary>
    /// <returns>An empty list where the database account cannot see the object at all.</returns>
    internal static async Task<List<SqlDiscoveredColumn>> ReadColumnsAsync(
        ISqlProvider provider,
        DbConnection connection,
        string? schemaName,
        string objectName,
        CancellationToken cancellationToken = default)
    {
        var columns = new List<SqlDiscoveredColumn>();

        using var command = provider.CreateCommand(connection, provider.ColumnsCommandText);

        // Schema and object names are values here, not identifiers, so they are bound rather than
        // interpolated even though the identifiers themselves have already been validated.
        command.Parameters.Add(provider.CreateParameter(SqlCatalogueParameters.SchemaName, schemaName));
        command.Parameters.Add(provider.CreateParameter(SqlCatalogueParameters.ObjectName, objectName));

        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columnNameOrdinal = reader.GetOrdinal(SqlCatalogueColumns.ColumnName);
        var dataTypeNameOrdinal = reader.GetOrdinal(SqlCatalogueColumns.DataTypeName);
        var maxLengthOrdinal = reader.GetOrdinal(SqlCatalogueColumns.MaxLength);
        var precisionOrdinal = reader.GetOrdinal(SqlCatalogueColumns.NumericPrecision);
        var scaleOrdinal = reader.GetOrdinal(SqlCatalogueColumns.NumericScale);
        var isNullableOrdinal = reader.GetOrdinal(SqlCatalogueColumns.IsNullable);

        while (await reader.ReadAsync(cancellationToken))
        {
            var columnType = new SqlColumnType(
                reader.GetString(dataTypeNameOrdinal),
                GetNullableInt(reader, precisionOrdinal),
                GetNullableInt(reader, scaleOrdinal),
                GetNullableInt(reader, maxLengthOrdinal));

            columns.Add(new SqlDiscoveredColumn(
                reader.GetString(columnNameOrdinal),
                columnType,
                string.Equals(GetNullableString(reader, isNullableOrdinal), "YES", StringComparison.OrdinalIgnoreCase)));
        }

        return columns;
    }

    /// <summary>
    /// The columns an administrator-supplied SELECT statement returns, which no catalogue describes.
    /// </summary>
    /// <remarks>
    /// The statement is executed schema-only, so the database plans it and reports its result columns
    /// without reading a single row; on a large table that difference is the whole cost. The column
    /// metadata it hands back carries the same type name, precision and scale a catalogue would, so
    /// nothing downstream can tell a statement's column from a table's.
    /// </remarks>
    /// <exception cref="DbException">The database would not accept the statement.</exception>
    internal static async Task<List<SqlDiscoveredColumn>> ReadStatementColumnsAsync(
        ISqlProvider provider,
        DbConnection connection,
        string selectStatement,
        CancellationToken cancellationToken = default)
    {
        using var command = provider.CreateCommand(connection, selectStatement);
        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, cancellationToken);

        return [.. reader.GetColumnSchema()
            .Select(column => new SqlDiscoveredColumn(
                column.ColumnName,
                new SqlColumnType(column.DataTypeName ?? string.Empty, column.NumericPrecision, column.NumericScale, column.ColumnSize),
                column.AllowDBNull ?? true))];
    }

    internal static string? GetNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// Reads a catalogue's numeric column without assuming its CLR type: SQL Server reports precision
    /// and scale as small integers, Oracle as NUMBER, which a driver may hand back as a decimal.
    /// </summary>
    internal static int? GetNullableInt(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
}

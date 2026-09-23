// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Utilities;
using Serilog;
using System.Data.Common;

namespace JIM.Connectors.Sql;

/// <summary>
/// Binds the values an import's statements compare with columns (a watermark, a page boundary, the
/// anchors a lookup is keyed on), each in the type of the column it meets wherever the dialect's binding
/// depends on that.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the column's type matters at all (#1451).</b> Every such value was read out of the very
/// column it is compared with, and is meant to compare equal to the row it came from. Microsoft SQL
/// Server's legacy datetime breaks that unless the value goes back in as a datetime: see
/// <see cref="SqlServerProvider.CreateParameter"/> for what was measured, which included a Delta Import
/// skipping changes without an error and another that never finished.
/// </para>
/// <para>
/// <b>The catalogue is read lazily, once per source per call.</b> Only a value the dialect says needs
/// it (<see cref="ISqlProvider.NeedsColumnTypeToBind"/>) causes a read, so an import keyed on integers
/// and GUIDs, which is nearly every import, pays nothing. A table or view is described by its
/// catalogue, as export and schema discovery describe it; an administrator-supplied statement by
/// running it schema-only, as schema discovery does.
/// </para>
/// <para>
/// A column the catalogue does not describe is bound as the dialect binds it by default, which is
/// right for every type except the one this exists for. It is not refused: schema discovery already
/// refuses an object type whose source or related tables the catalogue does not show, so this is only
/// reachable for a change-log table the catalogue does not show (one read through a synonym, say), and
/// failing an import that works over it would be worse than the edge it guards.
/// </para>
/// </remarks>
internal sealed class SqlComparisonBinder
{
    private readonly ISqlProvider _provider;
    private readonly DbConnection _connection;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<SqlColumnSource, Dictionary<string, SqlColumnType>> _columnTypes = [];

    internal SqlComparisonBinder(ISqlProvider provider, DbConnection connection, ILogger logger, CancellationToken cancellationToken)
    {
        _provider = provider;
        _connection = connection;
        _logger = logger;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Binds a value a statement compares with <paramref name="columnName"/> of <paramref name="source"/>.
    /// </summary>
    internal async ValueTask<DbParameter> BindAsync(string parameterName, object? value, SqlColumnSource source, string columnName)
    {
        var columnType = _provider.NeedsColumnTypeToBind(value) ? await GetColumnTypeAsync(source, columnName) : null;
        return _provider.CreateParameter(parameterName, value, columnType);
    }

    private async ValueTask<SqlColumnType?> GetColumnTypeAsync(SqlColumnSource source, string columnName)
    {
        if (!_columnTypes.TryGetValue(source, out var columnTypes))
        {
            var columns = source.IsStatement
                ? await SqlCatalogueReader.ReadStatementColumnsAsync(_provider, _connection, source.SelectStatement!, _cancellationToken)
                : await SqlCatalogueReader.ReadColumnsAsync(_provider, _connection, source.SchemaName, source.ObjectName!, _cancellationToken);

            // Added rather than collected into a dictionary directly, so a statement returning two
            // columns of one name (which the page read would refuse anyway) cannot fail it here first.
            columnTypes = new Dictionary<string, SqlColumnType>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
                columnTypes.TryAdd(column.Name, column.ColumnType);

            _columnTypes[source] = columnTypes;
        }

        if (columnTypes.TryGetValue(columnName, out var columnType))
            return columnType;

        _logger.Debug("SqlComparisonBinder: the catalogue does not describe column {Column} of {Source}, so values compared with it are bound by the dialect's default",
            LogSanitiser.Sanitise(columnName), LogSanitiser.Sanitise(source.Describe()));

        return null;
    }
}

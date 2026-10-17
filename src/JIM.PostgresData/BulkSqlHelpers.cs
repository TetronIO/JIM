// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Utilities;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData;

/// <summary>
/// Shared static helpers for raw SQL bulk operations across repositories.
/// </summary>
internal static class BulkSqlHelpers
{
    /// <summary>
    /// Maximum number of parameters per SQL statement. PostgreSQL supports up to 65,535
    /// but we use a conservative limit to avoid edge-case issues with very wide rows.
    /// </summary>
    internal const int MaxParametersPerStatement = 60000;

    /// <summary>
    /// Creates a typed NpgsqlParameter for nullable values. Npgsql's ExecuteSqlRawAsync cannot
    /// infer the PostgreSQL type from DBNull.Value when ALL rows in a chunk have null for a column.
    /// Using an explicit NpgsqlDbType ensures the parameter is correctly typed even when null.
    /// </summary>
    internal static NpgsqlParameter NullableParam(object? value, NpgsqlDbType dbType)
    {
        return new NpgsqlParameter { Value = value ?? DBNull.Value, NpgsqlDbType = dbType };
    }

    /// <summary>
    /// An attribute value's date as it is written (#2153): canonical, UTC and floored to the microsecond, or null for
    /// none. Every raw writer of a <c>DateTimeValue</c> column writes through this or
    /// <see cref="DateTimeAttributeValue.ToCanonical"/>, because Npgsql's own conversion truncates toward 2000-01-01,
    /// which rounds a pre-2000 value with a sub-microsecond part up into the next microsecond.
    /// </summary>
    internal static DateTime? CanonicalDate(DateTime? value) =>
        value.HasValue ? DateTimeAttributeValue.ToCanonical(value.Value) : null;

    /// <summary>
    /// Renders a column list as quoted, comma-separated SQL identifiers, e.g. "Id", "Created".
    /// </summary>
    internal static string ToQuotedList(string[] columns) =>
        string.Join(", ", columns.Select(c => $"\"{c}\""));

    /// <summary>
    /// Orders rows by uuid exactly as PostgreSQL orders them (bytewise, which is the canonical text form's ordinal
    /// order; .NET's <see cref="Guid.CompareTo(Guid)"/> differs). Writers that lock the same rows in one transaction
    /// or statement each must take the locks in this one order, or two of them can deadlock (#2092).
    /// </summary>
    internal static List<T> OrderByDatabaseUuid<T>(IEnumerable<T> source, Func<T, Guid> id) =>
        source.OrderBy(item => id(item).ToString(), StringComparer.Ordinal).ToList();

    /// <summary>
    /// Splits a list into chunks of at most the specified size.
    /// </summary>
    internal static List<List<T>> ChunkList<T>(List<T> source, int chunkSize)
    {
        var chunks = new List<List<T>>();
        for (var i = 0; i < source.Count; i += chunkSize)
        {
            chunks.Add(source.GetRange(i, Math.Min(chunkSize, source.Count - i)));
        }
        return chunks;
    }
}

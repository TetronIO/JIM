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

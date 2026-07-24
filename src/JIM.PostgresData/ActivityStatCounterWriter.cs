// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace JIM.PostgresData;

/// <summary>
/// Persists Activity stat counter deltas (#1078) with a multi-row
/// <c>INSERT ... ON CONFLICT ... DO UPDATE</c> upsert, so concurrent persistence batches for the
/// same Activity accumulate atomically without read-modify-write races.
/// </summary>
internal static class ActivityStatCounterWriter
{
    /// <summary>
    /// Upserts the given counter deltas on the context's connection, participating in the
    /// context's current transaction when one is active. No-op for an empty delta set and for
    /// non-relational providers (the EF in-memory test provider has no connection or upsert;
    /// stats there always derive from aggregation, as before #1078).
    /// </summary>
    public static async Task UpsertDeltasAsync(JimDbContext context, IReadOnlyDictionary<ActivityStatCounterKey, long> deltas)
    {
        if (deltas.Count == 0 || !context.Database.IsRelational())
            return;

        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var transaction = (NpgsqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction();
        await UpsertDeltasAsync(connection, transaction, deltas);
    }

    /// <summary>
    /// Upserts the given counter deltas on an already-open connection. Rows are written in a
    /// deterministic (ActivityId, Dimension, Key) order so concurrent upserts for the same
    /// Activity acquire row locks in the same order and cannot deadlock each other.
    /// </summary>
    public static async Task UpsertDeltasAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyDictionary<ActivityStatCounterKey, long> deltas)
    {
        if (deltas.Count == 0)
            return;

        var orderedDeltas = deltas
            .OrderBy(d => d.Key.ActivityId)
            .ThenBy(d => d.Key.Dimension)
            .ThenBy(d => d.Key.Key, StringComparer.Ordinal)
            .ToList();

        const int columnsPerRow = 4;
        var chunkSize = BulkSqlHelpers.MaxParametersPerStatement / columnsPerRow;
        var sql = new System.Text.StringBuilder();

        foreach (var chunk in BulkSqlHelpers.ChunkList(orderedDeltas, chunkSize))
        {
            sql.Clear();
            sql.Append(@"INSERT INTO ""ActivityStatCounters"" (""ActivityId"", ""Dimension"", ""Key"", ""Count"") VALUES ");

            await using var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append($"(@p{offset}, @p{offset + 1}, @p{offset + 2}, @p{offset + 3})");

                var (key, delta) = chunk[i];
                command.Parameters.Add(new NpgsqlParameter($"p{offset}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = key.ActivityId });
                command.Parameters.Add(new NpgsqlParameter($"p{offset + 1}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)key.Dimension });
                command.Parameters.Add(new NpgsqlParameter($"p{offset + 2}", NpgsqlTypes.NpgsqlDbType.Text) { Value = key.Key });
                command.Parameters.Add(new NpgsqlParameter($"p{offset + 3}", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = delta });
            }

            sql.Append(@" ON CONFLICT (""ActivityId"", ""Dimension"", ""Key"") DO UPDATE SET ""Count"" = ""ActivityStatCounters"".""Count"" + EXCLUDED.""Count""");
            command.CommandText = sql.ToString();
            await command.ExecuteNonQueryAsync();
        }
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Activities;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Writes a Run Profile execution's progress on a connection of its own (#2521): the Activity's message and counters, and
/// the steps that changed, in one transaction and one round trip before the commit.
/// </summary>
/// <remarks>
/// <para>
/// A connection of its own, because a write may come from a timer while the run's own connection is busy with the run's
/// work, and a DbContext must never be used by two callers at once. The connection is taken from the pool for the write
/// and returned straight after.
/// </para>
/// <para>
/// The commit waits for the write-ahead log as every other commit does. A paged import's progress was expensive because it
/// committed several times per page; written at most once a second (<c>ActivityProgressThrottle</c>), one commit's wait is
/// not worth trading durability settings for.
/// </para>
/// </remarks>
internal sealed class ActivityProgressWriter(string connectionString) : IActivityProgressWriter
{
    internal const string UpdateActivitySql =
        """UPDATE "Activities" SET "Message" = $1, "MessageDetailJson" = $2, "ObjectsProcessed" = $3, "ObjectsToProcess" = $4 WHERE "Id" = $5""";

    public async Task WriteAsync(ActivityProgressWrite progress)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var batch = new NpgsqlBatch(connection, transaction))
        {
            var update = new NpgsqlBatchCommand(UpdateActivitySql);
            update.Parameters.Add(new NpgsqlParameter { Value = (object?)progress.Message ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            update.Parameters.Add(new NpgsqlParameter { Value = (object?)progress.MessageDetailJson ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            update.Parameters.Add(new NpgsqlParameter { Value = progress.ObjectsProcessed, NpgsqlDbType = NpgsqlDbType.Integer });
            update.Parameters.Add(new NpgsqlParameter { Value = progress.ObjectsToProcess, NpgsqlDbType = NpgsqlDbType.Integer });
            update.Parameters.Add(new NpgsqlParameter { Value = progress.ActivityId, NpgsqlDbType = NpgsqlDbType.Uuid });
            batch.BatchCommands.Add(update);

            if (progress.Phases.Count > 0)
                batch.BatchCommands.Add(BuildPhaseUpsert(progress.Phases));

            await batch.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// The same upsert <see cref="SyncRepository.SaveActivityPhasesAsync"/> makes: a step the Activity has not seen is
    /// inserted, and one it has takes the state given.
    /// </summary>
    private static NpgsqlBatchCommand BuildPhaseUpsert(IReadOnlyList<ActivityPhase> phases)
    {
        var columns = ActivityPhaseBulkColumns.ActivityPhases;
        var command = new NpgsqlBatchCommand();
        var rows = new List<string>(phases.Count);

        // The values added below MUST match the order of ActivityPhaseBulkColumns.ActivityPhases.
        foreach (var phase in phases)
        {
            var first = command.Parameters.Count + 1;
            command.Parameters.Add(new NpgsqlParameter { Value = phase.Id, NpgsqlDbType = NpgsqlDbType.Uuid });
            command.Parameters.Add(new NpgsqlParameter { Value = phase.ActivityId, NpgsqlDbType = NpgsqlDbType.Uuid });
            command.Parameters.Add(new NpgsqlParameter { Value = phase.Order, NpgsqlDbType = NpgsqlDbType.Integer });
            command.Parameters.Add(new NpgsqlParameter { Value = phase.Key, NpgsqlDbType = NpgsqlDbType.Text });
            command.Parameters.Add(new NpgsqlParameter { Value = phase.Name, NpgsqlDbType = NpgsqlDbType.Text });
            command.Parameters.Add(BulkSqlHelpers.NullableParam(phase.ParentKey, NpgsqlDbType.Text));
            command.Parameters.Add(new NpgsqlParameter { Value = (int)phase.Status, NpgsqlDbType = NpgsqlDbType.Integer });
            command.Parameters.Add(BulkSqlHelpers.NullableParam(phase.Started, NpgsqlDbType.TimestampTz));
            command.Parameters.Add(BulkSqlHelpers.NullableParam(phase.Ended, NpgsqlDbType.TimestampTz));
            rows.Add($"({string.Join(", ", Enumerable.Range(first, columns.Length).Select(i => $"${i}"))})");
        }

        var updateAssignments = string.Join(", ",
            ActivityPhaseBulkColumns.ActivityPhasesUpdate.Select(c => $"\"{c}\" = EXCLUDED.\"{c}\""));

        command.CommandText = $"""
            INSERT INTO "ActivityPhases" ({BulkSqlHelpers.ToQuotedList(columns)})
            VALUES {string.Join(", ", rows)}
            ON CONFLICT ("Id") DO UPDATE SET {updateAssignments}
            """;
        return command;
    }
}

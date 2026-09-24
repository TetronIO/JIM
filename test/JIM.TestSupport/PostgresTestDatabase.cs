// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Npgsql;

namespace JIM.TestSupport;

/// <summary>
/// Shared plumbing for the real-PostgreSQL (<c>RequiresPostgres</c>) test tier.
/// </summary>
public static class PostgresTestDatabase
{
    /// <summary>
    /// The one table a reset leaves alone: EF Core's migration history, which is what tells the next fixture's
    /// migration step that the schema is already at head.
    /// </summary>
    public const string MigrationHistoryTable = "__EFMigrationsHistory";

    // One TRUNCATE naming every table, not one per table. The per-table form every fixture used to carry ran over
    // 400 cascaded truncations on JIM's 83 tables before each test and dominated the database-tests job: the tier
    // took 17.1 minutes locally that way and 3.9 minutes with this statement. Naming every table in one statement
    // also means CASCADE has nothing left to reach, so the whole reset is a single pass. The IF covers a schema with
    // nothing to empty, where string_agg yields NULL and EXECUTE would refuse it.
    private const string ResetSql = $"""
        DO $$
        DECLARE tables text;
        BEGIN
            SELECT string_agg(format('%I', tablename), ', ') INTO tables
            FROM pg_tables
            WHERE schemaname = 'public' AND tablename <> '{MigrationHistoryTable}';

            IF tables IS NOT NULL THEN
                EXECUTE 'TRUNCATE TABLE ' || tables || ' RESTART IDENTITY CASCADE';
            END IF;
        END $$;
        """;

    /// <summary>
    /// Empties every table in the <c>public</c> schema except <see cref="MigrationHistoryTable"/> and restarts
    /// their identity sequences. Call it from a fixture's <c>[SetUp]</c> so each test starts from an empty,
    /// already-migrated schema.
    /// </summary>
    public static async Task ResetAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ResetSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// How long a <c>CREATE DATABASE</c> or <c>DROP DATABASE</c> may take before the command gives up. Npgsql's
    /// default is 30 seconds, which is not enough on a database that keeps its durability settings.
    /// </summary>
    /// <remarks>
    /// Both statements force an immediate checkpoint, flushing everything written since the last one. CI's
    /// throwaway database runs with <c>fsync</c> off, so that is near-instant there. The devcontainer's does not:
    /// straight after the rest of the tier has run, one checkpoint took 56 seconds to write about 2,400 buffers,
    /// and every test in the fixture that issued it failed in its one-time setup without running.
    /// </remarks>
    public const int DatabaseCreateDropTimeoutSeconds = 120;

    /// <summary>
    /// Runs a <c>CREATE DATABASE</c> or <c>DROP DATABASE</c> statement on the given (administrative) connection,
    /// allowing for the checkpoint it forces (see <see cref="DatabaseCreateDropTimeoutSeconds"/>). Neither
    /// statement can be parameterised or run inside a transaction, so the database name must be a constant the
    /// caller controls, never input.
    /// </summary>
    public static async Task ExecuteDatabaseCreateDropAsync(string adminConnectionString, string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = DatabaseCreateDropTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

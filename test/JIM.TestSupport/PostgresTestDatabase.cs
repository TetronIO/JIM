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
}

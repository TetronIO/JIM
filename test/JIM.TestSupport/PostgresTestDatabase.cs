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
    /// The connection string for the <c>RequiresPostgres</c> fixtures, from the <c>JIM_TEST_RESET_*</c> environment
    /// variables; null when <c>JIM_TEST_RESET_DB</c> is not set, which is how those fixtures know to skip.
    /// </summary>
    /// <param name="databaseSuffix">
    /// Names a database of the calling assembly's own, <c>JIM_TEST_RESET_DB</c> followed by this suffix. The
    /// database-tests job runs every test assembly at once against one server, and the Worker suite empties every
    /// table before each of its tests, so any other assembly sharing its database has its rows removed mid-test.
    /// Every assembly but the Worker suite gives one, and creates the database with <see cref="CreateDatabaseIfMissing"/>.
    /// </param>
    public static string? ConnectionStringFromEnvironment(string? databaseSuffix = null)
    {
        var database = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(database))
            return null;
        database += databaseSuffix;

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var password = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        return $"Host={host};Port={port};Database={database};Username={user};Password={password}";
    }

    /// <summary>
    /// Creates the database a connection string names, if the server does not hold it yet, connecting to the
    /// server's <c>postgres</c> database to do so.
    /// </summary>
    public static void CreateDatabaseIfMissing(string connectionString)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var database = target.Database ?? throw new ArgumentException("The connection string names no database.", nameof(connectionString));
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };

        using var connection = new NpgsqlConnection(admin.ConnectionString);
        connection.Open();
        using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection))
        {
            exists.Parameters.AddWithValue("name", database);
            if (exists.ExecuteScalar() != null)
                return;
        }

        // A database name cannot be a parameter; it comes from the test environment, and is quoted as an identifier.
        using var create = new NpgsqlCommand($"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"", connection);
        create.ExecuteNonQuery();
    }

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

    /// <summary>
    /// Runs a statement with the database's referential actions and checks suspended, for its own transaction only: a
    /// row deleted this way leaves every row that names it naming an id with nothing behind it, and a row updated this
    /// way may name an object that does not exist. That is how a test seeds a history record whose object has gone
    /// without the record being rewritten (#2669), which the foreign keys on history tables otherwise prevent.
    /// </summary>
    /// <remarks>
    /// Done with <c>SET LOCAL session_replication_role = replica</c>, which stops the triggers that enforce foreign
    /// keys from firing; it needs a superuser, which the test databases' user is. Scoped to the transaction, so the
    /// pooled connection that runs the next statement enforces every constraint as normal. The statement text must be
    /// the caller's own constant: only values go in <paramref name="parameters"/>.
    /// </remarks>
    public static async Task ExecuteWithReferentialChecksSuspendedAsync(
        string connectionString, string sql, IReadOnlyDictionary<string, object> parameters, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var suspend = new NpgsqlCommand("SET LOCAL session_replication_role = replica", connection, transaction))
            await suspend.ExecuteNonQueryAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}

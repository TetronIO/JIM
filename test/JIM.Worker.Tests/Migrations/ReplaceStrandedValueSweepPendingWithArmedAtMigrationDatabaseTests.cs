// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Proves the <c>ReplaceStrandedValueSweepPendingWithArmedAt</c> migration's data backfill (#1605): a
/// Connected System whose #1549 <c>StrandedValueSweepPending</c> flag was TRUE at upgrade time must come out
/// the other side with a non-null <c>StrandedValueSweepArmedAt</c>, and one whose flag was FALSE must come
/// out with a null armed-at, so no system loses or gains an arming purely from running the migration.
/// <para>
/// Runs against its own scratch database, created and dropped per fixture, migrated in two phases exactly
/// as <see cref="MigrationUpgradePathDatabaseTests"/> does: first to the previous migration (so the boolean
/// flag column exists and can be seeded), then to head (so the migration under test actually runs). Opt-in
/// via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ReplaceStrandedValueSweepPendingWithArmedAtMigrationDatabaseTests
{
    private const string ScratchDatabaseName = "jim_1605_migration_test";
    private const string PreviousMigrationId = "20260901213528_AddStrandedValueSweepPending";

    private string _adminConnectionString = null!;
    private string _scratchConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL migration backfill tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";

        _adminConnectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";
        _scratchConnectionString = $"Host={host};Port={port};Database={ScratchDatabaseName};Username={user};Password={pass}";

        // WITH (FORCE) terminates any connection a previous aborted run left behind.
        await ExecuteAdminSqlAsync($"DROP DATABASE IF EXISTS {ScratchDatabaseName} WITH (FORCE)");
        await ExecuteAdminSqlAsync($"CREATE DATABASE {ScratchDatabaseName}");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_adminConnectionString != null)
            await ExecuteAdminSqlAsync($"DROP DATABASE IF EXISTS {ScratchDatabaseName} WITH (FORCE)");
    }

    [Test]
    public async Task Migration_BackfillsArmedAtOnlyForSystemsWhoseFlagWasTrueAsync()
    {
        // Phase one: bring the scratch database to just before the migration under test, so the old boolean
        // column still exists and can be seeded exactly as an upgrading customer's data would be.
        int armedSystemId;
        int unarmedSystemId;
        var beforeMigration = DateTime.UtcNow;
        await using (var priorContext = NewScratchContext())
        {
            await priorContext.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);

            var definition = new ConnectorDefinition { Name = "1605-migration-def" };
            priorContext.ConnectorDefinitions.Add(definition);
            await priorContext.SaveChangesAsync();

            armedSystemId = await InsertConnectedSystemAsync(priorContext, definition.Id, "1605-armed-system", strandedValueSweepPending: true);
            unarmedSystemId = await InsertConnectedSystemAsync(priorContext, definition.Id, "1605-unarmed-system", strandedValueSweepPending: false);
        }

        // Phase two: a fresh context (a new app process, as an upgrade is) applies the migration under test.
        await using (var headContext = NewScratchContext())
        {
            await headContext.Database.MigrateAsync();
        }

        await using var verifyContext = NewScratchContext();
        var armedSystem = await verifyContext.ConnectedSystems.SingleAsync(cs => cs.Id == armedSystemId);
        var unarmedSystem = await verifyContext.ConnectedSystems.SingleAsync(cs => cs.Id == unarmedSystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(armedSystem.StrandedValueSweepArmedAt, Is.Not.Null,
                "a system whose #1549 flag was TRUE must come out of the migration armed");
            Assert.That(armedSystem.StrandedValueSweepArmedAt!.Value, Is.GreaterThanOrEqualTo(beforeMigration),
                "the backfilled arming must be stamped with (approximately) the migration's own run time");
            Assert.That(armedSystem.LastSuccessfulFullImportCompletedAt, Is.Null,
                "the backfill only stamps the arming; it must not invent a successful Full Import");
            Assert.That(unarmedSystem.StrandedValueSweepArmedAt, Is.Null,
                "a system whose #1549 flag was FALSE must come out of the migration with no arming");
        }
    }

    private static async Task<int> InsertConnectedSystemAsync(JimDbContext context, int connectorDefinitionId, string name, bool strandedValueSweepPending)
    {
        // Raw SQL rather than the EF model: at this point in the phased migration the model in this assembly
        // (which no longer declares StrandedValueSweepPending) does not match the database's actual schema,
        // so an EF-tracked insert cannot write the column under test.
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            @"INSERT INTO ""ConnectedSystems""
                (""Name"", ""ConnectorDefinitionId"", ""StrandedValueSweepPending"", ""Created"", ""CreatedByType"",
                 ""LastUpdatedByType"", ""Status"", ""SettingValuesValid"", ""ObjectMatchingRuleMode"")
              VALUES (@name, @connectorDefinitionId, @pending, @created, 0, 0, 0, false, 0)
              RETURNING ""Id""";
        command.Parameters.Add(new NpgsqlParameter("name", name));
        command.Parameters.Add(new NpgsqlParameter("connectorDefinitionId", connectorDefinitionId));
        command.Parameters.Add(new NpgsqlParameter("pending", strandedValueSweepPending));
        command.Parameters.Add(new NpgsqlParameter("created", DateTime.UtcNow));

        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();

        var result = await command.ExecuteScalarAsync();
        return (int)result!;
    }

    private JimDbContext NewScratchContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_scratchConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    private async Task ExecuteAdminSqlAsync(string sql)
    {
        // CREATE/DROP DATABASE cannot be parameterised or run in a transaction; the name is a constant above,
        // never input.
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

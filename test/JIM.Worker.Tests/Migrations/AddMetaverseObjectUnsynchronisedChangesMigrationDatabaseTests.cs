// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Proves the <c>AddMetaverseObjectUnsynchronisedChanges</c> migration's data step (#2615): a Metaverse Object JIM
/// creates or changes outside synchronisation is flagged for review, but every internal Metaverse Object that already
/// exists (the initial administrator, Example Data objects) was created before the flag was set on them and no
/// synchronisation ever visits one, so on an upgraded database an export rule that predates the object would never
/// provision it. After the upgrade every internal Metaverse Object is flagged for its one review; a projected object
/// is left alone (synchronisation visits it), and so is one that was already flagged.
/// <para>
/// Runs against its own scratch database, migrated in two phases as
/// <see cref="RepairApiKeyInitiatorsRecordedAsUserMigrationDatabaseTests"/> does: first to the previous migration,
/// where the rows are seeded with raw SQL as an upgrading customer's database holds them, then to head. Opt-in via the
/// <c>JIM_TEST_RESET_*</c> environment variables.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class AddMetaverseObjectUnsynchronisedChangesMigrationDatabaseTests
{
    private const string ScratchDatabaseName = "jim_2615_internal_review_test";
    private const string PreviousMigrationId = "20261027223001_AddConnectorDefinitionSettingExample";

    private static readonly int Internal = (int)MetaverseObjectOrigin.Internal;
    private static readonly int Projected = (int)MetaverseObjectOrigin.Projected;

    private readonly Guid _internalUnflagged = Guid.NewGuid();
    private readonly Guid _internalAlreadyFlagged = Guid.NewGuid();
    private readonly Guid _projectedUnflagged = Guid.NewGuid();
    private readonly Guid _projectedAlreadyFlagged = Guid.NewGuid();

    private string _adminConnectionString = null!;
    private string _scratchConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL migration data tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";

        _adminConnectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";
        _scratchConnectionString = $"Host={host};Port={port};Database={ScratchDatabaseName};Username={user};Password={pass}";

        // WITH (FORCE) terminates any connection a previous aborted run left behind.
        await ExecuteAdminSqlAsync($"DROP DATABASE IF EXISTS {ScratchDatabaseName} WITH (FORCE)");
        await ExecuteAdminSqlAsync($"CREATE DATABASE {ScratchDatabaseName}");

        await using (var priorContext = NewScratchContext())
        {
            await priorContext.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);
        }

        await SeedAsync();

        await using (var headContext = NewScratchContext())
        {
            await headContext.Database.MigrateAsync();
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_adminConnectionString != null)
            await ExecuteAdminSqlAsync($"DROP DATABASE IF EXISTS {ScratchDatabaseName} WITH (FORCE)");
    }

    [Test]
    public async Task Migration_InternalMetaverseObjectNotYetFlagged_IsFlaggedForReviewAsync()
    {
        await using var connection = await OpenAsync();

        Assert.That(await IsFlaggedAsync(connection, _internalUnflagged), Is.True,
            "an internal object is never visited by synchronisation, so the upgrade must flag it for its one review");
    }

    [Test]
    public async Task Migration_ProjectedMetaverseObject_IsLeftAloneAsync()
    {
        await using var connection = await OpenAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await IsFlaggedAsync(connection, _projectedUnflagged), Is.False,
                "synchronisation visits a projected object, so the upgrade has nothing to add");
            Assert.That(await IsFlaggedAsync(connection, _projectedAlreadyFlagged), Is.True,
                "a flag already set on a projected object is not the migration's to clear");
        }
    }

    [Test]
    public async Task Migration_InternalMetaverseObjectAlreadyFlagged_IsUnchangedAsync()
    {
        await using var connection = await OpenAsync();

        Assert.That(await IsFlaggedAsync(connection, _internalAlreadyFlagged), Is.True);
    }

    [Test]
    public async Task Migration_FlaggedInternalMetaverseObjects_RecordNoUnsynchronisedChangesAsync()
    {
        await using var connection = await OpenAsync();

        Assert.That(await CountAsync(connection,
                @"SELECT count(*) FROM ""MetaverseObjects"" WHERE ""UnsynchronisedChangesJson"" IS NOT NULL"),
            Is.Zero, "no unsynchronised change is known for an object that existed before the upgrade");
    }

    [Test]
    public async Task Migration_Rows_AreNotAddedOrRemovedAsync()
    {
        await using var connection = await OpenAsync();

        Assert.That(await CountAsync(connection, @"SELECT count(*) FROM ""MetaverseObjects"""), Is.EqualTo(4));
    }

    private async Task SeedAsync()
    {
        await using var connection = await OpenAsync();

        var metaverseObjectTypeId = (int)(await ScalarAsync(connection, """
            INSERT INTO "MetaverseObjectTypes"
                ("Name", "PluralName", "Created", "CreatedByType", "LastUpdatedByType", "BuiltIn", "DeletionRule",
                 "DeletionTriggerConnectedSystemIds")
            VALUES ('2615 Person', '2615 People', now(), 0, 0, false, 0, '{}')
            RETURNING "Id"
            """))!;

        await InsertMetaverseObjectAsync(connection, _internalUnflagged, metaverseObjectTypeId, Internal, scopeReviewPending: false);
        await InsertMetaverseObjectAsync(connection, _internalAlreadyFlagged, metaverseObjectTypeId, Internal, scopeReviewPending: true);
        await InsertMetaverseObjectAsync(connection, _projectedUnflagged, metaverseObjectTypeId, Projected, scopeReviewPending: false);
        await InsertMetaverseObjectAsync(connection, _projectedAlreadyFlagged, metaverseObjectTypeId, Projected, scopeReviewPending: true);
    }

    private static Task InsertMetaverseObjectAsync(NpgsqlConnection connection, Guid id, int typeId, int origin, bool scopeReviewPending) =>
        ExecuteAsync(connection, """
            INSERT INTO "MetaverseObjects" ("Id", "Created", "TypeId", "Status", "DeletionInitiatedByType", "Origin", "ScopeReviewPending")
            VALUES (@id, now(), @typeId, 0, 0, @origin, @scopeReviewPending)
            """, ("id", id), ("typeId", typeId), ("origin", origin), ("scopeReviewPending", scopeReviewPending));

    private static async Task<bool> IsFlaggedAsync(NpgsqlConnection connection, Guid id) =>
        (bool)(await ScalarAsync(connection, @"SELECT ""ScopeReviewPending"" FROM ""MetaverseObjects"" WHERE ""Id"" = @id", ("id", id)))!;

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters) =>
        (long)(await ScalarAsync(connection, sql, parameters))!;

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = BuildCommand(connection, sql, parameters);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = BuildCommand(connection, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlCommand BuildCommand(NpgsqlConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_scratchConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private JimDbContext NewScratchContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_scratchConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    // CREATE/DROP DATABASE cannot be parameterised or run in a transaction; the name is a constant above, never input.
    private Task ExecuteAdminSqlAsync(string sql) => PostgresTestDatabase.ExecuteDatabaseCreateDropAsync(_adminConnectionString, sql);
}

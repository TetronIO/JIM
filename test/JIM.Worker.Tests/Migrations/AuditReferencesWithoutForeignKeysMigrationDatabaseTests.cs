// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Proves the <c>AuditReferencesWithoutForeignKeys</c> migration's data step (#2669). Before it, deleting a Metaverse
/// Object re-keyed every change record of the object (<c>DeletedMetaverseObjectId</c> set, <c>MetaverseObjectId</c>
/// nulled), and a Connected System Object's deletion record was written with its subject id null and the id in
/// <c>DeletedConnectedSystemObjectId</c>. Once history keeps the subject id, the migration restores it from those columns
/// on every row whose subject id is null, so an object's records are found by one id however old they are. A record
/// that held neither id lost it for good, and is left as it is.
/// <para>
/// Runs against its own scratch database, migrated in two phases as
/// <see cref="AddMetaverseObjectUnsynchronisedChangesMigrationDatabaseTests"/> does: first to the previous migration,
/// where the rows are seeded with raw SQL as an upgrading customer's database holds them, then to head. Opt-in via the
/// <c>JIM_TEST_RESET_*</c> environment variables.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class AuditReferencesWithoutForeignKeysMigrationDatabaseTests
{
    private const string ScratchDatabaseName = "jim_2669_audit_references_test";
    private const string PreviousMigrationId = "20261105173358_AddScopingCriterionConnectedObjectType";

    private readonly Guid _liveMvo = Guid.NewGuid();
    private readonly Guid _deletedMvo = Guid.NewGuid();
    private readonly Guid _liveMvoChange = Guid.NewGuid();
    private readonly Guid _rekeyedMvoChange = Guid.NewGuid();
    private readonly Guid _mvoDeletionRecord = Guid.NewGuid();
    private readonly Guid _deletedCso = Guid.NewGuid();
    private readonly Guid _csoDeletionRecord = Guid.NewGuid();
    private readonly Guid _csoChangeWithNoId = Guid.NewGuid();

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
    public async Task Migration_MetaverseObjectChangeRekeyedOnDeletion_GetsItsSubjectIdBackAsync()
    {
        await using var connection = await OpenAsync();

        Assert.That(await MvoChangeSubjectAsync(connection, _rekeyedMvoChange), Is.EqualTo(_deletedMvo),
            "an earlier change of a deleted object is found by the object's id again");
    }

    [Test]
    public async Task Migration_MetaverseObjectDeletionRecord_GetsItsSubjectIdAsync()
    {
        await using var connection = await OpenAsync();

        Assert.That(await MvoChangeSubjectAsync(connection, _mvoDeletionRecord), Is.EqualTo(_deletedMvo));
    }

    [Test]
    public async Task Migration_LiveMetaverseObjectChange_IsUnchangedAsync()
    {
        await using var connection = await OpenAsync();

        Assert.That(await MvoChangeSubjectAsync(connection, _liveMvoChange), Is.EqualTo(_liveMvo));
    }

    [Test]
    public async Task Migration_ConnectedSystemObjectDeletionRecord_GetsItsSubjectIdAsync()
    {
        await using var connection = await OpenAsync();

        Assert.That(await CsoChangeSubjectAsync(connection, _csoDeletionRecord), Is.EqualTo(_deletedCso));
    }

    [Test]
    public async Task Migration_ConnectedSystemObjectChangeThatHeldNoId_StaysWithoutOneAsync()
    {
        await using var connection = await OpenAsync();

        Assert.That(await CsoChangeSubjectAsync(connection, _csoChangeWithNoId), Is.Null,
            "an earlier change whose reference a deletion cleared kept no id to restore it from");
    }

    [Test]
    public async Task Migration_DeletedObjectIdColumns_AreKeptAsync()
    {
        await using var connection = await OpenAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ScalarAsync(connection, @"SELECT ""DeletedMetaverseObjectId"" FROM ""MetaverseObjectChanges"" WHERE ""Id"" = @id",
                ("id", _rekeyedMvoChange)), Is.EqualTo(_deletedMvo));
            Assert.That(await ScalarAsync(connection, @"SELECT ""DeletedConnectedSystemObjectId"" FROM ""ConnectedSystemObjectChanges"" WHERE ""Id"" = @id",
                ("id", _csoDeletionRecord)), Is.EqualTo(_deletedCso));
        }
    }

    private async Task SeedAsync()
    {
        await using var connection = await OpenAsync();

        var metaverseObjectTypeId = (int)(await ScalarAsync(connection, """
            INSERT INTO "MetaverseObjectTypes"
                ("Name", "PluralName", "Created", "CreatedByType", "LastUpdatedByType", "BuiltIn", "DeletionRule",
                 "DeletionTriggerConnectedSystemIds")
            VALUES ('2669 Person', '2669 People', now(), 0, 0, false, 0, '{}')
            RETURNING "Id"
            """))!;
        await ExecuteAsync(connection, """
            INSERT INTO "MetaverseObjects" ("Id", "Created", "TypeId", "Status", "DeletionInitiatedByType", "Origin")
            VALUES (@id, now(), @typeId, 0, 0, @origin)
            """, ("id", _liveMvo), ("typeId", metaverseObjectTypeId), ("origin", (int)MetaverseObjectOrigin.Projected));

        // As a release before #2669 left them: a live object's change, and a deleted object's earlier change re-keyed and
        // deletion record written with the subject id null.
        await InsertMvoChangeAsync(connection, _liveMvoChange, _liveMvo, null, ObjectChangeType.Updated);
        await InsertMvoChangeAsync(connection, _rekeyedMvoChange, null, _deletedMvo, ObjectChangeType.Updated);
        await InsertMvoChangeAsync(connection, _mvoDeletionRecord, null, _deletedMvo, ObjectChangeType.Deleted);

        await InsertCsoChangeAsync(connection, _csoDeletionRecord, _deletedCso, ObjectChangeType.Deleted);
        await InsertCsoChangeAsync(connection, _csoChangeWithNoId, null, ObjectChangeType.Updated);
    }

    private static Task InsertMvoChangeAsync(NpgsqlConnection connection, Guid id, Guid? subjectId, Guid? deletedId, ObjectChangeType changeType) =>
        ExecuteAsync(connection, """
            INSERT INTO "MetaverseObjectChanges" ("Id", "MetaverseObjectId", "DeletedMetaverseObjectId", "ChangeTime", "InitiatedByType", "ChangeInitiatorType", "ChangeType")
            VALUES (@id, @subject, @deleted, now(), 0, 0, @changeType)
            """, ("id", id), ("subject", (object?)subjectId ?? DBNull.Value), ("deleted", (object?)deletedId ?? DBNull.Value), ("changeType", (int)changeType));

    private static Task InsertCsoChangeAsync(NpgsqlConnection connection, Guid id, Guid? deletedId, ObjectChangeType changeType) =>
        ExecuteAsync(connection, """
            INSERT INTO "ConnectedSystemObjectChanges" ("Id", "ConnectedSystemId", "DeletedConnectedSystemObjectId", "ChangeTime", "ChangeType", "InitiatedByType")
            VALUES (@id, 1, @deleted, now(), @changeType, 0)
            """, ("id", id), ("deleted", (object?)deletedId ?? DBNull.Value), ("changeType", (int)changeType));

    private static async Task<Guid?> MvoChangeSubjectAsync(NpgsqlConnection connection, Guid id) =>
        await ScalarAsync(connection, @"SELECT ""MetaverseObjectId"" FROM ""MetaverseObjectChanges"" WHERE ""Id"" = @id", ("id", id)) as Guid?;

    private static async Task<Guid?> CsoChangeSubjectAsync(NpgsqlConnection connection, Guid id) =>
        await ScalarAsync(connection, @"SELECT ""ConnectedSystemObjectId"" FROM ""ConnectedSystemObjectChanges"" WHERE ""Id"" = @id", ("id", id)) as Guid?;

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

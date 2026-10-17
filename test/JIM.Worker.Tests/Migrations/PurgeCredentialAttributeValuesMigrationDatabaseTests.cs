// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Proves the <c>PurgeCredentialAttributeValues</c> migration's clean-up (#2091): before the upgrade an Attribute Flow
/// could stage a cleartext <c>password</c> into a Pending Export and the export could record it on the Connected
/// System Object. After the upgrade JIM holds no value for any attribute on the credential denylist, every other
/// attribute's value is untouched, and a Pending Export is removed only when the credential was all it carried.
/// <para>
/// Runs against its own scratch database, migrated in two phases as
/// <see cref="RepairApiKeyInitiatorsRecordedAsUserMigrationDatabaseTests"/> does: first to the previous migration,
/// where the rows are seeded with raw SQL as an upgrading customer's database holds them, then to head. Opt-in via the
/// <c>JIM_TEST_RESET_*</c> environment variables.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PurgeCredentialAttributeValuesMigrationDatabaseTests
{
    private const string ScratchDatabaseName = "jim_2091_credential_purge_test";
    private const string PreviousMigrationId = "20261016210457_AddConnectedSystemAttributeDateTimeResolution";

    private const int PendingExportCreate = 0;
    private const int PendingExportUpdate = 1;

    private const int TitleAttributeId = 1;
    private const int PasswordAttributeId = 2;
    private const int UnicodePwdAttributeId = 3;

    private readonly Guid _csoId = Guid.NewGuid();
    private readonly Guid _updateWithOnlyPassword = Guid.NewGuid();
    private readonly Guid _updateWithPasswordAndTitle = Guid.NewGuid();
    private readonly Guid _createWithPasswordAndTitle = Guid.NewGuid();
    private readonly Guid _createWithOnlyPassword = Guid.NewGuid();
    private readonly Guid _updateWithOnlyTitle = Guid.NewGuid();
    private readonly Guid _updateWithNoChanges = Guid.NewGuid();

    private string _adminConnectionString = null!;
    private string _scratchConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL migration clean-up tests.");

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
    public async Task Migration_DeniedAttributeValues_AreGoneFromPendingExportsAndConnectedSystemObjectsAsync()
    {
        await using var connection = await OpenAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync(connection, @"SELECT count(*) FROM ""PendingExportAttributeValueChanges"" WHERE ""AttributeId"" IN (2, 3)"),
                Is.Zero, "no Pending Export may still carry a credential");
            Assert.That(await CountAsync(connection, @"SELECT count(*) FROM ""ConnectedSystemObjectAttributeValues"" WHERE ""AttributeId"" IN (2, 3)"),
                Is.Zero, "no Connected System Object may still hold a credential");
            Assert.That(await CountAsync(connection, @"SELECT count(*) FROM ""PendingExportAttributeValueChanges"" WHERE ""StringValue"" = 'S3cret!'"),
                Is.Zero);
        }
    }

    [Test]
    public async Task Migration_OtherAttributeValues_AreUntouchedAsync()
    {
        await using var connection = await OpenAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync(connection,
                    @"SELECT count(*) FROM ""PendingExportAttributeValueChanges"" WHERE ""AttributeId"" = 1 AND ""StringValue"" = 'Engineer'"),
                Is.EqualTo(3), "the title changes on the three exports that carried one are kept");
            Assert.That(await CountAsync(connection,
                    @"SELECT count(*) FROM ""ConnectedSystemObjectAttributeValues"" WHERE ""AttributeId"" = 1 AND ""StringValue"" = 'Engineer'"),
                Is.EqualTo(1));
            Assert.That(await CountAsync(connection, @"SELECT count(*) FROM ""ConnectedSystemAttributes"""), Is.EqualTo(3),
                "attribute definitions stay; schema refresh and Synchronisation Rule validation deal with them");
        }
    }

    [Test]
    public async Task Migration_UpdateThatCarriedOnlyACredential_IsRemovedAsync()
    {
        await using var connection = await OpenAsync();

        Assert.That(await PendingExportExistsAsync(connection, _updateWithOnlyPassword), Is.False,
            "an update left with nothing to export is removed rather than exported empty");
    }

    [Test]
    public async Task Migration_EveryOtherPendingExport_IsKeptAsync()
    {
        await using var connection = await OpenAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await PendingExportExistsAsync(connection, _updateWithPasswordAndTitle), Is.True,
                "an update with another change still has work to do");
            Assert.That(await PendingExportExistsAsync(connection, _createWithPasswordAndTitle), Is.True);
            Assert.That(await PendingExportExistsAsync(connection, _createWithOnlyPassword), Is.True,
                "a create still creates the object without its credential");
            Assert.That(await PendingExportExistsAsync(connection, _updateWithOnlyTitle), Is.True);
            Assert.That(await PendingExportExistsAsync(connection, _updateWithNoChanges), Is.True,
                "an update that never carried a credential is not the migration's to remove");
        }
    }

    private async Task SeedAsync()
    {
        await using var connection = await OpenAsync();

        await ExecuteAsync(connection, """
            INSERT INTO "ConnectorDefinitions" ("Id", "Name", "Created", "CreatedByType", "LastUpdatedByType", "BuiltIn",
                "SupportsFullImport", "SupportsDeltaImport", "SupportsExport", "SupportsPartitions",
                "SupportsPartitionContainers", "SupportsSecondaryExternalId", "SupportsUserSelectedExternalId",
                "SupportsUserSelectedAttributeTypes", "SupportsAutoConfirmExport", "SupportsParallelExport")
            VALUES (1, 'Test Connector', now(), 0, 0, false, true, false, true, false, false, false, false, false, false, false);
            INSERT INTO "ConnectedSystems" ("Id", "Name", "Created", "CreatedByType", "LastUpdatedByType", "Status",
                "ConnectorDefinitionId", "SettingValuesValid", "ObjectMatchingRuleMode")
            VALUES (1, 'Service Provider', now(), 0, 0, 0, 1, true, 0);
            INSERT INTO "ConnectedSystemObjectTypes" ("Id", "Name", "Created", "ConnectedSystemId", "Selected",
                "RemoveContributedAttributesOnObsoletion")
            VALUES (1, 'User', now(), 1, true, false);
            INSERT INTO "ConnectedSystemAttributes" ("Id", "Created", "Name", "Type", "AttributePlurality",
                "ConnectedSystemObjectTypeId", "Selected", "IsExternalId", "IsSecondaryExternalId", "SelectionLocked",
                "Writability")
            VALUES (1, now(), 'title', 0, 0, 1, true, false, false, false, 0),
                   (2, now(), 'Password', 0, 0, 1, true, false, false, false, 0),
                   (3, now(), 'unicodePwd', 0, 0, 1, true, false, false, false, 0);
            """);

        await ExecuteAsync(connection, """
            INSERT INTO "ConnectedSystemObjects" ("Id", "Created", "TypeId", "ConnectedSystemId", "ExternalIdAttributeId",
                "Status", "JoinType")
            VALUES (@cso, now(), 1, 1, 1, 0, 0);
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "AttributeId", "ConnectedSystemObjectId", "StringValue")
            VALUES (gen_random_uuid(), 1, @cso, 'Engineer'),
                   (gen_random_uuid(), 2, @cso, 'S3cret!'),
                   (gen_random_uuid(), 3, @cso, 'S3cret!');
            """, ("cso", _csoId));

        await InsertPendingExportAsync(connection, _updateWithOnlyPassword, PendingExportUpdate, PasswordAttributeId);
        await InsertPendingExportAsync(connection, _updateWithPasswordAndTitle, PendingExportUpdate, PasswordAttributeId, TitleAttributeId);
        await InsertPendingExportAsync(connection, _createWithPasswordAndTitle, PendingExportCreate, UnicodePwdAttributeId, TitleAttributeId);
        await InsertPendingExportAsync(connection, _createWithOnlyPassword, PendingExportCreate, PasswordAttributeId);
        await InsertPendingExportAsync(connection, _updateWithOnlyTitle, PendingExportUpdate, TitleAttributeId);
        await InsertPendingExportAsync(connection, _updateWithNoChanges, PendingExportUpdate);
    }

    private static async Task InsertPendingExportAsync(NpgsqlConnection connection, Guid id, int changeType, params int[] attributeIds)
    {
        await ExecuteAsync(connection, """
            INSERT INTO "PendingExports" ("Id", "ConnectedSystemId", "ChangeType", "Status", "ErrorCount", "MaxRetries",
                "HasUnresolvedReferences", "CreatedAt")
            VALUES (@id, 1, @changeType, 0, 0, 5, false, now());
            """, ("id", id), ("changeType", changeType));

        foreach (var attributeId in attributeIds)
        {
            await ExecuteAsync(connection, """
                INSERT INTO "PendingExportAttributeValueChanges" ("Id", "PendingExportId", "AttributeId", "ChangeType",
                    "Status", "ExportAttemptCount", "StringValue")
                VALUES (gen_random_uuid(), @id, @attributeId, 0, 0, 0, @value);
                """, ("id", id), ("attributeId", attributeId), ("value", attributeId == TitleAttributeId ? "Engineer" : "S3cret!"));
        }
    }

    private static async Task<bool> PendingExportExistsAsync(NpgsqlConnection connection, Guid id) =>
        await CountAsync(connection, @"SELECT count(*) FROM ""PendingExports"" WHERE ""Id"" = @id", ("id", id)) == 1;

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = BuildCommand(connection, sql, parameters);
        return (long)(await command.ExecuteScalarAsync())!;
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

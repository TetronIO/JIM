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
/// Proves the <c>ChangeHistoryAttributePlurality</c> migration's data step (#2936): a change record's attribute rows
/// copy their attribute's plurality from now on, and existing rows take it from the attribute while it is still there.
/// A row whose attribute has already been deleted, or whose id an earlier release cleared, stays single-valued, which is
/// how it reads today.
/// <para>
/// Runs against its own scratch database, migrated in two phases as
/// <see cref="AddMetaverseObjectUnsynchronisedChangesMigrationDatabaseTests"/> does: first to the previous migration,
/// where the rows are seeded with raw SQL as an upgrading customer's database holds them, then to head. Opt-in via the
/// <c>JIM_TEST_RESET_*</c> environment variables.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ChangeHistoryAttributePluralityMigrationDatabaseTests
{
    private const string ScratchDatabaseName = "jim_2936_attribute_plurality_test";
    private const string PreviousMigrationId = "20261116024512_AddConnectedSystemImportIncompleteSince";

    private const int SingleValuedAttributeId = 1;
    private const int MultiValuedAttributeId = 2;
    private const int DeletedAttributeId = 999;

    private static readonly int SingleValued = (int)AttributePlurality.SingleValued;
    private static readonly int MultiValued = (int)AttributePlurality.MultiValued;

    private static readonly string[] Tables = ["ConnectedSystemObjectChangeAttributes", "MetaverseObjectChangeAttributes"];

    // One row per case, on each side.
    private readonly Dictionary<string, (Guid SingleValued, Guid MultiValued, Guid Deleted, Guid Cleared)> _rows = new();

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

    [TestCaseSource(nameof(Tables))]
    public async Task Migration_RowOfALiveMultiValuedAttribute_TakesItsPluralityAsync(string table)
    {
        await using var connection = await OpenAsync();

        Assert.That(await PluralityAsync(connection, table, _rows[table].MultiValued), Is.EqualTo(MultiValued));
    }

    [TestCaseSource(nameof(Tables))]
    public async Task Migration_RowOfALiveSingleValuedAttribute_StaysSingleValuedAsync(string table)
    {
        await using var connection = await OpenAsync();

        Assert.That(await PluralityAsync(connection, table, _rows[table].SingleValued), Is.EqualTo(SingleValued));
    }

    [TestCaseSource(nameof(Tables))]
    public async Task Migration_RowWhoseAttributeHasGone_StaysSingleValuedAsReadTodayAsync(string table)
    {
        await using var connection = await OpenAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await PluralityAsync(connection, table, _rows[table].Deleted), Is.EqualTo(SingleValued),
                "an attribute deleted since leaves nothing to take its plurality from");
            Assert.That(await PluralityAsync(connection, table, _rows[table].Cleared), Is.EqualTo(SingleValued),
                "an id an earlier release cleared names no attribute at all");
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
            VALUES (1, 'Panoply Directory', now(), 0, 0, 0, 1, true, 0);
            INSERT INTO "ConnectedSystemObjectTypes" ("Id", "Name", "Created", "ConnectedSystemId", "Selected",
                "RemoveContributedAttributesOnObsoletion")
            VALUES (1, 'user', now(), 1, true, false);
            INSERT INTO "ConnectedSystemAttributes" ("Id", "Created", "Name", "Type", "AttributePlurality",
                "ConnectedSystemObjectTypeId", "Selected", "IsExternalId", "IsSecondaryExternalId", "SelectionLocked",
                "Writability")
            VALUES (@single, now(), 'title', 0, @singleValued, 1, true, false, false, false, 0),
                   (@multi, now(), 'proxyAddresses', 0, @multiValued, 1, true, false, false, false, 0);
            INSERT INTO "MetaverseAttributes" ("Id", "Name", "Type", "AttributePlurality", "BuiltIn", "Created", "CreatedByType",
                "LastUpdatedByType", "RenderingHint")
            VALUES (@single, 'jobTitle', 0, @singleValued, false, now(), 0, 0, 0),
                   (@multi, 'proxyAddresses', 0, @multiValued, false, now(), 0, 0, 0);
            """, ("single", SingleValuedAttributeId), ("multi", MultiValuedAttributeId),
            ("singleValued", SingleValued), ("multiValued", MultiValued));

        var csoChangeId = Guid.NewGuid();
        await ExecuteAsync(connection, """
            INSERT INTO "ConnectedSystemObjectChanges" ("Id", "ConnectedSystemId", "ChangeTime", "ChangeType", "InitiatedByType")
            VALUES (@id, 1, now(), 0, 0)
            """, ("id", csoChangeId));
        _rows["ConnectedSystemObjectChangeAttributes"] = await InsertAttributeRowsAsync(
            connection, "ConnectedSystemObjectChangeAttributes", "ConnectedSystemChangeId", csoChangeId);

        var mvoChangeId = Guid.NewGuid();
        await ExecuteAsync(connection, """
            INSERT INTO "MetaverseObjectChanges" ("Id", "ChangeTime", "InitiatedByType", "ChangeInitiatorType", "ChangeType")
            VALUES (@id, now(), 0, 0, 0)
            """, ("id", mvoChangeId));
        _rows["MetaverseObjectChangeAttributes"] = await InsertAttributeRowsAsync(
            connection, "MetaverseObjectChangeAttributes", "MetaverseObjectChangeId", mvoChangeId);
    }

    private static async Task<(Guid SingleValued, Guid MultiValued, Guid Deleted, Guid Cleared)> InsertAttributeRowsAsync(
        NpgsqlConnection connection, string table, string changeColumn, Guid changeId)
    {
        var rows = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        // The table and column names are the constants above, never input.
        await ExecuteAsync(connection, $"""
            INSERT INTO "{table}" ("Id", "{changeColumn}", "AttributeId", "AttributeName", "AttributeType")
            VALUES (@singleRow, @change, @single, 'single', 0),
                   (@multiRow, @change, @multi, 'multi', 0),
                   (@deletedRow, @change, @deleted, 'deleted', 0),
                   (@clearedRow, @change, NULL, 'cleared', 0)
            """,
            ("singleRow", rows.Item1), ("multiRow", rows.Item2), ("deletedRow", rows.Item3), ("clearedRow", rows.Item4),
            ("change", changeId), ("single", SingleValuedAttributeId), ("multi", MultiValuedAttributeId),
            ("deleted", DeletedAttributeId));
        return rows;
    }

    private static async Task<int> PluralityAsync(NpgsqlConnection connection, string table, Guid id) =>
        (int)(await ScalarAsync(connection, $"""SELECT "AttributePlurality" FROM "{table}" WHERE "Id" = @id""", ("id", id)))!;

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

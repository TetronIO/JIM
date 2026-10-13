// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using JIM.TestSupport;
using Npgsql;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Proves the <c>DeclareCaseInsensitiveAttributes</c> migration's data step (#2195): an upgraded deployment's LDAP
/// Connected Systems get their distinguished names declared case-insensitive without waiting for a schema refresh,
/// and nothing else is touched. Migrated in two phases against a scratch database, as
/// <see cref="AddScheduleFailureHandlingMigrationDatabaseTests"/> is.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class DeclareCaseInsensitiveAttributesMigrationDatabaseTests
{
    private const string ScratchDatabaseName = "jim_2195_migration_test";
    private const string PreviousMigrationId = "20261013085847_UnifyCriterionOperators";

    private const int LdapObjectTypeId = 1;
    private const int SqlObjectTypeId = 2;

    private string _adminConnectionString = null!;
    private string _scratchConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL migration data-mapping tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";

        _adminConnectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";
        _scratchConnectionString = $"Host={host};Port={port};Database={ScratchDatabaseName};Username={user};Password={pass}";

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
    public async Task Migration_DeclaresLdapDistinguishedNamesCaseInsensitiveAndNothingElseAsync()
    {
        await using (var priorContext = NewScratchContext())
        {
            await priorContext.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);

            await InsertConnectedSystemAsync(priorContext, 1, "JIM LDAP Connector", LdapObjectTypeId);
            await InsertConnectedSystemAsync(priorContext, 2, "JIM SQL Connector", SqlObjectTypeId);

            await InsertAttributeAsync(priorContext, 10, LdapObjectTypeId, "distinguishedName", type: 1);
            await InsertAttributeAsync(priorContext, 11, LdapObjectTypeId, "manager", type: 5);
            await InsertAttributeAsync(priorContext, 12, LdapObjectTypeId, "displayName", type: 1);
            await InsertAttributeAsync(priorContext, 20, SqlObjectTypeId, "distinguishedName", type: 1);
            await InsertAttributeAsync(priorContext, 21, SqlObjectTypeId, "managerId", type: 5);
        }

        await using (var headContext = NewScratchContext())
        {
            await headContext.Database.MigrateAsync();
        }

        await using var verifyContext = NewScratchContext();
        var caseInsensitiveById = await verifyContext.ConnectedSystemAttributes.ToDictionaryAsync(a => a.Id, a => a.CaseInsensitive);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caseInsensitiveById[10], Is.True, "an LDAP distinguishedName");
            Assert.That(caseInsensitiveById[11], Is.True, "an LDAP attribute of DN syntax");
            Assert.That(caseInsensitiveById[12], Is.False, "LDAP text that is not a DN keeps its case");
            Assert.That(caseInsensitiveById[20], Is.False, "another Connector's attribute, whatever it is called");
            Assert.That(caseInsensitiveById[21], Is.False, "another Connector's reference");
        }
    }

    private static async Task InsertConnectedSystemAsync(JimDbContext context, int id, string connectorName, int objectTypeId)
    {
        await ExecuteAsync(context,
            @"INSERT INTO ""ConnectorDefinitions""
                (""Id"", ""Name"", ""Created"", ""CreatedByType"", ""LastUpdatedByType"", ""BuiltIn"", ""SupportsFullImport"",
                 ""SupportsDeltaImport"", ""SupportsExport"", ""SupportsPartitions"", ""SupportsPartitionContainers"",
                 ""SupportsSecondaryExternalId"", ""SupportsUserSelectedExternalId"", ""SupportsUserSelectedAttributeTypes"",
                 ""SupportsAutoConfirmExport"", ""SupportsParallelExport"")
              VALUES (@id, @name, @created, 0, 0, true, true, false, true, false, false, false, false, false, false, false)",
            ("id", id), ("name", connectorName));

        await ExecuteAsync(context,
            @"INSERT INTO ""ConnectedSystems""
                (""Id"", ""Name"", ""Created"", ""CreatedByType"", ""LastUpdatedByType"", ""Status"", ""ConnectorDefinitionId"",
                 ""SettingValuesValid"", ""ObjectMatchingRuleMode"")
              VALUES (@id, @name, @created, 0, 0, 0, @id, true, 0)",
            ("id", id), ("name", $"{connectorName} system"));

        await ExecuteAsync(context,
            @"INSERT INTO ""ConnectedSystemObjectTypes""
                (""Id"", ""Name"", ""Created"", ""ConnectedSystemId"", ""Selected"", ""RemoveContributedAttributesOnObsoletion"")
              VALUES (@objectTypeId, 'user', @created, @id, true, false)",
            ("id", id), ("objectTypeId", objectTypeId));
    }

    private static Task InsertAttributeAsync(JimDbContext context, int id, int objectTypeId, string name, int type) =>
        // Raw SQL: at this point the model in this assembly declares a column the database does not have yet.
        ExecuteAsync(context,
            @"INSERT INTO ""ConnectedSystemAttributes""
                (""Id"", ""Created"", ""Name"", ""Type"", ""AttributePlurality"", ""ConnectedSystemObjectTypeId"", ""Selected"",
                 ""IsExternalId"", ""IsSecondaryExternalId"", ""SelectionLocked"", ""Writability"")
              VALUES (@id, @created, @name, @type, 0, @objectTypeId, true, false, false, false, 0)",
            ("id", id), ("name", name), ("type", type), ("objectTypeId", objectTypeId));

    private static async Task ExecuteAsync(JimDbContext context, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.Add(new NpgsqlParameter(name, value));
        command.Parameters.Add(new NpgsqlParameter("created", DateTime.UtcNow));

        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();

        await command.ExecuteNonQueryAsync();
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

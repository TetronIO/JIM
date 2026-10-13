// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Search;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Proves the <c>UnifyCriterionOperators</c> migration's data rewrite (#2053): Example Data dependencies move from the
/// retired <c>ComparisonType</c> ordinals to the operator catalogue's, and scoping criteria that used "Equals with no
/// value" as "has no value" become exactly that, without changing which objects any criterion selects.
/// <para>
/// Migrated in two phases against a scratch database, as <see cref="RepairApiKeyInitiatorsRecordedAsUserMigrationDatabaseTests"/>
/// is: to the previous migration, where rows are seeded with raw SQL against that schema, then to head. Opt-in via the
/// <c>JIM_TEST_RESET_*</c> environment variables.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class UnifyCriterionOperatorsMigrationDatabaseTests
{
    private const string ScratchDatabaseName = "jim_2053_criterion_operators_test";
    private const string PreviousMigrationId = "20261012162234_AddJoinMatchSourceToSyncOutcomes";

    // The retired ComparisonType ordinals, as an upgrading database holds them.
    private const int OldEquals = 0, OldNotEquals = 1, OldLessThan = 2, OldGreaterThan = 3, OldGreaterThanOrEqual = 4, OldLessThanOrEqual = 5, OldLike = 6;

    private string _adminConnectionString = null!;
    private string _scratchConnectionString = null!;
    private readonly Dictionary<string, int> _dependencyIds = new();
    private readonly Dictionary<string, int> _criterionIds = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL migration tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _adminConnectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";
        _scratchConnectionString = $"Host={host};Port={port};Database={ScratchDatabaseName};Username={user};Password={pass}";

        await PostgresTestDatabase.ExecuteDatabaseCreateDropAsync(_adminConnectionString, $"DROP DATABASE IF EXISTS {ScratchDatabaseName} WITH (FORCE)");
        await PostgresTestDatabase.ExecuteDatabaseCreateDropAsync(_adminConnectionString, $"CREATE DATABASE {ScratchDatabaseName}");

        await using (var priorContext = NewScratchContext())
            await priorContext.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);

        await SeedAsync();

        await using (var headContext = NewScratchContext())
            await headContext.Database.MigrateAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_adminConnectionString != null)
            await PostgresTestDatabase.ExecuteDatabaseCreateDropAsync(_adminConnectionString, $"DROP DATABASE IF EXISTS {ScratchDatabaseName} WITH (FORCE)");
    }

    [TestCase("Equals", SearchComparisonType.Equals)]
    [TestCase("NotEquals", SearchComparisonType.NotEquals)]
    [TestCase("LessThan", SearchComparisonType.LessThan)]
    [TestCase("GreaterThan", SearchComparisonType.GreaterThan)]
    [TestCase("GreaterThanOrEqual", SearchComparisonType.GreaterThanOrEquals)]
    [TestCase("LessThanOrEqual", SearchComparisonType.LessThanOrEquals)]
    [TestCase("Like", SearchComparisonType.Contains)]
    public async Task ExampleDataDependency_RetiredOperator_BecomesTheCataloguesAsync(string seeded, SearchComparisonType expected)
    {
        var stored = await ScalarAsync("""SELECT "ComparisonType" FROM "ExampleDataTemplateAttributeDependencies" WHERE "Id" = @id""", _dependencyIds[seeded]);

        Assert.That((SearchComparisonType)stored, Is.EqualTo(expected));
    }

    [TestCase("Text Equals no value", SearchComparisonType.HasNoValue)]
    [TestCase("Number Equals no value", SearchComparisonType.HasNoValue)]
    [TestCase("Text NotEquals no value", SearchComparisonType.HasValue)]
    [TestCase("Boolean NotEquals no value", SearchComparisonType.HasValue)]
    [TestCase("Guid NotEquals no value", SearchComparisonType.HasValue)]
    // Never matched anything; rewriting it to "has a value" would bring objects into scope, so it is left alone.
    [TestCase("Number NotEquals no value", SearchComparisonType.NotEquals)]
    [TestCase("Text Equals a value", SearchComparisonType.Equals)]
    [TestCase("Text NotEquals a value", SearchComparisonType.NotEquals)]
    public async Task ScopingCriterion_IsRewrittenOnlyWhereTheMeaningIsUnchangedAsync(string seeded, SearchComparisonType expected)
    {
        var stored = await ScalarAsync("""SELECT "ComparisonType" FROM "SyncRuleScopingCriteria" WHERE "Id" = @id""", _criterionIds[seeded]);

        Assert.That((SearchComparisonType)stored, Is.EqualTo(expected));
    }

    private async Task SeedAsync()
    {
        await using var connection = new NpgsqlConnection(_scratchConnectionString);
        await connection.OpenAsync();

        async Task<int> AttributeAsync(string name, int type)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO "MetaverseAttributes" ("Name", "Type", "AttributePlurality", "BuiltIn", "Created", "CreatedByType", "LastUpdatedByType", "RenderingHint")
                VALUES (@name, @type, 0, false, now(), 0, 0, 0) RETURNING "Id"
                """, connection);
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("type", type);
            return (int)(await command.ExecuteScalarAsync())!;
        }

        // AttributeDataType ordinals: Text 1, Number 2, Guid 6, Boolean 7.
        var text = await AttributeAsync("Department", 1);
        var number = await AttributeAsync("Level", 2);
        var guid = await AttributeAsync("Badge", 6);
        var boolean = await AttributeAsync("Active", 7);

        foreach (var (label, ordinal) in new[]
                 {
                     ("Equals", OldEquals), ("NotEquals", OldNotEquals), ("LessThan", OldLessThan), ("GreaterThan", OldGreaterThan),
                     ("GreaterThanOrEqual", OldGreaterThanOrEqual), ("LessThanOrEqual", OldLessThanOrEqual), ("Like", OldLike)
                 })
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO "ExampleDataTemplateAttributeDependencies" ("MetaverseAttributeId", "StringValue", "ComparisonType")
                VALUES (@attribute, '3', @op) RETURNING "Id"
                """, connection);
            command.Parameters.AddWithValue("attribute", number);
            command.Parameters.AddWithValue("op", ordinal);
            _dependencyIds[label] = (int)(await command.ExecuteScalarAsync())!;
        }

        async Task CriterionAsync(string label, int attribute, SearchComparisonType op, string? stringValue = null)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO "SyncRuleScopingCriteria" ("MetaverseAttributeId", "ComparisonType", "CaseSensitive", "ValueMode", "StringValue")
                VALUES (@attribute, @op, false, 0, @value) RETURNING "Id"
                """, connection);
            command.Parameters.AddWithValue("attribute", attribute);
            command.Parameters.AddWithValue("op", (int)op);
            command.Parameters.AddWithValue("value", (object?)stringValue ?? DBNull.Value);
            _criterionIds[label] = (int)(await command.ExecuteScalarAsync())!;
        }

        await CriterionAsync("Text Equals no value", text, SearchComparisonType.Equals);
        await CriterionAsync("Number Equals no value", number, SearchComparisonType.Equals);
        await CriterionAsync("Text NotEquals no value", text, SearchComparisonType.NotEquals);
        await CriterionAsync("Boolean NotEquals no value", boolean, SearchComparisonType.NotEquals);
        await CriterionAsync("Guid NotEquals no value", guid, SearchComparisonType.NotEquals);
        await CriterionAsync("Number NotEquals no value", number, SearchComparisonType.NotEquals);
        await CriterionAsync("Text Equals a value", text, SearchComparisonType.Equals, "Finance");
        await CriterionAsync("Text NotEquals a value", text, SearchComparisonType.NotEquals, "Finance");
    }

    private async Task<int> ScalarAsync(string sql, int id)
    {
        await using var connection = new NpgsqlConnection(_scratchConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private JimDbContext NewScratchContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_scratchConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }
}

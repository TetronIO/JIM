// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL cover for the Connected System's runtime state (#2950): the values the Worker maintains as it runs
/// (the Connector's persisted data, which holds its Delta Import watermark; the Delta Synchronisation watermark; the
/// stranded-value sweep's arming; the last Full Import's completion; the import-incomplete marker). Each is written only by
/// its own narrow statement. A save of the whole Connected System, made from a copy loaded earlier (the portal's page, or
/// the Worker's run), must not write back what it loaded: a Delta Import watermark put back after a Connector Space clear
/// means the next Delta Import never re-reads the cleared objects. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemRuntimeStateDatabaseTests
{
    private static readonly DateTime Earlier = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 11, 16, 10, 3, 12, DateTimeKind.Utc);

    /// <summary>Every runtime state column of a Connected System. A new one carries <see cref="RuntimeStateAttribute"/> and joins this list.</summary>
    private static readonly string[] RuntimeStateColumns =
    [
        nameof(ConnectedSystem.PersistedConnectorData),
        nameof(ConnectedSystem.LastSyncCompletedAt),
        nameof(ConnectedSystem.StrandedValueSweepArmedAt),
        nameof(ConnectedSystem.LastSuccessfulFullImportCompletedAt),
        nameof(ConnectedSystem.ImportIncompleteSince)
    ];

    private string _connectionString = null!;

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.NoTracking)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(tracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Connected System runtime state tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        TestUtilities.SetEnvironmentVariables();
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public void RuntimeStateAttribute_MarksExactlyTheRuntimeStateColumns()
    {
        var marked = typeof(ConnectedSystem).GetProperties()
            .Where(p => Attribute.IsDefined(p, typeof(RuntimeStateAttribute)))
            .Select(p => p.Name);

        Assert.That(marked, Is.EquivalentTo(RuntimeStateColumns),
            "a runtime state column carries the attribute so a whole-system save leaves it out, and is listed here so its round trip is tested");
    }

    [TestCaseSource(nameof(RuntimeStateColumns))]
    public async Task UpdateConnectedSystemAsync_LoadedBeforeTheWorkerWroteIt_LeavesTheColumnAsync(string column)
    {
        var systemId = await SeedAsync();

        // The portal loads the Connected System, the Worker then writes the column with its own statement, and the
        // administrator saves a setting change from what the page loaded.
        ConnectedSystem loadedByThePortal;
        await using (var ctx = NewContext())
            loadedByThePortal = (await new PostgresDataRepository(ctx).ConnectedSystems.GetConnectedSystemCoreAsync(systemId))!;

        await WriteColumnAsync(systemId, column, laterValue: true);

        loadedByThePortal.Description = "Changed in the portal";
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.UpdateConnectedSystemAsync(loadedByThePortal);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadDescriptionAsync(systemId), Is.EqualTo("Changed in the portal"), "precondition: the portal's change was saved");
            Assert.That(await ReadColumnAsync(systemId, column), Is.EqualTo(Expected(column, laterValue: true)),
                $"{column} is written only by its own statement; a save of a copy loaded earlier must not put back what it loaded");
        }
    }

    [Test]
    public async Task SetLastSyncCompletedAtAsync_WorkerTrackingContextHoldsAStaleCopy_WritesOnlyTheSynchronisationWatermarkAsync()
    {
        var systemId = await SeedAsync();
        await WriteColumnAsync(systemId, nameof(ConnectedSystem.PersistedConnectorData), laterValue: false);

        // As a synchronisation runs: the Connected System is tracked as it stood at the run's start, an import of the same
        // system (a parallel Schedule step) then records a newer Delta Import watermark, and the synchronisation completes.
        await using (var ctx = NewContext(QueryTrackingBehavior.TrackAll))
        {
            var repository = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repository);
            var tracked = (await jim.ConnectedSystems.GetConnectedSystemCoreAsync(systemId, withChangeTracking: true))!;

            await WriteColumnAsync(systemId, nameof(ConnectedSystem.PersistedConnectorData), laterValue: true);

            await repository.Sync.SetLastSyncCompletedAtAsync(systemId, Later);
            tracked.LastSyncCompletedAt = Later;
            await ctx.SaveChangesAsync();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadColumnAsync(systemId, nameof(ConnectedSystem.LastSyncCompletedAt)), Is.EqualTo(Later),
                "the synchronisation's watermark is recorded");
            Assert.That(await ReadColumnAsync(systemId, nameof(ConnectedSystem.PersistedConnectorData)), Is.EqualTo(Expected(nameof(ConnectedSystem.PersistedConnectorData), laterValue: true)),
                "the import's newer Delta Import watermark is kept");
        }
    }

    #region Helpers

    private async Task<int> SeedAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.SqlConnectorName, BuiltIn = true };
        var system = new ConnectedSystem { Name = "Source", ConnectorDefinition = connectorDefinition };
        seed.ConnectorDefinitions.Add(connectorDefinition);
        seed.ConnectedSystems.Add(system);
        await seed.SaveChangesAsync();
        return system.Id;
    }

    private static object Expected(string column, bool laterValue) =>
        column == nameof(ConnectedSystem.PersistedConnectorData)
            ? laterValue ? "{\"watermark\":\"later\"}" : "{\"watermark\":\"earlier\"}"
            : laterValue ? Later : Earlier;

    /// <summary>Writes one runtime column as the Worker's own statement would, outside any context under test.</summary>
    private async Task WriteColumnAsync(int systemId, string column, bool laterValue)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        // The column name comes from the fixed list above, never from input.
        await using var command = new NpgsqlCommand($"UPDATE \"ConnectedSystems\" SET \"{column}\" = @value WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("value", Expected(column, laterValue));
        command.Parameters.AddWithValue("id", systemId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ReadColumnAsync(int systemId, string column)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT \"{column}\" FROM \"ConnectedSystems\" WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("id", systemId);
        var value = await command.ExecuteScalarAsync();
        return value switch
        {
            DBNull => null,
            DateTime dateTime => DateTime.SpecifyKind(dateTime.ToUniversalTime(), DateTimeKind.Utc),
            _ => value
        };
    }

    private async Task<string?> ReadDescriptionAsync(int systemId)
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystems.Where(cs => cs.Id == systemId).Select(cs => cs.Description).SingleAsync();
    }

    #endregion
}

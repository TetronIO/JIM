// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL cover for the Connected System's import-incomplete marker (<see cref="ConnectedSystem.ImportIncompleteSince"/>,
/// #2521): the column round-trips through the narrow writes an import makes, a marker already set keeps its earlier time,
/// clearing the Connector Space takes it down with the objects it was about, and a portal save of a Connected System loaded
/// before the marker was set cannot take it down by writing back what it loaded. Opt-in via <c>JIM_TEST_RESET_*</c>;
/// ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemImportIncompleteDatabaseTests
{
    private static readonly DateTime FirstStop = new(2026, 11, 16, 10, 3, 12, DateTimeKind.Utc);

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL import-incomplete marker tests.");

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
    public async Task MarkThenClear_RoundTripsTheMarkerAsync()
    {
        var systemId = await SeedAsync();

        DateTime held;
        await using (var ctx = NewContext())
            held = await new PostgresDataRepository(ctx).Sync.MarkImportIncompleteAsync(systemId, FirstStop);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held, Is.EqualTo(FirstStop), "the write returns the marker as it now stands");
            Assert.That(await ReadMarkerAsync(systemId), Is.EqualTo(FirstStop), "the marker must round-trip exactly");
        }

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.ClearImportIncompleteAsync(systemId);

        Assert.That(await ReadMarkerAsync(systemId), Is.Null);
    }

    [Test]
    public async Task GetConnectedSystemHeaderAsync_Marked_ReportsTheMarkerAsync()
    {
        var systemId = await SeedAsync();
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.MarkImportIncompleteAsync(systemId, FirstStop);

        await using var readContext = NewContext();
        var header = await new PostgresDataRepository(readContext).ConnectedSystems.GetConnectedSystemHeaderAsync(systemId);

        Assert.That(header!.ImportIncompleteSince is { } since ? DateTime.SpecifyKind(since, DateTimeKind.Utc) : (DateTime?)null, Is.EqualTo(FirstStop),
            "the list shows which Connected Systems wait for a Full Import");
    }

    [Test]
    public async Task MarkImportIncompleteAsync_AlreadyMarked_KeepsTheEarlierTimeAsync()
    {
        var systemId = await SeedAsync();
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.MarkImportIncompleteAsync(systemId, FirstStop);

        DateTime held;
        await using (var ctx = NewContext())
            held = await new PostgresDataRepository(ctx).Sync.MarkImportIncompleteAsync(systemId, FirstStop.AddHours(2));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held, Is.EqualTo(FirstStop), "the caller learns the time the marker kept");
            Assert.That(await ReadMarkerAsync(systemId), Is.EqualTo(FirstStop));
        }
    }

    [Test]
    public async Task MarkImportIncompleteAsync_WorkerTrackingContextSavesAfterwards_KeepsTheMarkerAsync()
    {
        var systemId = await SeedAsync();

        // As the Worker runs an import: the Connected System is tracked, the marker is written by its own statement, and the
        // run later saves other changes on the same context. The tracked instance is kept in step, so it writes back the same.
        await using (var ctx = NewContext(QueryTrackingBehavior.TrackAll))
        {
            var repository = new PostgresDataRepository(ctx);
            using var jim = new JimApplication(repository);
            var tracked = (await jim.ConnectedSystems.GetConnectedSystemCoreAsync(systemId, withChangeTracking: true))!;
            tracked.ImportIncompleteSince = await repository.Sync.MarkImportIncompleteAsync(systemId, FirstStop);
            tracked.Description = "Saved after the marker";
            await ctx.SaveChangesAsync();
        }

        Assert.That(await ReadMarkerAsync(systemId), Is.EqualTo(FirstStop));
    }

    [Test]
    public async Task ClearConnectedSystemObjectsAsync_Marked_ClearsTheMarkerAsync()
    {
        var systemId = await SeedAsync();
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.MarkImportIncompleteAsync(systemId, FirstStop);

        await using (var ctx = NewContext())
        {
            using var jim = new JimApplication(new PostgresDataRepository(ctx));
            await jim.ConnectedSystems.ClearConnectedSystemObjectsAsync(systemId);
        }

        Assert.That(await ReadMarkerAsync(systemId), Is.Null,
            "the clear removes every object the stopped import half-saved, so nothing is left for a synchronisation to misread");
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_LoadedBeforeTheMarkerWasSet_LeavesTheMarkerAsync()
    {
        var systemId = await SeedAsync();

        // The portal loads the Connected System, an import then marks it, and the administrator saves a setting change from
        // what the page loaded. The save must not write back the empty marker it loaded, or a synchronisation would run
        // against the half-saved import.
        ConnectedSystem loadedByThePortal;
        await using (var ctx = NewContext())
            loadedByThePortal = (await new PostgresDataRepository(ctx).ConnectedSystems.GetConnectedSystemCoreAsync(systemId))!;

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.MarkImportIncompleteAsync(systemId, FirstStop);

        loadedByThePortal.Description = "Changed in the portal";
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.UpdateConnectedSystemAsync(loadedByThePortal);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadDescriptionAsync(systemId), Is.EqualTo("Changed in the portal"), "precondition: the portal's change was saved");
            Assert.That(await ReadMarkerAsync(systemId), Is.EqualTo(FirstStop),
                "only a completed import, or clearing the Connector Space, takes the marker down");
        }
    }

    [Test]
    public async Task UpdateConnectedSystemAsync_LoadedWhileMarked_DoesNotPutBackAMarkerAnImportClearedAsync()
    {
        var systemId = await SeedAsync();
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.MarkImportIncompleteAsync(systemId, FirstStop);

        ConnectedSystem loadedByThePortal;
        await using (var ctx = NewContext())
            loadedByThePortal = (await new PostgresDataRepository(ctx).ConnectedSystems.GetConnectedSystemCoreAsync(systemId))!;

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.ClearImportIncompleteAsync(systemId);

        loadedByThePortal.Description = "Changed in the portal";
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.UpdateConnectedSystemAsync(loadedByThePortal);

        Assert.That(await ReadMarkerAsync(systemId), Is.Null,
            "a stale page must not stop synchronisation again after a Full Import completed");
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

    private async Task<DateTime?> ReadMarkerAsync(int systemId)
    {
        await using var ctx = NewContext();
        var marker = await ctx.ConnectedSystems.Where(cs => cs.Id == systemId).Select(cs => cs.ImportIncompleteSince).SingleAsync();
        return marker.HasValue ? DateTime.SpecifyKind(marker.Value, DateTimeKind.Utc) : null;
    }

    private async Task<string?> ReadDescriptionAsync(int systemId)
    {
        await using var ctx = NewContext();
        return await ctx.ConnectedSystems.Where(cs => cs.Id == systemId).Select(cs => cs.Description).SingleAsync();
    }

    #endregion
}

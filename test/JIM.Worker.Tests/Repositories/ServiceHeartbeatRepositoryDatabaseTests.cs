// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Operations;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the service heartbeat repository. The upsert is raw SQL (one INSERT ... ON
/// CONFLICT DO UPDATE per write, because it runs every few seconds from every service), so only a round trip can
/// prove the values land in the right columns and that a second write for the same instance updates rather than
/// duplicates. Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ServiceHeartbeatRepositoryDatabaseTests
{
    private static readonly DateTime T0 = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    private PostgresDataRepository NewRepository() => new(NewContext());

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL service heartbeat tests.");

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
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"DELETE FROM ""ServiceHeartbeats"";");
    }

    private static ServiceHeartbeat Heartbeat(JimService service, string instanceId, DateTime lastSeenAt) => new()
    {
        Service = service,
        InstanceId = instanceId,
        HostName = "host",
        Version = "0.15.0",
        StartedAt = T0.AddHours(-1),
        LastSeenAt = lastSeenAt,
        CurrentWork = "Full Import: Corporate Directory",
        CurrentWorkStartedAt = T0.AddMinutes(-3),
        LastProgressAt = T0.AddMinutes(-1),
        Detail = "1 task in flight"
    };

    [Test]
    public async Task UpsertServiceHeartbeatAsync_NewInstance_PersistsEveryFieldAsync()
    {
        using var repository = NewRepository();
        var heartbeat = Heartbeat(JimService.WorkerSync, "host-a1b2c3", T0);

        await repository.System.UpsertServiceHeartbeatAsync(heartbeat);

        await using var read = NewContext();
        var stored = await read.ServiceHeartbeats.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Service, Is.EqualTo(JimService.WorkerSync));
            Assert.That(stored.InstanceId, Is.EqualTo("host-a1b2c3"));
            Assert.That(stored.HostName, Is.EqualTo("host"));
            Assert.That(stored.Version, Is.EqualTo("0.15.0"));
            Assert.That(stored.StartedAt, Is.EqualTo(T0.AddHours(-1)));
            Assert.That(stored.LastSeenAt, Is.EqualTo(T0));
            Assert.That(stored.CurrentWork, Is.EqualTo("Full Import: Corporate Directory"));
            Assert.That(stored.CurrentWorkStartedAt, Is.EqualTo(T0.AddMinutes(-3)));
            Assert.That(stored.LastProgressAt, Is.EqualTo(T0.AddMinutes(-1)));
            Assert.That(stored.Detail, Is.EqualTo("1 task in flight"));
        }
    }

    [Test]
    public async Task UpsertServiceHeartbeatAsync_NullOptionalFields_PersistsNullsAsync()
    {
        using var repository = NewRepository();
        var heartbeat = Heartbeat(JimService.Scheduler, "host-a1b2c3", T0);
        heartbeat.CurrentWork = null;
        heartbeat.CurrentWorkStartedAt = null;
        heartbeat.LastProgressAt = null;
        heartbeat.Detail = null;

        await repository.System.UpsertServiceHeartbeatAsync(heartbeat);

        await using var read = NewContext();
        var stored = await read.ServiceHeartbeats.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.CurrentWork, Is.Null);
            Assert.That(stored.CurrentWorkStartedAt, Is.Null);
            Assert.That(stored.LastProgressAt, Is.Null);
            Assert.That(stored.Detail, Is.Null);
        }
    }

    [Test]
    public async Task UpsertServiceHeartbeatAsync_SameInstanceTwice_UpdatesTheOneRowAsync()
    {
        using var repository = NewRepository();
        await repository.System.UpsertServiceHeartbeatAsync(Heartbeat(JimService.WorkerSync, "host-a1b2c3", T0));

        var later = Heartbeat(JimService.WorkerSync, "host-a1b2c3", T0.AddSeconds(5));
        later.CurrentWork = null;
        later.CurrentWorkStartedAt = null;
        later.LastProgressAt = null;
        later.Detail = "idle";
        await repository.System.UpsertServiceHeartbeatAsync(later);

        await using var read = NewContext();
        var rows = await read.ServiceHeartbeats.AsNoTracking().ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rows[0].LastSeenAt, Is.EqualTo(T0.AddSeconds(5)));
            Assert.That(rows[0].CurrentWork, Is.Null);
            Assert.That(rows[0].Detail, Is.EqualTo("idle"));
        }
    }

    [Test]
    public async Task UpsertServiceHeartbeatAsync_SameInstanceIdDifferentService_TwoRowsAsync()
    {
        // The Worker process hosts two services under one instance id; they must not overwrite each other.
        using var repository = NewRepository();

        await repository.System.UpsertServiceHeartbeatAsync(Heartbeat(JimService.WorkerSync, "host-a1b2c3", T0));
        await repository.System.UpsertServiceHeartbeatAsync(Heartbeat(JimService.WorkerDelivery, "host-a1b2c3", T0));

        await using var read = NewContext();
        Assert.That(await read.ServiceHeartbeats.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetLatestServiceHeartbeatsAsync_TwoInstancesOfOneService_ReturnsTheNewestOnlyAsync()
    {
        using var repository = NewRepository();
        await repository.System.UpsertServiceHeartbeatAsync(Heartbeat(JimService.WorkerSync, "host-old", T0.AddMinutes(-30)));
        await repository.System.UpsertServiceHeartbeatAsync(Heartbeat(JimService.WorkerSync, "host-new", T0));
        await repository.System.UpsertServiceHeartbeatAsync(Heartbeat(JimService.Scheduler, "host-s", T0.AddMinutes(-1)));

        var latest = await repository.System.GetLatestServiceHeartbeatsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(latest, Has.Count.EqualTo(2));
            Assert.That(latest.Single(h => h.Service == JimService.WorkerSync).InstanceId, Is.EqualTo("host-new"));
            Assert.That(latest.Single(h => h.Service == JimService.Scheduler).InstanceId, Is.EqualTo("host-s"));
        }
    }

    [Test]
    public async Task PruneServiceHeartbeatsAsync_OnlyOldRowsOfThatService_RemovedAndCountedAsync()
    {
        using var repository = NewRepository();
        await repository.System.UpsertServiceHeartbeatAsync(Heartbeat(JimService.WorkerSync, "host-old-1", T0.AddDays(-2)));
        await repository.System.UpsertServiceHeartbeatAsync(Heartbeat(JimService.WorkerSync, "host-old-2", T0.AddDays(-3)));
        await repository.System.UpsertServiceHeartbeatAsync(Heartbeat(JimService.WorkerSync, "host-new", T0));
        await repository.System.UpsertServiceHeartbeatAsync(Heartbeat(JimService.Scheduler, "host-s-old", T0.AddDays(-2)));

        var removed = await repository.System.PruneServiceHeartbeatsAsync(JimService.WorkerSync, T0.AddDays(-1));

        await using var read = NewContext();
        var remaining = await read.ServiceHeartbeats.AsNoTracking().Select(h => h.InstanceId).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(2));
            Assert.That(remaining, Is.EquivalentTo(new[] { "host-new", "host-s-old" }));
        }
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage for <c>ConnectedSystemRepository.GetConnectedSystemObjectObsoleteCountAsync</c>,
/// the figure the Connected System page states so that objects waiting on a synchronisation are visible
/// somewhere other than the status column of individual objects (#1527).
/// </summary>
/// <remarks>
/// Real PostgreSQL rather than the in-memory provider because the whole method is one translated predicate
/// over two columns: a fixture that evaluated it in memory would prove the LINQ compiles, not that the
/// query filters by the right system and the right status. Both halves matter, and getting the system
/// wrong is the failure that would show a shared figure across every Connected System.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ObsoleteConnectedSystemObjectCountDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL obsolete object count tests.");

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
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
    }

    [Test]
    public async Task GetConnectedSystemObjectObsoleteCountAsync_MixedStatuses_CountsOnlyTheObsoleteOnesAsync()
    {
        var systemId = await SeedAsync(
            ConnectedSystemObjectStatus.Obsolete,
            ConnectedSystemObjectStatus.Obsolete,
            ConnectedSystemObjectStatus.Normal,
            ConnectedSystemObjectStatus.PendingProvisioning);

        var count = await CountAsync(systemId);

        Assert.That(count, Is.EqualTo(2),
            "Only objects awaiting a synchronisation run count. Including Normal objects would report the whole " +
            "connector space as waiting, which is the opposite of the signal.");
    }

    [Test]
    public async Task GetConnectedSystemObjectObsoleteCountAsync_NoneObsolete_ReturnsZeroAsync()
    {
        // Zero is what keeps the notice off a healthy system's page, so it is worth asserting rather than
        // assuming: a predicate inverted by accident would show the notice permanently.
        var systemId = await SeedAsync(ConnectedSystemObjectStatus.Normal, ConnectedSystemObjectStatus.Normal);

        var count = await CountAsync(systemId);

        Assert.That(count, Is.Zero);
    }

    [Test]
    public async Task GetConnectedSystemObjectObsoleteCountAsync_AnotherSystemHasObsoleteObjects_DoesNotCountThemAsync()
    {
        var quietSystemId = await SeedAsync(ConnectedSystemObjectStatus.Normal);
        await SeedAsync(ConnectedSystemObjectStatus.Obsolete, ConnectedSystemObjectStatus.Obsolete);

        var count = await CountAsync(quietSystemId);

        Assert.That(count, Is.Zero,
            "The count is stated on one Connected System's page, so a missing system predicate would put another " +
            "system's backlog on it.");
    }

    /// <summary>Reads the count through the application layer, exactly as the Connected System page does.</summary>
    private async Task<int> CountAsync(int connectedSystemId)
    {
        await using var ctx = NewContext();
        using var jim = new JimApplication(new PostgresDataRepository(ctx));
        return await jim.ConnectedSystems.GetConnectedSystemObjectObsoleteCountAsync(connectedSystemId);
    }

    /// <summary>
    /// Seeds one Connected System holding an object per status supplied.
    /// </summary>
    /// <returns>The Connected System's id.</returns>
    private async Task<int> SeedAsync(params ConnectedSystemObjectStatus[] statuses)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid()}", BuiltIn = true };
        var connectedSystem = new ConnectedSystem { Name = $"Glitterband {Guid.NewGuid()}", ConnectorDefinition = connectorDefinition };
        var objectType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = connectedSystem, Selected = true };
        seed.AddRange(connectorDefinition, connectedSystem, objectType);
        await seed.SaveChangesAsync();

        foreach (var status in statuses)
            seed.Add(new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = connectedSystem.Id,
                TypeId = objectType.Id,
                Status = status,
                Created = DateTime.UtcNow
            });

        await seed.SaveChangesAsync();
        return connectedSystem.Id;
    }
}

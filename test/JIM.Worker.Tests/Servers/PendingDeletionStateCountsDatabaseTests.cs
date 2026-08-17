// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the pending-deletion state counts
/// (<see cref="JIM.Application.Servers.MetaverseServer.GetMetaverseObjectsPendingDeletionStateCountsAsync"/>)
/// behind the four cards above the Pending Deletions list. The in-memory provider runs both halves of the
/// awaiting-grace-period predicate in .NET (a correlated <c>Any()</c> over the Connected System Objects, and a
/// timestamp-plus-interval comparison against a type's grace period), so only a real database proves they
/// translate to SQL at all. The context is NoTracking, matching JIM.Web's configuration.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingDeletionStateCountsDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL pending-deletion state count tests.");

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

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    /// <summary>
    /// Seeds a User type with a 30 day grace period and objects in all three states: <paramref name="connected"/>
    /// still holding a Connected System Object (deprovisioning), <paramref name="withinGracePeriod"/> disconnected
    /// yesterday (awaiting), and <paramref name="pastGracePeriod"/> disconnected a year ago (ready). Returns the
    /// type's id.
    /// </summary>
    private async Task<int> SeedAsync(int connected, int withinGracePeriod, int pastGracePeriod)
    {
        await using var ctx = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Test System", ConnectorDefinition = connectorDefinition };
        // A Connected System Object's type is a required foreign key, so the system needs one object type before
        // any object can be hung off it.
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var type = new MetaverseObjectType
        {
            Name = "User",
            PluralName = "Users",
            BuiltIn = true,
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriod = TimeSpan.FromDays(30)
        };

        ctx.ConnectorDefinitions.Add(connectorDefinition);
        ctx.ConnectedSystems.Add(system);
        ctx.ConnectedSystemObjectTypes.Add(csType);
        ctx.MetaverseObjectTypes.Add(type);
        await ctx.SaveChangesAsync();

        MetaverseObject Pending(double disconnectedDaysAgo, string label) => new()
        {
            Id = Guid.NewGuid(),
            Origin = MetaverseObjectOrigin.Projected,
            Type = type,
            LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-disconnectedDaysAgo),
            CachedDisplayName = label
        };

        for (var i = 0; i < connected; i++)
        {
            var mvo = Pending(365, $"Deprovisioning {i:D3}");
            mvo.ConnectedSystemObjects.Add(new ConnectedSystemObject
            {
                Id = Guid.NewGuid(),
                ConnectedSystem = system,
                Type = csType,
                MetaverseObject = mvo
            });
            ctx.MetaverseObjects.Add(mvo);
        }

        for (var i = 0; i < withinGracePeriod; i++)
            ctx.MetaverseObjects.Add(Pending(1, $"Awaiting {i:D3}"));

        for (var i = 0; i < pastGracePeriod; i++)
            ctx.MetaverseObjects.Add(Pending(365, $"Ready {i:D3}"));

        await ctx.SaveChangesAsync();
        return type.Id;
    }

    [Test]
    public async Task StateCounts_MixedStatesBeyondOneHundredObjects_CountsTheWholeMatchSetAsync()
    {
        // Deliberately more than a hundred objects in one state: the fault these counts replaced read the first
        // hundred pending deletions and counted the states within them, so the figures stopped growing there.
        await SeedAsync(connected: 4, withinGracePeriod: 6, pastGracePeriod: 110);
        var jim = NewJim();

        var counts = await jim.Metaverse.GetMetaverseObjectsPendingDeletionStateCountsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts.Total, Is.EqualTo(120));
            Assert.That(counts.Deprovisioning, Is.EqualTo(4));
            Assert.That(counts.AwaitingGracePeriod, Is.EqualTo(6));
            Assert.That(counts.ReadyForDeletion, Is.EqualTo(110));
        }
    }

    [Test]
    public async Task StateCounts_ObjectTypeFilter_CountsOnlyThatTypeAsync()
    {
        var userTypeId = await SeedAsync(connected: 1, withinGracePeriod: 2, pastGracePeriod: 3);

        // A second type with its own pending object, which the filtered read must exclude.
        await using (var ctx = NewContext())
        {
            var groupType = new MetaverseObjectType
            {
                Name = "Group",
                PluralName = "Groups",
                BuiltIn = true,
                DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected
            };
            ctx.MetaverseObjectTypes.Add(groupType);
            ctx.MetaverseObjects.Add(new MetaverseObject
            {
                Id = Guid.NewGuid(),
                Origin = MetaverseObjectOrigin.Projected,
                Type = groupType,
                LastConnectorDisconnectedDate = DateTime.UtcNow.AddDays(-365),
                CachedDisplayName = "Pending Group"
            });
            await ctx.SaveChangesAsync();
        }

        var jim = NewJim();
        var counts = await jim.Metaverse.GetMetaverseObjectsPendingDeletionStateCountsAsync(userTypeId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts.Total, Is.EqualTo(6));
            Assert.That(counts.Deprovisioning, Is.EqualTo(1));
            Assert.That(counts.AwaitingGracePeriod, Is.EqualTo(2));
            Assert.That(counts.ReadyForDeletion, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task StateCounts_TotalAgreesWithTheListItSitsAboveAsync()
    {
        await SeedAsync(connected: 2, withinGracePeriod: 3, pastGracePeriod: 5);
        var jim = NewJim();

        var counts = await jim.Metaverse.GetMetaverseObjectsPendingDeletionStateCountsAsync();
        var window = await jim.Metaverse.GetMetaverseObjectsPendingDeletionRangeAsync(offset: 0, count: 5);

        Assert.That(counts.Total, Is.EqualTo(window.TotalResults));
    }

    [Test]
    public async Task StateCounts_NothingPendingDeletion_ReturnsZeroesAsync()
    {
        var jim = NewJim();

        var counts = await jim.Metaverse.GetMetaverseObjectsPendingDeletionStateCountsAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(counts.Total, Is.Zero);
            Assert.That(counts.Deprovisioning, Is.Zero);
            Assert.That(counts.AwaitingGracePeriod, Is.Zero);
            Assert.That(counts.ReadyForDeletion, Is.Zero);
        }
    }
}

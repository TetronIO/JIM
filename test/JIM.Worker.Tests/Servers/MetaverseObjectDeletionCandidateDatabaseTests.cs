// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the population a Metaverse Object Type deletion-settings preview reads (#1114),
/// and of the one thing that matters most about it: that evaluating the objects under the settings **currently in
/// force** produces exactly the set the housekeeping sweep would delete.
///
/// Those are two separate pieces of code (a scalar rule in the preview, an EF predicate in the sweep) answering the
/// same question, and they must not drift. If they do, the preview's headline number is wrong in the one direction
/// that matters: an administrator is told a change deletes nothing, saves it, and the sweep deletes thousands. The
/// in-memory provider cannot answer this, because it does not translate the sweep's predicate the way PostgreSQL
/// does and enforces no relational behaviour behind it.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other real-database fixtures.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectDeletionCandidateDatabaseTests
{
    private string _connectionString = null!;
    private int _objectTypeId;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL deletion candidate tests.");

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
    public async Task StreamDeletionCandidates_ReturnsMarkedProjectedObjectsOfTheTypeOnlyAsync()
    {
        await SeedTypeAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.FromDays(30));
        var otherTypeId = await SeedTypeAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, null, "Group");

        var marked = await SeedObjectAsync("Ada", disconnectedDaysAgo: 40);
        await SeedObjectAsync("Grace", disconnectedDaysAgo: null);
        await SeedObjectAsync("Katherine", disconnectedDaysAgo: 5, origin: MetaverseObjectOrigin.Internal);
        await SeedObjectAsync("Engineers", disconnectedDaysAgo: 5, objectTypeId: otherTypeId);

        var candidates = await ReadCandidatesAsync();

        Assert.That(candidates.Select(c => c.Id), Is.EquivalentTo(new[] { marked }),
            "an object with no disconnection mark cannot be deleted by any settings, an Internal object is protected, and another type's objects are another type's business");
    }

    [Test]
    public async Task StreamDeletionCandidates_ReportsWhetherConnectorsRemainAsync()
    {
        await SeedTypeAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.FromDays(30));
        var orphaned = await SeedObjectAsync("Ada", disconnectedDaysAgo: 40);
        var stillConnected = await SeedObjectAsync("Grace", disconnectedDaysAgo: 40, connectedSystemObjects: 1);

        var candidates = await ReadCandidatesAsync();

        Assert.Multiple(() =>
        {
            // The fact that decides whether the authoritative-source rule would delete an object the last-connector
            // rule is holding back, so getting it from the database rather than assuming it is the whole point.
            Assert.That(candidates.Single(c => c.Id == orphaned).HasConnectedSystemObjects, Is.False);
            Assert.That(candidates.Single(c => c.Id == stillConnected).HasConnectedSystemObjects, Is.True);
        });
    }

    [Test]
    public async Task CandidateCount_MatchesTheStreamedPopulationAsync()
    {
        await SeedTypeAsync(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, TimeSpan.FromDays(30));
        await SeedObjectAsync("Ada", disconnectedDaysAgo: 40);
        await SeedObjectAsync("Grace", disconnectedDaysAgo: 5);
        await SeedObjectAsync("Katherine", disconnectedDaysAgo: null);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var count = await repository.Metaverse.GetMetaverseObjectDeletionCandidateCountAsync(_objectTypeId);

        Assert.That(count, Is.EqualTo(2),
            "the estimate that decides where the preview runs must count the same objects the preview then evaluates");
    }

    [TestCase(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, 30)]
    [TestCase(MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, 0)]
    [TestCase(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, 30)]
    [TestCase(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected, 0)]
    [TestCase(MetaverseObjectDeletionRule.Manual, 30)]
    public async Task EligibilityRule_AgreesWithTheHousekeepingSweepAsync(MetaverseObjectDeletionRule rule, int graceDays)
    {
        var grace = graceDays == 0 ? (TimeSpan?)null : TimeSpan.FromDays(graceDays);
        await SeedTypeAsync(rule, grace);

        // A matrix that crosses every dimension the rule reads: long past its grace period, inside it, and with and
        // without connectors remaining.
        await SeedObjectAsync("Long gone", disconnectedDaysAgo: 90);
        await SeedObjectAsync("Just outside", disconnectedDaysAgo: 31);
        await SeedObjectAsync("Inside grace", disconnectedDaysAgo: 5);
        await SeedObjectAsync("Long gone, still connected", disconnectedDaysAgo: 90, connectedSystemObjects: 1);
        await SeedObjectAsync("Inside grace, still connected", disconnectedDaysAgo: 5, connectedSystemObjects: 1);
        await SeedObjectAsync("Never disconnected", disconnectedDaysAgo: null, connectedSystemObjects: 1);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var sweepWouldDelete = (await repository.Metaverse.GetMetaverseObjectsEligibleForDeletionAsync(maxResults: 1_000))
            .Select(mvo => mvo.Id)
            .ToList();

        var settings = new MetaverseObjectDeletionSettings(rule, grace);
        var asAt = DateTime.UtcNow;
        var previewSaysEligible = new List<Guid>();
        await foreach (var candidate in repository.Metaverse.StreamMetaverseObjectDeletionCandidates(_objectTypeId))
        {
            if (settings.IsEligibleAt(candidate.LastConnectorDisconnectedDate, candidate.HasConnectedSystemObjects, asAt))
                previewSaysEligible.Add(candidate.Id);
        }

        Assert.That(previewSaysEligible, Is.EquivalentTo(sweepWouldDelete),
            "the preview's rule and the housekeeping sweep's query have drifted apart; one of them is now lying about deletions");
    }

    private async Task<List<MetaverseObjectDeletionCandidate>> ReadCandidatesAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var candidates = new List<MetaverseObjectDeletionCandidate>();
        await foreach (var candidate in repository.Metaverse.StreamMetaverseObjectDeletionCandidates(_objectTypeId))
            candidates.Add(candidate);
        return candidates;
    }

    /// <summary>
    /// The Metaverse Object Type under test, plus a second type and a Connected System to hang test objects off.
    /// Seeded as one graph in one context: the entities have required relationships, and building them piecemeal
    /// across contexts is how a fixture ends up testing its own seeding rather than the query.
    /// </summary>
    private async Task<int> SeedTypeAsync(MetaverseObjectDeletionRule rule, TimeSpan? gracePeriod, string name = "User")
    {
        await using var seed = NewContext();

        var type = new MetaverseObjectType
        {
            Name = name,
            PluralName = name + "s",
            DeletionRule = rule,
            DeletionGracePeriod = gracePeriod
        };
        seed.MetaverseObjectTypes.Add(type);

        if (name == "User")
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Test System", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            seed.ConnectorDefinitions.Add(connectorDefinition);
            seed.ConnectedSystems.Add(system);
            seed.ConnectedSystemObjectTypes.Add(csType);
        }

        await seed.SaveChangesAsync();

        if (name == "User")
            _objectTypeId = type.Id;

        return type.Id;
    }

    private async Task<Guid> SeedObjectAsync(string displayName, int? disconnectedDaysAgo,
        MetaverseObjectOrigin origin = MetaverseObjectOrigin.Projected, int connectedSystemObjects = 0,
        int? objectTypeId = null)
    {
        await using var seed = NewContext();

        var type = await seed.MetaverseObjectTypes.AsTracking().SingleAsync(t => t.Id == (objectTypeId ?? _objectTypeId));
        var mvo = new MetaverseObject
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            CachedDisplayName = displayName,
            Origin = origin,
            LastConnectorDisconnectedDate = disconnectedDaysAgo.HasValue
                ? DateTime.UtcNow.AddDays(-disconnectedDaysAgo.Value)
                : null
        };
        seed.MetaverseObjects.Add(mvo);

        if (connectedSystemObjects > 0)
        {
            var csType = await seed.ConnectedSystemObjectTypes.AsTracking().Include(t => t.ConnectedSystem).FirstAsync();
            for (var i = 0; i < connectedSystemObjects; i++)
            {
                seed.ConnectedSystemObjects.Add(new ConnectedSystemObject
                {
                    Id = Guid.CreateVersion7(),
                    ConnectedSystemId = csType.ConnectedSystemId,
                    Type = csType,
                    MetaverseObject = mvo,
                    JoinType = ConnectedSystemObjectJoinType.Joined
                });
            }
        }

        await seed.SaveChangesAsync();
        return mvo.Id;
    }
}

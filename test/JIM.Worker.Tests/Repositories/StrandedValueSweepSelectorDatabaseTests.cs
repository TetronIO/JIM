// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for the stranded-value sweep's selector (#1549): a Metaverse Object holding a
/// value contributed by a Synchronisation Rule is "stranded" only when it holds NO Connected System Object
/// of the rule's Connected System (join-absence via a parameterised NOT EXISTS). The in-memory test
/// provider has no real foreign keys and cannot express this predicate faithfully, so this needs a real
/// provider (per the PRD's explicit requirement). Also covers the paired
/// <c>SetStrandedValueSweepPendingAsync</c> status-mark update round-trip.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class StrandedValueSweepSelectorDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL stranded-value sweep selector tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .Options);

    private sealed record Seeded(
        int ConnectedSystemId, int SyncRuleId, Guid JoinedMvoId, Guid StrandedMvoId, int AttributeId);

    private async Task<Seeded> SeedTopologyAsync(string suffix)
    {
        await using var seedCtx = NewContext();

        var definition = new ConnectorDefinition { Name = $"strand-def-{suffix}" };
        seedCtx.ConnectorDefinitions.Add(definition);
        await seedCtx.SaveChangesAsync();

        var system = new ConnectedSystem
        {
            Name = $"strand-system-{suffix}",
            ConnectorDefinitionId = definition.Id,
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.ConnectedSystem
        };
        seedCtx.ConnectedSystems.Add(system);
        await seedCtx.SaveChangesAsync();

        var objectType = new ConnectedSystemObjectType { ConnectedSystemId = system.Id, Name = "user", Selected = true };
        seedCtx.ConnectedSystemObjectTypes.Add(objectType);
        await seedCtx.SaveChangesAsync();

        var mvoType = new MetaverseObjectType { Name = $"strand-person-{suffix}", PluralName = $"strand-people-{suffix}" };
        seedCtx.MetaverseObjectTypes.Add(mvoType);
        await seedCtx.SaveChangesAsync();

        var attribute = new MetaverseAttribute
        {
            Name = $"strand-desc-{suffix}",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        seedCtx.MetaverseAttributes.Add(attribute);
        await seedCtx.SaveChangesAsync();

        var syncRule = new SyncRule
        {
            Name = $"strand-import-{suffix}",
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = objectType.Id,
            MetaverseObjectTypeId = mvoType.Id
        };
        seedCtx.SyncRules.Add(syncRule);
        await seedCtx.SaveChangesAsync();

        // The healthy sibling: a Metaverse Object holding the contributed value AND a joined Connected
        // System Object of the same system. Must never be reported as stranded.
        var joinedMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        seedCtx.MetaverseObjects.Add(joinedMvo);
        var joinedCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            TypeId = objectType.Id,
            MetaverseObjectId = joinedMvo.Id,
            Status = ConnectedSystemObjectStatus.Normal
        };
        seedCtx.ConnectedSystemObjects.Add(joinedCso);
        await seedCtx.SaveChangesAsync();
        seedCtx.MetaverseObjectAttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = joinedMvo,
            AttributeId = attribute.Id,
            StringValue = "Joined value",
            ContributedBySyncRuleId = syncRule.Id,
            ContributedBySystemId = system.Id
        });

        // The stranded object: holds the contributed value but NO Connected System Object of this system.
        var strandedMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow };
        seedCtx.MetaverseObjects.Add(strandedMvo);
        await seedCtx.SaveChangesAsync();
        seedCtx.MetaverseObjectAttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            MetaverseObject = strandedMvo,
            AttributeId = attribute.Id,
            StringValue = "Stranded value",
            ContributedBySyncRuleId = syncRule.Id,
            ContributedBySystemId = system.Id
        });
        await seedCtx.SaveChangesAsync();

        return new Seeded(system.Id, syncRule.Id, joinedMvo.Id, strandedMvo.Id, attribute.Id);
    }

    [Test]
    public async Task GetMetaverseObjectIdsWithStrandedValuesContributedBySyncRuleAsync_ReturnsOnlyTheJoinAbsentObjectAsync()
    {
        var seeded = await SeedTopologyAsync(Guid.NewGuid().ToString("N")[..8]);

        await using var assertCtx = NewContext();
        var repo = new MetaverseRepository(new PostgresDataRepository(assertCtx));

        var strandedIds = await repo.GetMetaverseObjectIdsWithStrandedValuesContributedBySyncRuleAsync(seeded.SyncRuleId, seeded.ConnectedSystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(strandedIds, Does.Contain(seeded.StrandedMvoId), "the join-absent object must be reported as stranded");
            Assert.That(strandedIds, Does.Not.Contain(seeded.JoinedMvoId), "an object that still holds a Connected System Object of this system must never be reported as stranded");
        }
    }

    [Test]
    public async Task GetMetaverseObjectIdsWithValuesContributedBySyncRuleAsync_StillReturnsBothObjectsAsync()
    {
        var seeded = await SeedTopologyAsync(Guid.NewGuid().ToString("N")[..8]);

        await using var assertCtx = NewContext();
        var repo = new MetaverseRepository(new PostgresDataRepository(assertCtx));

        var allContributedIds = await repo.GetMetaverseObjectIdsWithValuesContributedBySyncRuleAsync(seeded.SyncRuleId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allContributedIds, Does.Contain(seeded.StrandedMvoId),
                "the healthy sibling selector must be unaffected by the new join-absence predicate");
            Assert.That(allContributedIds, Does.Contain(seeded.JoinedMvoId));
        }
    }

    [Test]
    public async Task SetStrandedValueSweepPendingAsync_RoundTripsTrueThenFalseAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        int connectedSystemId;
        await using (var seedCtx = NewContext())
        {
            var definition = new ConnectorDefinition { Name = $"strand-flag-def-{suffix}" };
            seedCtx.ConnectorDefinitions.Add(definition);
            await seedCtx.SaveChangesAsync();

            var system = new ConnectedSystem { Name = $"strand-flag-system-{suffix}", ConnectorDefinitionId = definition.Id };
            seedCtx.ConnectedSystems.Add(system);
            await seedCtx.SaveChangesAsync();
            connectedSystemId = system.Id;

            Assert.That(system.StrandedValueSweepPending, Is.False, "precondition: a freshly created system is not armed");
        }

        await using (var setCtx = NewContext())
        {
            var repo = new ConnectedSystemRepository(new PostgresDataRepository(setCtx));
            await repo.SetStrandedValueSweepPendingAsync(connectedSystemId, pending: true);
        }

        await using (var readCtx = NewContext())
        {
            var armed = await readCtx.ConnectedSystems.SingleAsync(cs => cs.Id == connectedSystemId);
            Assert.That(armed.StrandedValueSweepPending, Is.True, "the flag must be set true");
        }

        await using (var clearCtx = NewContext())
        {
            var repo = new ConnectedSystemRepository(new PostgresDataRepository(clearCtx));
            await repo.SetStrandedValueSweepPendingAsync(connectedSystemId, pending: false);
        }

        await using var finalCtx = NewContext();
        var cleared = await finalCtx.ConnectedSystems.SingleAsync(cs => cs.Id == connectedSystemId);
        Assert.That(cleared.StrandedValueSweepPending, Is.False, "the flag must be cleared back to false");
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the durable join record (#348): the Synchronisation Rule a Connected System
/// Object was joined by must survive every raw-SQL path that writes or breaks a join, and deleting that rule must
/// keep the object and its name snapshot. The create paths are covered by <c>CsoBulkCreateColumnRoundTripDatabaseTests</c>
/// and the export matching claim by <c>ExportMatchingClaimDatabaseTests</c>. Opt-in via <c>JIM_TEST_RESET_*</c>.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class CsoJoinRecordPersistenceDatabaseTests
{
    private const string RuleName = "Yellowstone HR Import";

    private string _connectionString = null!;

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.TrackAll)
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL join record persistence tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    private sealed record Seeded(int SystemId, int TypeId, int ExternalIdAttributeId, int RuleId, Guid MvoId, Guid CsoId);

    /// <summary>
    /// Seeds a Connected System, an import Synchronisation Rule, a Metaverse Object, and one Connected System Object:
    /// joined to the Metaverse Object with the rule recorded when <paramref name="joined"/>, unjoined otherwise.
    /// </summary>
    private async Task<Seeded> SeedAsync(bool joined)
    {
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone HR", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var externalIdAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        csType.Attributes.Add(externalIdAttribute);
        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow };
        var rule = new SyncRule
        {
            Name = RuleName, Direction = SyncRuleDirection.Import, ConnectedSystem = system,
            ConnectedSystemObjectType = csType, MetaverseObjectType = mvType
        };
        seed.AddRange(connectorDefinition, system, csType, mvType, mvo, rule);
        await seed.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            TypeId = csType.Id,
            ExternalIdAttributeId = externalIdAttribute.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        if (joined)
        {
            cso.MetaverseObjectId = mvo.Id;
            cso.RecordJoin(ConnectedSystemObjectJoinType.Projected, rule, DateTime.UtcNow);
        }
        seed.ConnectedSystemObjects.Add(cso);
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, csType.Id, externalIdAttribute.Id, rule.Id, mvo.Id, cso.Id);
    }

    private async Task<ConnectedSystemObject> ReadCsoAsync(Guid id)
    {
        await using var read = NewContext();
        return await read.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    [Test]
    public async Task UpdateConnectedSystemObjectJoinStatesAsync_JoinRecorded_PersistsTheRuleAsync()
    {
        var s = await SeedAsync(joined: false);
        await using (var write = NewContext())
        {
            var cso = await write.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == s.CsoId);
            cso.MetaverseObjectId = s.MvoId;
            cso.RecordJoin(ConnectedSystemObjectJoinType.Joined, new SyncRule { Id = s.RuleId, Name = RuleName }, DateTime.UtcNow);
            await new PostgresDataRepository(write).Sync.UpdateConnectedSystemObjectJoinStatesAsync([cso]);
        }

        var stored = await ReadCsoAsync(s.CsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.MetaverseObjectId, Is.EqualTo(s.MvoId));
            Assert.That(stored.JoinType, Is.EqualTo(ConnectedSystemObjectJoinType.Joined));
            Assert.That(stored.JoinSyncRuleId, Is.EqualTo(s.RuleId));
            Assert.That(stored.JoinSyncRuleName, Is.EqualTo(RuleName));
        }
    }

    [Test]
    public async Task UpdateConnectedSystemObjectJoinStatesAsync_JoinReleased_ClearsTheRuleAsync()
    {
        var s = await SeedAsync(joined: true);
        await using (var write = NewContext())
        {
            var cso = await write.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == s.CsoId);
            cso.MetaverseObjectId = null;
            cso.ClearJoinRecord();
            await new PostgresDataRepository(write).Sync.UpdateConnectedSystemObjectJoinStatesAsync([cso]);
        }

        var stored = await ReadCsoAsync(s.CsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.MetaverseObjectId, Is.Null);
            Assert.That(stored.JoinSyncRuleId, Is.Null);
            Assert.That(stored.JoinSyncRuleName, Is.Null);
        }
    }

    [Test]
    public async Task UpdateConnectedSystemObjectsAsync_JoinRecorded_PersistsTheRuleAsync()
    {
        var s = await SeedAsync(joined: false);
        await using (var write = NewContext())
        {
            var cso = await write.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == s.CsoId);
            cso.MetaverseObjectId = s.MvoId;
            cso.RecordJoin(ConnectedSystemObjectJoinType.Joined, new SyncRule { Id = s.RuleId, Name = RuleName }, DateTime.UtcNow);
            await new PostgresDataRepository(write).ConnectedSystems.UpdateConnectedSystemObjectsAsync([cso]);
        }

        var stored = await ReadCsoAsync(s.CsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.JoinSyncRuleId, Is.EqualTo(s.RuleId));
            Assert.That(stored.JoinSyncRuleName, Is.EqualTo(RuleName));
        }
    }

    [Test]
    public async Task DisconnectConnectedSystemObjectsAsync_JoinedObject_ClearsTheRuleOnRowAndTrackedInstanceAsync()
    {
        var s = await SeedAsync(joined: true);
        await using (var write = NewContext())
        {
            var tracked = await write.ConnectedSystemObjects.SingleAsync(c => c.Id == s.CsoId);
            await new PostgresDataRepository(write).Sync.DisconnectConnectedSystemObjectsAsync([s.CsoId]);

            Assert.That(tracked.JoinSyncRuleName, Is.Null, "the tracked instance must be fixed up to match the row");
            Assert.That(async () => await write.SaveChangesAsync(), Throws.Nothing);
        }

        var stored = await ReadCsoAsync(s.CsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.MetaverseObjectId, Is.Null);
            Assert.That(stored.JoinSyncRuleId, Is.Null);
            Assert.That(stored.JoinSyncRuleName, Is.Null);
        }
    }

    [Test]
    public async Task DeleteMetaverseObjectsAsync_JoinedObject_ClearsTheRuleAsync()
    {
        var s = await SeedAsync(joined: true);
        await using (var write = NewContext())
        {
            var mvo = await write.MetaverseObjects.SingleAsync(m => m.Id == s.MvoId);
            await new PostgresDataRepository(write).Sync.DeleteMetaverseObjectsAsync([mvo]);
        }

        var stored = await ReadCsoAsync(s.CsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.MetaverseObjectId, Is.Null);
            Assert.That(stored.JoinSyncRuleId, Is.Null);
            Assert.That(stored.JoinSyncRuleName, Is.Null);
        }
    }

    [Test]
    public async Task DeleteMetaverseObjectAsync_JoinedObject_ClearsTheRuleAsync()
    {
        var s = await SeedAsync(joined: true);
        await using (var write = NewContext())
        {
            var mvo = await write.MetaverseObjects.SingleAsync(m => m.Id == s.MvoId);
            await new PostgresDataRepository(write).Metaverse.DeleteMetaverseObjectAsync(mvo);
        }

        var stored = await ReadCsoAsync(s.CsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.MetaverseObjectId, Is.Null);
            Assert.That(stored.JoinSyncRuleId, Is.Null);
            Assert.That(stored.JoinSyncRuleName, Is.Null);
        }
    }

    /// <summary>
    /// PRD requirement 29: deleting the Synchronisation Rule must not fail or remove the joined object; the reference
    /// is cleared and the name kept, through the portal's untracked context.
    /// </summary>
    [Test]
    public async Task DeleteSyncRuleAsync_RuleRecordedOnJoin_NullsTheReferenceAndKeepsTheNameAsync()
    {
        var s = await SeedAsync(joined: true);
        await using (var write = NewContext(QueryTrackingBehavior.NoTracking))
        {
            var rule = await write.SyncRules.SingleAsync(r => r.Id == s.RuleId);
            await new PostgresDataRepository(write).ConnectedSystems.DeleteSyncRuleAsync(rule);
        }

        var stored = await ReadCsoAsync(s.CsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.MetaverseObjectId, Is.EqualTo(s.MvoId), "the object stays joined");
            Assert.That(stored.JoinSyncRuleId, Is.Null);
            Assert.That(stored.JoinSyncRuleName, Is.EqualTo(RuleName));
        }
    }
}

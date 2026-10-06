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
/// Real-PostgreSQL proof of the raw SQL behind the <c>ScopeReviewPending</c> flag, which the in-memory store can only
/// imitate. Three writers share the flag: an export Synchronisation Rule's configuration change flags every Metaverse
/// Object of its type (#1925), the Temporal Scope Reconciler flags objects whose time-driven scope flipped (#892), and
/// a synchronisation's drain clears what it has re-evaluated. Each must leave the others' flags alone: a flag lost is
/// an object whose provisioning or deprovisioning silently never happens. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored
/// when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ScopeReviewFlagDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL scope review flag tests.");

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

    #region Flagging a type (#1925)

    [Test]
    public async Task FlagMetaverseObjectsOfTypeForScopeReviewAsync_FlagsEveryObjectOfThatTypeAndNoOtherAsync()
    {
        var seeded = await SeedAsync();

        int newlyFlagged;
        await using (var context = NewContext())
            newlyFlagged = await new PostgresDataRepository(context).Sync.FlagMetaverseObjectsOfTypeForScopeReviewAsync(seeded.PersonTypeId);

        var flags = await ReadMvoFlagsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flags[seeded.Alice], Is.True);
            Assert.That(flags[seeded.Bob], Is.True, "already flagged, and stays so");
            Assert.That(flags[seeded.Finance], Is.False, "a Group is not covered by a Person rule");
            Assert.That(newlyFlagged, Is.EqualTo(1), "Bob was flagged already, so only Alice is new");
        }
    }

    [Test]
    public async Task FlagMetaverseObjectsOfTypeForScopeReviewAsync_TrackedObject_ReadsAsPersistedAsync()
    {
        // The worker's context outlives a statement: a tracked object left holding the old value would write it back
        // on its next whole-entity save, silently undoing the flag.
        var seeded = await SeedAsync();

        await using var context = NewContext();
        var tracked = await context.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == seeded.Alice);
        await new PostgresDataRepository(context).Sync.FlagMetaverseObjectsOfTypeForScopeReviewAsync(seeded.PersonTypeId);

        var entry = context.Entry(tracked).Property(m => m.ScopeReviewPending);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracked.ScopeReviewPending, Is.True);
            Assert.That(entry.IsModified, Is.False, "the value is the persisted one, not a pending change");
        }
    }

    #endregion

    #region The drain's clear

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_NoExportRuleChangedSinceTheRunReadThem_ClearsAsync()
    {
        var seeded = await SeedAsync();

        bool cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync([seeded.Bob], seeded.ExportRuleStamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.True);
            Assert.That((await ReadMvoFlagsAsync())[seeded.Bob], Is.False);
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_ExportRuleUpdatedSinceTheRunReadThem_KeepsTheFlagAsync()
    {
        // The run evaluated Bob against the rules it read at its start. The rule has changed since, and the change has
        // flagged Bob for review against the rule as it now stands: clearing would lose that review.
        var seeded = await SeedAsync();
        await StampExportRuleAsync(seeded.ExportRuleId, seeded.ExportRuleStamp.AddSeconds(5));

        bool cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync([seeded.Bob], seeded.ExportRuleStamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.False);
            Assert.That((await ReadMvoFlagsAsync())[seeded.Bob], Is.True);
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_ExportRuleCreatedSinceTheRunReadThem_KeepsTheFlagAsync()
    {
        var seeded = await SeedAsync();
        await using (var context = NewContext())
        {
            var system = await context.ConnectedSystems.SingleAsync();
            context.SyncRules.Add(new SyncRule
            {
                Name = "Second Export", Direction = SyncRuleDirection.Export, Enabled = true,
                Created = seeded.ExportRuleStamp.AddSeconds(5),
                ConnectedSystemId = system.Id,
                ConnectedSystemObjectTypeId = (await context.ConnectedSystemObjectTypes.SingleAsync()).Id,
                MetaverseObjectTypeId = seeded.PersonTypeId
            });
            await context.SaveChangesAsync();
        }

        bool cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync([seeded.Bob], seeded.ExportRuleStamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.False);
            Assert.That((await ReadMvoFlagsAsync())[seeded.Bob], Is.True);
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_RunReadNoExportRulesAndOneNowExists_KeepsTheFlagAsync()
    {
        var seeded = await SeedAsync();

        bool cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync([seeded.Bob], exportRulesReadWatermark: null);

        Assert.That(cleared, Is.False, "the run held no export rule at all, and there is one now");
    }

    #endregion

    #region The reconciler leaves other flags alone

    [Test]
    public async Task MarkMetaverseObjectsScopeEvaluatedAsync_ObjectFlaggedByAnotherWriter_KeepsTheFlagAsync()
    {
        // Bob is flagged (by a configuration change, or by another rule's sweep). This rule's sweep finds Bob in
        // agreement with its own scope, which says nothing about why the flag was set.
        var seeded = await SeedAsync();
        var now = DateTime.UtcNow;

        await using (var context = NewContext())
            await new PostgresDataRepository(context).Metaverse.MarkMetaverseObjectsScopeEvaluatedAsync([seeded.Alice, seeded.Bob], [], now);

        var flags = await ReadMvoFlagsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flags[seeded.Bob], Is.True);
            Assert.That(flags[seeded.Alice], Is.False);
        }
    }

    [Test]
    public async Task MarkMetaverseObjectsScopeEvaluatedAsync_ObjectFlaggedBySweep_FlagsItAndRecordsTheEvaluationAsync()
    {
        var seeded = await SeedAsync();
        var now = TruncateToMicroseconds(DateTime.UtcNow);

        await using (var context = NewContext())
            await new PostgresDataRepository(context).Metaverse.MarkMetaverseObjectsScopeEvaluatedAsync([seeded.Alice], [seeded.Alice], now);

        await using var read = NewContext();
        var alice = await read.MetaverseObjects.AsNoTracking().SingleAsync(m => m.Id == seeded.Alice);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(alice.ScopeReviewPending, Is.True);
            Assert.That(alice.LastScopeEvaluatedAt, Is.EqualTo(now));
        }
    }

    [Test]
    public async Task MarkConnectedSystemObjectsScopeEvaluatedAsync_ObjectFlaggedByAnotherRule_KeepsTheFlagAsync()
    {
        // Two import rules on one system, each with a relative-date criterion: the second's sweep must not undo the
        // first's flag.
        var seeded = await SeedAsync();

        await using (var context = NewContext())
            await new PostgresDataRepository(context).ConnectedSystems.MarkConnectedSystemObjectsScopeEvaluatedAsync([seeded.FlaggedCso], [], DateTime.UtcNow);

        await using var read = NewContext();
        var cso = await read.ConnectedSystemObjects.AsNoTracking().SingleAsync(c => c.Id == seeded.FlaggedCso);
        Assert.That(cso.ScopeReviewPending, Is.True);
    }

    #endregion

    #region Seeding

    private sealed record Seeded(int PersonTypeId, Guid Alice, Guid Bob, Guid Finance, int ExportRuleId, DateTime ExportRuleStamp, Guid FlaggedCso);

    /// <summary>
    /// People Alice (not flagged) and Bob (flagged), Group Finance (not flagged), one export rule for People last
    /// updated at a known instant, and one flagged Connected System Object. One context, so every foreign key resolves.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();

        var person = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var group = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
        var alice = new MetaverseObject { Type = person, Created = DateTime.UtcNow };
        var bob = new MetaverseObject { Type = person, Created = DateTime.UtcNow, ScopeReviewPending = true };
        var finance = new MetaverseObject { Type = group, Created = DateTime.UtcNow };

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var stamp = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(-10));
        var exportRule = new SyncRule
        {
            Name = "Directory Export", Direction = SyncRuleDirection.Export, Enabled = true,
            ConnectedSystem = system, ConnectedSystemObjectType = csType, MetaverseObjectType = person,
            Created = stamp.AddDays(-1), LastUpdated = stamp
        };
        var flaggedCso = new ConnectedSystemObject
        {
            Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal, ScopeReviewPending = true
        };

        seed.AddRange(person, group, alice, bob, finance, connectorDefinition, system, csType, exportRule, flaggedCso);
        await seed.SaveChangesAsync();

        return new Seeded(person.Id, alice.Id, bob.Id, finance.Id, exportRule.Id, stamp, flaggedCso.Id);
    }

    private async Task StampExportRuleAsync(int syncRuleId, DateTime lastUpdated)
    {
        await using var context = NewContext();
        var rule = await context.SyncRules.AsTracking().SingleAsync(r => r.Id == syncRuleId);
        rule.LastUpdated = lastUpdated;
        await context.SaveChangesAsync();
    }

    private async Task<Dictionary<Guid, bool>> ReadMvoFlagsAsync()
    {
        await using var read = NewContext();
        return await read.MetaverseObjects.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.ScopeReviewPending);
    }

    private static DateTime TruncateToMicroseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, value.Kind);

    #endregion
}

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

    #region Flagging disconnected objects for re-provisioning (#2318)

    [Test]
    public async Task FlagMetaverseObjectsForScopeReviewAsync_FlagsTheGivenObjectsAndNoOtherAsync()
    {
        var seeded = await SeedAsync();

        int written;
        await using (var context = NewContext())
            written = await new PostgresDataRepository(context).Sync.FlagMetaverseObjectsForScopeReviewAsync([seeded.Alice, seeded.Bob, Guid.NewGuid()]);

        var flags = await ReadMvoFlagsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(flags[seeded.Alice], Is.True);
            Assert.That(flags[seeded.Bob], Is.True, "already flagged, and stays so");
            Assert.That(flags[seeded.Finance], Is.False, "not given");
            Assert.That(written, Is.EqualTo(2), "Alice and Bob exist and are written, flagged already or not; the unknown id matches nothing");
        }
    }

    [Test]
    public async Task FlagMetaverseObjectsForScopeReviewAsync_ObjectAlreadyFlagged_MovesItsRowVersionSoAGuardedClearKeepsItAsync()
    {
        // The page flush flags an object the drain may already hold: loaded flagged at version V, then flagged again
        // because it lost another account. The flag does not change, so unless the row is rewritten its version stays
        // V, and the drain's guarded clear (which cannot tell a rewrite from no write) would clear a flag whose new
        // reason the review it ran never saw.
        var seeded = await SeedAsync();
        var seen = await SeenAsync(seeded.Bob);

        await using (var context = NewContext())
            await new PostgresDataRepository(context).Sync.FlagMetaverseObjectsForScopeReviewAsync([seeded.Bob]);

        Assert.That((await SeenAsync(seeded.Bob))[seeded.Bob], Is.Not.EqualTo(seen[seeded.Bob]), "an already-flagged row is rewritten, so its version moves");

        await using (var context = NewContext())
            await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(seen, seeded.ExportRuleStamp);

        Assert.That((await ReadMvoFlagsAsync())[seeded.Bob], Is.True, "the clear holds the version read before the second flag, so it keeps the flag");
    }

    [Test]
    public async Task FlagMetaverseObjectsForScopeReviewAsync_TrackedObjectAlreadyFlagged_KeepsItsRowVersionCurrentAsync()
    {
        // The rewrite moves the version; a tracked instance must follow it, or its next whole-entity save fails the
        // row version check against the row the flag statement wrote.
        var seeded = await SeedAsync();

        await using var context = NewContext();
        var tracked = await context.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == seeded.Bob);
        await new PostgresDataRepository(context).Sync.FlagMetaverseObjectsForScopeReviewAsync([seeded.Bob]);

        tracked.CachedDisplayName = "Bob Renamed";
        await context.SaveChangesAsync();

        var stored = await ReadMvoAsync(seeded.Bob);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.ScopeReviewPending, Is.True);
            Assert.That(stored.CachedDisplayName, Is.EqualTo("Bob Renamed"));
        }
    }

    [Test]
    public async Task FlagMetaverseObjectsForScopeReviewAsync_TrackedObject_ReadsAsPersistedAsync()
    {
        var seeded = await SeedAsync();

        await using var context = NewContext();
        var tracked = await context.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == seeded.Alice);
        await new PostgresDataRepository(context).Sync.FlagMetaverseObjectsForScopeReviewAsync([seeded.Alice]);

        var entry = context.Entry(tracked).Property(m => m.ScopeReviewPending);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracked.ScopeReviewPending, Is.True);
            Assert.That(entry.IsModified, Is.False, "the value is the persisted one, not a pending change");
        }

        // And a whole-entity save of that instance does not write the old value back.
        tracked.CachedDisplayName = "Alice Renamed";
        await context.SaveChangesAsync();
        Assert.That((await ReadMvoFlagsAsync())[seeded.Alice], Is.True);
    }

    [Test]
    public async Task FlagMetaverseObjectsForScopeReviewAsync_NoIds_WritesNothingAsync()
    {
        await SeedAsync();

        await using var context = NewContext();
        Assert.That(await new PostgresDataRepository(context).Sync.FlagMetaverseObjectsForScopeReviewAsync([]), Is.Zero);
    }

    #endregion

    #region The drain's clear

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_NoExportRuleChangedSinceTheRunReadThem_ClearsAsync()
    {
        var seeded = await SeedAsync();

        bool cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(await SeenAsync(seeded.Bob), seeded.ExportRuleStamp);

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
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(await SeenAsync(seeded.Bob), seeded.ExportRuleStamp);

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
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(await SeenAsync(seeded.Bob), seeded.ExportRuleStamp);

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
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(await SeenAsync(seeded.Bob), exportRulesReadWatermark: null);

        Assert.That(cleared, Is.False, "the run held no export rule at all, and there is one now");
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_RowWrittenSinceItsVersionWasRead_KeepsThatFlagAndClearsTheRestAsync()
    {
        // Bob and Finance are flagged and loaded by the drain. Before the drain clears them, Bob is written (a sign-in
        // refreshing his profile, say): the drain never saw that change, so his flag must survive for the next run.
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);
        var seen = await SeenAsync(seeded.Bob, seeded.Finance);
        await WriteRowAsync(seeded.Bob);

        bool cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(seen, seeded.ExportRuleStamp);

        var flags = await ReadMvoFlagsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.True, "no export rule changed, so the run is not blocked");
            Assert.That(flags[seeded.Bob], Is.True, "written since the drain loaded it, so it still wants a review");
            Assert.That(flags[seeded.Finance], Is.False, "untouched since it was loaded");
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_ObjectNotNamedByTheVersions_KeepsItsFlagAsync()
    {
        // The clear only ever touches the rows the drain loaded and reviewed.
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);

        await using (var context = NewContext())
            await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(await SeenAsync(seeded.Bob), seeded.ExportRuleStamp);

        Assert.That((await ReadMvoFlagsAsync())[seeded.Finance], Is.True);
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_ExportRulesChangedAndRowWritten_KeepsEveryFlagAsync()
    {
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);
        var seen = await SeenAsync(seeded.Bob, seeded.Finance);
        await StampExportRuleAsync(seeded.ExportRuleId, seeded.ExportRuleStamp.AddSeconds(5));
        await WriteRowAsync(seeded.Bob);

        bool cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(seen, seeded.ExportRuleStamp);

        var flags = await ReadMvoFlagsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.False);
            Assert.That(flags[seeded.Bob], Is.True);
            Assert.That(flags[seeded.Finance], Is.True, "the rule change keeps even a row nothing has written since");
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_TrackedObject_ReadsAsPersistedAndSavesAfterwardsAsync()
    {
        // The worker's context outlives a statement: a tracked object left holding the old flag and row version would
        // either write the flag back or fail its next save on a row version the clear has moved.
        var seeded = await SeedAsync();

        await using var context = NewContext();
        var tracked = await context.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == seeded.Bob);
        await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(
            new Dictionary<Guid, uint> { [seeded.Bob] = tracked.xmin }, seeded.ExportRuleStamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracked.ScopeReviewPending, Is.False);
            Assert.That(context.Entry(tracked).Property(m => m.ScopeReviewPending).IsModified, Is.False, "the value is the persisted one, not a pending change");
        }

        tracked.CachedDisplayName = "Bob Renamed";
        await context.SaveChangesAsync();
        var stored = await ReadMvoAsync(seeded.Bob);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.ScopeReviewPending, Is.False, "the save did not write the old flag back");
            Assert.That(stored.CachedDisplayName, Is.EqualTo("Bob Renamed"));
        }
    }

    #endregion

    #region An export rule saved while a clear statement runs (#2615)

    // The clear statements check "has an export rule changed since the run read them" and clear in one statement, on
    // that statement's snapshot. A rule saved and committed after the snapshot but before the statement commits is
    // invisible to the check, and its own flagging statement skips a row that is still flagged, so without a second
    // look the clear would lose the flag. The tests simulate that rule save with a trigger that touches the export
    // rule inside the clearing statement's own transaction: the rule's change commits together with the clear, after
    // the clear's check has run on its snapshot, which is the same ordering from the clear's point of view.

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_ExportRuleSavedWhileTheClearRuns_FlagsTheClearedObjectsAgainAndReturnsFalseAsync()
    {
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);
        await SetChangesAsync(seeded.Bob, ChangesJson("old"));
        var seen = await SeenAsync(seeded.Bob, seeded.Finance);

        bool cleared;
        await InstallRuleSavedDuringClearTriggerAsync();
        try
        {
            await using var context = NewContext();
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(seen, seeded.ExportRuleStamp);
        }
        finally
        {
            await RemoveRuleSavedDuringClearTriggerAsync();
        }

        var flags = await ReadMvoFlagsAsync();
        var bob = await ReadMvoAsync(seeded.Bob);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ExportRuleLastUpdatedAsync(seeded.ExportRuleId), Is.GreaterThan(seeded.ExportRuleStamp), "the simulated rule save happened");
            Assert.That(cleared, Is.False, "an export rule changed under the clear, so the drain must stop and let the next run review");
            Assert.That(flags[seeded.Bob], Is.True, "flagged again, so the next synchronisation reviews it against the rule as it now stands");
            Assert.That(flags[seeded.Finance], Is.True);
            Assert.That(bob.UnsynchronisedChangesJson, Is.Null, "the record was delivered by the review that ran; the flag alone brings the object back");
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_NoExportRuleSavedWhileTheClearRuns_LeavesTheObjectsClearedAsync()
    {
        // The same statement pair with nothing saved in between: the second look must not undo a genuine clear.
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);
        var seen = await SeenAsync(seeded.Bob, seeded.Finance);

        bool cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(seen, seeded.ExportRuleStamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.True);
            Assert.That((await ReadMvoFlagsAsync()).Values, Is.All.False);
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_ExportRuleSavedWhileTheClearRunsAndObjectTracked_ReadsAsFlaggedAndSavesAfterwardsAsync()
    {
        // The tracked instance must follow both writes: the clear's and the re-flag's. Left holding the cleared flag or
        // the clear's row version, it would write the flag back off or fail its next save on the version check.
        var seeded = await SeedAsync();

        await InstallRuleSavedDuringClearTriggerAsync();
        try
        {
            await using var context = NewContext();
            var tracked = await context.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == seeded.Bob);
            var cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(
                new Dictionary<Guid, uint> { [seeded.Bob] = tracked.xmin }, seeded.ExportRuleStamp);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(cleared, Is.False);
                Assert.That(tracked.ScopeReviewPending, Is.True);
                Assert.That(context.Entry(tracked).Property(m => m.ScopeReviewPending).IsModified, Is.False, "the value is the persisted one, not a pending change");
            }

            tracked.CachedDisplayName = "Bob Renamed";
            await context.SaveChangesAsync();
        }
        finally
        {
            await RemoveRuleSavedDuringClearTriggerAsync();
        }

        var stored = await ReadMvoAsync(seeded.Bob);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.ScopeReviewPending, Is.True, "the save did not write the cleared flag back");
            Assert.That(stored.CachedDisplayName, Is.EqualTo("Bob Renamed"));
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync_ExportRuleSavedWhileTheClearRuns_FlagsTheClearedObjectsAgainAndReturnsZeroAsync()
    {
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);
        await SetChangesAsync(seeded.Finance, ChangesJson("old"));

        int cleared;
        await InstallRuleSavedDuringClearTriggerAsync();
        try
        {
            await using var context = NewContext();
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync(
                [seeded.PersonTypeId], seeded.ExportRuleStamp);
        }
        finally
        {
            await RemoveRuleSavedDuringClearTriggerAsync();
        }

        var flags = await ReadMvoFlagsAsync();
        var finance = await ReadMvoAsync(seeded.Finance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ExportRuleLastUpdatedAsync(seeded.ExportRuleId), Is.GreaterThan(seeded.ExportRuleStamp), "the simulated rule save happened");
            Assert.That(cleared, Is.Zero, "the count left cleared: the rule saved under the clear may be the one that now covers Groups");
            Assert.That(flags[seeded.Finance], Is.True, "flagged again for review against the rules as they now stand");
            Assert.That(flags[seeded.Bob], Is.True, "a Person is covered, so this clear never touched him");
            Assert.That(finance.UnsynchronisedChangesJson, Is.Null);
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync_NoExportRuleSavedWhileTheClearRuns_LeavesTheObjectsClearedAsync()
    {
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);

        int cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync(
                [seeded.PersonTypeId], seeded.ExportRuleStamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That((await ReadMvoFlagsAsync())[seeded.Finance], Is.False);
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync_ExportRuleSavedWhileTheClearRunsAndObjectTracked_ReadsAsFlaggedAndSavesAfterwardsAsync()
    {
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);

        await InstallRuleSavedDuringClearTriggerAsync();
        try
        {
            await using var context = NewContext();
            var tracked = await context.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == seeded.Finance);
            await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync(
                [seeded.PersonTypeId], seeded.ExportRuleStamp);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tracked.ScopeReviewPending, Is.True);
                Assert.That(context.Entry(tracked).Property(m => m.ScopeReviewPending).IsModified, Is.False, "the value is the persisted one, not a pending change");
            }

            tracked.CachedDisplayName = "Finance Renamed";
            await context.SaveChangesAsync();
        }
        finally
        {
            await RemoveRuleSavedDuringClearTriggerAsync();
        }

        var stored = await ReadMvoAsync(seeded.Finance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.ScopeReviewPending, Is.True, "the save did not write the cleared flag back");
            Assert.That(stored.CachedDisplayName, Is.EqualTo("Finance Renamed"));
        }
    }

    #endregion

    #region The record of what changed outside synchronisation (#2615)

    private static string ChangesJson(string removedText)
    {
        var changes = new MetaverseObjectUnsynchronisedChanges();
        changes.Record([new MetaverseObjectAttributeValue { AttributeId = 1, StringValue = "new" }],
            [new MetaverseObjectAttributeValue { AttributeId = 1, StringValue = removedText }]);
        return changes.ToJson();
    }

    private async Task SetChangesAsync(Guid id, string json)
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(@"UPDATE ""MetaverseObjects"" SET ""UnsynchronisedChangesJson"" = {0}::jsonb WHERE ""Id"" = {1}", json, id);
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_ObjectWithARecordOfChanges_ClearsTheRecordWithTheFlagAsync()
    {
        var seeded = await SeedAsync();
        await SetChangesAsync(seeded.Bob, ChangesJson("old"));

        await using (var context = NewContext())
            await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(await SeenAsync(seeded.Bob), seeded.ExportRuleStamp);

        var bob = await ReadMvoAsync(seeded.Bob);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bob.ScopeReviewPending, Is.False);
            Assert.That(bob.UnsynchronisedChangesJson, Is.Null, "delivered with the review, so nothing is left to deliver");
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_RecordWrittenSinceTheObjectWasLoaded_KeepsTheFlagAndTheRecordAsync()
    {
        // The drain loads Bob with one record; a later edit (a second sign-in) adds to it. Clearing on the version the
        // drain read would erase a change it never delivered.
        var seeded = await SeedAsync();
        await SetChangesAsync(seeded.Bob, ChangesJson("first"));
        var seen = await SeenAsync(seeded.Bob);
        await SetChangesAsync(seeded.Bob, ChangesJson("second"));

        await using (var context = NewContext())
            await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(seen, seeded.ExportRuleStamp);

        var bob = await ReadMvoAsync(seeded.Bob);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bob.ScopeReviewPending, Is.True);
            Assert.That(MetaverseObjectUnsynchronisedChanges.FromJson(bob.UnsynchronisedChangesJson)?.Removals.Select(r => r.StringValue), Is.EqualTo(new[] { "second" }));
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_ExportRuleChanged_KeepsTheRecordAsync()
    {
        var seeded = await SeedAsync();
        await SetChangesAsync(seeded.Bob, ChangesJson("old"));
        await StampExportRuleAsync(seeded.ExportRuleId, seeded.ExportRuleStamp.AddSeconds(5));

        await using (var context = NewContext())
            await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(await SeenAsync(seeded.Bob), seeded.ExportRuleStamp);

        Assert.That((await ReadMvoAsync(seeded.Bob)).UnsynchronisedChangesJson, Is.Not.Null);
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync_ObjectWithARecordOfChanges_ClearsTheRecordAsync()
    {
        // No enabled export rule covers Groups, so a change recorded on one has nowhere to be delivered.
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);
        await SetChangesAsync(seeded.Finance, ChangesJson("old"));
        await SetChangesAsync(seeded.Bob, ChangesJson("old"));

        await using (var context = NewContext())
            await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync([seeded.PersonTypeId], seeded.ExportRuleStamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That((await ReadMvoAsync(seeded.Finance)).UnsynchronisedChangesJson, Is.Null);
            Assert.That((await ReadMvoAsync(seeded.Bob)).UnsynchronisedChangesJson, Is.Not.Null, "a Person is covered, so the review will deliver it");
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectAsync_RecordOfChanges_RoundTripsThroughTheJsonbColumnAsync()
    {
        var seeded = await SeedAsync();
        var json = ChangesJson("old");

        await using (var context = NewContext())
        {
            var tracked = await context.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == seeded.Alice);
            tracked.ScopeReviewPending = true;
            tracked.UnsynchronisedChangesJson = json;
            await new PostgresDataRepository(context).Metaverse.UpdateMetaverseObjectAsync(tracked);
        }

        var alice = await ReadMvoAsync(seeded.Alice);
        var stored = MetaverseObjectUnsynchronisedChanges.FromJson(alice.UnsynchronisedChangesJson);
        var expected = MetaverseObjectUnsynchronisedChanges.FromJson(json)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored?.AttributeIds, Is.EqualTo(expected.AttributeIds));
            Assert.That(stored?.Removals, Is.EqualTo(expected.Removals));
        }
    }

    #endregion

    #region Writes that must move the row version the drain's clear is guarded by (#2615)

    [Test]
    public async Task UpdateMetaverseObjectAsync_ObjectAlreadyFlagged_StillMovesItsRowVersionAsync()
    {
        // Sign-in flags the person it writes (MetaverseServer), but a person already flagged by an earlier write has a
        // flag that does not change. The save must still rewrite the row: if it did not, a drain that loaded the person
        // before the edit would find the row version it read unchanged and clear the flag, losing the edit's review.
        var seeded = await SeedAsync();
        var before = (await SeenAsync(seeded.Bob))[seeded.Bob];

        await using (var context = NewContext())
        {
            var tracked = await context.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == seeded.Bob);
            tracked.ScopeReviewPending = true;
            await new PostgresDataRepository(context).Metaverse.UpdateMetaverseObjectAsync(tracked);
        }

        Assert.That((await SeenAsync(seeded.Bob))[seeded.Bob], Is.Not.EqualTo(before));
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingAsync_ObjectEditedAfterTheDrainLoadedIt_KeepsTheFlagAsync()
    {
        // The race the guard exists for, end to end through the repository: the drain loads Bob (flagged, version V),
        // a sign-in then edits him and flags him again, and the drain clears the batch it loaded.
        var seeded = await SeedAsync();
        var seen = await SeenAsync(seeded.Bob);

        await using (var context = NewContext())
        {
            var tracked = await context.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == seeded.Bob);
            tracked.CachedDisplayName = "Bob Refreshed";
            tracked.ScopeReviewPending = true;
            await new PostgresDataRepository(context).Metaverse.UpdateMetaverseObjectAsync(tracked);
        }

        await using (var context = NewContext())
            await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingAsync(seen, seeded.ExportRuleStamp);

        Assert.That((await ReadMvoFlagsAsync())[seeded.Bob], Is.True, "the edit was never reviewed, so the flag stays for the next synchronisation");
    }

    #endregion

    #region Clearing the flag on types no enabled export rule covers (#2615)

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync_FlagsOnCoveredAndUncoveredTypes_ClearsOnlyTheUncoveredAsync()
    {
        // People are covered by the Directory export rule; Groups are not. A review of a Group can neither provision nor
        // deprovision anything, so its flag is cleared without loading the object.
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);

        int cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync(
                [seeded.PersonTypeId], seeded.ExportRuleStamp);

        var flags = await ReadMvoFlagsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(flags[seeded.Finance], Is.False, "no export rule covers Groups");
            Assert.That(flags[seeded.Bob], Is.True, "a Person is covered, so the drain still reviews him");
            Assert.That(flags[seeded.Alice], Is.False);
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync_NoTypeCovered_ClearsEveryFlagAsync()
    {
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);

        int cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync(
                [], seeded.ExportRuleStamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.EqualTo(2));
            Assert.That((await ReadMvoFlagsAsync()).Values, Is.All.False);
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync_ExportRuleUpdatedSinceTheRunReadThem_ClearsNothingAsync()
    {
        // The rule change is what flagged these objects for review against the rules as they now stand, and it may be
        // the rule that now covers their type.
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);
        await StampExportRuleAsync(seeded.ExportRuleId, seeded.ExportRuleStamp.AddSeconds(5));

        int cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync(
                [seeded.PersonTypeId], seeded.ExportRuleStamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.Zero);
            Assert.That((await ReadMvoFlagsAsync())[seeded.Finance], Is.True);
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync_ExportRuleCreatedSinceTheRunReadThem_ClearsNothingAsync()
    {
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);
        await using (var context = NewContext())
        {
            var system = await context.ConnectedSystems.SingleAsync();
            context.SyncRules.Add(new SyncRule
            {
                Name = "Group Export", Direction = SyncRuleDirection.Export, Enabled = true,
                Created = seeded.ExportRuleStamp.AddSeconds(5),
                ConnectedSystemId = system.Id,
                ConnectedSystemObjectTypeId = (await context.ConnectedSystemObjectTypes.SingleAsync()).Id,
                MetaverseObjectTypeId = seeded.GroupTypeId
            });
            await context.SaveChangesAsync();
        }

        int cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync(
                [seeded.PersonTypeId], seeded.ExportRuleStamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.Zero);
            Assert.That((await ReadMvoFlagsAsync())[seeded.Finance], Is.True, "the new rule covers Groups, and flagged them for review");
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync_RunReadNoExportRulesAndOneNowExists_ClearsNothingAsync()
    {
        var seeded = await SeedAsync();

        int cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync(
                [], exportRulesReadWatermark: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.Zero, "the run held no export rule at all, and there is one now");
            Assert.That((await ReadMvoFlagsAsync())[seeded.Bob], Is.True);
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync_NoExportRuleExists_ClearsEveryFlagAsync()
    {
        // A deployment with no export rule at all (an initial administrator and nothing provisioned): the run read none
        // and none has appeared, so nothing can need a review.
        var seeded = await SeedAsync();
        await using (var context = NewContext())
            await context.Database.ExecuteSqlRawAsync(@"DELETE FROM ""SyncRules""");

        int cleared;
        await using (var context = NewContext())
            cleared = await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync(
                [], exportRulesReadWatermark: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That((await ReadMvoFlagsAsync())[seeded.Bob], Is.False);
        }
    }

    [Test]
    public async Task ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync_TrackedObject_ReadsAsPersistedAndSavesAfterwardsAsync()
    {
        var seeded = await SeedAsync();
        await SetFlagAsync(seeded.Finance, true);

        await using var context = NewContext();
        var tracked = await context.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == seeded.Finance);
        await new PostgresDataRepository(context).Sync.ClearMetaverseObjectScopeReviewPendingForUncoveredTypesAsync(
            [seeded.PersonTypeId], seeded.ExportRuleStamp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracked.ScopeReviewPending, Is.False);
            Assert.That(context.Entry(tracked).Property(m => m.ScopeReviewPending).IsModified, Is.False, "the value is the persisted one, not a pending change");
        }

        tracked.CachedDisplayName = "Finance Renamed";
        await context.SaveChangesAsync();
        var stored = await ReadMvoAsync(seeded.Finance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.ScopeReviewPending, Is.False, "the save did not write the old flag back");
            Assert.That(stored.CachedDisplayName, Is.EqualTo("Finance Renamed"));
        }
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

    private sealed record Seeded(int PersonTypeId, int GroupTypeId, Guid Alice, Guid Bob, Guid Finance, int ExportRuleId, DateTime ExportRuleStamp, Guid FlaggedCso);

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

        return new Seeded(person.Id, group.Id, alice.Id, bob.Id, finance.Id, exportRule.Id, stamp, flaggedCso.Id);
    }

    private async Task StampExportRuleAsync(int syncRuleId, DateTime lastUpdated)
    {
        await using var context = NewContext();
        var rule = await context.SyncRules.AsTracking().SingleAsync(r => r.Id == syncRuleId);
        rule.LastUpdated = lastUpdated;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Simulates an export rule saved at the worst moment for a clear: a trigger that touches every Synchronisation
    /// Rule whenever a Metaverse Object's flag goes from set to clear, inside the clearing statement's own
    /// transaction. The clear's check ran on its snapshot before the trigger fired, so it sees no change; the rule's
    /// new <c>LastUpdated</c> commits with the clear, and only a look after the clear can see it.
    /// </summary>
    private async Task InstallRuleSavedDuringClearTriggerAsync()
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION test_rule_saved_during_clear() RETURNS trigger AS $$
            BEGIN
                UPDATE "SyncRules" SET "LastUpdated" = now();
                RETURN NEW;
            END
            $$ LANGUAGE plpgsql;
            """);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER test_rule_saved_during_clear AFTER UPDATE ON "MetaverseObjects"
            FOR EACH ROW WHEN (OLD."ScopeReviewPending" AND NOT NEW."ScopeReviewPending")
            EXECUTE FUNCTION test_rule_saved_during_clear();
            """);
    }

    private async Task RemoveRuleSavedDuringClearTriggerAsync()
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(@"DROP TRIGGER IF EXISTS test_rule_saved_during_clear ON ""MetaverseObjects""");
        await context.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS test_rule_saved_during_clear()");
    }

    private async Task<DateTime?> ExportRuleLastUpdatedAsync(int syncRuleId)
    {
        await using var read = NewContext();
        return await read.SyncRules.AsNoTracking().Where(r => r.Id == syncRuleId).Select(r => r.LastUpdated).SingleAsync();
    }

    private async Task<Dictionary<Guid, bool>> ReadMvoFlagsAsync()
    {
        await using var read = NewContext();
        return await read.MetaverseObjects.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.ScopeReviewPending);
    }

    private async Task<MetaverseObject> ReadMvoAsync(Guid id)
    {
        await using var read = NewContext();
        return await read.MetaverseObjects.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    /// <summary>The row version (xmin) of each given Metaverse Object, as the drain reads it when it loads them.</summary>
    private async Task<Dictionary<Guid, uint>> SeenAsync(params Guid[] ids)
    {
        await using var read = NewContext();
        return await read.MetaverseObjects.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.xmin);
    }

    private async Task SetFlagAsync(Guid id, bool flagged)
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(@"UPDATE ""MetaverseObjects"" SET ""ScopeReviewPending"" = {0} WHERE ""Id"" = {1}", flagged, id);
    }

    /// <summary>Any write to the row, which moves its row version whatever it changes.</summary>
    private async Task WriteRowAsync(Guid id)
    {
        await using var context = NewContext();
        await context.Database.ExecuteSqlRawAsync(@"UPDATE ""MetaverseObjects"" SET ""CachedDisplayName"" = 'written since' WHERE ""Id"" = {0}", id);
    }

    private static DateTime TruncateToMicroseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, value.Kind);

    #endregion
}

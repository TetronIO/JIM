// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Scim;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// The SCIM store's write path (#124) against real PostgreSQL: the change feed, the write lock that makes commit order
/// equal change-sequence order, uniqueness among live resources, tombstones and memberships, the journal, the tombstone
/// purge, and the store's removal with its Connected System.
/// </summary>
/// <remarks>
/// <para>
/// The change feed is the one new integrity-critical mechanism in the SCIM Service Provider: an import reads "every
/// change after watermark w", so a write that commits with a lower sequence than one already visible would be skipped
/// for good. <see cref="ConcurrentWriters_EveryReaderSnapshotIsComplete_NoChangeCommitsBehindAHorizonAsync"/> asserts
/// exactly that property under contention.
/// </para>
/// <para>
/// Real PostgreSQL and a no-tracking context, as JIM.Web (which serves the SCIM endpoints) runs. The in-memory provider
/// has no transactions, row locks or unique indexes, so it could not see any of this. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScimStoreDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL SCIM store tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    // ─── The change feed and the write lock ───

    [Test]
    public async Task BeginWriteAsync_ConnectedSystemDoesNotExist_ReturnsNullAndWritesNothingAsync()
    {
        await using var ctx = NewContext();
        var store = new PostgresDataRepository(ctx).ScimStore;

        var session = await store.BeginWriteAsync(int.MaxValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(session, Is.Null);
            Assert.That(await CountAsync("ScimStoreStates"), Is.Zero);
        }
    }

    [Test]
    public async Task BeginWriteAsync_SuccessiveCommits_AssignConsecutiveSequencesPerConnectedSystemAsync()
    {
        var first = await SeedSystemAsync("first");
        var second = await SeedSystemAsync("second");

        // Interleaved, so that each system's sequence is seen to ignore the other's writes.
        var first1 = await CommitEmptyWriteAsync(first);
        var first2 = await CommitEmptyWriteAsync(first);
        var second1 = await CommitEmptyWriteAsync(second);
        var first3 = await CommitEmptyWriteAsync(first);
        var second2 = await CommitEmptyWriteAsync(second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(new[] { first1, first2, first3 }, Is.EqualTo(new long[] { 1, 2, 3 }));
            Assert.That(new[] { second1, second2 }, Is.EqualTo(new long[] { 1, 2 }), "each Connected System has its own sequence");
            Assert.That((await GetStateAsync(first))!.LastChangeSequence, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task BeginWriteAsync_SessionDisposedWithoutCommit_LeavesNoTraceAndFreesTheSequenceAsync()
    {
        var system = await SeedSystemAsync("rollback");
        await CommitEmptyWriteAsync(system);

        await using (var ctx = NewContext())
        {
            await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system))!;
            Assert.That(await session.InsertResourceAsync(User("rolled-back")), Is.EqualTo(ScimStoreWriteOutcome.Written));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("ScimStoreResources"), Is.Zero);
            Assert.That(await CommitEmptyWriteAsync(system), Is.EqualTo(2), "a rolled-back write consumes no sequence");
        }
    }

    [Test]
    public async Task BeginWriteAsync_AnotherSessionHoldsTheLock_WaitsUntilItCommitsAsync()
    {
        var system = await SeedSystemAsync("lock");

        await using var holderContext = NewContext();
        var holder = (await new PostgresDataRepository(holderContext).ScimStore.BeginWriteAsync(system))!;

        await using var waiterContext = NewContext();
        var waiting = new PostgresDataRepository(waiterContext).ScimStore.BeginWriteAsync(system);

        await Task.Delay(500);
        Assert.That(waiting.IsCompleted, Is.False, "a second write must wait for the store's write lock");

        await holder.CommitAsync();
        await holder.DisposeAsync();
        await using var waiter = (await waiting)!;

        Assert.That(waiter.ChangeSequence, Is.EqualTo(holder.ChangeSequence + 1));
    }

    [Test]
    public async Task ConcurrentWriters_EveryReaderSnapshotIsComplete_NoChangeCommitsBehindAHorizonAsync()
    {
        const int writers = 8;
        const int writesPerWriter = 25;
        var system = await SeedSystemAsync("concurrent");
        var snapshots = new ConcurrentBag<(long Horizon, HashSet<Guid> Visible)>();
        using var stop = new CancellationTokenSource();

        // The reader does what an import does: in one snapshot, read the horizon and every change at or below it.
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
                var horizon = await ScalarAsync<long?>(connection, transaction,
                    "SELECT \"LastChangeSequence\" FROM \"ScimStoreStates\" WHERE \"ConnectedSystemId\" = @s", system) ?? 0;
                var visible = await ReadIdsAsync(connection, transaction,
                    "SELECT \"Id\" FROM \"ScimStoreResources\" WHERE \"ConnectedSystemId\" = @s AND \"ChangeSequence\" <= @h",
                    system, horizon);
                await transaction.CommitAsync();
                snapshots.Add((horizon, visible));
            }
        });

        var random = new Random(124);
        var delays = Enumerable.Range(0, writers * writesPerWriter).Select(_ => random.Next(0, 4)).ToArray();
        var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < writesPerWriter; i++)
            {
                await using var ctx = NewContext();
                await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system))!;
                await session.InsertResourceAsync(User($"user-{w}-{i}"));
                // Work inside the lock, so that writers genuinely queue behind each other.
                await Task.Delay(delays[w * writesPerWriter + i]);
                await session.CommitAsync();
            }
        })).ToArray();

        await Task.WhenAll(writerTasks);
        await stop.CancelAsync();
        await reader;

        var final = await ReadAllAsync(system);
        var violations = snapshots
            .Select(s => (s.Horizon, Missed: final.Where(r => r.ChangeSequence <= s.Horizon).Select(r => r.Id).Except(s.Visible).ToList()))
            .Where(s => s.Missed.Count > 0)
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshots, Has.Count.GreaterThan(10), "the reader must have sampled while the writers ran");
            Assert.That(violations, Is.Empty,
                "a change committed with a sequence at or below a horizon a reader had already seen; an import would skip it");
            Assert.That(final.Select(r => r.ChangeSequence).OrderBy(s => s),
                Is.EqualTo(Enumerable.Range(1, writers * writesPerWriter).Select(i => (long)i)),
                "sequences are dense and unique");
        }
    }

    // ─── Resources ───

    [Test]
    public async Task InsertResourceAsync_FullyPopulatedResource_PersistsEveryColumnAsync()
    {
        var system = await SeedSystemAsync("insert");
        var created = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
        var resource = User("ada", externalId: "E-1", displayName: "Ada Lovelace", payload: """{"userName":"ada","active":true}""");
        resource.Created = created;

        long sequence;
        DateTime timestamp;
        await using (var ctx = NewContext())
        {
            await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system))!;
            Assert.That(await session.InsertResourceAsync(resource), Is.EqualTo(ScimStoreWriteOutcome.Written));
            sequence = session.ChangeSequence;
            timestamp = session.Timestamp;
            await session.CommitAsync();
        }

        var stored = (await ReadAllAsync(system)).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Id, Is.EqualTo(resource.Id));
            Assert.That(stored.ConnectedSystemId, Is.EqualTo(system));
            Assert.That(stored.ResourceType, Is.EqualTo(ScimStoreResourceType.User));
            Assert.That(stored.UserName, Is.EqualTo("ada"));
            Assert.That(stored.ExternalId, Is.EqualTo("E-1"));
            Assert.That(stored.DisplayName, Is.EqualTo("Ada Lovelace"));
            Assert.That(stored.Version, Is.EqualTo(1));
            Assert.That(stored.ChangeSequence, Is.EqualTo(sequence));
            Assert.That(stored.Created, Is.EqualTo(created));
            Assert.That(stored.LastModified, Is.EqualTo(timestamp).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That(stored.DeletedAt, Is.Null);
            Assert.That(stored.Payload, Is.EqualTo("""{"active": true, "userName": "ada"}"""), "stored as jsonb");
        }
    }

    [Test]
    public async Task InsertResourceAsync_UserNameDiffersOnlyInCase_ReportsUserNameTakenAsync()
    {
        var system = await SeedSystemAsync("case");
        await InsertCommittedAsync(system, User("Ada.Lovelace"));

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system))!;

        Assert.That(await session.InsertResourceAsync(User("ada.lovelace")), Is.EqualTo(ScimStoreWriteOutcome.UserNameTaken));
    }

    [Test]
    public async Task InsertResourceAsync_ExternalIdInUse_ReportsExternalIdTakenOnlyWithinTheSameTypeAndSystemAsync()
    {
        var system = await SeedSystemAsync("external");
        var other = await SeedSystemAsync("external-other");
        await InsertCommittedAsync(system, User("one", externalId: "X-1"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await TryInsertAsync(system, User("two", externalId: "X-1")), Is.EqualTo(ScimStoreWriteOutcome.ExternalIdTaken));
            Assert.That(await TryInsertAsync(system, User("three", externalId: "x-1")), Is.EqualTo(ScimStoreWriteOutcome.Written),
                "externalId is case-exact");
            Assert.That(await TryInsertAsync(system, Group("Admins", externalId: "X-1")), Is.EqualTo(ScimStoreWriteOutcome.Written),
                "uniqueness is per resource type");
            Assert.That(await TryInsertAsync(other, User("one", externalId: "X-1")), Is.EqualTo(ScimStoreWriteOutcome.Written),
                "uniqueness is per Connected System");
        }
    }

    [Test]
    public async Task UpdateResourceAsync_LiveResource_ReplacesItAndRaisesTheVersionAsync()
    {
        var system = await SeedSystemAsync("update");
        var resource = User("ada", externalId: "E-1", payload: """{"userName":"ada"}""");
        await InsertCommittedAsync(system, resource);

        long sequence;
        await using (var ctx = NewContext())
        {
            await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system))!;
            resource.UserName = "ada.l";
            resource.ExternalId = null;
            resource.DisplayName = "Ada";
            resource.Payload = """{"userName":"ada.l"}""";
            var (outcome, version) = await session.UpdateResourceAsync(resource);
            Assert.That((outcome, version), Is.EqualTo((ScimStoreWriteOutcome.Written, 2L)));
            sequence = session.ChangeSequence;
            await session.CommitAsync();
        }

        var stored = (await ReadAllAsync(system)).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.UserName, Is.EqualTo("ada.l"));
            Assert.That(stored.ExternalId, Is.Null);
            Assert.That(stored.DisplayName, Is.EqualTo("Ada"));
            Assert.That(stored.Payload, Is.EqualTo("""{"userName": "ada.l"}"""));
            Assert.That(stored.Version, Is.EqualTo(2));
            Assert.That(stored.ChangeSequence, Is.EqualTo(sequence));
        }
    }

    [Test]
    public async Task UpdateResourceAsync_TakesAnotherResourcesUserName_ReportsUserNameTakenAsync()
    {
        var system = await SeedSystemAsync("update-clash");
        await InsertCommittedAsync(system, User("ada"));
        var grace = User("grace");
        await InsertCommittedAsync(system, grace);

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system))!;
        grace.UserName = "ADA";

        Assert.That((await session.UpdateResourceAsync(grace)).Outcome, Is.EqualTo(ScimStoreWriteOutcome.UserNameTaken));
    }

    [Test]
    public async Task GetLiveResourceTypesAsync_MixedIds_ReturnsOnlyThisSystemsLiveResourcesAsync()
    {
        var system = await SeedSystemAsync("types");
        var other = await SeedSystemAsync("types-other");
        var user = User("ada");
        var group = Group("Admins");
        var deleted = User("gone");
        var elsewhere = User("ada");
        await InsertCommittedAsync(system, user, group, deleted);
        await InsertCommittedAsync(other, elsewhere);
        await TombstoneCommittedAsync(system, deleted);

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system))!;
        var types = await session.GetLiveResourceTypesAsync([user.Id, group.Id, deleted.Id, elsewhere.Id, Guid.NewGuid()]);

        Assert.That(types, Is.EquivalentTo(new Dictionary<Guid, ScimStoreResourceType>
        {
            [user.Id] = ScimStoreResourceType.User,
            [group.Id] = ScimStoreResourceType.Group
        }));
    }

    // ─── Tombstones and memberships ───

    [Test]
    public async Task TombstoneResourceAsync_MemberOfGroups_ClearsItRemovesMembershipsAndVersionsTheGroupsAsync()
    {
        var system = await SeedSystemAsync("tombstone");
        var ada = User("ada", externalId: "E-1", displayName: "Ada", payload: """{"userName":"ada"}""");
        var grace = User("grace");
        var admins = Group("Admins");
        var staff = Group("Staff");
        var unrelated = Group("Unrelated");
        await InsertCommittedAsync(system, ada, grace, admins, staff, unrelated);
        await WriteCommittedAsync(system, async s =>
        {
            await s.AddGroupMembersAsync(admins.Id, [ada.Id, grace.Id]);
            await s.AddGroupMembersAsync(staff.Id, [ada.Id]);
        });

        IReadOnlyList<Guid>? affected = null;
        long sequence = 0;
        await WriteCommittedAsync(system, async s =>
        {
            affected = await s.TombstoneResourceAsync(ScimStoreResourceType.User, ada.Id);
            sequence = s.ChangeSequence;
        });

        var rows = (await ReadAllAsync(system)).ToDictionary(r => r.Id);
        var tombstone = rows[ada.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(affected, Is.EquivalentTo(new[] { admins.Id, staff.Id }));
            Assert.That(tombstone.DeletedAt, Is.Not.Null);
            Assert.That(tombstone.UserName, Is.Null);
            Assert.That(tombstone.ExternalId, Is.Null);
            Assert.That(tombstone.DisplayName, Is.Null);
            Assert.That(tombstone.Payload, Is.Null, "a tombstone keeps nothing identifying");
            Assert.That(tombstone.Version, Is.EqualTo(2));
            Assert.That(tombstone.ChangeSequence, Is.EqualTo(sequence));
            Assert.That(rows[admins.Id].Version, Is.EqualTo(3), "versioned by the membership write and again by the deletion");
            Assert.That(rows[admins.Id].ChangeSequence, Is.EqualTo(sequence));
            Assert.That(rows[staff.Id].ChangeSequence, Is.EqualTo(sequence));
            Assert.That(rows[unrelated.Id].Version, Is.EqualTo(1));
            Assert.That(await MembersAsync(admins.Id), Is.EquivalentTo(new[] { grace.Id }));
            Assert.That(await MembersAsync(staff.Id), Is.Empty);
        }
    }

    [Test]
    public async Task TombstoneResourceAsync_Group_RemovesItsOwnMembershipsAsync()
    {
        var system = await SeedSystemAsync("tombstone-group");
        var ada = User("ada");
        var admins = Group("Admins");
        await InsertCommittedAsync(system, ada, admins);
        await WriteCommittedAsync(system, async s => await s.AddGroupMembersAsync(admins.Id, [ada.Id]));

        await WriteCommittedAsync(system, async s => await s.TombstoneResourceAsync(ScimStoreResourceType.Group, admins.Id));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("ScimStoreGroupMembers"), Is.Zero);
            Assert.That((await ReadAllAsync(system)).Single(r => r.Id == ada.Id).Version, Is.EqualTo(1),
                "a member is not changed by its Group's deletion");
        }
    }

    [Test]
    public async Task TombstoneResourceAsync_ResourceNotLive_ReturnsNullAsync()
    {
        var system = await SeedSystemAsync("tombstone-missing");
        var ada = User("ada");
        await InsertCommittedAsync(system, ada);
        await TombstoneCommittedAsync(system, ada);

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await session.TombstoneResourceAsync(ScimStoreResourceType.User, ada.Id), Is.Null, "already a tombstone");
            Assert.That(await session.TombstoneResourceAsync(ScimStoreResourceType.Group, ada.Id), Is.Null, "wrong type");
            Assert.That(await session.TombstoneResourceAsync(ScimStoreResourceType.User, Guid.NewGuid()), Is.Null);
        }
    }

    [Test]
    public async Task TombstoneResourceAsync_FreesItsIdentifiersForReuseAsync()
    {
        var system = await SeedSystemAsync("reuse");
        var ada = User("ada", externalId: "E-1");
        await InsertCommittedAsync(system, ada);
        await TombstoneCommittedAsync(system, ada);

        Assert.That(await TryInsertAsync(system, User("ADA", externalId: "E-1")), Is.EqualTo(ScimStoreWriteOutcome.Written));
    }

    [Test]
    public async Task GroupMembership_SeveralOperationsInOneWrite_RaiseTheVersionOnceAsync()
    {
        var system = await SeedSystemAsync("members");
        var ada = User("ada");
        var grace = User("grace");
        var alan = User("alan");
        var admins = Group("Admins");
        await InsertCommittedAsync(system, ada, grace, alan, admins);

        var versions = new List<long>();
        await WriteCommittedAsync(system, async s =>
        {
            versions.Add(await s.AddGroupMembersAsync(admins.Id, [ada.Id, grace.Id]));
            versions.Add(await s.AddGroupMembersAsync(admins.Id, [ada.Id]));
            versions.Add(await s.RemoveGroupMembersAsync(admins.Id, [grace.Id, alan.Id]));
            admins.DisplayName = "Administrators";
            versions.Add((await s.UpdateResourceAsync(admins)).Version);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(versions, Is.All.EqualTo(2), "one write is one version");
            Assert.That(await MembersAsync(admins.Id), Is.EquivalentTo(new[] { ada.Id }), "adding a member twice is harmless");
        }

        await WriteCommittedAsync(system, async s => Assert.That(
            await s.ReplaceGroupMembersAsync(admins.Id, [grace.Id, alan.Id]), Is.EqualTo(3)));

        Assert.That(await MembersAsync(admins.Id), Is.EquivalentTo(new[] { grace.Id, alan.Id }));
    }

    [Test]
    public async Task GroupMembership_GroupNotLive_ThrowsAsync()
    {
        var system = await SeedSystemAsync("members-missing");

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system))!;

        Assert.That(async () => await session.AddGroupMembersAsync(Guid.NewGuid(), [Guid.NewGuid()]),
            Throws.InvalidOperationException);
    }

    // ─── The journal ───

    [Test]
    public async Task AppendJournalEntriesAsync_StampsTheWriteAndKeepsTheKeysNameAfterItIsDeletedAsync()
    {
        var system = await SeedSystemAsync("journal");
        var key = await SeedApiKeyAsync("Entra provisioning");
        var resourceId = Guid.NewGuid();

        long sequence = 0;
        DateTime timestamp = default;
        await WriteCommittedAsync(system, async s =>
        {
            sequence = s.ChangeSequence;
            timestamp = s.Timestamp;
            await s.AppendJournalEntriesAsync(
            [
                new ScimStoreJournalEntry
                {
                    ResourceId = resourceId,
                    ResourceType = ScimStoreResourceType.User,
                    Operation = ScimStoreOperation.Patched,
                    ChangedAttributes = ["name.givenName", "emails"],
                    ApiKeyId = key.Id,
                    ApiKeyName = key.Name,
                    ClientAddress = "192.0.2.10"
                }
            ]);
        });

        await using (var ctx = NewContext())
        {
            ctx.ApiKeys.Remove(await ctx.ApiKeys.AsTracking().SingleAsync(k => k.Id == key.Id));
            await ctx.SaveChangesAsync();
        }

        await using var read = NewContext();
        var entry = await read.ScimStoreJournalEntries.SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.ConnectedSystemId, Is.EqualTo(system));
            Assert.That(entry.ChangeSequence, Is.EqualTo(sequence));
            Assert.That(entry.ResourceId, Is.EqualTo(resourceId));
            Assert.That(entry.ResourceType, Is.EqualTo(ScimStoreResourceType.User));
            Assert.That(entry.Operation, Is.EqualTo(ScimStoreOperation.Patched));
            Assert.That(entry.ChangedAttributes, Is.EqualTo(new[] { "name.givenName", "emails" }));
            Assert.That(entry.ApiKeyId, Is.Null, "the deleted key's reference is cleared");
            Assert.That(entry.ApiKeyName, Is.EqualTo("Entra provisioning"), "but the entry still says who made the request");
            Assert.That(entry.ClientAddress, Is.EqualTo("192.0.2.10"));
            Assert.That(entry.Occurred, Is.EqualTo(timestamp).Within(TimeSpan.FromMilliseconds(1)));
        }
    }

    [Test]
    public async Task DeleteJournalEntriesOlderThanAsync_RemovesOnlyOlderEntriesAsync()
    {
        var system = await SeedSystemAsync("journal-retention");
        await WriteCommittedAsync(system, s => s.AppendJournalEntriesAsync([Entry(), Entry(), Entry(), Entry()]));
        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE \"ScimStoreJournalEntries\" SET \"Occurred\" = now() - (\"Id\" * interval '100 days') WHERE \"Id\" <= 3");

        await using var context = NewContext();
        var store = new PostgresDataRepository(context).ScimStore;
        var removed = await store.DeleteJournalEntriesOlderThanAsync(DateTime.UtcNow.AddDays(-30), maxRecords: 2);

        await using var read = NewContext();
        var remaining = await read.ScimStoreJournalEntries.Select(e => e.Id).OrderBy(id => id).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(2), "a pass is bounded by the batch size");
            Assert.That(remaining, Is.EqualTo(new long[] { 1, 4 }), "the oldest go first, and the recent one stays");
        }
    }

    // ─── Purging tombstones ───

    [Test]
    public async Task PurgeTombstonesAsync_RemovesOnlyImportedAndOldTombstonesAndRaisesTheFloorAsync()
    {
        var system = await SeedSystemAsync("purge");
        var live = User("live");
        var oldImported = User("old-imported");
        var oldNotImported = User("old-not-imported");
        var recentImported = User("recent-imported");
        await InsertCommittedAsync(system, live, oldImported, oldNotImported, recentImported);
        var oldImportedSequence = await TombstoneCommittedAsync(system, oldImported);
        var recentSequence = await TombstoneCommittedAsync(system, recentImported);
        var notImportedSequence = await TombstoneCommittedAsync(system, oldNotImported);
        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE \"ScimStoreResources\" SET \"DeletedAt\" = now() - interval '30 days' WHERE \"Id\" = ANY({0})",
                new[] { oldImported.Id, oldNotImported.Id });

        // An import has read up to (and including) the recent tombstone, but not the last one.
        await using var context = NewContext();
        var store = new PostgresDataRepository(context).ScimStore;
        var purged = await store.PurgeTombstonesAsync(system, importedUpToChangeSequence: notImportedSequence - 1,
            deletedBefore: DateTime.UtcNow.AddDays(-7));

        var remaining = (await ReadAllAsync(system)).Select(r => r.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recentSequence, Is.LessThan(notImportedSequence));
            Assert.That(purged, Is.EqualTo(1));
            Assert.That(remaining, Is.EquivalentTo(new[] { live.Id, oldNotImported.Id, recentImported.Id }));
            Assert.That((await store.GetStateAsync(system))!.TombstonePurgeFloor, Is.EqualTo(oldImportedSequence));
        }

        Assert.That(await store.PurgeTombstonesAsync(system, 0, DateTime.UtcNow), Is.Zero);
        Assert.That((await store.GetStateAsync(system))!.TombstonePurgeFloor, Is.EqualTo(oldImportedSequence),
            "the floor never falls");
    }

    [Test]
    public async Task GetStateAsync_BeforeTheFirstWrite_ReturnsNullAsync()
    {
        var system = await SeedSystemAsync("no-state");

        Assert.That(await GetStateAsync(system), Is.Null);
    }

    // ─── Lifecycle ───

    [Test]
    public async Task DeleteConnectedSystemAsync_WithAStore_RemovesTheStoreAndLeavesOtherSystemsAloneAsync()
    {
        var system = await SeedSystemAsync("deleted");
        var other = await SeedSystemAsync("kept");
        var ada = User("ada");
        var admins = Group("Admins");
        await InsertCommittedAsync(system, ada, admins);
        await WriteCommittedAsync(system, async s =>
        {
            await s.AddGroupMembersAsync(admins.Id, [ada.Id]);
            await s.AppendJournalEntriesAsync([Entry()]);
        });
        await InsertCommittedAsync(other, User("grace"));

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            Assert.That(async () => await repository.ConnectedSystems.DeleteConnectedSystemAsync(system), Throws.Nothing);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadAllAsync(system), Is.Empty);
            Assert.That(await CountAsync("ScimStoreGroupMembers"), Is.Zero);
            Assert.That(await CountAsync("ScimStoreJournalEntries"), Is.Zero);
            Assert.That(await GetStateAsync(system), Is.Null);
            Assert.That(await ReadAllAsync(other), Has.Count.EqualTo(1));
        }
    }

    // ─── Helpers ───

    private static ScimStoreResource User(string userName, string? externalId = null, string? displayName = null, string payload = "{}") => new()
    {
        Id = Guid.NewGuid(),
        ResourceType = ScimStoreResourceType.User,
        UserName = userName,
        ExternalId = externalId,
        DisplayName = displayName,
        Created = DateTime.UtcNow,
        Payload = payload
    };

    private static ScimStoreResource Group(string displayName, string? externalId = null) => new()
    {
        Id = Guid.NewGuid(),
        ResourceType = ScimStoreResourceType.Group,
        ExternalId = externalId,
        DisplayName = displayName,
        Created = DateTime.UtcNow,
        Payload = "{}"
    };

    private static ScimStoreJournalEntry Entry() => new()
    {
        ResourceId = Guid.NewGuid(),
        ResourceType = ScimStoreResourceType.User,
        Operation = ScimStoreOperation.Created,
        ChangedAttributes = ["userName"]
    };

    private async Task<int> SeedSystemAsync(string suffix)
    {
        await using var ctx = NewContext();
        var definition = new ConnectorDefinition { Name = $"scim-store-def-{suffix}" };
        ctx.ConnectorDefinitions.Add(definition);
        await ctx.SaveChangesAsync();

        var system = new ConnectedSystem { Name = $"scim-store-system-{suffix}", ConnectorDefinitionId = definition.Id };
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();
        return system.Id;
    }

    private async Task<ApiKey> SeedApiKeyAsync(string name)
    {
        await using var ctx = NewContext();
        var key = new ApiKey { Id = Guid.NewGuid(), Name = name, KeyHash = "hash", KeyPrefix = "jim_ak_test" };
        ctx.ApiKeys.Add(key);
        await ctx.SaveChangesAsync();
        return key;
    }

    private async Task<long> WriteCommittedAsync(int system, Func<IScimStoreWriteSession, Task> write)
    {
        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system))!;
        await write(session);
        await session.CommitAsync();
        return session.ChangeSequence;
    }

    private Task<long> CommitEmptyWriteAsync(int system) => WriteCommittedAsync(system, _ => Task.CompletedTask);

    private Task<long> InsertCommittedAsync(int system, params ScimStoreResource[] resources) =>
        WriteCommittedAsync(system, async s =>
        {
            foreach (var resource in resources)
                Assert.That(await s.InsertResourceAsync(resource), Is.EqualTo(ScimStoreWriteOutcome.Written));
        });

    private Task<long> TombstoneCommittedAsync(int system, ScimStoreResource resource) =>
        WriteCommittedAsync(system, async s => Assert.That(await s.TombstoneResourceAsync(resource.ResourceType, resource.Id), Is.Not.Null));

    private async Task<ScimStoreWriteOutcome> TryInsertAsync(int system, ScimStoreResource resource)
    {
        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system))!;
        var outcome = await session.InsertResourceAsync(resource);
        if (outcome == ScimStoreWriteOutcome.Written)
            await session.CommitAsync();
        return outcome;
    }

    private async Task<ScimStoreState?> GetStateAsync(int system)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).ScimStore.GetStateAsync(system);
    }

    private async Task<List<ScimStoreResource>> ReadAllAsync(int system)
    {
        await using var ctx = NewContext();
        return await ctx.ScimStoreResources.Where(r => r.ConnectedSystemId == system).ToListAsync();
    }

    private async Task<List<Guid>> MembersAsync(Guid groupId)
    {
        await using var ctx = NewContext();
        return await ctx.ScimStoreGroupMembers.Where(m => m.GroupId == groupId).Select(m => m.MemberId).ToListAsync();
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, int system)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("s", system);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task<HashSet<Guid>> ReadIdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, int system, long horizon)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("s", system);
        command.Parameters.AddWithValue("h", horizon);
        var ids = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));
        return ids;
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
/// The SCIM store (#124) against real PostgreSQL: the change feed, the write lock that makes commit order equal
/// change-sequence order, resource types and schemas held as data, uniqueness and references as rows, tombstones, the
/// journal, the tombstone purge, and the store's removal with its Connected System.
/// </summary>
/// <remarks>
/// <para>
/// The change feed is the one new integrity-critical mechanism in the SCIM Service Provider: an import reads "every
/// change after watermark w", so a write that commits with a lower sequence than one already visible would be skipped
/// for good. <see cref="ConcurrentWriters_EveryReaderSnapshotIsComplete_NoChangeCommitsBehindAHorizonAsync"/> asserts
/// exactly that property under contention.
/// </para>
/// <para>
/// Nothing in the store names User or Group (D18), so several tests use a custom resource type to prove a new type or a
/// new unique attribute needs no change to the store's shape.
/// </para>
/// <para>
/// Real PostgreSQL and a no-tracking context, as JIM.Web (which serves the SCIM endpoints) runs. The in-memory provider
/// has no transactions, row locks or unique keys, so it could not see any of this. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScimStoreDatabaseTests
{
    private const string UserName = "userName";
    private const string ExternalId = "externalId";
    private const string Members = "members";

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
            Assert.That((await GetStateAsync(first.Id))!.LastChangeSequence, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task BeginWriteAsync_SessionDisposedWithoutCommit_LeavesNoTraceAndFreesTheSequenceAsync()
    {
        var system = await SeedSystemAsync("rollback");
        await CommitEmptyWriteAsync(system);

        await using (var ctx = NewContext())
        {
            await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
            var (resource, unique) = User(system, "rolled-back");
            Assert.That((await session.InsertResourceAsync(resource, unique)).Outcome, Is.EqualTo(ScimStoreWriteOutcome.Written));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("ScimStoreResources"), Is.Zero);
            Assert.That(await CountAsync("ScimStoreUniqueValues"), Is.Zero);
            Assert.That(await CommitEmptyWriteAsync(system), Is.EqualTo(2), "a rolled-back write consumes no sequence");
        }
    }

    [Test]
    public async Task BeginWriteAsync_AnotherSessionHoldsTheLock_WaitsUntilItCommitsAsync()
    {
        var system = await SeedSystemAsync("lock");

        await using var holderContext = NewContext();
        var holder = (await new PostgresDataRepository(holderContext).ScimStore.BeginWriteAsync(system.Id))!;

        await using var waiterContext = NewContext();
        var waiting = new PostgresDataRepository(waiterContext).ScimStore.BeginWriteAsync(system.Id);

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
                    "SELECT \"LastChangeSequence\" FROM \"ScimStoreStates\" WHERE \"ConnectedSystemId\" = @s", system.Id) ?? 0;
                var visible = await ReadIdsAsync(connection, transaction,
                    "SELECT \"Id\" FROM \"ScimStoreResources\" WHERE \"ConnectedSystemId\" = @s AND \"ChangeSequence\" <= @h",
                    system.Id, horizon);
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
                await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
                var (resource, unique) = User(system, $"user-{w}-{i}");
                await session.InsertResourceAsync(resource, unique);
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

    // ─── Resource types and schemas as data ───

    [Test]
    public async Task AddDefinitionsAsync_ResourceTypesAndSchemas_RoundTripPerConnectedSystemAsync()
    {
        var system = await SeedSystemAsync("definitions");
        var other = await SeedSystemAsync("definitions-other");

        await using var ctx = NewContext();
        var store = new PostgresDataRepository(ctx).ScimStore;
        var types = await store.GetResourceTypesAsync(system.Id);
        var schemas = await store.GetSchemasAsync(system.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(types.Select(t => (t.Name, t.Endpoint)), Is.EquivalentTo(new[] { ("User", "/Users"), ("Group", "/Groups") }));
            Assert.That(types.Single(t => t.Name == "User").Definition, Does.Contain("\"endpoint\": \"/Users\""), "stored as jsonb");
            Assert.That(schemas.Select(s => s.Urn), Is.EquivalentTo(new[] { UserSchemaUrn, GroupSchemaUrn }));
            Assert.That(types.Select(t => t.Id).Intersect([other.UserTypeId, other.GroupTypeId]), Is.Empty, "each Connected System has its own");
        }
    }

    [Test]
    public async Task AddDefinitionsAsync_NameAlreadyDefined_AddsNothingAsync()
    {
        var system = await SeedSystemAsync("definitions-clash");

        await using (var ctx = NewContext())
        {
            var store = new PostgresDataRepository(ctx).ScimStore;
            Assert.That(async () => await store.AddDefinitionsAsync(system.Id,
                [Schema("urn:example:Device")],
                [ResourceType("Device", "/Devices", "urn:example:Device"), ResourceType("User", "/People", UserSchemaUrn)]),
                Throws.InstanceOf<DbUpdateException>());
        }

        await using var read = NewContext();
        var store2 = new PostgresDataRepository(read).ScimStore;
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await store2.GetResourceTypesAsync(system.Id)).Select(t => t.Name), Is.EquivalentTo(new[] { "User", "Group" }));
            Assert.That((await store2.GetSchemasAsync(system.Id)).Select(s => s.Urn), Has.No.Member("urn:example:Device"),
                "definitions are added together or not at all");
        }
    }

    [Test]
    public async Task CustomResourceType_DefinedAsData_IsWrittenWithItsOwnUniquenessRuleAsync()
    {
        var system = await SeedSystemAsync("custom-type");
        var deviceTypeId = await AddDeviceTypeAsync(system);

        var laptop = Resource(system.Id, deviceTypeId, """{"serialNumber":"SN-1"}""");
        var clash = Resource(system.Id, deviceTypeId, """{"serialNumber":"sn-1"}""");
        await InsertCommittedAsync(system, (laptop, [Unique("serialNumber", "SN-1")]));

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
        var result = await session.InsertResourceAsync(clash, [Unique("serialNumber", "SN-1")]);
        var types = await session.GetLiveResourceTypeIdsAsync([laptop.Id]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(ScimStoreWriteOutcome.UniquenessConflict));
            Assert.That(result.ConflictingAttribute, Is.EqualTo("serialNumber"));
            Assert.That(types[laptop.Id], Is.EqualTo(deviceTypeId));
        }
    }

    [Test]
    public async Task InsertResourceAsync_AnotherConnectedSystemsResourceType_IsRefusedAsync()
    {
        var system = await SeedSystemAsync("foreign-type");
        var other = await SeedSystemAsync("foreign-type-other");

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
        var resource = Resource(system.Id, other.UserTypeId, "{}");

        Assert.That(async () => await session.InsertResourceAsync(resource, []),
            Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.ForeignKeyViolation));
    }

    [Test]
    public async Task DeletingAResourceTypeThatHasResources_IsRefusedAsync()
    {
        var system = await SeedSystemAsync("type-in-use");
        await InsertCommittedAsync(system, User(system, "ada"));

        await using var ctx = NewContext();
        Assert.That(async () => await ctx.ScimStoreResourceTypes.Where(t => t.Id == system.UserTypeId).ExecuteDeleteAsync(),
            Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.ForeignKeyViolation));
    }

    // ─── Resources and uniqueness ───

    [Test]
    public async Task InsertResourceAsync_FullyPopulatedResource_PersistsEveryColumnAndItsUniqueValuesAsync()
    {
        var system = await SeedSystemAsync("insert");
        var created = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
        var resource = Resource(system.Id, system.UserTypeId, """{"userName":"Ada","active":true}""");
        resource.Created = created;

        long sequence;
        DateTime timestamp;
        await using (var ctx = NewContext())
        {
            await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
            var result = await session.InsertResourceAsync(resource, [Unique(UserName, "ada"), Unique(ExternalId, "E-1")]);
            Assert.That((result.Outcome, result.Version), Is.EqualTo((ScimStoreWriteOutcome.Written, 1L)));
            sequence = session.ChangeSequence;
            timestamp = session.Timestamp;
            await session.CommitAsync();
        }

        var stored = (await ReadAllAsync(system)).Single();
        var unique = await UniqueValuesAsync(resource.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Id, Is.EqualTo(resource.Id));
            Assert.That(stored.ConnectedSystemId, Is.EqualTo(system.Id));
            Assert.That(stored.ResourceTypeId, Is.EqualTo(system.UserTypeId));
            Assert.That(stored.Version, Is.EqualTo(1));
            Assert.That(stored.ChangeSequence, Is.EqualTo(sequence));
            Assert.That(stored.Created, Is.EqualTo(created));
            Assert.That(stored.LastModified, Is.EqualTo(timestamp).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That(stored.DeletedAt, Is.Null);
            Assert.That(stored.Payload, Is.EqualTo("""{"active": true, "userName": "Ada"}"""), "stored as jsonb");
            Assert.That(unique.Select(u => (u.ConnectedSystemId, u.ResourceTypeId, u.Attribute, u.Value)), Is.EquivalentTo(new[]
            {
                (system.Id, system.UserTypeId, UserName, "ada"),
                (system.Id, system.UserTypeId, ExternalId, "E-1")
            }));
            Assert.That(unique.Single(u => u.Attribute == UserName).ValueHash, Is.EqualTo(SHA256.HashData(Encoding.UTF8.GetBytes("ada"))));
        }
    }

    [Test]
    public async Task InsertResourceAsync_ValueAlreadyHeld_ReportsTheAttributeAndLeavesTheSessionUsableAsync()
    {
        var system = await SeedSystemAsync("conflict");
        await InsertCommittedAsync(system, User(system, "ada", externalId: "E-1"));

        await using (var ctx = NewContext())
        {
            await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
            var (clash, clashUnique) = User(system, "grace", externalId: "E-1");
            var result = await session.InsertResourceAsync(clash, clashUnique);
            Assert.That((result.Outcome, result.ConflictingAttribute), Is.EqualTo((ScimStoreWriteOutcome.UniquenessConflict, ExternalId)));

            var (other, otherUnique) = User(system, "alan");
            Assert.That((await session.InsertResourceAsync(other, otherUnique)).Outcome, Is.EqualTo(ScimStoreWriteOutcome.Written),
                "a refused write leaves nothing behind and the session can carry on");
            await session.CommitAsync();
        }

        Assert.That(await ReadAllAsync(system), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task InsertResourceAsync_SameValueInAnotherTypeOrSystem_IsNotAConflictAsync()
    {
        var system = await SeedSystemAsync("scope");
        var other = await SeedSystemAsync("scope-other");
        await InsertCommittedAsync(system, User(system, "ada", externalId: "X-1"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await TryInsertAsync(system, Group(system, "Admins", externalId: "X-1")), Is.EqualTo(ScimStoreWriteOutcome.Written),
                "uniqueness is per resource type");
            Assert.That(await TryInsertAsync(other, User(other, "ada", externalId: "X-1")), Is.EqualTo(ScimStoreWriteOutcome.Written),
                "uniqueness is per Connected System");
        }
    }

    [Test]
    public async Task UpdateResourceAsync_LiveResource_ReplacesPayloadAndUniqueValuesAndRaisesTheVersionAsync()
    {
        var system = await SeedSystemAsync("update");
        var (ada, adaUnique) = User(system, "ada", externalId: "E-1");
        await InsertCommittedAsync(system, (ada, adaUnique));

        long sequence;
        await using (var ctx = NewContext())
        {
            await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
            ada.Payload = """{"userName":"ada.l"}""";
            var result = await session.UpdateResourceAsync(ada, [Unique(UserName, "ada.l")]);
            Assert.That((result.Outcome, result.Version), Is.EqualTo((ScimStoreWriteOutcome.Written, 2L)));
            sequence = session.ChangeSequence;
            await session.CommitAsync();
        }

        var stored = (await ReadAllAsync(system)).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Payload, Is.EqualTo("""{"userName": "ada.l"}"""));
            Assert.That(stored.Version, Is.EqualTo(2));
            Assert.That(stored.ChangeSequence, Is.EqualTo(sequence));
            Assert.That((await UniqueValuesAsync(ada.Id)).Select(u => (u.Attribute, u.Value)), Is.EqualTo(new[] { (UserName, "ada.l") }),
                "the old values are released with the update");
            Assert.That(await TryInsertAsync(system, User(system, "ada", externalId: "E-1")), Is.EqualTo(ScimStoreWriteOutcome.Written));
        }
    }

    [Test]
    public async Task UpdateResourceAsync_KeepsItsOwnValues_IsNotAConflictAsync()
    {
        var system = await SeedSystemAsync("update-self");
        var (ada, adaUnique) = User(system, "ada", externalId: "E-1");
        await InsertCommittedAsync(system, (ada, adaUnique));

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;

        Assert.That((await session.UpdateResourceAsync(ada, adaUnique)).Outcome, Is.EqualTo(ScimStoreWriteOutcome.Written));
    }

    [Test]
    public async Task UpdateResourceAsync_TakesAnotherResourcesValue_ReportsItAndWritesNothingAsync()
    {
        var system = await SeedSystemAsync("update-clash");
        await InsertCommittedAsync(system, User(system, "ada"));
        var (grace, graceUnique) = User(system, "grace");
        await InsertCommittedAsync(system, (grace, graceUnique));

        await using (var ctx = NewContext())
        {
            await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
            grace.Payload = """{"userName":"ADA"}""";
            var result = await session.UpdateResourceAsync(grace, [Unique(UserName, "ada")]);
            Assert.That((result.Outcome, result.ConflictingAttribute), Is.EqualTo((ScimStoreWriteOutcome.UniquenessConflict, UserName)));
            await session.CommitAsync();
        }

        var stored = (await ReadAllAsync(system)).Single(r => r.Id == grace.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Version, Is.EqualTo(1));
            Assert.That((await UniqueValuesAsync(grace.Id)).Single().Value, Is.EqualTo("grace"), "the refused update kept nothing");
        }
    }

    [Test]
    public async Task GetLiveResourceTypeIdsAsync_MixedIds_ReturnsOnlyThisSystemsLiveResourcesAsync()
    {
        var system = await SeedSystemAsync("types");
        var other = await SeedSystemAsync("types-other");
        var user = User(system, "ada");
        var group = Group(system, "Admins");
        var deleted = User(system, "gone");
        var elsewhere = User(other, "ada");
        await InsertCommittedAsync(system, user, group, deleted);
        await InsertCommittedAsync(other, elsewhere);
        await TombstoneCommittedAsync(system, deleted.Resource);

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
        var types = await session.GetLiveResourceTypeIdsAsync(
            [user.Resource.Id, group.Resource.Id, deleted.Resource.Id, elsewhere.Resource.Id, Guid.NewGuid()]);

        Assert.That(types, Is.EquivalentTo(new Dictionary<Guid, int>
        {
            [user.Resource.Id] = system.UserTypeId,
            [group.Resource.Id] = system.GroupTypeId
        }));
    }

    // ─── Tombstones and references ───

    [Test]
    public async Task TombstoneResourceAsync_ReferencedByGroups_ClearsItRemovesItsRowsAndVersionsTheGroupsAsync()
    {
        var system = await SeedSystemAsync("tombstone");
        var ada = User(system, "ada", externalId: "E-1");
        var grace = User(system, "grace");
        var admins = Group(system, "Admins");
        var staff = Group(system, "Staff");
        var unrelated = Group(system, "Unrelated");
        await InsertCommittedAsync(system, ada, grace, admins, staff, unrelated);
        await WriteCommittedAsync(system, async s =>
        {
            await s.AddReferencesAsync(admins.Resource.Id, Members, [ada.Resource.Id, grace.Resource.Id]);
            await s.AddReferencesAsync(staff.Resource.Id, Members, [ada.Resource.Id]);
        });

        IReadOnlyList<ScimStoreReferrer>? affected = null;
        long sequence = 0;
        await WriteCommittedAsync(system, async s =>
        {
            affected = await s.TombstoneResourceAsync(system.UserTypeId, ada.Resource.Id);
            sequence = s.ChangeSequence;
        });

        var rows = (await ReadAllAsync(system)).ToDictionary(r => r.Id);
        var tombstone = rows[ada.Resource.Id];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(affected, Is.EquivalentTo(new[]
            {
                new ScimStoreReferrer(admins.Resource.Id, system.GroupTypeId, Members),
                new ScimStoreReferrer(staff.Resource.Id, system.GroupTypeId, Members)
            }), "each resource that lost a reference, with its type and the attribute, so the caller can journal it");
            Assert.That(tombstone.DeletedAt, Is.Not.Null);
            Assert.That(tombstone.Payload, Is.Null, "a tombstone keeps nothing identifying");
            Assert.That(await UniqueValuesAsync(ada.Resource.Id), Is.Empty, "its unique values are free for reuse");
            Assert.That(tombstone.Version, Is.EqualTo(2));
            Assert.That(tombstone.ChangeSequence, Is.EqualTo(sequence));
            Assert.That(rows[admins.Resource.Id].Version, Is.EqualTo(3), "versioned by the membership write and again by the deletion");
            Assert.That(rows[admins.Resource.Id].ChangeSequence, Is.EqualTo(sequence));
            Assert.That(rows[staff.Resource.Id].ChangeSequence, Is.EqualTo(sequence));
            Assert.That(rows[unrelated.Resource.Id].Version, Is.EqualTo(1));
            Assert.That(await TargetsAsync(admins.Resource.Id), Is.EquivalentTo(new[] { grace.Resource.Id }));
            Assert.That(await TargetsAsync(staff.Resource.Id), Is.Empty);
        }

        Assert.That(await TryInsertAsync(system, User(system, "ada", externalId: "E-1")), Is.EqualTo(ScimStoreWriteOutcome.Written));
    }

    [Test]
    public async Task TombstoneResourceAsync_Group_RemovesItsOwnReferencesAsync()
    {
        var system = await SeedSystemAsync("tombstone-group");
        var ada = User(system, "ada");
        var admins = Group(system, "Admins");
        await InsertCommittedAsync(system, ada, admins);
        await WriteCommittedAsync(system, async s => await s.AddReferencesAsync(admins.Resource.Id, Members, [ada.Resource.Id]));

        await WriteCommittedAsync(system, async s => await s.TombstoneResourceAsync(system.GroupTypeId, admins.Resource.Id));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("ScimStoreReferences"), Is.Zero);
            Assert.That((await ReadAllAsync(system)).Single(r => r.Id == ada.Resource.Id).Version, Is.EqualTo(1),
                "a member is not changed by its Group's deletion");
        }
    }

    [Test]
    public async Task TombstoneResourceAsync_ResourceNotLive_ReturnsNullAsync()
    {
        var system = await SeedSystemAsync("tombstone-missing");
        var ada = User(system, "ada");
        await InsertCommittedAsync(system, ada);
        await TombstoneCommittedAsync(system, ada.Resource);

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await session.TombstoneResourceAsync(system.UserTypeId, ada.Resource.Id), Is.Null, "already a tombstone");
            Assert.That(await session.TombstoneResourceAsync(system.GroupTypeId, ada.Resource.Id), Is.Null, "wrong type");
            Assert.That(await session.TombstoneResourceAsync(system.UserTypeId, Guid.NewGuid()), Is.Null);
        }
    }

    [Test]
    public async Task References_SeveralOperationsInOneWrite_RaiseTheVersionOnceAsync()
    {
        var system = await SeedSystemAsync("references");
        var ada = User(system, "ada");
        var grace = User(system, "grace");
        var alan = User(system, "alan");
        var admins = Group(system, "Admins");
        await InsertCommittedAsync(system, ada, grace, alan, admins);

        var versions = new List<long>();
        await WriteCommittedAsync(system, async s =>
        {
            versions.Add(await s.AddReferencesAsync(admins.Resource.Id, Members, [ada.Resource.Id, grace.Resource.Id]));
            versions.Add(await s.AddReferencesAsync(admins.Resource.Id, Members, [ada.Resource.Id]));
            versions.Add(await s.RemoveReferencesAsync(admins.Resource.Id, Members, [grace.Resource.Id, alan.Resource.Id]));
            admins.Resource.Payload = """{"displayName":"Administrators"}""";
            versions.Add((await s.UpdateResourceAsync(admins.Resource, admins.Unique)).Version);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(versions, Is.All.EqualTo(2), "one write is one version");
            Assert.That(await TargetsAsync(admins.Resource.Id), Is.EquivalentTo(new[] { ada.Resource.Id }), "adding a member twice is harmless");
        }

        await WriteCommittedAsync(system, async s => Assert.That(
            await s.ReplaceReferencesAsync(admins.Resource.Id, Members, [grace.Resource.Id, alan.Resource.Id]), Is.EqualTo(3)));

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
        Assert.That((await session.GetReferencesAsync(admins.Resource.Id)).Select(r => r.TargetId),
            Is.EquivalentTo(new[] { grace.Resource.Id, alan.Resource.Id }));
    }

    [Test]
    public async Task References_AreKeptPerAttributeAsync()
    {
        var system = await SeedSystemAsync("reference-attributes");
        var ada = User(system, "ada");
        var grace = User(system, "grace");
        await InsertCommittedAsync(system, ada, grace);

        await WriteCommittedAsync(system, async s =>
        {
            await s.AddReferencesAsync(ada.Resource.Id, "reports", [grace.Resource.Id]);
            await s.AddReferencesAsync(ada.Resource.Id, "delegates", [grace.Resource.Id]);
            await s.ReplaceReferencesAsync(ada.Resource.Id, "delegates", []);
        });

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await session.GetReferencesAsync(ada.Resource.Id),
                Is.EqualTo(new[] { new ScimStoreReferenceTarget("reports", grace.Resource.Id, system.UserTypeId) }));
        }
    }

    [Test]
    public async Task GetReferencesAsync_AmongTargets_ReturnsOnlyThoseHeldWithTheirTypesAsync()
    {
        var system = await SeedSystemAsync("references-among");
        var ada = User(system, "ada");
        var grace = User(system, "grace");
        var staff = Group(system, "Staff");
        var admins = Group(system, "Admins");
        await InsertCommittedAsync(system, ada, grace, staff, admins);
        await WriteCommittedAsync(system, async s => await s.AddReferencesAsync(admins.Resource.Id, Members,
            [ada.Resource.Id, grace.Resource.Id, staff.Resource.Id]));

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
        var held = await session.GetReferencesAsync(admins.Resource.Id, [ada.Resource.Id, staff.Resource.Id, Guid.NewGuid()]);

        Assert.That(held, Is.EquivalentTo(new[]
        {
            new ScimStoreReferenceTarget(Members, ada.Resource.Id, system.UserTypeId),
            new ScimStoreReferenceTarget(Members, staff.Resource.Id, system.GroupTypeId)
        }), "a Group's held members are looked up by the ids a request names, never read whole");
    }

    [Test]
    public async Task Repository_GetLiveResourceAsync_ReadsOnlyALiveResourceOfThatTypeAndSystemAsync()
    {
        var system = await SeedSystemAsync("read-live");
        var other = await SeedSystemAsync("read-live-other");
        var ada = User(system, "ada");
        var grace = User(system, "grace");
        await InsertCommittedAsync(system, ada, grace);
        await TombstoneCommittedAsync(system, grace.Resource);

        await using var ctx = NewContext();
        var store = new PostgresDataRepository(ctx).ScimStore;
        var read = await store.GetLiveResourceAsync(system.Id, system.UserTypeId, ada.Resource.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(read!.Payload!), JsonNode.Parse(ada.Resource.Payload!)), Is.True, "jsonb normalises spacing");
            Assert.That(read?.Version, Is.EqualTo(1));
            Assert.That(await store.GetLiveResourceAsync(system.Id, system.GroupTypeId, ada.Resource.Id), Is.Null, "wrong type");
            Assert.That(await store.GetLiveResourceAsync(other.Id, other.UserTypeId, ada.Resource.Id), Is.Null, "another system's");
            Assert.That(await store.GetLiveResourceAsync(system.Id, system.UserTypeId, grace.Resource.Id), Is.Null, "a tombstone");
        }
    }

    [Test]
    public async Task Repository_GetReferencesAsync_ReadsEveryReferenceWithItsTargetTypeAsync()
    {
        var system = await SeedSystemAsync("read-references");
        var ada = User(system, "ada");
        var staff = Group(system, "Staff");
        var admins = Group(system, "Admins");
        await InsertCommittedAsync(system, ada, staff, admins);
        await WriteCommittedAsync(system, async s => await s.AddReferencesAsync(admins.Resource.Id, Members, [ada.Resource.Id, staff.Resource.Id]));

        await using var ctx = NewContext();
        var references = await new PostgresDataRepository(ctx).ScimStore.GetReferencesAsync(admins.Resource.Id);

        Assert.That(references, Is.EquivalentTo(new[]
        {
            new ScimStoreReferenceTarget(Members, ada.Resource.Id, system.UserTypeId),
            new ScimStoreReferenceTarget(Members, staff.Resource.Id, system.GroupTypeId)
        }));
    }

    [Test]
    public async Task DisposeAsync_Twice_ReleasesTheConnectionOnceAsync()
    {
        // The server releases the write lock early on the members direct path, then reads on the same context, so a
        // second dispose must not close a connection opened since.
        var system = await SeedSystemAsync("dispose-twice");
        await using var ctx = NewContext();
        var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
        await session.DisposeAsync();
        await ctx.Database.OpenConnectionAsync();

        await session.DisposeAsync();

        Assert.That(ctx.Database.GetDbConnection().State, Is.EqualTo(System.Data.ConnectionState.Open));
    }

    [Test]
    public async Task References_SourceNotLive_ThrowsAsync()
    {
        var system = await SeedSystemAsync("references-missing");

        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;

        Assert.That(async () => await session.AddReferencesAsync(Guid.NewGuid(), Members, [Guid.NewGuid()]),
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
                    ResourceTypeName = "User",
                    Operation = ScimStoreOperation.Patched,
                    ChangedAttributes = ["name.givenName", "emails"],
                    IgnoredAttributes = ["favouriteColour", "urn:example:custom:2.0:User:badge"],
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
            Assert.That(entry.ConnectedSystemId, Is.EqualTo(system.Id));
            Assert.That(entry.ChangeSequence, Is.EqualTo(sequence));
            Assert.That(entry.ResourceId, Is.EqualTo(resourceId));
            Assert.That(entry.ResourceTypeName, Is.EqualTo("User"));
            Assert.That(entry.Operation, Is.EqualTo(ScimStoreOperation.Patched));
            Assert.That(entry.ChangedAttributes, Is.EqualTo(new[] { "name.givenName", "emails" }));
            Assert.That(entry.IgnoredAttributes, Is.EqualTo(new[] { "favouriteColour", "urn:example:custom:2.0:User:badge" }));
            Assert.That(entry.ApiKeyId, Is.Null, "the deleted key's reference is cleared");
            Assert.That(entry.ApiKeyName, Is.EqualTo("Entra provisioning"), "but the entry still says who made the request");
            Assert.That(entry.ClientAddress, Is.EqualTo("192.0.2.10"));
            Assert.That(entry.Occurred, Is.EqualTo(timestamp).Within(TimeSpan.FromMilliseconds(1)));
        }
    }

    [Test]
    public async Task DeleteJournalEntriesOlderThanAsync_RemovesTheOldestFirstUpToTheBatchSizeAsync()
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
        var live = User(system, "live");
        var oldImported = User(system, "old-imported");
        var oldNotImported = User(system, "old-not-imported");
        var recentImported = User(system, "recent-imported");
        await InsertCommittedAsync(system, live, oldImported, oldNotImported, recentImported);
        var oldImportedSequence = await TombstoneCommittedAsync(system, oldImported.Resource);
        var recentSequence = await TombstoneCommittedAsync(system, recentImported.Resource);
        var notImportedSequence = await TombstoneCommittedAsync(system, oldNotImported.Resource);
        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE \"ScimStoreResources\" SET \"DeletedAt\" = now() - interval '30 days' WHERE \"Id\" = ANY({0})",
                new[] { oldImported.Resource.Id, oldNotImported.Resource.Id });

        // An import has read up to (and including) the recent tombstone, but not the last one.
        await using var context = NewContext();
        var store = new PostgresDataRepository(context).ScimStore;
        var purged = await store.PurgeTombstonesAsync(system.Id, importedUpToChangeSequence: notImportedSequence - 1,
            deletedBefore: DateTime.UtcNow.AddDays(-7));

        var remaining = (await ReadAllAsync(system)).Select(r => r.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(recentSequence, Is.LessThan(notImportedSequence));
            Assert.That(purged, Is.EqualTo(1));
            Assert.That(remaining, Is.EquivalentTo(new[] { live.Resource.Id, oldNotImported.Resource.Id, recentImported.Resource.Id }));
            Assert.That((await store.GetStateAsync(system.Id))!.TombstonePurgeFloor, Is.EqualTo(oldImportedSequence));
        }

        Assert.That(await store.PurgeTombstonesAsync(system.Id, 0, DateTime.UtcNow), Is.Zero);
        Assert.That((await store.GetStateAsync(system.Id))!.TombstonePurgeFloor, Is.EqualTo(oldImportedSequence),
            "the floor never falls");
    }

    [Test]
    public async Task GetStateAsync_BeforeTheFirstWrite_ReturnsNullAsync()
    {
        var system = await SeedSystemAsync("no-state");

        Assert.That(await GetStateAsync(system.Id), Is.Null);
    }

    // ─── Lifecycle ───

    [Test]
    public async Task DeleteConnectedSystemAsync_WithAStore_RemovesTheStoreAndItsDefinitionsAndLeavesOtherSystemsAloneAsync()
    {
        var system = await SeedSystemAsync("deleted");
        var other = await SeedSystemAsync("kept");
        var ada = User(system, "ada");
        var admins = Group(system, "Admins");
        await InsertCommittedAsync(system, ada, admins);
        await WriteCommittedAsync(system, async s =>
        {
            await s.AddReferencesAsync(admins.Resource.Id, Members, [ada.Resource.Id]);
            await s.AppendJournalEntriesAsync([Entry()]);
        });
        await InsertCommittedAsync(other, User(other, "grace"));

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            Assert.That(async () => await repository.ConnectedSystems.DeleteConnectedSystemAsync(system.Id), Throws.Nothing);
        }

        await using var read = NewContext();
        var store = new PostgresDataRepository(read).ScimStore;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadAllAsync(system), Is.Empty);
            Assert.That(await CountAsync("ScimStoreReferences"), Is.Zero);
            Assert.That(await CountAsync("ScimStoreJournalEntries"), Is.Zero);
            Assert.That(await GetStateAsync(system.Id), Is.Null);
            Assert.That(await store.GetResourceTypesAsync(system.Id), Is.Empty);
            Assert.That(await store.GetSchemasAsync(system.Id), Is.Empty);
            Assert.That(await ReadAllAsync(other), Has.Count.EqualTo(1));
            Assert.That(await store.GetResourceTypesAsync(other.Id), Has.Count.EqualTo(2));
            Assert.That(await CountAsync("ScimStoreUniqueValues"), Is.EqualTo(1), "only the other system's User's userName");
        }
    }

    // ─── Helpers ───

    private const string UserSchemaUrn = "urn:ietf:params:scim:schemas:core:2.0:User";
    private const string GroupSchemaUrn = "urn:ietf:params:scim:schemas:core:2.0:Group";

    private sealed record SeededSystem(int Id, int UserTypeId, int GroupTypeId);

    private sealed record Pending(ScimStoreResource Resource, ScimStoreUniqueValue[] Unique)
    {
        public static implicit operator Pending((ScimStoreResource Resource, ScimStoreUniqueValue[] Unique) value) =>
            new(value.Resource, value.Unique);
    }

    private static Pending User(SeededSystem system, string userName, string? externalId = null)
    {
        ScimStoreUniqueValue[] unique = externalId == null
            ? [Unique(UserName, userName.ToLowerInvariant())]
            : [Unique(UserName, userName.ToLowerInvariant()), Unique(ExternalId, externalId)];
        return new Pending(Resource(system.Id, system.UserTypeId, $$"""{"userName":"{{userName}}"}"""), unique);
    }

    private static Pending Group(SeededSystem system, string displayName, string? externalId = null) =>
        new(Resource(system.Id, system.GroupTypeId, $$"""{"displayName":"{{displayName}}"}"""),
            externalId == null ? [] : [Unique(ExternalId, externalId)]);

    private static ScimStoreResource Resource(int systemId, int typeId, string payload) => new()
    {
        Id = Guid.NewGuid(),
        ConnectedSystemId = systemId,
        ResourceTypeId = typeId,
        Created = DateTime.UtcNow,
        Payload = payload
    };

    private static ScimStoreUniqueValue Unique(string attribute, string value) => new() { Attribute = attribute, Value = value };

    private static ScimStoreJournalEntry Entry() => new()
    {
        ResourceId = Guid.NewGuid(),
        ResourceTypeName = "User",
        Operation = ScimStoreOperation.Created,
        ChangedAttributes = [UserName]
    };

    private static ScimStoreSchema Schema(string urn) =>
        new() { Urn = urn, Definition = $$"""{"id":"{{urn}}","name":"{{urn}}","attributes":[]}""" };

    private static ScimStoreResourceType ResourceType(string name, string endpoint, string schemaUrn) => new()
    {
        Name = name,
        Endpoint = endpoint,
        Definition = $$"""{"id":"{{name}}","name":"{{name}}","endpoint":"{{endpoint}}","schema":"{{schemaUrn}}","schemaExtensions":[]}"""
    };

    /// <summary>
    /// Seeds a bare Connected System with the User and Group resource types its store needs.
    /// </summary>
    private async Task<SeededSystem> SeedSystemAsync(string suffix)
    {
        int systemId;
        await using (var ctx = NewContext())
        {
            var definition = new ConnectorDefinition { Name = $"scim-store-def-{suffix}" };
            ctx.ConnectorDefinitions.Add(definition);
            await ctx.SaveChangesAsync();

            var system = new ConnectedSystem { Name = $"scim-store-system-{suffix}", ConnectorDefinitionId = definition.Id };
            ctx.ConnectedSystems.Add(system);
            await ctx.SaveChangesAsync();
            systemId = system.Id;
        }

        await using var context = NewContext();
        var store = new PostgresDataRepository(context).ScimStore;
        await store.AddDefinitionsAsync(systemId,
            [Schema(UserSchemaUrn), Schema(GroupSchemaUrn)],
            [ResourceType("User", "/Users", UserSchemaUrn), ResourceType("Group", "/Groups", GroupSchemaUrn)]);
        var types = await store.GetResourceTypesAsync(systemId);
        return new SeededSystem(systemId, types.Single(t => t.Name == "User").Id, types.Single(t => t.Name == "Group").Id);
    }

    private async Task<int> AddDeviceTypeAsync(SeededSystem system)
    {
        await using var ctx = NewContext();
        var store = new PostgresDataRepository(ctx).ScimStore;
        await store.AddDefinitionsAsync(system.Id, [Schema("urn:example:Device")], [ResourceType("Device", "/Devices", "urn:example:Device")]);
        return (await store.GetResourceTypesAsync(system.Id)).Single(t => t.Name == "Device").Id;
    }

    private async Task<ApiKey> SeedApiKeyAsync(string name)
    {
        await using var ctx = NewContext();
        var key = new ApiKey { Id = Guid.NewGuid(), Name = name, KeyHash = "hash", KeyPrefix = "jim_ak_test" };
        ctx.ApiKeys.Add(key);
        await ctx.SaveChangesAsync();
        return key;
    }

    private async Task<long> WriteCommittedAsync(SeededSystem system, Func<IScimStoreWriteSession, Task> write)
    {
        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
        await write(session);
        await session.CommitAsync();
        return session.ChangeSequence;
    }

    private Task<long> CommitEmptyWriteAsync(SeededSystem system) => WriteCommittedAsync(system, _ => Task.CompletedTask);

    private Task<long> InsertCommittedAsync(SeededSystem system, params Pending[] resources) =>
        WriteCommittedAsync(system, async s =>
        {
            foreach (var (resource, unique) in resources)
                Assert.That((await s.InsertResourceAsync(resource, unique)).Outcome, Is.EqualTo(ScimStoreWriteOutcome.Written));
        });

    private Task<long> TombstoneCommittedAsync(SeededSystem system, ScimStoreResource resource) =>
        WriteCommittedAsync(system, async s => Assert.That(await s.TombstoneResourceAsync(resource.ResourceTypeId, resource.Id), Is.Not.Null));

    private async Task<ScimStoreWriteOutcome> TryInsertAsync(SeededSystem system, Pending pending)
    {
        await using var ctx = NewContext();
        await using var session = (await new PostgresDataRepository(ctx).ScimStore.BeginWriteAsync(system.Id))!;
        var result = await session.InsertResourceAsync(pending.Resource, pending.Unique);
        if (result.Outcome == ScimStoreWriteOutcome.Written)
            await session.CommitAsync();
        return result.Outcome;
    }

    private async Task<ScimStoreState?> GetStateAsync(int systemId)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).ScimStore.GetStateAsync(systemId);
    }

    private async Task<List<ScimStoreResource>> ReadAllAsync(SeededSystem system)
    {
        await using var ctx = NewContext();
        return await ctx.ScimStoreResources.Where(r => r.ConnectedSystemId == system.Id).ToListAsync();
    }

    private async Task<List<ScimStoreUniqueValue>> UniqueValuesAsync(Guid resourceId)
    {
        await using var ctx = NewContext();
        return await ctx.ScimStoreUniqueValues.Where(u => u.ResourceId == resourceId).ToListAsync();
    }

    private async Task<List<Guid>> TargetsAsync(Guid sourceId)
    {
        await using var ctx = NewContext();
        return await ctx.ScimStoreReferences.Where(r => r.SourceId == sourceId).Select(r => r.TargetId).ToListAsync();
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

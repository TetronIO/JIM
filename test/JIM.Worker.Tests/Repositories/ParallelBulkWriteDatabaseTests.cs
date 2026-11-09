// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL proof that the bulk writers' parallel paths (#2872) write exactly what their single-connection paths
/// write, and fail the way those paths do. Since the gate counts rows rather than objects and no longer shrinks with the
/// core count, every large host takes the parallel path for a page of groups; until then it never had. The parallel
/// path commits on each connection separately and in phases (objects, then values), so it cannot be one transaction:
/// these tests hold it to what its callers rely on instead. A failure surfaces as the write's own exception, and leaves
/// nothing of the write behind, as the single-connection rollback leaves nothing; an object that already existed is
/// never touched by that removal; and a caller holding a transaction keeps every write inside it.
/// </summary>
/// <remarks>
/// Each test points the repository at this fixture's database directly and forces the path it means to exercise (four
/// connections and a threshold of zero, or a threshold no call reaches), asserting the decision with the same predicate
/// the writers use, so none can pass by quietly taking the other path. Opt-in via <c>JIM_TEST_RESET_*</c>.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ParallelBulkWriteDatabaseTests
{
    private const int Parallelism = 4;
    private static readonly DateTime Created = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL parallel bulk write tests.");

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

    #region Repository set-up

    private enum WritePath { SingleConnection, Parallel }

    /// <summary>
    /// A repository forced onto one write path for a call of <paramref name="rows"/> rows, with the decision asserted.
    /// </summary>
    private SyncRepository Repository(JimDbContext context, WritePath path, int rows)
    {
        var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
        sync.ConnectionStringForParallelWrites = _connectionString;
        sync.WriteParallelismOverride = Parallelism;
        sync.ParallelWriteRowThreshold = path == WritePath.Parallel ? 0 : int.MaxValue;
        Assert.That(sync.ShouldWriteInParallel(rows), Is.EqualTo(path == WritePath.Parallel),
            $"the test means to exercise the {path} path; the gate must agree");
        return sync;
    }

    #endregion

    #region Seeding

    private sealed record Seeded(
        MetaverseObjectType PersonType, MetaverseObjectType GroupType, int NameAttributeId, int ManagerAttributeId,
        int MemberAttributeId, int SystemId, int SyncRuleId, int CsTypeId, int ExternalIdAttributeId,
        int CsNameAttributeId, int CsMemberAttributeId, Guid ActivityId);

    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();
        var personType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
        var name = new MetaverseAttribute { Name = "displayName", Type = AttributeDataType.Text };
        var manager = new MetaverseAttribute { Name = "manager", Type = AttributeDataType.Reference };
        var member = new MetaverseAttribute { Name = "member", Type = AttributeDataType.Reference };

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "object", ConnectedSystem = system, Selected = true };
        var externalId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var csName = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var csMember = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        csType.Attributes.AddRange([externalId, csName, csMember]);
        var syncRule = new SyncRule
        {
            Name = "Directory Import", ConnectedSystem = system, ConnectedSystemObjectType = csType,
            MetaverseObjectType = groupType, Direction = SyncRuleDirection.Import
        };
        var activity = new Activity
        {
            Id = Guid.NewGuid(), TargetName = "Full Synchronisation", TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.InProgress, InitiatedByType = ActivityInitiatorType.System
        };

        seed.AddRange(personType, groupType, name, manager, member, connectorDefinition, system, csType, syncRule, activity);
        await seed.SaveChangesAsync();

        return new Seeded(personType, groupType, name.Id, manager.Id, member.Id, system.Id, syncRule.Id, csType.Id,
            externalId.Id, csName.Id, csMember.Id, activity.Id);
    }

    /// <summary>
    /// Deterministic ids, so the same page can be built twice and written once down each path.
    /// </summary>
    private static Func<Guid> IdSequence(int seed)
    {
        var random = new Random(seed);
        return () =>
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            return new Guid(bytes);
        };
    }

    private static MetaverseObjectAttributeValue MvoValue(Seeded s, Func<Guid> nextId, int attributeId) => new()
    {
        Id = nextId(),
        AttributeId = attributeId,
        ContributedBySystemId = s.SystemId,
        ContributedBySyncRuleId = s.SyncRuleId,
        ContributedByKind = ValueContributorKind.SynchronisationRule
    };

    /// <summary>
    /// People written by an earlier page, which the page under test references.
    /// </summary>
    private async Task<List<Guid>> SeedEarlierPeopleAsync(Seeded s, int count)
    {
        var nextId = IdSequence(1);
        var people = Enumerable.Range(0, count).Select(i =>
        {
            var person = new MetaverseObject { Id = nextId(), Type = s.PersonType, Created = Created };
            var name = MvoValue(s, nextId, s.NameAttributeId);
            name.StringValue = $"Earlier {i}";
            person.AttributeValues.Add(name);
            return person;
        }).ToList();

        await using var context = NewContext();
        await Repository(context, WritePath.SingleConnection, 0).CreateMetaverseObjectsAsync(people);
        return people.Select(p => p.Id).ToList();
    }

    /// <summary>
    /// A synchronisation page mixing people and groups: people managed by earlier people and by people on this page,
    /// one large group whose members are earlier people and people on this page, and small groups.
    /// </summary>
    private static List<MetaverseObject> BuildMixedMvoPage(Seeded s, List<Guid> earlierPeople)
    {
        var nextId = IdSequence(2);
        var page = new List<MetaverseObject>();
        var people = new List<MetaverseObject>();
        for (var i = 0; i < 200; i++)
        {
            var person = new MetaverseObject { Id = nextId(), Type = s.PersonType, Created = Created, CachedDisplayName = $"Person {i}" };
            var name = MvoValue(s, nextId, s.NameAttributeId);
            name.StringValue = $"Person {i}";
            person.AttributeValues.Add(name);
            var manager = MvoValue(s, nextId, s.ManagerAttributeId);
            if (i % 2 == 0)
                manager.ReferenceValueId = earlierPeople[i];
            else
                manager.ReferenceValue = people[i - 1]; // a person on this page, by navigation as synchronisation sets it
            person.AttributeValues.Add(manager);
            people.Add(person);
        }

        page.AddRange(people);

        var largeGroup = new MetaverseObject { Id = nextId(), Type = s.GroupType, Created = Created };
        var largeName = MvoValue(s, nextId, s.NameAttributeId);
        largeName.StringValue = "Everyone";
        largeGroup.AttributeValues.Add(largeName);
        foreach (var earlier in earlierPeople)
        {
            var member = MvoValue(s, nextId, s.MemberAttributeId);
            member.ReferenceValueId = earlier;
            largeGroup.AttributeValues.Add(member);
        }

        foreach (var person in people)
        {
            var member = MvoValue(s, nextId, s.MemberAttributeId);
            member.ReferenceValue = person;
            largeGroup.AttributeValues.Add(member);
        }

        // Placed in the middle of the page, so phase 1's partition boundaries and phase 2's do not coincide.
        page.Insert(100, largeGroup);

        for (var g = 0; g < 50; g++)
        {
            var group = new MetaverseObject { Id = nextId(), Type = s.GroupType, Created = Created };
            var groupName = MvoValue(s, nextId, s.NameAttributeId);
            groupName.StringValue = $"Group {g}";
            group.AttributeValues.Add(groupName);
            for (var m = 0; m < 5; m++)
            {
                var member = MvoValue(s, nextId, s.MemberAttributeId);
                member.ReferenceValue = people[(g * 5 + m) % people.Count];
                group.AttributeValues.Add(member);
            }

            page.Add(group);
        }

        return page;
    }

    private static int Rows(IEnumerable<MetaverseObject> page) => page.Sum(m => 1 + m.AttributeValues.Count);

    private static int Rows(IEnumerable<ConnectedSystemObject> page) => page.Sum(c => 1 + c.AttributeValues.Count);

    #endregion

    #region Reading back

    /// <summary>
    /// Every column of every row, as the database holds it, in a stable order: the comparison is of rows, not of what
    /// a retrieval method chooses to load.
    /// </summary>
    private async Task<List<string>> SnapshotAsync(string table, string keyColumn, IEnumerable<Guid> keys)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""SELECT to_jsonb(t)::text FROM "{table}" t WHERE "{keyColumn}" = ANY(@keys) ORDER BY "Id" """, connection);
        command.Parameters.AddWithValue("keys", keys.ToArray());
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }

    private async Task<long> CountAsync(string table, string keyColumn, IEnumerable<Guid> keys)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""SELECT count(*) FROM "{table}" WHERE "{keyColumn}" = ANY(@keys)""", connection);
        command.Parameters.AddWithValue("keys", keys.ToArray());
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Deletes a page's objects, their values first: a value referencing another object of the same page would
    /// otherwise stop the objects' deletion on its foreign key before the cascade reached it.
    /// </summary>
    private async Task DeleteAsync(string valueTable, string ownerColumn, string objectTable, IEnumerable<Guid> ids)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
                 {
                     $"""DELETE FROM "{valueTable}" WHERE "{ownerColumn}" = ANY(@ids)""",
                     $"""DELETE FROM "{objectTable}" WHERE "Id" = ANY(@ids)"""
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("ids", ids.ToArray());
            await command.ExecuteNonQueryAsync();
        }
    }

    #endregion

    #region Metaverse Objects

    /// <summary>
    /// The same page written down each path gives identical rows, every column of every object and value, including
    /// references to objects of an earlier page, references by navigation to objects on this page, and one group whose
    /// membership spans every phase-2 partition.
    /// </summary>
    [Test]
    public async Task CreateMetaverseObjectsAsync_ParallelPath_WritesExactlyWhatTheSingleConnectionPathWritesAsync()
    {
        var s = await SeedAsync();
        var earlierPeople = await SeedEarlierPeopleAsync(s, 300);

        var singlePage = BuildMixedMvoPage(s, earlierPeople);
        var pageIds = singlePage.Select(m => m.Id).ToList();
        await using (var context = NewContext())
            await Repository(context, WritePath.SingleConnection, Rows(singlePage)).CreateMetaverseObjectsAsync(singlePage);
        var singleObjects = await SnapshotAsync("MetaverseObjects", "Id", pageIds);
        var singleValues = await SnapshotAsync("MetaverseObjectAttributeValues", "MetaverseObjectId", pageIds);
        await DeleteAsync("MetaverseObjectAttributeValues", "MetaverseObjectId", "MetaverseObjects", pageIds);

        var parallelPage = BuildMixedMvoPage(s, earlierPeople);
        await using (var context = NewContext())
            await Repository(context, WritePath.Parallel, Rows(parallelPage)).CreateMetaverseObjectsAsync(parallelPage);
        var parallelObjects = await SnapshotAsync("MetaverseObjects", "Id", pageIds);
        var parallelValues = await SnapshotAsync("MetaverseObjectAttributeValues", "MetaverseObjectId", pageIds);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(singleObjects, Has.Count.EqualTo(singlePage.Count), "sanity: the single-connection path wrote every object");
            Assert.That(singleValues, Has.Count.EqualTo(singlePage.Sum(m => m.AttributeValues.Count)), "sanity: and every value");
            Assert.That(parallelObjects, Is.EqualTo(singleObjects), "every object row, every column");
            Assert.That(parallelValues, Is.EqualTo(singleValues), "every value row, every column, references resolved alike");
        }
    }

    /// <summary>
    /// A value that cannot be written fails phase 2 after phase 1 has committed every object. The caller must get the
    /// failure, and find nothing of the page: no object, and no value of any object, whichever connection wrote it.
    /// Without the removal the objects survive, some with part of their values, joined to nothing, and the next run
    /// projects the same objects again beside them.
    /// </summary>
    [Test]
    public async Task CreateMetaverseObjectsAsync_ParallelPathValueWriteFails_ThrowsAndLeavesNothingOfThePageAsync()
    {
        var s = await SeedAsync();
        var earlierPeople = await SeedEarlierPeopleAsync(s, 300);
        var page = BuildMixedMvoPage(s, earlierPeople);
        page[^1].AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = int.MaxValue, StringValue = "poisoned" });
        var pageIds = page.Select(m => m.Id).ToList();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, Rows(page));
            Assert.That(async () => await repository.CreateMetaverseObjectsAsync(page),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.ForeignKeyViolation),
                "the write's own failure must reach the caller");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("MetaverseObjects", "Id", pageIds), Is.Zero, "no object of the failed page may remain");
            Assert.That(await CountAsync("MetaverseObjectAttributeValues", "MetaverseObjectId", pageIds), Is.Zero,
                "nor any value another connection committed for it");
            Assert.That(await CountAsync("MetaverseObjects", "Id", earlierPeople), Is.EqualTo(earlierPeople.Count),
                "the earlier page's objects, which the failed page referenced, are untouched");
            Assert.That(await CountAsync("MetaverseObjectAttributeValues", "MetaverseObjectId", earlierPeople), Is.EqualTo(earlierPeople.Count),
                "and so are their values");
        }
    }

    /// <summary>
    /// An object handed to the create that already exists fails its phase-1 partition on the primary key while the
    /// other partitions commit. The removal takes back what the call committed and nothing else: the object that was
    /// already there, and its values, must be exactly as they were.
    /// </summary>
    [Test]
    public async Task CreateMetaverseObjectsAsync_ParallelPathObjectAlreadyExists_RemovesOnlyWhatTheCallWroteAsync()
    {
        var s = await SeedAsync();
        var earlierPeople = await SeedEarlierPeopleAsync(s, 300);
        var existingId = earlierPeople[0];
        var existingBefore = await SnapshotAsync("MetaverseObjectAttributeValues", "MetaverseObjectId", [existingId]);

        var page = BuildMixedMvoPage(s, earlierPeople);
        var duplicate = new MetaverseObject { Id = existingId, Type = s.PersonType, Created = Created };
        var duplicateName = MvoValue(s, Guid.NewGuid, s.NameAttributeId);
        duplicateName.StringValue = "Impostor";
        duplicate.AttributeValues.Add(duplicateName);
        page.Add(duplicate);
        var newIds = page.Where(m => m.Id != existingId).Select(m => m.Id).ToList();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, Rows(page));
            Assert.That(async () => await repository.CreateMetaverseObjectsAsync(page),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.UniqueViolation));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("MetaverseObjects", "Id", newIds), Is.Zero, "the objects the call committed are removed");
            Assert.That(await CountAsync("MetaverseObjects", "Id", [existingId]), Is.EqualTo(1), "the object that already existed is not");
            Assert.That(await SnapshotAsync("MetaverseObjectAttributeValues", "MetaverseObjectId", [existingId]), Is.EqualTo(existingBefore),
                "and its values are exactly as they were");
        }
    }

    #endregion

    #region Connected System Objects

    private static List<ConnectedSystemObject> BuildCsoPage(Seeded s, List<Guid> earlierBatch, int seed)
    {
        var nextId = IdSequence(seed);
        ConnectedSystemObject NewCso(string name)
        {
            var cso = new ConnectedSystemObject
            {
                Id = nextId(), ConnectedSystemId = s.SystemId, TypeId = s.CsTypeId, ExternalIdAttributeId = s.ExternalIdAttributeId,
                Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined, Created = Created
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = nextId(), AttributeId = s.ExternalIdAttributeId, GuidValue = nextId() });
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = nextId(), AttributeId = s.CsNameAttributeId, StringValue = name });
            return cso;
        }

        var users = Enumerable.Range(0, 200).Select(i => NewCso($"User {i}")).ToList();
        var group = NewCso("Everyone");
        foreach (var earlier in earlierBatch)
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = nextId(), AttributeId = s.CsMemberAttributeId, ReferenceValueId = earlier });
        foreach (var user in users)
            group.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = nextId(), AttributeId = s.CsMemberAttributeId, ReferenceValue = user });

        var page = new List<ConnectedSystemObject>(users);
        page.Insert(100, group);
        return page;
    }

    private async Task<List<Guid>> SeedEarlierCsoBatchAsync(Seeded s)
    {
        var batch = BuildCsoPage(s, [], seed: 3);
        await using var context = NewContext();
        await Repository(context, WritePath.SingleConnection, 0).CreateConnectedSystemObjectsAsync(batch);
        return batch.Select(c => c.Id).ToList();
    }

    /// <summary>
    /// The same import batch written down each path gives identical rows: every CSO and value column, references to an
    /// earlier committed batch and within this one, and the synchronisation-pending mark on every row (#2092), though
    /// the parallel path now writes one group's values across every connection and marks the rows afterwards.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsAsync_ParallelPath_WritesExactlyWhatTheSingleConnectionPathWritesAsync()
    {
        var s = await SeedAsync();
        var earlierBatch = await SeedEarlierCsoBatchAsync(s);

        var singlePage = BuildCsoPage(s, earlierBatch, seed: 4);
        var pageIds = singlePage.Select(c => c.Id).ToList();
        await using (var context = NewContext())
            await Repository(context, WritePath.SingleConnection, Rows(singlePage)).CreateConnectedSystemObjectsAsync(singlePage, [.. earlierBatch]);
        var singleObjects = await SnapshotAsync("ConnectedSystemObjects", "Id", pageIds);
        var singleValues = await SnapshotAsync("ConnectedSystemObjectAttributeValues", "ConnectedSystemObjectId", pageIds);
        await DeleteAsync("ConnectedSystemObjectAttributeValues", "ConnectedSystemObjectId", "ConnectedSystemObjects", pageIds);

        var parallelPage = BuildCsoPage(s, earlierBatch, seed: 4);
        await using (var context = NewContext())
            await Repository(context, WritePath.Parallel, Rows(parallelPage)).CreateConnectedSystemObjectsAsync(parallelPage, [.. earlierBatch]);
        var parallelObjects = await SnapshotAsync("ConnectedSystemObjects", "Id", pageIds);
        var parallelValues = await SnapshotAsync("ConnectedSystemObjectAttributeValues", "ConnectedSystemObjectId", pageIds);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(singleObjects, Has.Count.EqualTo(singlePage.Count), "sanity: the single-connection path wrote every object");
            Assert.That(singleValues, Has.Count.EqualTo(singlePage.Sum(c => c.AttributeValues.Count)), "sanity: and every value");
            Assert.That(singleObjects, Has.All.Contains("\"SynchronisationPending\": true"), "sanity: and marked them pending");
            Assert.That(parallelObjects, Is.EqualTo(singleObjects), "every object row, every column, the pending mark included");
            Assert.That(parallelValues, Is.EqualTo(singleValues), "every value row, every column, references resolved alike");
            Assert.That(parallelPage.All(c => c.SynchronisationPending), Is.True, "the in-memory objects carry the mark as written");
        }
    }

    /// <summary>
    /// A value that cannot be written fails phase 2 after phase 1 has committed every CSO row. The import must get the
    /// failure and find nothing of the batch: a CSO left without its values has no external identifier, so the next
    /// import cannot match it and creates the object again beside it.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsAsync_ParallelPathValueWriteFails_ThrowsAndLeavesNothingOfTheBatchAsync()
    {
        var s = await SeedAsync();
        var earlierBatch = await SeedEarlierCsoBatchAsync(s);
        var page = BuildCsoPage(s, earlierBatch, seed: 4);
        page[^1].AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = int.MaxValue, StringValue = "poisoned" });
        var pageIds = page.Select(c => c.Id).ToList();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, Rows(page));
            Assert.That(async () => await repository.CreateConnectedSystemObjectsAsync(page, [.. earlierBatch]),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.ForeignKeyViolation));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("ConnectedSystemObjects", "Id", pageIds), Is.Zero, "no CSO of the failed batch may remain");
            Assert.That(await CountAsync("ConnectedSystemObjectAttributeValues", "ConnectedSystemObjectId", pageIds), Is.Zero,
                "nor any value another connection committed for it");
            Assert.That(await CountAsync("ConnectedSystemObjects", "Id", earlierBatch), Is.EqualTo(earlierBatch.Count),
                "the earlier batch, which the failed one referenced, is untouched");
            Assert.That(page.Any(c => c.SynchronisationPending), Is.False, "nothing was marked pending in memory either");
        }
    }

    #endregion

    #region Run Profile Execution Items

    private static List<ActivityRunProfileExecutionItem> BuildRpeis(Guid activityId, int count)
    {
        var rpeis = new List<ActivityRunProfileExecutionItem>(count);
        for (var i = 0; i < count; i++)
        {
            var rpei = new ActivityRunProfileExecutionItem
            {
                ActivityId = activityId, ObjectChangeType = ObjectChangeType.Projected, ObjectTypeSnapshot = "user"
            };
            var root = new ActivityRunProfileExecutionItemSyncOutcome { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected };
            var child = new ActivityRunProfileExecutionItemSyncOutcome
            {
                OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, ParentSyncOutcome = root
            };
            root.Children.Add(child);
            rpei.SyncOutcomes.Add(root);
            rpei.SyncOutcomes.Add(child);
            rpeis.Add(rpei);
        }

        return rpeis;
    }

    /// <summary>
    /// The last step of the parallel path (on the main connection: causal edges, match candidates, stat counters) fails
    /// after the items and their outcomes have committed on their own connections. The caller must get the failure and
    /// find nothing of the batch: no item, no outcome, no counter.
    /// </summary>
    [Test]
    public async Task BulkInsertRpeisAsync_ParallelPathLastStepFails_ThrowsAndLeavesNothingOfTheBatchAsync()
    {
        var s = await SeedAsync();
        var rpeis = BuildRpeis(s.ActivityId, 100);

        // Two causal edges with one primary key: the edge write, in the last step, fails on it.
        var edgeId = Guid.NewGuid();
        foreach (var rpei in rpeis.Take(2))
        {
            rpei.CausalEdges.Add(new CausalEdge
            {
                Id = edgeId, EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
                ReasonCode = CausalReasonCode.AuthoritativeSourceDisconnected, CauseDisplayName = "Tina Adams",
                ConnectedSystemId = s.SystemId, ConnectedSystemName = "Directory"
            });
        }

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, rpeis.Count * 3);
            Assert.That(async () => await repository.BulkInsertRpeisAsync(rpeis),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.UniqueViolation));
        }

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpeis.All(r => r.Id != Guid.Empty), Is.True, "sanity: the items were given ids, so step 1 ran");
            Assert.That(await verify.ActivityRunProfileExecutionItems.CountAsync(), Is.Zero, "no item of the failed batch may remain");
            Assert.That(await verify.ActivityRunProfileExecutionItemSyncOutcomes.CountAsync(), Is.Zero, "nor any outcome");
            Assert.That(await verify.CausalEdges.CountAsync(), Is.Zero, "nor any edge");
            Assert.That(await verify.ActivityStatCounters.CountAsync(), Is.Zero, "nor any counter");
        }
    }

    /// <summary>
    /// A caller holding a transaction on its context gets every write inside it, however large the batch: writes on
    /// connections of their own would commit whatever the caller then rolls back. Rolled back, nothing remains.
    /// </summary>
    [Test]
    public async Task BulkInsertRpeisAsync_CallerHoldsTransaction_WritesInsideItSoARollbackLeavesNothingAsync()
    {
        var s = await SeedAsync();
        var rpeis = BuildRpeis(s.ActivityId, 100);

        await using (var context = NewContext())
        {
            var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
            sync.ConnectionStringForParallelWrites = _connectionString;
            sync.WriteParallelismOverride = Parallelism;
            sync.ParallelWriteRowThreshold = 0;

            await using var transaction = await context.Database.BeginTransactionAsync();
            Assert.That(sync.ShouldWriteInParallel(rpeis.Count * 3), Is.False,
                "a batch large enough for the parallel path is written on the caller's connection while it holds a transaction");
            await sync.BulkInsertRpeisAsync(rpeis);
            Assert.That(await context.ActivityRunProfileExecutionItems.CountAsync(), Is.EqualTo(rpeis.Count),
                "sanity: the caller sees its own writes inside its transaction");
            await transaction.RollbackAsync();
        }

        await using var verify = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ActivityRunProfileExecutionItems.CountAsync(), Is.Zero, "the rollback takes every item with it");
            Assert.That(await verify.ActivityRunProfileExecutionItemSyncOutcomes.CountAsync(), Is.Zero, "and every outcome");
        }
    }

    #endregion
}

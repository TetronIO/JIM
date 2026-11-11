// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
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
/// Real-PostgreSQL proof that an import's batch of new Connected System Objects is written together with its history
/// (#2909): the change records are written while the objects and their values are, on connections of their own, and
/// their value rows are partitioned by row as the objects' values are (#2872). Held to three things: the rows written are
/// exactly those the old serial path (the objects, then the execution items, then the change records) wrote, every column
/// of every row; a failure in either concurrent part, or in the step that finishes the batch, leaves nothing of the batch,
/// so no object is left without its history and no history without its object; and the history really is written while
/// the objects' values are, which is the point.
/// </summary>
/// <remarks>
/// Each test forces the path it means to exercise (four connections, a threshold of zero and every connection used
/// however few the rows, or a threshold no call reaches) and asserts the decision with the writers' own predicate.
/// Opt-in via <c>JIM_TEST_RESET_*</c>.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class CsoCreateWithHistoryDatabaseTests
{
    private const int Parallelism = 4;
    private static readonly DateTime Created = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ActivityId = new("29090000-0000-0000-0000-000000000001");

    /// <summary>
    /// Every table the batch writes to, read whole: each run starts from an empty, freshly seeded database.
    /// </summary>
    private static readonly string[] BatchTables =
    [
        "ConnectedSystemObjects", "ConnectedSystemObjectAttributeValues", "ActivityRunProfileExecutionItems",
        "ActivityRunProfileExecutionItemSyncOutcomes", "ConnectedSystemObjectChanges", "ConnectedSystemObjectChangeAttributes",
        "ConnectedSystemObjectChangeAttributeValues", "ActivityStatCounters", "CausalEdges"
    ];

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
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL create-with-history tests.");
        _connectionString = connectionString!;

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
        sync.ParallelWriteRowsPerConnection = 1; // every connection the parallelism allows, however few the rows (#2669)
        Assert.That(sync.ShouldWriteInParallel(rows), Is.EqualTo(path == WritePath.Parallel),
            $"the test means to exercise the {path} path; the gate must agree");
        return sync;
    }

    #endregion

    #region Seeding and building

    private sealed record Seeded(
        int SystemId, int CsTypeId, ConnectedSystemObjectTypeAttribute ExternalId, ConnectedSystemObjectTypeAttribute Name,
        ConnectedSystemObjectTypeAttribute Member);

    private sealed record Batch(List<ConnectedSystemObject> Objects, List<ActivityRunProfileExecutionItem> Items)
    {
        public List<Guid> ObjectIds => Objects.Select(o => o.Id).ToList();

        public List<ConnectedSystemObjectChange> Changes => Items.Where(i => i.ConnectedSystemObjectChange != null).Select(i => i.ConnectedSystemObjectChange!).ToList();

        public List<Guid> ChangeValueIds => Changes
            .SelectMany(c => c.AttributeChanges.SelectMany(a => a.ValueChanges.Select(v => v.Id))).ToList();

        /// <summary>
        /// Every row the batch writes: objects, values, items, outcomes, change records, attribute and value changes.
        /// </summary>
        public int Rows => Objects.Sum(o => 1 + o.AttributeValues.Count) + Items.Count * 2
            + Changes.Sum(c => 1 + c.AttributeChanges.Sum(a => 1 + a.ValueChanges.Count));
    }

    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "object", ConnectedSystem = system, Selected = true };
        var externalId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var name = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        csType.Attributes.AddRange([externalId, name, member]);
        var activity = new Activity
        {
            Id = ActivityId, TargetName = "Full Import", TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.InProgress, InitiatedByType = ActivityInitiatorType.System
        };

        seed.AddRange(connectorDefinition, system, csType, activity);
        await seed.SaveChangesAsync();
        return new Seeded(system.Id, csType.Id, externalId, name, member);
    }

    /// <summary>
    /// Deterministic ids, so the same batch can be built for each run and written once down each path.
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

    private static string Dn(string name) => $"CN={name},OU=People,DC=glitterband,DC=local";

    private static ConnectedSystemObject NewCso(Seeded s, Func<Guid> nextId, string name)
    {
        var cso = new ConnectedSystemObject
        {
            Id = nextId(), ConnectedSystemId = s.SystemId, TypeId = s.CsTypeId, ExternalIdAttributeId = s.ExternalId.Id,
            Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined, Created = Created
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = nextId(), Attribute = s.ExternalId, AttributeId = s.ExternalId.Id, GuidValue = nextId(), ConnectedSystemObject = cso
        });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = nextId(), Attribute = s.Name, AttributeId = s.Name.Id, StringValue = name, ConnectedSystemObject = cso
        });
        return cso;
    }

    private static ConnectedSystemObjectAttributeValue Member(Seeded s, Func<Guid> nextId, ConnectedSystemObject group) => new()
    {
        Id = nextId(), Attribute = s.Member, AttributeId = s.Member.Id, ConnectedSystemObject = group
    };

    /// <summary>
    /// An import batch: users, and one group in the middle whose members are users of an earlier batch (by id), users of
    /// this batch (by navigation, as reference resolution sets them), a DN that matched no object, and an object of a
    /// later batch (by id, which the create may not write). Each object has its execution item with one outcome.
    /// </summary>
    private static Batch BuildBatch(Seeded s, List<Guid> earlierBatch, int seed, int users = 200)
    {
        var nextId = IdSequence(seed);
        var objects = Enumerable.Range(0, users).Select(i => NewCso(s, nextId, $"User {seed}-{i}")).ToList();

        if (earlierBatch.Count > 0 || users > 0)
        {
            var group = NewCso(s, nextId, $"Everyone {seed}");
            for (var i = 0; i < earlierBatch.Count; i++)
            {
                var member = Member(s, nextId, group);
                member.ReferenceValueId = earlierBatch[i];
                member.UnresolvedReferenceValue = Dn($"Earlier {i}");
                group.AttributeValues.Add(member);
            }

            foreach (var user in objects.ToList())
            {
                var member = Member(s, nextId, group);
                member.ReferenceValue = user;
                member.UnresolvedReferenceValue = Dn(user.AttributeValues[1].StringValue!);
                group.AttributeValues.Add(member);
            }

            var unmatched = Member(s, nextId, group);
            unmatched.UnresolvedReferenceValue = Dn("Nobody");
            group.AttributeValues.Add(unmatched);

            var later = Member(s, nextId, group);
            later.ReferenceValueId = nextId();
            later.UnresolvedReferenceValue = Dn("Later");
            group.AttributeValues.Add(later);

            objects.Insert(objects.Count / 2, group);
        }

        var items = objects.Select(o =>
        {
            var item = new ActivityRunProfileExecutionItem
            {
                Id = nextId(), ActivityId = ActivityId, ObjectChangeType = ObjectChangeType.Added, ConnectedSystemObject = o,
                ObjectTypeSnapshot = "object"
            };
            item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
            {
                Id = nextId(), OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded
            });
            return item;
        }).ToList();

        return new Batch(objects, items);
    }

    /// <summary>
    /// Readies the batch as the import does: the objects prepared for the create, then their change records built by
    /// the application's own builder, onto their execution items. Change times and ids are fixed, so two runs of the same
    /// batch can be compared row for row.
    /// </summary>
    private async Task ReadyAsync(Batch batch, List<Guid> earlierBatch, int seed, bool changeTrackingEnabled = true)
    {
        await using var context = NewContext();
        var repository = new PostgresDataRepository(context);
        repository.Sync.PrepareConnectedSystemObjectsForCreate(batch.Objects, [.. earlierBatch]);
        using var jim = new JimApplication(repository);
        jim.ConnectedSystems.LinkCreateChangeRecords(batch.Objects, batch.Items, changeTrackingEnabled);

        var nextId = IdSequence(seed + 1000);
        foreach (var change in batch.Changes)
        {
            change.Id = nextId();
            change.ChangeTime = Created;
            foreach (var attributeChange in change.AttributeChanges)
            {
                attributeChange.Id = nextId();
                foreach (var valueChange in attributeChange.ValueChanges)
                    valueChange.Id = nextId();
            }
        }
    }

    /// <summary>
    /// An earlier, committed batch the batch under test references.
    /// </summary>
    private async Task<List<Guid>> SeedEarlierBatchAsync(Seeded s)
    {
        var batch = BuildBatch(s, [], seed: 3, users: 50);
        await ReadyAsync(batch, [], seed: 3);
        await using var context = NewContext();
        await Repository(context, WritePath.SingleConnection, 0).CreateConnectedSystemObjectsWithHistoryAsync(batch.Objects, batch.Items);
        return batch.ObjectIds;
    }

    /// <summary>
    /// A batch built and readied against a freshly seeded database, with the earlier batch it references committed.
    /// </summary>
    private async Task<(Seeded Seeded, List<Guid> EarlierBatch, Batch Batch)> ArrangeAsync(int seed = 4, int users = 200, bool changeTrackingEnabled = true)
    {
        var s = await SeedAsync();
        var earlierBatch = await SeedEarlierBatchAsync(s);
        var batch = BuildBatch(s, earlierBatch, seed, users);
        await ReadyAsync(batch, earlierBatch, seed, changeTrackingEnabled);
        return (s, earlierBatch, batch);
    }

    #endregion

    #region Reading back

    /// <summary>
    /// Every column of every row of every table the batch writes to, as the database holds them, in a stable order.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> SnapshotAsync()
    {
        var snapshot = new Dictionary<string, List<string>>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        foreach (var table in BatchTables)
        {
            await using var command = new NpgsqlCommand($"""SELECT to_jsonb(t)::text AS row FROM "{table}" t ORDER BY 1""", connection);
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(reader.GetString(0));
            snapshot[table] = rows;
        }

        return snapshot;
    }

    private async Task<long> CountAsync(string table, string keyColumn, IEnumerable<Guid> keys)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""SELECT count(*) FROM "{table}" WHERE "{keyColumn}" = ANY(@keys)""", connection);
        command.Parameters.AddWithValue("keys", keys.ToArray());
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Asserts nothing of the batch is in the database, and the earlier batch it referenced is whole. An item id the batch
    /// reused from another batch (<paramref name="itemIdsOwnedElsewhere"/>) is that batch's, and is left out of the count.
    /// </summary>
    private async Task AssertNothingOfTheBatchRemainsAsync(Batch batch, List<Guid> earlierBatch, Dictionary<string, List<string>> before,
        IReadOnlySet<Guid>? itemIdsOwnedElsewhere = null)
    {
        var after = await SnapshotAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("ConnectedSystemObjects", "Id", batch.ObjectIds), Is.Zero, "no object of the failed batch may remain");
            Assert.That(await CountAsync("ConnectedSystemObjectAttributeValues", "ConnectedSystemObjectId", batch.ObjectIds), Is.Zero,
                "nor any of its values");
            Assert.That(await CountAsync("ActivityRunProfileExecutionItems", "Id", batch.Items.Select(i => i.Id).Where(id => itemIdsOwnedElsewhere?.Contains(id) != true)), Is.Zero,
                "nor any execution item");
            Assert.That(await CountAsync("ConnectedSystemObjectChanges", "Id", batch.Changes.Select(c => c.Id)), Is.Zero,
                "nor any change record");
            Assert.That(await CountAsync("ConnectedSystemObjectChangeAttributeValues", "Id", batch.ChangeValueIds), Is.Zero,
                "nor any change record value, whichever connection wrote it");
            Assert.That(await CountAsync("ConnectedSystemObjects", "Id", earlierBatch), Is.EqualTo(earlierBatch.Count),
                "the earlier batch, which the failed one referenced, is untouched");
            foreach (var table in BatchTables)
                Assert.That(after[table], Is.EqualTo(before[table]), $"{table} is exactly as it was before the batch");
        }
    }

    #endregion

    #region Equivalence

    /// <summary>
    /// The objects, then the execution items, then the change records, each written on the context's connection: what
    /// the import wrote, one write after another, before #2909.
    /// </summary>
    private async Task WriteTheOldWayAsync(Batch batch, List<Guid> earlierBatch)
    {
        await using var context = NewContext();
        var repository = Repository(context, WritePath.SingleConnection, batch.Rows);
        await repository.CreateConnectedSystemObjectsAsync(batch.Objects, [.. earlierBatch]);
        await repository.BulkInsertRpeisAsync(batch.Items);
        foreach (var item in batch.Items.Where(i => i.ConnectedSystemObjectChange != null))
            item.ConnectedSystemObjectChange!.ActivityRunProfileExecutionItemId = item.Id;
        await repository.PersistRpeiCsoChangesAsync(batch.Items);
    }

    /// <summary>
    /// The same batch written the old way, concurrently with its history partitioned across connections, and in one
    /// transaction, gives identical rows: every column of every object, value, execution item, outcome, change record,
    /// attribute change, value change and stat counter, including references to an earlier batch, to this batch, to a
    /// later batch and to no object at all, and a group whose values and value changes span every partition.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsWithHistoryAsync_EachPath_WritesExactlyWhatTheOldSerialWritesWroteAsync()
    {
        var (_, earlierBatch, oldBatch) = await ArrangeAsync();
        await WriteTheOldWayAsync(oldBatch, earlierBatch);
        var oldRows = await SnapshotAsync();
        await PostgresTestDatabase.ResetAsync(_connectionString);

        var (_, _, concurrentBatch) = await ArrangeAsync();
        await using (var context = NewContext())
            await Repository(context, WritePath.Parallel, concurrentBatch.Rows)
                .CreateConnectedSystemObjectsWithHistoryAsync(concurrentBatch.Objects, concurrentBatch.Items, [.. earlierBatch]);
        var concurrentRows = await SnapshotAsync();
        await PostgresTestDatabase.ResetAsync(_connectionString);

        var (_, _, serialBatch) = await ArrangeAsync();
        await using (var context = NewContext())
            await Repository(context, WritePath.SingleConnection, serialBatch.Rows)
                .CreateConnectedSystemObjectsWithHistoryAsync(serialBatch.Objects, serialBatch.Items, [.. earlierBatch]);
        var serialRows = await SnapshotAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(oldRows["ConnectedSystemObjectChangeAttributeValues"], Has.Count.GreaterThan(oldBatch.ChangeValueIds.Count),
                "sanity: the old way wrote a value change for every value of both batches");
            Assert.That(oldRows["ConnectedSystemObjectChangeAttributeValues"].Count(r => r.Contains("\"IsUnresolvedReference\": true")), Is.EqualTo(4),
                "sanity: the unmatched DN and the later batch's object are unresolved, in each batch's group");
            Assert.That(oldRows["ActivityStatCounters"], Is.Not.Empty, "sanity: the old way counted the items");
            foreach (var table in BatchTables)
            {
                Assert.That(concurrentRows[table], Is.EqualTo(oldRows[table]), $"concurrent path: {table}, every row and column");
                Assert.That(serialRows[table], Is.EqualTo(oldRows[table]), $"single-transaction path: {table}, every row and column");
            }

            Assert.That(concurrentBatch.Objects.All(o => o.SynchronisationPending), Is.True, "the in-memory objects carry the mark as written");
        }
    }

    #endregion

    #region Concurrency

    /// <summary>
    /// The point of #2909: the batch's history is written while its objects' values are. The values' table is held
    /// locked by another session, so the values cannot be written until it lets go; the execution items and every change
    /// record row must nonetheless be committed in the meantime. The old serial path wrote them only after the values,
    /// so it commits nothing of the history until the lock is released.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsWithHistoryAsync_ParallelPath_WritesTheHistoryWhileTheValuesAreHeldUpAsync()
    {
        var (_, earlierBatch, batch) = await ArrangeAsync();

        await using var lockConnection = new NpgsqlConnection(_connectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand("""LOCK TABLE "ConnectedSystemObjectAttributeValues" IN SHARE MODE""", lockConnection, lockTransaction))
            await lockCommand.ExecuteNonQueryAsync();

        await using var context = NewContext();
        var repository = Repository(context, WritePath.Parallel, batch.Rows);
        var write = Task.Run(() => repository.CreateConnectedSystemObjectsWithHistoryAsync(batch.Objects, batch.Items, [.. earlierBatch]));

        var historyWrittenWhileValuesHeld = false;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !write.IsCompleted)
            {
                if (await CountAsync("ConnectedSystemObjectChangeAttributeValues", "Id", batch.ChangeValueIds) == batch.ChangeValueIds.Count
                    && await CountAsync("ActivityRunProfileExecutionItems", "Id", batch.Items.Select(i => i.Id)) == batch.Items.Count)
                {
                    historyWrittenWhileValuesHeld = true;
                    break;
                }

                await Task.Delay(100);
            }

            Assert.That(await CountAsync("ConnectedSystemObjectAttributeValues", "ConnectedSystemObjectId", batch.ObjectIds), Is.Zero,
                "sanity: the lock held the objects' values back");
        }
        finally
        {
            await lockTransaction.RollbackAsync();
        }

        await write;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(historyWrittenWhileValuesHeld, Is.True,
                "the execution items and every change record value are committed while the objects' values wait");
            Assert.That(await CountAsync("ConnectedSystemObjectAttributeValues", "ConnectedSystemObjectId", batch.ObjectIds),
                Is.EqualTo(batch.Objects.Sum(o => o.AttributeValues.Count)), "and the values are written once the lock is gone");
        }
    }

    #endregion

    #region Failure leaves nothing

    /// <summary>
    /// The objects' part fails (a value naming no attribute) after its object rows have committed, while the history is
    /// written beside it. The import must get the failure and find nothing of the batch: no object, and no history naming
    /// one.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsWithHistoryAsync_ObjectValueWriteFails_ThrowsAndLeavesNothingOfTheBatchAsync()
    {
        var (_, earlierBatch, batch) = await ArrangeAsync();
        batch.Objects[^1].AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = int.MaxValue, StringValue = "poisoned" });
        var before = await SnapshotAsync();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, batch.Rows);
            Assert.That(async () => await repository.CreateConnectedSystemObjectsWithHistoryAsync(batch.Objects, batch.Items, [.. earlierBatch]),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.ForeignKeyViolation));
        }

        await AssertNothingOfTheBatchRemainsAsync(batch, earlierBatch, before);
        Assert.That(batch.Objects.Any(o => o.SynchronisationPending), Is.False, "nothing was marked pending in memory either");
    }

    /// <summary>
    /// The history's part fails in its last phase (two value changes with one id, in different partitions), after the
    /// execution items, the change records, their attribute changes and part of their value changes have committed. The
    /// import must find nothing of the batch: no history, and no object without one.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsWithHistoryAsync_ChangeValueWriteFails_ThrowsAndLeavesNothingOfTheBatchAsync()
    {
        var (s, earlierBatch, batch) = await ArrangeAsync();
        var valueChanges = batch.Changes.SelectMany(c => c.AttributeChanges.SelectMany(a => a.ValueChanges)).ToList();
        valueChanges[^1].Id = valueChanges[0].Id;
        batch.Items[0].CausalEdges.Add(NewEdge(s, Guid.NewGuid()));
        var before = await SnapshotAsync();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, batch.Rows);
            Assert.That(async () => await repository.CreateConnectedSystemObjectsWithHistoryAsync(batch.Objects, batch.Items, [.. earlierBatch]),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.UniqueViolation));
        }

        await AssertNothingOfTheBatchRemainsAsync(batch, earlierBatch, before);
        AssertBatchLeftReadyForARetry(batch, edgesOnFirstItems: 1);
    }

    private static CausalEdge NewEdge(Seeded s, Guid id) => new()
    {
        Id = id, EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
        ReasonCode = CausalReasonCode.AuthoritativeSourceDisconnected, CauseDisplayName = "Tina Adams",
        ConnectedSystemId = s.SystemId, ConnectedSystemName = "Directory"
    };

    /// <summary>
    /// A failed batch leaves its in-memory objects and items as they were: no object marked pending, and the items' causal
    /// edges still buffered, which is what a retry of the batch needs.
    /// </summary>
    private static void AssertBatchLeftReadyForARetry(Batch batch, int edgesOnFirstItems)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.Objects.Any(o => o.SynchronisationPending), Is.False, "nothing is marked pending in memory");
            Assert.That(batch.Items[0].CausalEdges, Has.Count.EqualTo(edgesOnFirstItems), "the edge buffers are not drained");
        }
    }

    /// <summary>
    /// The step that finishes the batch, once both parts have committed (the pending mark, the causal edges and the stat
    /// counters, in one transaction), fails on two edges with one id. Nothing of the batch may remain, counters included.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsWithHistoryAsync_FinishingStepFails_ThrowsAndLeavesNothingOfTheBatchAsync()
    {
        var (s, earlierBatch, batch) = await ArrangeAsync();
        var edgeId = Guid.NewGuid();
        foreach (var item in batch.Items.Take(2))
            item.CausalEdges.Add(NewEdge(s, edgeId));

        var before = await SnapshotAsync();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, batch.Rows);
            Assert.That(async () => await repository.CreateConnectedSystemObjectsWithHistoryAsync(batch.Objects, batch.Items, [.. earlierBatch]),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.UniqueViolation));
        }

        await AssertNothingOfTheBatchRemainsAsync(batch, earlierBatch, before);
        AssertBatchLeftReadyForARetry(batch, edgesOnFirstItems: 1);
    }

    /// <summary>
    /// The history's first step fails (an execution item whose id another batch's item already holds), so none of the
    /// history commits, while the objects' part commits its rows and values beside it. The objects must be removed, and
    /// nothing the other batch owns may be touched: its item, outcomes and change record keep the id the failed batch
    /// tried to reuse, and a removal by the history's ids would take them.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsWithHistoryAsync_HistoryHeadFails_RemovesTheObjectsAndLeavesTheOtherBatchsHistoryAsync()
    {
        var (_, earlierBatch, batch) = await ArrangeAsync();
        Guid takenItemId;
        await using (var context = NewContext())
            takenItemId = await context.ActivityRunProfileExecutionItems.Select(i => i.Id).OrderBy(id => id).FirstAsync();
        batch.Items[^1].Id = takenItemId;
        var before = await SnapshotAsync();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, batch.Rows);
            Assert.That(async () => await repository.CreateConnectedSystemObjectsWithHistoryAsync(batch.Objects, batch.Items, [.. earlierBatch]),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.UniqueViolation));
        }

        await AssertNothingOfTheBatchRemainsAsync(batch, earlierBatch, before, new HashSet<Guid> { takenItemId });
        Assert.That(await CountAsync("ActivityRunProfileExecutionItems", "Id", [takenItemId]), Is.EqualTo(1),
            "the other batch's item, whose id the failed batch reused, survives");
    }

    /// <summary>
    /// With change tracking off the items carry no change records, so the history has no rows to partition and the
    /// objects take every connection. The rows written are those the old serial writes wrote.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsWithHistoryAsync_ChangeTrackingOff_WritesWhatTheOldSerialWritesWroteAsync()
    {
        var (_, earlierBatch, oldBatch) = await ArrangeAsync(changeTrackingEnabled: false);
        await WriteTheOldWayAsync(oldBatch, earlierBatch);
        var oldRows = await SnapshotAsync();
        await PostgresTestDatabase.ResetAsync(_connectionString);

        var (_, _, batch) = await ArrangeAsync(changeTrackingEnabled: false);
        Assert.That(ParallelBatchWriter.SplitConnections(Parallelism, batch.Objects.Sum(o => 1 + o.AttributeValues.Count), 0), Is.EqualTo((Parallelism, 0)),
            "sanity: the history gets no connections of its own");
        await using (var context = NewContext())
            await Repository(context, WritePath.Parallel, batch.Rows)
                .CreateConnectedSystemObjectsWithHistoryAsync(batch.Objects, batch.Items, [.. earlierBatch]);
        var rows = await SnapshotAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.Changes, Is.Empty, "sanity: no change records were built");
            Assert.That(rows["ActivityRunProfileExecutionItems"], Has.Count.EqualTo(oldRows["ActivityRunProfileExecutionItems"].Count));
            foreach (var table in BatchTables)
                Assert.That(rows[table], Is.EqualTo(oldRows[table]), $"{table}, every row and column");
        }
    }

    /// <summary>
    /// A call with items and their change records but no objects writes the history alone, partitioned across every
    /// connection.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsWithHistoryAsync_ItemsWithoutObjects_WritesTheHistoryAloneAsync()
    {
        var (_, earlierBatch, batch) = await ArrangeAsync();

        await using (var context = NewContext())
            await Repository(context, WritePath.Parallel, batch.Rows).CreateConnectedSystemObjectsWithHistoryAsync([], batch.Items, [.. earlierBatch]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("ConnectedSystemObjects", "Id", batch.ObjectIds), Is.Zero, "no object was handed over");
            Assert.That(await CountAsync("ActivityRunProfileExecutionItems", "Id", batch.Items.Select(i => i.Id)), Is.EqualTo(batch.Items.Count));
            Assert.That(await CountAsync("ConnectedSystemObjectChangeAttributeValues", "Id", batch.ChangeValueIds), Is.EqualTo(batch.ChangeValueIds.Count));
        }
    }

    /// <summary>
    /// A call with objects but no items writes the objects alone, and marks them pending.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsWithHistoryAsync_ObjectsWithoutItems_WritesTheObjectsAloneAsync()
    {
        var (_, earlierBatch, batch) = await ArrangeAsync();

        await using (var context = NewContext())
            await Repository(context, WritePath.Parallel, batch.Rows).CreateConnectedSystemObjectsWithHistoryAsync(batch.Objects, [], [.. earlierBatch]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("ConnectedSystemObjectAttributeValues", "ConnectedSystemObjectId", batch.ObjectIds),
                Is.EqualTo(batch.Objects.Sum(o => o.AttributeValues.Count)));
            Assert.That(await CountAsync("ActivityRunProfileExecutionItems", "Id", batch.Items.Select(i => i.Id)), Is.Zero, "no item was handed over");
            Assert.That(batch.Objects.All(o => o.SynchronisationPending), Is.True);
        }
    }

    /// <summary>
    /// A batch below the parallel threshold, with no caller transaction, is one transaction of its own: a failure part
    /// way (a value naming no attribute) rolls back everything, and leaves the in-memory batch ready for a retry.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsWithHistoryAsync_SingleTransactionPathFails_ThrowsAndLeavesNothingOfTheBatchAsync()
    {
        var (s, earlierBatch, batch) = await ArrangeAsync();
        batch.Objects[^1].AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = int.MaxValue, StringValue = "poisoned" });
        batch.Items[0].CausalEdges.Add(NewEdge(s, Guid.NewGuid()));
        var before = await SnapshotAsync();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.SingleConnection, batch.Rows);
            Assert.That(async () => await repository.CreateConnectedSystemObjectsWithHistoryAsync(batch.Objects, batch.Items, [.. earlierBatch]),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.ForeignKeyViolation));
        }

        await AssertNothingOfTheBatchRemainsAsync(batch, earlierBatch, before);
        AssertBatchLeftReadyForARetry(batch, edgesOnFirstItems: 1);
    }

    /// <summary>
    /// A caller holding a transaction keeps the whole batch inside it, however large: writes on connections of their own
    /// would commit what the caller then rolls back. Rolled back, nothing remains.
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsWithHistoryAsync_CallerHoldsTransaction_WritesInsideItSoARollbackLeavesNothingAsync()
    {
        var (_, earlierBatch, batch) = await ArrangeAsync();
        var before = await SnapshotAsync();

        await using (var context = NewContext())
        {
            var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
            sync.ConnectionStringForParallelWrites = _connectionString;
            sync.WriteParallelismOverride = Parallelism;
            sync.ParallelWriteRowThreshold = 0;

            await using var transaction = await context.Database.BeginTransactionAsync();
            Assert.That(sync.ShouldWriteInParallel(batch.Rows), Is.False, "a caller's transaction keeps the batch on its connection");
            await sync.CreateConnectedSystemObjectsWithHistoryAsync(batch.Objects, batch.Items, [.. earlierBatch]);
            Assert.That(await context.ConnectedSystemObjectChanges.CountAsync(c => c.ConnectedSystemObjectId == batch.Objects[0].Id), Is.EqualTo(1),
                "sanity: the caller sees the batch inside its transaction");
            await transaction.RollbackAsync();
        }

        await AssertNothingOfTheBatchRemainsAsync(batch, earlierBatch, before);
    }

    #endregion
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL behaviour of <c>SyncRepository.WriteSynchronisationHistoryAsync</c> (#2874): every column of every
/// history table it writes round-trips, the batch commits in one transaction or not at all, on one connection however
/// large it is, the rows a deletion already wrote are brought up to date rather than written again, and the stat
/// counters match those the item writer records. Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SynchronisationHistoryWriteDatabaseTests
{
    private string _connectionString = null!;
    private int _connectedSystemId;
    private int _syncRuleId;
    private int _connectedSystemAttributeId;
    private int _metaverseAttributeId;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL synchronisation history write tests.");
        _connectionString = connectionString;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        await SeedConfigurationAsync();
    }

    /// <summary>The configuration the history names: a Connected System with an attribute, a Metaverse attribute and a rule.</summary>
    private async Task SeedConfigurationAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "JIM Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        type.Attributes.Add(attribute);
        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var mvAttribute = new MetaverseAttribute
        {
            Name = "Display Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, MetaverseObjectTypes = [mvType]
        };
        var rule = new SyncRule
        {
            Name = "HR Import", ConnectedSystem = system, Direction = SyncRuleDirection.Import, ConnectedSystemObjectType = type, MetaverseObjectType = mvType
        };
        seed.AddRange(connectorDefinition, system, type, mvType, mvAttribute, rule);
        await seed.SaveChangesAsync();
        _connectedSystemId = system.Id;
        _syncRuleId = rule.Id;
        _connectedSystemAttributeId = attribute.Id;
        _metaverseAttributeId = mvAttribute.Id;
        _connectedSystemObjectTypeId = type.Id;
    }

    private int _connectedSystemObjectTypeId;

    /// <summary>
    /// A page's history is written while the next page saves its changes (#2924), which can create or delete the objects
    /// its change values refer to. Which referenced objects exist is looked up as the batch is handed off, before any of
    /// those changes, so the history written is the history of the page as it ended: here the referenced object is
    /// deleted after the look-up and before the write, and the value still names it, unmarked.
    /// </summary>
    [Test]
    public async Task WriteSynchronisationHistoryAsync_ReferenceTargetsFoundAtHandOff_WritesThemAsFoundThenAsync()
    {
        var activityId = await SeedActivityAsync();
        var referenced = Guid.NewGuid();
        var notYetWritten = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            seed.ConnectedSystemObjects.Add(new ConnectedSystemObject
            {
                Id = referenced, ConnectedSystemId = _connectedSystemId, TypeId = _connectedSystemObjectTypeId,
                Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var item = new ActivityRunProfileExecutionItem { ActivityId = activityId, ObjectChangeType = ObjectChangeType.NoChange };
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = _connectedSystemId, ConnectedSystemObjectId = Guid.NewGuid(), ChangeType = ObjectChangeType.Added,
            ChangeTime = DateTime.UtcNow, InitiatedByType = ActivityInitiatorType.System
        };
        var attribute = new ConnectedSystemObjectChangeAttribute
        {
            ConnectedSystemChange = change, AttributeName = "member", AttributeType = AttributeDataType.Reference,
            Attribute = new ConnectedSystemObjectTypeAttribute { Id = _connectedSystemAttributeId, Name = "displayName" }
        };
        var existing = new ConnectedSystemObjectChangeAttributeValue
            { ConnectedSystemObjectChangeAttribute = attribute, ValueChangeType = ValueChangeType.Add, StringValue = "CN=Jo", ReferenceValueId = referenced };
        var later = new ConnectedSystemObjectChangeAttributeValue
            { ConnectedSystemObjectChangeAttribute = attribute, ValueChangeType = ValueChangeType.Add, StringValue = "CN=Sam", ReferenceValueId = notYetWritten };
        attribute.ValueChanges.AddRange([existing, later]);
        change.AttributeChanges.Add(attribute);
        item.ConnectedSystemObjectChange = change;

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Sync;
        var batch = repository.PrepareSynchronisationHistory([item], null, true, [], [], 1, 1);

        await repository.FindSynchronisationHistoryReferenceTargetsAsync(batch);
        await ExecuteAsync($"""DELETE FROM "ConnectedSystemObjects" WHERE "Id" = '{referenced}'""");
        await using (var seed = NewContext())
        {
            seed.ConnectedSystemObjects.Add(new ConnectedSystemObject
            {
                Id = notYetWritten, ConnectedSystemId = _connectedSystemId, TypeId = _connectedSystemObjectTypeId,
                Status = ConnectedSystemObjectStatus.Normal, Created = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using (var writerContext = NewContext())
            await new PostgresDataRepository(writerContext).Sync.WriteSynchronisationHistoryAsync(batch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await ReadColumnAsync("ConnectedSystemObjectChangeAttributeValues", existing.Id, "ReferenceValueId"), Is.EqualTo(referenced),
                "the object existed as the page ended, so the value names it");
            Assert.That(await ReadColumnAsync("ConnectedSystemObjectChangeAttributeValues", existing.Id, "IsUnresolvedReference"), Is.EqualTo(false));
            Assert.That(await ReadColumnAsync("ConnectedSystemObjectChangeAttributeValues", later.Id, "ReferenceValueId"), Is.Null,
                "the object did not exist as the page ended, so the value is left for the reference fix-up, as it was before the overlap");
            Assert.That(await ReadColumnAsync("ConnectedSystemObjectChangeAttributeValues", later.Id, "IsUnresolvedReference"), Is.EqualTo(true));
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedActivityAsync()
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            TargetName = "Full Synchronisation", TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.InProgress, InitiatedByType = ActivityInitiatorType.System
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        return activity.Id;
    }

    private SynchronisationHistoryBatchBuilder NewFlush(Guid activityId) =>
        new(activityId, _connectedSystemId, _syncRuleId, _connectedSystemAttributeId, _metaverseAttributeId);

    [Test]
    public async Task WriteSynchronisationHistoryAsync_FullyPopulatedBatch_RoundTripsEveryColumnOfEveryTableAsync()
    {
        var activityId = await SeedActivityAsync();
        var existingChangeId = await SeedMetaverseObjectChangeAsync(activityId);
        var flush = NewFlush(activityId);
        var append = SynchronisationHistoryBatchBuilder.AppendTo(existingChangeId);
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Sync;
        var batch = repository.PrepareSynchronisationHistory(flush.Items, null, true, [flush.MetaverseObjectChange], [append], 1, 1);

        var released = await repository.WriteSynchronisationHistoryAsync(batch);

        Assert.That(released, Is.True, "the items were written outside change tracking, so the run releases them");
        foreach (var item in flush.Items)
            await AssertRowAsync("ActivityRunProfileExecutionItems", item.Id, item);
        foreach (var outcome in batch.Outcomes)
            await AssertRowAsync("ActivityRunProfileExecutionItemSyncOutcomes", outcome.Id, outcome);
        foreach (var change in new[] { flush.OutcomeChangeRecord, flush.ItemChangeRecord })
            await AssertConnectedSystemObjectChangeAsync(change);
        await AssertRowAsync("CausalEdges", flush.Edge.Id, flush.Edge);
        await AssertRowAsync("ActivityRunProfileExecutionItemMatchCandidates", flush.Candidate.Id, flush.Candidate);
        await AssertMetaverseObjectChangeAsync(flush.MetaverseObjectChange, rowExpected: true);
        await AssertMetaverseObjectChangeAsync(append, rowExpected: false);
        var counters = await ReadCountersAsync(activityId);
        Assert.That(counters, Is.EquivalentTo(batch.CounterDeltas.Select(d => (d.Key.Dimension, d.Key.Key, d.Value))));
    }

    [Test]
    public async Task WriteSynchronisationHistoryAsync_LastRowsFail_LeavesNothingOfTheBatchAsync()
    {
        var activityId = await SeedActivityAsync();
        var flush = NewFlush(activityId);
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Sync;
        // An append to a change record that does not exist: its rows are the last the write makes, and fail its foreign key.
        var batch = repository.PrepareSynchronisationHistory(flush.Items, null, true, [flush.MetaverseObjectChange],
            [SynchronisationHistoryBatchBuilder.AppendTo(Guid.NewGuid())], 1, 1);

        Assert.That(() => repository.WriteSynchronisationHistoryAsync(batch), Throws.InstanceOf<PostgresException>());

        using (Assert.EnterMultipleScope())
        {
            foreach (var table in HistoryTables)
                Assert.That(await CountAsync(table), Is.Zero, $"nothing of the batch is left in {table}");
            Assert.That(await CountAsync("ActivityStatCounters"), Is.Zero, "nor are its counters");
        }
    }

    [Test]
    public async Task WriteSynchronisationHistoryAsync_ItemWrittenWithItsDeletion_IsUpdatedNotInsertedAsync()
    {
        var activityId = await SeedActivityAsync();
        var flush = NewFlush(activityId);
        var item = flush.ProjectedItem;
        item.Id = Guid.NewGuid();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Sync;
        // The deletion's transaction wrote the item's row and its change record (#2807), before its outcomes were known.
        var summaryWhenDeleted = item.OutcomeSummary;
        item.OutcomeSummary = "CsoDeleted:1";
        await repository.InsertRpeiRowsAsync([item]);
        flush.ItemChangeRecord.ActivityRunProfileExecutionItemId = item.Id;
        await repository.PersistCsoChangesAsync([flush.ItemChangeRecord]);
        item.OutcomeSummary = summaryWhenDeleted;

        var batch = repository.PrepareSynchronisationHistory(flush.Items, new HashSet<Guid> { item.Id }, true,
            [flush.MetaverseObjectChange], [], 1, 1);
        await repository.WriteSynchronisationHistoryAsync(batch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("ActivityRunProfileExecutionItems", item.Id), Is.EqualTo(1), "the row is not written twice");
            Assert.That(await ReadColumnAsync("ActivityRunProfileExecutionItems", item.Id, "OutcomeSummary"), Is.EqualTo(summaryWhenDeleted),
                "the row is brought up to date");
            Assert.That(await CountAsync("ActivityRunProfileExecutionItemSyncOutcomes"), Is.EqualTo(2), "its outcomes are written");
            Assert.That(await CountWhereAsync("ConnectedSystemObjectChanges", "ActivityRunProfileExecutionItemId", item.Id), Is.EqualTo(1),
                "its change record is not written twice");
        }
    }

    [Test]
    public async Task WriteSynchronisationHistoryAsync_BatchLargerThanTheParallelThreshold_WritesInOneTransactionOnOneConnectionAsync()
    {
        var activityId = await SeedActivityAsync();
        await using var ctx = NewContext();

        // Make the parallel path available, and prove it would engage for this many rows, so a write that stays on one
        // connection does so because it holds its transaction (#2872), not because parallel writing was unavailable.
        using var parallelPath = ParallelWritePathScope.Enter();
        var repository = new PostgresDataRepository(ctx).Sync;
        var flush = NewFlush(activityId);
        var items = flush.Items;
        while (items.Count < ParallelWritePathScope.Threshold)
            items.Add(new ActivityRunProfileExecutionItem { ActivityId = activityId, ObjectChangeType = ObjectChangeType.NoChange, DisplayNameSnapshot = $"Item {items.Count}" });
        ParallelWritePathScope.AssertEngaged(items.Count);

        var batch = repository.PrepareSynchronisationHistory(items, null, true, [flush.MetaverseObjectChange], [], 1, 1);
        await repository.WriteSynchronisationHistoryAsync(batch);

        // Rows a transaction writes carry its id as their xmin: one id across every table is one transaction, which runs
        // on one connection.
        var transactionIds = new HashSet<string>();
        foreach (var table in HistoryTables)
            transactionIds.UnionWith(await ReadTransactionIdsAsync(table));
        Assert.That(transactionIds, Has.Count.EqualTo(1), "every row of the batch was written by one transaction");
    }

    [Test]
    public async Task WriteSynchronisationHistoryAsync_CountersMatchThoseTheItemWriterRecordsAsync()
    {
        var viaItemWriter = await SeedActivityAsync();
        var viaHistoryWrite = await SeedActivityAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Sync;

        await repository.BulkInsertRpeisAsync(NewFlush(viaItemWriter).Items);
        var flush = NewFlush(viaHistoryWrite);
        await repository.WriteSynchronisationHistoryAsync(
            repository.PrepareSynchronisationHistory(flush.Items, null, false, [], [], 1, 1));

        var expected = await ReadCountersAsync(viaItemWriter);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected, Is.Not.Empty, "precondition: the item writer recorded counters");
            Assert.That(await ReadCountersAsync(viaHistoryWrite), Is.EquivalentTo(expected));
        }
    }

    [Test]
    public async Task WriteSynchronisationHistoryAsync_RepositoryHoldsATransaction_ThrowsAndWritesNothingAsync()
    {
        var activityId = await SeedActivityAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Sync;
        var flush = NewFlush(activityId);
        var batch = repository.PrepareSynchronisationHistory(flush.Items, null, true, [], [], 1, 1);

        await using (await repository.BeginTransactionAsync())
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(repository.HasOpenTransaction, Is.True);
                Assert.That(() => repository.WriteSynchronisationHistoryAsync(batch),
                    Throws.InvalidOperationException.With.Message.Contain("its own transaction"));
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.HasOpenTransaction, Is.False);
            Assert.That(repository.SupportsConcurrentHistoryWrites, Is.True);
            Assert.That(await CountAsync("ActivityRunProfileExecutionItems"), Is.Zero);
        }
    }

    #region Helpers

    private static readonly string[] HistoryTables =
    [
        "ActivityRunProfileExecutionItems", "ActivityRunProfileExecutionItemSyncOutcomes", "ConnectedSystemObjectChanges",
        "ConnectedSystemObjectChangeAttributes", "ConnectedSystemObjectChangeAttributeValues", "CausalEdges",
        "ActivityRunProfileExecutionItemMatchCandidates", "MetaverseObjectChanges", "MetaverseObjectChangeAttributes",
        "MetaverseObjectChangeAttributeValues"
    ];

    /// <summary>A Metaverse Object change record an earlier flush wrote, for a batch to append to.</summary>
    private async Task<Guid> SeedMetaverseObjectChangeAsync(Guid activityId)
    {
        await using var ctx = NewContext();
        var change = new MetaverseObjectChange
        {
            ActivityId = activityId, ChangeTime = DateTime.UtcNow, ChangeType = ObjectChangeType.Projected, MetaverseObjectId = Guid.NewGuid()
        };
        await new PostgresDataRepository(ctx).Sync.PersistPendingMvoChangesAsync([change], []);
        return change.Id;
    }

    private async Task AssertConnectedSystemObjectChangeAsync(ConnectedSystemObjectChange change)
    {
        await AssertRowAsync("ConnectedSystemObjectChanges", change.Id, change,
            new() { ["DeletedObjectTypeId"] = change.DeletedObjectType?.Id });
        foreach (var attribute in change.AttributeChanges)
        {
            await AssertRowAsync("ConnectedSystemObjectChangeAttributes", attribute.Id, attribute,
                new() { ["ConnectedSystemChangeId"] = change.Id, ["AttributeId"] = attribute.AttributeIdForWrite() });
            foreach (var value in attribute.ValueChanges)
                await AssertRowAsync("ConnectedSystemObjectChangeAttributeValues", value.Id, value,
                    new() { ["ConnectedSystemObjectChangeAttributeId"] = attribute.Id });
        }
    }

    private async Task AssertMetaverseObjectChangeAsync(MetaverseObjectChange change, bool rowExpected)
    {
        if (rowExpected)
            await AssertRowAsync("MetaverseObjectChanges", change.Id, change);
        foreach (var attribute in change.AttributeChanges)
        {
            await AssertRowAsync("MetaverseObjectChangeAttributes", attribute.Id, attribute,
                new() { ["MetaverseObjectChangeId"] = change.Id, ["AttributeId"] = attribute.AttributeIdForWrite() });
            foreach (var value in attribute.ValueChanges)
                await AssertRowAsync("MetaverseObjectChangeAttributeValues", value.Id, value,
                    new() { ["MetaverseObjectChangeAttributeId"] = attribute.Id });
        }
    }

    /// <summary>
    /// Reads the row with the given id and checks every column against the property of the same name on the entity, or
    /// the value given for it, so a column the writer leaves out or writes wrongly fails, and so does a column added
    /// later that this test does not know about.
    /// </summary>
    private async Task AssertRowAsync(string table, Guid id, object entity, Dictionary<string, object?>? expectedByColumn = null)
    {
        var row = await ReadRowAsync(table, id);
        Assert.That(row, Is.Not.Null, $"{table} has no row {id}");
        foreach (var (column, actual) in row!)
        {
            object? expected;
            if (expectedByColumn != null && expectedByColumn.TryGetValue(column, out var given))
                expected = given;
            else
            {
                var property = entity.GetType().GetProperty(column, BindingFlags.Public | BindingFlags.Instance);
                Assert.That(property, Is.Not.Null, $"{table}.{column} has no property of the same name; give the test its expected value");
                expected = property!.GetValue(entity);
            }

            Assert.That(Normalise(actual), Is.EqualTo(Normalise(expected)), $"{table}.{column}");
        }
    }

    private static object? Normalise(object? value) => value switch
    {
        null => null,
        Enum e => Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture),
        // PostgreSQL keeps microseconds.
        DateTime d => new DateTime(d.Ticks - d.Ticks % 10, DateTimeKind.Unspecified),
        DateTimeOffset o => new DateTime(o.UtcDateTime.Ticks - o.UtcDateTime.Ticks % 10, DateTimeKind.Unspecified),
        _ => value
    };

    private async Task<Dictionary<string, object?>?> ReadRowAsync(string table, Guid id)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        // The table name is fixed in code.
        await using var command = new NpgsqlCommand($"""SELECT * FROM "{table}" WHERE "Id" = @id""", connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        var row = new Dictionary<string, object?>();
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    private async Task<object?> ReadColumnAsync(string table, Guid id, string column)
        => (await ReadRowAsync(table, id))?[column];

    private async Task<long> CountAsync(string table, Guid? id = null)
        => id is { } value ? await CountWhereAsync(table, "Id", value) : await ScalarAsync<long>($"""SELECT count(*) FROM "{table}" """);

    private async Task<long> CountWhereAsync(string table, string column, Guid value)
        => await ScalarAsync<long>($"""SELECT count(*) FROM "{table}" WHERE "{column}" = @value""",
            new NpgsqlParameter("value", NpgsqlDbType.Uuid) { Value = value });

    private async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<List<string>> ReadTransactionIdsAsync(string table)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""SELECT DISTINCT xmin::text FROM "{table}" """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private async Task<List<(ActivityStatDimension Dimension, string Key, long Count)>> ReadCountersAsync(Guid activityId)
    {
        await using var ctx = NewContext();
        return (await ctx.ActivityStatCounters.AsNoTracking().Where(c => c.ActivityId == activityId).ToListAsync())
            .Select(c => (c.Dimension, c.Key, c.Count))
            .ToList();
    }

    #endregion
}

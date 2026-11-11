// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
/// Real-PostgreSQL proof that the change record writer's parallel path (#2909), which import update batches, exports and
/// deletions reach through <c>PersistCsoChangesAsync</c>, writes exactly what its single-connection path writes, spreads
/// a large group's value changes across connections rather than writing them on one, and leaves nothing behind when it
/// fails. Opt-in via <c>JIM_TEST_RESET_*</c>.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class CsoChangeRecordParallelWriteDatabaseTests
{
    private const int Parallelism = 4;
    private static readonly DateTime ChangeTime = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ChangeTables =
        ["ConnectedSystemObjectChanges", "ConnectedSystemObjectChangeAttributes", "ConnectedSystemObjectChangeAttributeValues"];

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL change record parallel write tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    private enum WritePath { SingleConnection, Parallel }

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

    private sealed record Seeded(int SystemId, ConnectedSystemObjectTypeAttribute Name, ConnectedSystemObjectTypeAttribute Member, List<Guid> People);

    /// <summary>
    /// A Connected System and twenty people the change records' references can name.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        int systemId, typeId;
        ConnectedSystemObjectTypeAttribute externalId, name, member;
        await using (var seed = NewContext())
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "object", ConnectedSystem = system, Selected = true };
            externalId = new ConnectedSystemObjectTypeAttribute
            {
                Name = "objectGUID", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
            };
            name = new ConnectedSystemObjectTypeAttribute
            {
                Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true
            };
            member = new ConnectedSystemObjectTypeAttribute
            {
                Name = "member", ConnectedSystemObjectType = csType, Type = AttributeDataType.Reference,
                AttributePlurality = AttributePlurality.MultiValued, Selected = true
            };
            csType.Attributes.AddRange([externalId, name, member]);
            seed.AddRange(connectorDefinition, system, csType);
            await seed.SaveChangesAsync();
            systemId = system.Id;
            typeId = csType.Id;
        }

        var nextId = IdSequence(1);
        var people = Enumerable.Range(0, 20).Select(_ =>
        {
            var cso = new ConnectedSystemObject
            {
                Id = nextId(), ConnectedSystemId = systemId, TypeId = typeId, ExternalIdAttributeId = externalId.Id,
                Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined, Created = ChangeTime
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = nextId(), AttributeId = externalId.Id, GuidValue = nextId() });
            return cso;
        }).ToList();

        await using (var context = NewContext())
            await Repository(context, WritePath.SingleConnection, 0).CreateConnectedSystemObjectsAsync(people);

        return new Seeded(systemId, name, member, people.Select(p => p.Id).ToList());
    }

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

    /// <summary>
    /// Change records as an update batch writes them: fifty with a name change each, and one group whose membership
    /// change carries <paramref name="groupMembers"/> value changes, naming written people by id, an object with no row
    /// by id, and a DN that matched nothing.
    /// </summary>
    private static List<ConnectedSystemObjectChange> BuildChanges(Seeded s, int groupMembers = 2_000)
    {
        var nextId = IdSequence(2);
        ConnectedSystemObjectChange NewChange() => new()
        {
            Id = nextId(), ConnectedSystemId = s.SystemId, ConnectedSystemObjectId = nextId(), ChangeType = ObjectChangeType.Updated,
            ChangeTime = ChangeTime, InitiatedByType = ActivityInitiatorType.System, InitiatedByName = "Full Import"
        };
        ConnectedSystemObjectChangeAttribute NewAttributeChange(ConnectedSystemObjectChange change, ConnectedSystemObjectTypeAttribute attribute)
        {
            var attributeChange = new ConnectedSystemObjectChangeAttribute
            {
                Id = nextId(), Attribute = attribute, AttributeName = attribute.Name, AttributeType = attribute.Type, ConnectedSystemChange = change
            };
            change.AttributeChanges.Add(attributeChange);
            return attributeChange;
        }

        var changes = new List<ConnectedSystemObjectChange>();
        for (var i = 0; i < 50; i++)
        {
            var change = NewChange();
            var nameChange = NewAttributeChange(change, s.Name);
            nameChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(nameChange, ValueChangeType.Remove, $"Old {i}") { Id = nextId() });
            nameChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(nameChange, ValueChangeType.Add, $"New {i}") { Id = nextId() });
            changes.Add(change);
        }

        var group = NewChange();
        var members = NewAttributeChange(group, s.Member);
        for (var m = 0; m < groupMembers; m++)
        {
            var value = (m % 3) switch
            {
                0 => new ConnectedSystemObjectChangeAttributeValue(members, ValueChangeType.Add, new ConnectedSystemObject { Id = s.People[m % s.People.Count] }),
                1 => new ConnectedSystemObjectChangeAttributeValue(members, ValueChangeType.Add, new ConnectedSystemObject { Id = nextId() }),
                _ => new ConnectedSystemObjectChangeAttributeValue(members, ValueChangeType.Remove, $"CN=Member {m},OU=People,DC=glitterband,DC=local")
            };
            value.Id = nextId();
            members.ValueChanges.Add(value);
        }

        changes.Insert(25, group);
        return changes;
    }

    private static int Rows(List<ConnectedSystemObjectChange> changes) =>
        changes.Sum(c => 1 + c.AttributeChanges.Sum(a => 1 + a.ValueChanges.Count));

    private static List<Guid> ValueIds(List<ConnectedSystemObjectChange> changes) =>
        changes.SelectMany(c => c.AttributeChanges.SelectMany(a => a.ValueChanges.Select(v => v.Id))).ToList();

    private async Task<Dictionary<string, List<string>>> SnapshotAsync()
    {
        var snapshot = new Dictionary<string, List<string>>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        foreach (var table in ChangeTables)
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

    private async Task<long> CountAsync(string table, IEnumerable<Guid> ids)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""SELECT count(*) FROM "{table}" WHERE "Id" = ANY(@ids)""", connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Test]
    public async Task PersistCsoChangesAsync_ParallelPath_WritesExactlyWhatTheSingleConnectionPathWritesAsync()
    {
        var s = await SeedAsync();
        var single = BuildChanges(s);
        await using (var context = NewContext())
            await Repository(context, WritePath.SingleConnection, Rows(single)).PersistCsoChangesAsync(single, allowParallelWrite: true);
        var singleRows = await SnapshotAsync();
        await PostgresTestDatabase.ResetAsync(_connectionString);

        s = await SeedAsync();
        var parallel = BuildChanges(s);
        await using (var context = NewContext())
            await Repository(context, WritePath.Parallel, Rows(parallel)).PersistCsoChangesAsync(parallel, allowParallelWrite: true);
        var parallelRows = await SnapshotAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(singleRows["ConnectedSystemObjectChangeAttributeValues"], Has.Count.EqualTo(ValueIds(single).Count), "sanity: every value change written");
            Assert.That(singleRows["ConnectedSystemObjectChangeAttributeValues"].Count(r => r.Contains("\"IsUnresolvedReference\": true")), Is.GreaterThan(0),
                "sanity: references to objects with no row are marked unresolved");
            foreach (var table in ChangeTables)
                Assert.That(parallelRows[table], Is.EqualTo(singleRows[table]), $"{table}, every row and column");
        }
    }

    /// <summary>
    /// A large group's value changes are written across several connections, each committing its own part: with one
    /// value change's id held by another session's uncommitted row, the connection writing that row waits, and the
    /// others' value changes are committed meanwhile. On one connection, as before #2909, every value change waits.
    /// </summary>
    [Test]
    public async Task PersistCsoChangesAsync_ParallelPath_CommitsValueChangesOnSeveralConnectionsAsync()
    {
        var s = await SeedAsync();
        var changes = BuildChanges(s);
        var valueIds = ValueIds(changes);
        var heldId = valueIds[^1];

        // Another session's uncommitted value change holding the id of the last value change written.
        await using var blockerConnection = new NpgsqlConnection(_connectionString);
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await HoldValueChangeIdAsync(blockerConnection, blockerTransaction, s, heldId);

        await using var context = NewContext();
        var repository = Repository(context, WritePath.Parallel, Rows(changes));
        var write = Task.Run(() => repository.PersistCsoChangesAsync(changes, allowParallelWrite: true));

        long committedWhileOneWaits = 0;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !write.IsCompleted)
            {
                committedWhileOneWaits = await CountAsync("ConnectedSystemObjectChangeAttributeValues", valueIds.Where(id => id != heldId));
                if (committedWhileOneWaits >= valueIds.Count / 2)
                    break;
                await Task.Delay(100);
            }
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
        }

        await write;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(committedWhileOneWaits, Is.GreaterThanOrEqualTo(valueIds.Count / 2),
                "the value changes on the connections not waiting are committed while one waits");
            Assert.That(await CountAsync("ConnectedSystemObjectChangeAttributeValues", valueIds), Is.EqualTo(valueIds.Count),
                "and every value change is written once the other session lets go");
        }
    }

    /// <summary>
    /// A caller that does not ask for parallel writes (an export, which writes several batches at once and must not take
    /// the parallelism's share of the pool for each) keeps its change records on one connection however large they are:
    /// with one value change held by another session, nothing of the write is committed.
    /// </summary>
    [Test]
    public async Task PersistCsoChangesAsync_ParallelWriteNotAllowed_WritesOnOneConnectionAsync()
    {
        var s = await SeedAsync();
        var changes = BuildChanges(s);
        var valueIds = ValueIds(changes);
        var heldId = valueIds[^1];

        await using var blockerConnection = new NpgsqlConnection(_connectionString);
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await HoldValueChangeIdAsync(blockerConnection, blockerTransaction, s, heldId);

        await using var context = NewContext();
        var repository = Repository(context, WritePath.Parallel, Rows(changes));
        var write = Task.Run(() => repository.PersistCsoChangesAsync(changes));

        long committedWhileOneWaits;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            committedWhileOneWaits = await CountAsync("ConnectedSystemObjectChangeAttributeValues", valueIds.Where(id => id != heldId));
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
        }

        await write;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(committedWhileOneWaits, Is.Zero, "one connection, one transaction: nothing commits while a row of it waits");
            Assert.That(await CountAsync("ConnectedSystemObjectChangeAttributeValues", valueIds), Is.EqualTo(valueIds.Count));
        }
    }

    /// <summary>
    /// Another session's uncommitted value change holding <paramref name="heldId"/>, so the write's row with that id waits
    /// for the session to end.
    /// </summary>
    private static async Task HoldValueChangeIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Seeded s, Guid heldId)
    {
        await using var blocker = new NpgsqlCommand(
            """
            INSERT INTO "ConnectedSystemObjectChanges" ("Id", "ConnectedSystemId", "ChangeTime", "ChangeType", "InitiatedByType") VALUES (@change, @system, now(), 0, 0);
            INSERT INTO "ConnectedSystemObjectChangeAttributes" ("Id", "ConnectedSystemChangeId", "AttributeName", "AttributeType") VALUES (@attribute, @change, 'member', 0);
            INSERT INTO "ConnectedSystemObjectChangeAttributeValues" ("Id", "ConnectedSystemObjectChangeAttributeId", "ValueChangeType", "IsUnresolvedReference", "IsPendingExportStub")
                VALUES (@held, @attribute, 0, false, false);
            """, connection, transaction);
        blocker.Parameters.AddWithValue("change", Guid.NewGuid());
        blocker.Parameters.AddWithValue("system", s.SystemId);
        blocker.Parameters.AddWithValue("attribute", Guid.NewGuid());
        blocker.Parameters.AddWithValue("held", heldId);
        await blocker.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The value changes fail (two with one id, in different partitions) after the change rows and attribute changes
    /// have committed, and part of the value changes with them. Nothing of the write may remain.
    /// </summary>
    [Test]
    public async Task PersistCsoChangesAsync_ParallelValueWriteFails_ThrowsAndLeavesNothingOfTheWriteAsync()
    {
        var s = await SeedAsync();
        var changes = BuildChanges(s);
        var values = changes.SelectMany(c => c.AttributeChanges.SelectMany(a => a.ValueChanges)).ToList();
        values[^1].Id = values[0].Id;

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, Rows(changes));
            Assert.That(async () => await repository.PersistCsoChangesAsync(changes, allowParallelWrite: true),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.UniqueViolation));
        }

        var after = await SnapshotAsync();
        using (Assert.EnterMultipleScope())
        {
            foreach (var table in ChangeTables)
                Assert.That(after[table], Is.Empty, $"nothing of the failed write remains in {table}");
        }
    }
}

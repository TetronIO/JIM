// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL proof that the Pending Export create's parallel path (#2919), which a synchronisation's page flush,
/// deletion staging, reference recall, Synchronised Deprovisioning and Synchronisation Rule deletion recall all reach
/// through <c>CreatePendingExportsAsync</c>, writes exactly what its single-connection path writes, spreads a large
/// group's changes across connections rather than writing them on one, and leaves nothing of a failed call behind while
/// leaving every other call's rows alone.
/// </summary>
/// <remarks>
/// Each test forces the path it means to exercise (four connections and a threshold of zero, or a threshold no call
/// reaches) and asserts the decision with the writers' own predicate. Opt-in via <c>JIM_TEST_RESET_*</c>.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportCreateParallelWriteDatabaseTests
{
    private const int Parallelism = 4;
    private const int GroupMembers = 3_000;
    private static readonly DateTime StagedAt = new(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string[] PendingExportTables = ["PendingExports", "PendingExportAttributeValueChanges"];

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Pending Export parallel create tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    #region Arrangement

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

    private sealed record Seeded(
        int SystemId, int SyncRuleId, ConnectedSystemObjectTypeAttribute Name, ConnectedSystemObjectTypeAttribute EmployeeNumber,
        ConnectedSystemObjectTypeAttribute Expires, ConnectedSystemObjectTypeAttribute ProxyAddresses, ConnectedSystemObjectTypeAttribute Member,
        ConnectedSystemObjectTypeAttribute Photo, List<Guid> Objects, List<Guid> People);

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

    /// <summary>
    /// A Connected System with the attributes a mixed batch changes, a Synchronisation Rule a provisioning export names,
    /// 150 Connected System Objects the exports are for, and twenty Metaverse Objects they come from.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        var nextId = IdSequence(1);
        await using var seed = NewContext();

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband LDAP", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "object", ConnectedSystem = system, Selected = true };
        ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType type, AttributePlurality plurality) => new()
        {
            Name = name, ConnectedSystemObjectType = csType, Type = type, AttributePlurality = plurality, Selected = true
        };
        var name = Attribute("displayName", AttributeDataType.Text, AttributePlurality.SingleValued);
        var employeeNumber = Attribute("employeeNumber", AttributeDataType.Number, AttributePlurality.SingleValued);
        var expires = Attribute("accountExpires", AttributeDataType.DateTime, AttributePlurality.SingleValued);
        var proxyAddresses = Attribute("proxyAddresses", AttributeDataType.Text, AttributePlurality.MultiValued);
        var member = Attribute("member", AttributeDataType.Reference, AttributePlurality.MultiValued);
        var photo = Attribute("thumbnailPhoto", AttributeDataType.Binary, AttributePlurality.SingleValued);
        csType.Attributes.AddRange([name, employeeNumber, expires, proxyAddresses, member, photo]);

        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var syncRule = new SyncRule
        {
            Name = "Provision people", ConnectedSystem = system, ConnectedSystemObjectType = csType,
            MetaverseObjectType = mvoType, Direction = SyncRuleDirection.Export
        };
        var objects = Enumerable.Range(0, 150)
            .Select(_ => new ConnectedSystemObject { Id = nextId(), Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal, Created = StagedAt })
            .ToList();
        var people = Enumerable.Range(0, 20)
            .Select(_ => new MetaverseObject { Id = nextId(), Type = mvoType, Created = StagedAt })
            .ToList();

        seed.AddRange(connectorDefinition, system, csType, mvoType, syncRule);
        seed.AddRange(objects);
        seed.AddRange(people);
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, syncRule.Id, name, employeeNumber, expires, proxyAddresses, member, photo,
            objects.Select(o => o.Id).ToList(), people.Select(p => p.Id).ToList());
    }

    /// <summary>
    /// A synchronisation page's exports: forty Updates of single-valued attributes (text, number, date, binary, one
    /// cleared), ten provisioning Creates naming their Synchronisation Rule and source with multi-valued additions, ten
    /// Deletes (some with no changes at all), and one group, in the middle, whose membership change carries
    /// <paramref name="groupMembers"/> reference changes: resolved, unresolved and removed.
    /// </summary>
    private static List<PendingExport> BuildBatch(Seeded s, int seed = 2, int firstObject = 0, int groupMembers = GroupMembers)
    {
        var nextId = IdSequence(seed);
        var objectIndex = firstObject;
        PendingExport NewExport(PendingExportChangeType changeType) => new()
        {
            Id = nextId(), ConnectedSystemId = s.SystemId, ConnectedSystemObjectId = s.Objects[objectIndex++], ChangeType = changeType,
            Status = PendingExportStatus.Pending, CreatedAt = StagedAt, SourceMetaverseObjectId = s.People[objectIndex % s.People.Count]
        };
        PendingExportAttributeValueChange NewChange(ConnectedSystemObjectTypeAttribute attribute, PendingExportAttributeChangeType changeType,
            Action<PendingExportAttributeValueChange>? set = null)
        {
            var change = new PendingExportAttributeValueChange
            {
                Id = nextId(), AttributeId = attribute.Id, ChangeType = changeType, SyncRuleId = s.SyncRuleId, SyncRuleName = "Provision people"
            };
            set?.Invoke(change);
            return change;
        }

        var exports = new List<PendingExport>();
        for (var i = 0; i < 40; i++)
        {
            var update = NewExport(PendingExportChangeType.Update);
            if (i % 4 == 0)
            {
                update.Status = PendingExportStatus.ExportNotConfirmed;
                update.ErrorCount = 2;
                update.FailingSince = StagedAt.AddHours(-1);
                update.LastAttemptedAt = StagedAt.AddMinutes(-5);
                update.LastErrorMessage = "the directory refused the change";
                update.LastErrorStackTrace = "at Glitterband.Write()";
            }

            update.RecordStager(nextId());
            update.AttributeValueChanges.Add(NewChange(s.Name, PendingExportAttributeChangeType.Update, c => { c.StringValue = $"Person {i}"; }));
            update.AttributeValueChanges.Add(NewChange(s.EmployeeNumber, PendingExportAttributeChangeType.Update, c => { c.IntValue = 1_000 + i; }));
            update.AttributeValueChanges.Add(NewChange(s.Expires, PendingExportAttributeChangeType.Update, c => { c.DateTimeValue = StagedAt.AddDays(i); }));
            update.AttributeValueChanges.Add(i % 2 == 0
                ? NewChange(s.Photo, PendingExportAttributeChangeType.Update, c => { c.ByteValue = [1, 2, (byte)i]; })
                : NewChange(s.Photo, PendingExportAttributeChangeType.RemoveAll));
            exports.Add(update);
        }

        for (var i = 0; i < 10; i++)
        {
            var create = NewExport(PendingExportChangeType.Create);
            create.ProvisioningSyncRuleId = s.SyncRuleId;
            create.HasUnresolvedReferences = i % 3 == 0;
            create.RecordStager(nextId());
            create.AttributeValueChanges.Add(NewChange(s.Name, PendingExportAttributeChangeType.Add, c => { c.StringValue = $"New person {i}"; }));
            for (var p = 0; p < 3; p++)
                create.AttributeValueChanges.Add(NewChange(s.ProxyAddresses, PendingExportAttributeChangeType.Add, c => { c.StringValue = $"smtp:new{i}.{p}@glitterband.local"; }));
            create.AttributeValueChanges.Add(NewChange(s.EmployeeNumber, PendingExportAttributeChangeType.Add, c =>
            {
                c.LongValue = 9_000_000_000L + i;
                c.DecimalValue = 1.5m + i;
                c.GuidValue = nextId();
                c.BoolValue = i % 2 == 0;
                c.AwaitsInitialPassword = i == 1;
                c.AwaitsExchange = i == 2;
            }));
            exports.Add(create);
        }

        var group = NewExport(PendingExportChangeType.Update);
        group.HasUnresolvedReferences = true;
        for (var m = 0; m < groupMembers; m++)
        {
            var change = (m % 3) switch
            {
                0 => NewChange(s.Member, PendingExportAttributeChangeType.Add, c =>
                {
                    c.StringValue = $"CN=Member {m},OU=People,DC=glitterband,DC=local";
                    c.ResolvedReferenceCsoId = s.Objects[m % s.Objects.Count];
                }),
                1 => NewChange(s.Member, PendingExportAttributeChangeType.Add, c => { c.UnresolvedReferenceValue = nextId().ToString(); }),
                _ => NewChange(s.Member, PendingExportAttributeChangeType.Remove, c =>
                {
                    c.StringValue = $"CN=Former {m},OU=People,DC=glitterband,DC=local";
                    c.Status = PendingExportAttributeChangeStatus.ExportedNotConfirmed;
                    c.ExportAttemptCount = 1;
                    c.LastExportedAt = StagedAt.AddMinutes(-1);
                    c.LastImportedValue = "CN=Former";
                })
            };
            group.AttributeValueChanges.Add(change);
        }

        exports.Insert(25, group);

        for (var i = 0; i < 10; i++)
        {
            var delete = NewExport(PendingExportChangeType.Delete);
            if (i % 2 == 0)
                delete.AttributeValueChanges.Add(NewChange(s.Name, PendingExportAttributeChangeType.Update, c => { c.StringValue = $"Departed {i}"; }));
            exports.Add(delete);
        }

        return exports;
    }

    private static int Rows(List<PendingExport> exports) => exports.Sum(pe => 1 + pe.AttributeValueChanges.Count);

    private static List<Guid> ChangeIds(IEnumerable<PendingExport> exports) =>
        exports.SelectMany(pe => pe.AttributeValueChanges.Select(c => c.Id)).ToList();

    /// <summary>
    /// Every column of every row of both Pending Export tables, as the database holds them, in a stable order.
    /// </summary>
    private async Task<Dictionary<string, List<string>>> SnapshotAsync()
    {
        var snapshot = new Dictionary<string, List<string>>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        foreach (var table in PendingExportTables)
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

    private async Task<long> CountAsync(string table, string column, IEnumerable<Guid> ids)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""SELECT count(*) FROM "{table}" WHERE "{column}" = ANY(@ids)""", connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Another call's exports, written and committed before the call under test: the rows a failed call must never
    /// remove. They are for the last objects, which <see cref="BuildBatch"/>'s default batch does not touch.
    /// </summary>
    private async Task<List<PendingExport>> WriteAnotherCallAsync(Seeded s)
    {
        var other = BuildBatch(s, seed: 99, firstObject: 70, groupMembers: 30).Take(8).ToList();
        await using var context = NewContext();
        await Repository(context, WritePath.SingleConnection, Rows(other)).CreatePendingExportsAsync(other);
        return other;
    }

    /// <summary>
    /// Asserts nothing of the failed call is in either table, and the other call's rows are exactly as they were.
    /// </summary>
    private async Task AssertNothingOfTheCallRemainsAsync(List<PendingExport> call, Dictionary<string, List<string>> before,
        IReadOnlySet<Guid>? exportIdsOwnedElsewhere = null, IReadOnlySet<Guid>? changeIdsOwnedElsewhere = null)
    {
        var after = await SnapshotAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("PendingExports", "Id", call.Select(pe => pe.Id).Where(id => exportIdsOwnedElsewhere?.Contains(id) != true)), Is.Zero,
                "no Pending Export of the failed call may remain");
            Assert.That(await CountAsync("PendingExportAttributeValueChanges", "Id", ChangeIds(call).Where(id => changeIdsOwnedElsewhere?.Contains(id) != true)), Is.Zero,
                "nor any of its changes, whichever connection wrote them");
            foreach (var table in PendingExportTables)
                Assert.That(after[table], Is.EqualTo(before[table]), $"{table} is exactly as it was before the call: the other call's rows are untouched");
        }
    }

    #endregion

    #region Equivalence

    /// <summary>
    /// The same mixed batch written down the single-connection path and down the parallel path gives identical rows:
    /// every column of every Pending Export and every change, Creates, Updates and Deletes, single and multi-valued,
    /// resolved and unresolved references, and a group whose changes span every partition.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_ParallelPath_WritesExactlyWhatTheSingleConnectionPathWritesAsync()
    {
        var s = await SeedAsync();
        var single = BuildBatch(s);
        await using (var context = NewContext())
            await Repository(context, WritePath.SingleConnection, Rows(single)).CreatePendingExportsAsync(single);
        var singleRows = await SnapshotAsync();
        await PostgresTestDatabase.ResetAsync(_connectionString);

        s = await SeedAsync();
        var parallel = BuildBatch(s);
        await using (var context = NewContext())
            await Repository(context, WritePath.Parallel, Rows(parallel)).CreatePendingExportsAsync(parallel);
        var parallelRows = await SnapshotAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(singleRows["PendingExports"], Has.Count.EqualTo(single.Count), "sanity: every export written");
            Assert.That(singleRows["PendingExportAttributeValueChanges"], Has.Count.EqualTo(ChangeIds(single).Count), "sanity: every change written");
            foreach (var table in PendingExportTables)
                Assert.That(parallelRows[table], Is.EqualTo(singleRows[table]), $"{table}, every row and column");
        }
    }

    #endregion

    #region Concurrency

    /// <summary>
    /// A large group's changes are written across several connections, each committing its own part: with one change's
    /// id held by another session's uncommitted row, the connection writing that row waits, and the others' changes are
    /// committed meanwhile. On one connection, as before #2919, every change waits.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_ParallelPath_CommitsChangesOnSeveralConnectionsAsync()
    {
        var s = await SeedAsync();
        var exports = BuildBatch(s);
        var changeIds = ChangeIds(exports);
        var heldId = changeIds[^1];

        await using var blockerConnection = new NpgsqlConnection(_connectionString);
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await HoldChangeIdAsync(blockerConnection, blockerTransaction, s, heldId);

        await using var context = NewContext();
        var repository = Repository(context, WritePath.Parallel, Rows(exports));
        var write = Task.Run(() => repository.CreatePendingExportsAsync(exports));

        long committedWhileOneWaits = 0;
        try
        {
            // Well inside the command timeout, so a write that waits for the whole of it (the single-connection path)
            // completes once the other session lets go, and the assertion below is what reports it.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !write.IsCompleted)
            {
                committedWhileOneWaits = await CountAsync("PendingExportAttributeValueChanges", "Id", changeIds.Where(id => id != heldId));
                if (committedWhileOneWaits >= changeIds.Count / 2)
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
            Assert.That(committedWhileOneWaits, Is.GreaterThanOrEqualTo(changeIds.Count / 2),
                "the changes on the connections not waiting are committed while one waits");
            Assert.That(await CountAsync("PendingExportAttributeValueChanges", "Id", changeIds), Is.EqualTo(changeIds.Count),
                "and every change is written once the other session lets go");
        }
    }

    /// <summary>
    /// Below the threshold the call stays on one connection, in one transaction: with one change held by another
    /// session, nothing of the call is committed.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_BelowThreshold_WritesOnOneConnectionAsync()
    {
        var s = await SeedAsync();
        var exports = BuildBatch(s);
        var changeIds = ChangeIds(exports);
        var heldId = changeIds[^1];

        await using var blockerConnection = new NpgsqlConnection(_connectionString);
        await blockerConnection.OpenAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await HoldChangeIdAsync(blockerConnection, blockerTransaction, s, heldId);

        await using var context = NewContext();
        var repository = Repository(context, WritePath.SingleConnection, Rows(exports));
        var write = Task.Run(() => repository.CreatePendingExportsAsync(exports));

        long exportsCommittedWhileOneWaits;
        long changesCommittedWhileOneWaits;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            exportsCommittedWhileOneWaits = await CountAsync("PendingExports", "Id", exports.Select(pe => pe.Id));
            changesCommittedWhileOneWaits = await CountAsync("PendingExportAttributeValueChanges", "Id", changeIds.Where(id => id != heldId));
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
        }

        await write;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exportsCommittedWhileOneWaits, Is.Zero, "one connection, one transaction: no export commits while a change waits");
            Assert.That(changesCommittedWhileOneWaits, Is.Zero, "nor any change");
            Assert.That(await CountAsync("PendingExportAttributeValueChanges", "Id", changeIds), Is.EqualTo(changeIds.Count));
        }
    }

    /// <summary>
    /// Another session's uncommitted Pending Export with one change holding <paramref name="heldId"/>, so the call's
    /// change with that id waits for the session to end.
    /// </summary>
    private static async Task HoldChangeIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Seeded s, Guid heldId)
    {
        await using var blocker = new NpgsqlCommand(
            """
            INSERT INTO "PendingExports" ("Id", "ConnectedSystemId", "ChangeType", "Status", "ErrorCount", "HasUnresolvedReferences", "CreatedAt")
                VALUES (@export, @system, 1, 0, 0, false, now());
            INSERT INTO "PendingExportAttributeValueChanges" ("Id", "PendingExportId", "AttributeId", "ChangeType", "Status", "ExportAttemptCount", "AwaitsInitialPassword", "AwaitsExchange")
                VALUES (@held, @export, @attribute, 0, 0, 0, false, false);
            """, connection, transaction);
        blocker.Parameters.AddWithValue("export", Guid.NewGuid());
        blocker.Parameters.AddWithValue("system", s.SystemId);
        blocker.Parameters.AddWithValue("attribute", s.Member.Id);
        blocker.Parameters.AddWithValue("held", heldId);
        await blocker.ExecuteNonQueryAsync();
    }

    #endregion

    #region Failure leaves nothing of the call, and nothing of another's is removed

    /// <summary>
    /// The changes fail (two with one id, in different partitions) after every export and part of the changes have
    /// committed. The call must throw and leave nothing of itself; the other call's exports stay exactly as they were.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_ChangeWriteFails_ThrowsAndLeavesNothingOfTheCallAsync()
    {
        var s = await SeedAsync();
        await WriteAnotherCallAsync(s);
        var exports = BuildBatch(s);
        var changes = exports.SelectMany(pe => pe.AttributeValueChanges).ToList();
        changes[^1].Id = changes[0].Id;
        var before = await SnapshotAsync();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, Rows(exports));
            Assert.That(async () => await repository.CreatePendingExportsAsync(exports),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.UniqueViolation));
        }

        await AssertNothingOfTheCallRemainsAsync(exports, before);
    }

    /// <summary>
    /// One change takes the id of a change another call already committed, so its partition fails while the exports and
    /// the other partitions' changes commit. What the call committed is removed by the exports it wrote, never by the
    /// changes' ids: the other call's change, which holds the id the failed call tried to reuse, survives.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_ChangeIdOwnedByAnotherCall_RemovesOnlyTheCallsRowsAsync()
    {
        var s = await SeedAsync();
        var other = await WriteAnotherCallAsync(s);
        var takenChangeId = ChangeIds(other)[0];
        var exports = BuildBatch(s);
        exports.Last(pe => pe.AttributeValueChanges.Count > 0).AttributeValueChanges[^1].Id = takenChangeId;
        var before = await SnapshotAsync();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, Rows(exports));
            Assert.That(async () => await repository.CreatePendingExportsAsync(exports),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.UniqueViolation));
        }

        await AssertNothingOfTheCallRemainsAsync(exports, before, changeIdsOwnedElsewhere: new HashSet<Guid> { takenChangeId });
        Assert.That(await CountAsync("PendingExportAttributeValueChanges", "Id", [takenChangeId]), Is.EqualTo(1),
            "the other call's change, whose id the failed call reused, survives");
    }

    /// <summary>
    /// The exports fail: the last one takes the id of an export another call already committed, so its partition fails
    /// while the other partitions' exports commit, and no change is written. The exports the call committed are removed,
    /// and only those: the other call's export with that id, and its changes, survive.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_ExportIdOwnedByAnotherCall_RemovesOnlyTheExportsTheCallCommittedAsync()
    {
        var s = await SeedAsync();
        var other = await WriteAnotherCallAsync(s);
        var takenExportId = other[0].Id;
        var exports = BuildBatch(s);
        exports[^1].Id = takenExportId;
        var before = await SnapshotAsync();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, Rows(exports));
            Assert.That(async () => await repository.CreatePendingExportsAsync(exports),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.UniqueViolation));
        }

        await AssertNothingOfTheCallRemainsAsync(exports, before, exportIdsOwnedElsewhere: new HashSet<Guid> { takenExportId });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("PendingExports", "Id", [takenExportId]), Is.EqualTo(1),
                "the other call's export, whose id the failed call reused, survives");
            Assert.That(await CountAsync("PendingExportAttributeValueChanges", "PendingExportId", [takenExportId]),
                Is.EqualTo(other[0].AttributeValueChanges.Count), "with every one of its changes");
        }
    }

    /// <summary>
    /// The exports fail on a foreign key (an export naming no Connected System) in one partition while the others
    /// commit. Nothing of the call remains.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_ExportWriteFails_ThrowsAndLeavesNothingOfTheCallAsync()
    {
        var s = await SeedAsync();
        await WriteAnotherCallAsync(s);
        var exports = BuildBatch(s);
        exports[^1].ConnectedSystemId = int.MaxValue;
        var before = await SnapshotAsync();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, Rows(exports));
            Assert.That(async () => await repository.CreatePendingExportsAsync(exports),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.ForeignKeyViolation));
        }

        await AssertNothingOfTheCallRemainsAsync(exports, before);
    }

    #endregion

    #region Caller transaction

    /// <summary>
    /// A caller holding a transaction keeps the exports inside it, however large the call: rows committed on connections
    /// of their own would survive the caller's rollback. Inside it the rows are visible to the caller and to no other
    /// session; rolled back, nothing remains.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_CallerHoldsTransaction_WritesInsideItSoARollbackLeavesNothingAsync()
    {
        var s = await SeedAsync();
        var exports = BuildBatch(s);

        await using (var context = NewContext())
        {
            var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
            sync.ConnectionStringForParallelWrites = _connectionString;
            sync.WriteParallelismOverride = Parallelism;
            sync.ParallelWriteRowThreshold = 0;

            await using var transaction = await context.Database.BeginTransactionAsync();
            Assert.That(sync.ShouldWriteInParallel(Rows(exports)), Is.False, "sanity: the gate keeps a caller's transaction on its connection");
            await sync.CreatePendingExportsAsync(exports);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await context.PendingExportAttributeValueChanges.CountAsync(), Is.EqualTo(ChangeIds(exports).Count),
                    "the caller sees every change inside its transaction");
                Assert.That(await CountAsync("PendingExports", "Id", exports.Select(pe => pe.Id)), Is.Zero,
                    "and no other session sees any of it: nothing was written on another connection");
            }

            await transaction.RollbackAsync();
        }

        var after = await SnapshotAsync();
        using (Assert.EnterMultipleScope())
        {
            foreach (var table in PendingExportTables)
                Assert.That(after[table], Is.Empty, $"the rollback takes every row of {table} with it");
        }
    }

    #endregion
}

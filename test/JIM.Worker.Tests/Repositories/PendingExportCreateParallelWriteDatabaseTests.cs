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
/// Each test forces the path it means to exercise (four connections, a threshold of zero and every connection used
/// however few the rows, or a threshold no call reaches) and asserts the decision with the writers' own predicate; the
/// people page test instead runs the default gate and asserts the connections it chooses. Opt-in via
/// <c>JIM_TEST_RESET_*</c>.
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
        sync.ParallelWriteRowsPerConnection = 1; // every connection the parallelism allows, however few the rows (#2669)
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
        var writePid = await OpenAndGetBackendPidAsync(context);
        var repository = Repository(context, WritePath.SingleConnection, Rows(exports));
        var write = Task.Run(() => repository.CreatePendingExportsAsync(exports));

        long exportsCommittedWhileOneWaits;
        long changesCommittedWhileOneWaits;
        try
        {
            Assert.That(await WaitUntilBlockedAsync(writePid), Is.True, "sanity: the write reaches the held change and waits");
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
    /// Opens the context's connection, so that everything the context does runs on it, and returns its server process.
    /// </summary>
    private static async Task<int> OpenAndGetBackendPidAsync(JimDbContext context)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", (NpgsqlConnection)context.Database.GetDbConnection());
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Waits until the session with server process <paramref name="pid"/> is waiting on a lock another session holds.
    /// False after 30 seconds. Scoped to the one session, so no other session's wait can satisfy it.
    /// </summary>
    private async Task<bool> WaitUntilBlockedAsync(int pid)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid)) > 0", connection);
        command.Parameters.AddWithValue("pid", pid);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if ((bool)(await command.ExecuteScalarAsync())!)
                return true;
            await Task.Delay(50);
        }

        return false;
    }

    /// <summary>
    /// A people page's staging as a synchronisation writes it: 500 Updates of 11 changes each, 6,000 rows, one for each of
    /// 500 Connected System Objects seeded for it (an object holds at most one Pending Export).
    /// </summary>
    private async Task<List<PendingExport>> BuildPeoplePageAsync(Seeded s)
    {
        var nextId = IdSequence(5);
        var objectIds = new List<Guid>();
        await using (var seed = NewContext())
        {
            var typeId = await seed.ConnectedSystemObjectTypes.Where(t => t.ConnectedSystemId == s.SystemId).Select(t => t.Id).SingleAsync();
            var objects = Enumerable.Range(0, 500)
                .Select(_ => new ConnectedSystemObject { Id = nextId(), TypeId = typeId, ConnectedSystemId = s.SystemId, Status = ConnectedSystemObjectStatus.Normal, Created = StagedAt })
                .ToList();
            seed.AddRange(objects);
            await seed.SaveChangesAsync();
            objectIds.AddRange(objects.Select(o => o.Id));
        }

        return objectIds.Select((objectId, i) =>
        {
            var export = new PendingExport
            {
                Id = nextId(), ConnectedSystemId = s.SystemId, ConnectedSystemObjectId = objectId, ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending, CreatedAt = StagedAt, SourceMetaverseObjectId = s.People[i % s.People.Count]
            };
            for (var c = 0; c < 11; c++)
            {
                export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
                {
                    Id = nextId(), AttributeId = s.ProxyAddresses.Id, ChangeType = PendingExportAttributeChangeType.Add,
                    StringValue = $"smtp:person{i}.{c}@glitterband.local", SyncRuleId = s.SyncRuleId, SyncRuleName = "Provision people"
                });
            }

            return export;
        }).ToList();
    }

    /// <summary>
    /// The number of transactions that wrote the given rows: each parallel connection commits its own partition, and every
    /// row a transaction writes carries its id as <c>xmin</c>.
    /// </summary>
    private async Task<long> CountWritingTransactionsAsync(string table, IEnumerable<Guid> ids)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""SELECT count(DISTINCT xmin::text) FROM "{table}" WHERE "Id" = ANY(@ids)""", connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// On the write gate's own defaults (threshold and rows a connection) on a 16-core host, a people page's 6,000 rows of
    /// staging go parallel, and each step takes the connections its own rows earn (#2669): the 5,500 changes four, as every
    /// other bulk writer's do at that size, and the 500 exports one. Spread across five, the exports' step cost 23 ms a
    /// page for a 2.2 ms COPY in the 500k run. The exports are counted once they are staged, before the last step makes
    /// them live in one UPDATE on the context's connection, which gives every one that transaction's id.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_PeoplePageOnDefaultGate_WritesExportsOnOneConnectionAndChangesOnFourAsync()
    {
        var s = await SeedAsync();
        var exports = await BuildPeoplePageAsync(s);
        Assert.That(Rows(exports), Is.EqualTo(6_000), "sanity: the page's rows");
        long? exportTransactionsWhenWritten = null;

        await using (var context = NewContext())
        {
            var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
            sync.ConnectionStringForParallelWrites = _connectionString;
            sync.WriteParallelismOverride = 16;
            Assert.That(sync.ConnectionsForWrite(Rows(exports)), Is.EqualTo(4), "the default gate's decision for 6,000 rows on a 16-core host");
            sync.PendingExportCreateStepReached = async step =>
            {
                if (step == SyncRepository.StepExportsWritten)
                    exportTransactionsWhenWritten = await CountWritingTransactionsAsync("PendingExports", exports.Select(pe => pe.Id));
            };
            await sync.CreatePendingExportsAsync(exports);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("PendingExports", "Id", exports.Select(pe => pe.Id)), Is.EqualTo(500), "every export is written");
            Assert.That(await CountAsync("PendingExportAttributeValueChanges", "Id", ChangeIds(exports)), Is.EqualTo(5_500), "and every change");
            Assert.That(exportTransactionsWhenWritten, Is.EqualTo(1), "the 500 exports are written on one connection");
            Assert.That(await CountWritingTransactionsAsync("PendingExportAttributeValueChanges", ChangeIds(exports)), Is.EqualTo(4),
                "and their 5,500 changes across four");
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
    /// Runs a call that must fail with <paramref name="sqlState"/>, and returns how many of its exports and changes were
    /// in the database (in whatever state) when it began removing itself: so a test can show the failure came after rows
    /// had committed, and cannot pass because nothing was ever written.
    /// </summary>
    private async Task<(long Exports, long Changes)> RunFailingCallAsync(List<PendingExport> exports, string sqlState,
        IReadOnlySet<Guid>? exportIdsOwnedElsewhere = null, IReadOnlySet<Guid>? changeIdsOwnedElsewhere = null)
    {
        long exportsBeforeRemoval = -1, changesBeforeRemoval = -1;
        await using var context = NewContext();
        var repository = Repository(context, WritePath.Parallel, Rows(exports));
        repository.PendingExportCreateStepReached = async step =>
        {
            if (step != SyncRepository.StepRemovingFailedCall)
                return;
            exportsBeforeRemoval = await CountAsync("PendingExports", "Id",
                exports.Select(pe => pe.Id).Where(id => exportIdsOwnedElsewhere?.Contains(id) != true));
            changesBeforeRemoval = await CountAsync("PendingExportAttributeValueChanges", "Id",
                ChangeIds(exports).Where(id => changeIdsOwnedElsewhere?.Contains(id) != true));
        };

        Assert.That(async () => await repository.CreatePendingExportsAsync(exports),
            Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(sqlState));
        return (exportsBeforeRemoval, changesBeforeRemoval);
    }

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

        var committed = await RunFailingCallAsync(exports, PostgresErrorCodes.UniqueViolation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(committed.Exports, Is.EqualTo(exports.Count), "sanity: every export had committed before the failure");
            Assert.That(committed.Changes, Is.GreaterThan(0), "sanity: and some of the changes");
        }

        await AssertNothingOfTheCallRemainsAsync(exports, before);
    }

    /// <summary>
    /// One change takes the id of a change another call already committed, so its partition fails while the exports and
    /// the other partitions' changes commit. What the call wrote is removed by the exports it wrote, never by the
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
        var ownedElsewhere = new HashSet<Guid> { takenChangeId };

        var committed = await RunFailingCallAsync(exports, PostgresErrorCodes.UniqueViolation, changeIdsOwnedElsewhere: ownedElsewhere);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(committed.Exports, Is.EqualTo(exports.Count), "sanity: every export had committed before the failure");
            Assert.That(committed.Changes, Is.GreaterThan(0), "sanity: and some of the changes");
        }

        await AssertNothingOfTheCallRemainsAsync(exports, before, changeIdsOwnedElsewhere: ownedElsewhere);
        Assert.That(await CountAsync("PendingExportAttributeValueChanges", "Id", [takenChangeId]), Is.EqualTo(1),
            "the other call's change, whose id the failed call reused, survives");
    }

    /// <summary>
    /// The exports fail: the last one takes the id of an export another call already committed, so its partition fails
    /// while the other partitions' exports commit, and no change is written. The call removes by every id it tried to
    /// write, but only rows still being written, so the other call's export with that id, and its changes, survive.
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
        var ownedElsewhere = new HashSet<Guid> { takenExportId };

        var committed = await RunFailingCallAsync(exports, PostgresErrorCodes.UniqueViolation, exportIdsOwnedElsewhere: ownedElsewhere);

        Assert.That(committed.Exports, Is.GreaterThan(0), "sanity: the other partitions' exports had committed before the failure");
        await AssertNothingOfTheCallRemainsAsync(exports, before, exportIdsOwnedElsewhere: ownedElsewhere);
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

        var committed = await RunFailingCallAsync(exports, PostgresErrorCodes.ForeignKeyViolation);

        Assert.That(committed.Exports, Is.GreaterThan(0), "sanity: the other partitions' exports had committed before the failure");
        await AssertNothingOfTheCallRemainsAsync(exports, before);
    }

    /// <summary>
    /// The last step fails: making the exports live meets the unique index on their object, because another session
    /// committed a Pending Export for one of them while the call was writing. Every export and change had committed;
    /// none may remain, and the other session's export is untouched.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_MakingLiveFails_ThrowsAndLeavesNothingOfTheCallAsync()
    {
        var s = await SeedAsync();
        var exports = BuildBatch(s);
        var contestedObject = exports[0].ConnectedSystemObjectId!.Value;
        var rival = Guid.NewGuid();

        long exportsBeforeRemoval = -1, changesBeforeRemoval = -1;
        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, Rows(exports));
            repository.PendingExportCreateStepReached = async step =>
            {
                if (step == SyncRepository.StepChangesWritten)
                {
                    await using var connection = new NpgsqlConnection(_connectionString);
                    await connection.OpenAsync();
                    await using var command = new NpgsqlCommand(
                        """
                        INSERT INTO "PendingExports" ("Id", "ConnectedSystemId", "ConnectedSystemObjectId", "ChangeType", "Status", "ErrorCount", "HasUnresolvedReferences", "CreatedAt")
                            VALUES (@id, @system, @object, 1, 0, 0, false, now())
                        """, connection);
                    command.Parameters.AddWithValue("id", rival);
                    command.Parameters.AddWithValue("system", s.SystemId);
                    command.Parameters.AddWithValue("object", contestedObject);
                    await command.ExecuteNonQueryAsync();
                }
                else if (step == SyncRepository.StepRemovingFailedCall)
                {
                    exportsBeforeRemoval = await CountAsync("PendingExports", "Id", exports.Select(pe => pe.Id));
                    changesBeforeRemoval = await CountAsync("PendingExportAttributeValueChanges", "Id", ChangeIds(exports));
                }
            };

            Assert.That(async () => await repository.CreatePendingExportsAsync(exports),
                Throws.InstanceOf<PostgresException>().With.Property(nameof(PostgresException.SqlState)).EqualTo(PostgresErrorCodes.UniqueViolation));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exportsBeforeRemoval, Is.EqualTo(exports.Count), "sanity: every export had committed before the failure");
            Assert.That(changesBeforeRemoval, Is.EqualTo(ChangeIds(exports).Count), "sanity: and every change");
            Assert.That(await CountAsync("PendingExports", "Id", exports.Select(pe => pe.Id)), Is.Zero, "no export of the call remains");
            Assert.That(await CountAsync("PendingExportAttributeValueChanges", "Id", ChangeIds(exports)), Is.Zero, "nor any change");
            Assert.That(await CountAsync("PendingExports", "Id", [rival]), Is.EqualTo(1), "the other session's export is untouched");
        }
    }

    #endregion

    #region No reader sees the call until it is whole

    private sealed record ReaderView(long Written, int Selected, int Executable, int Counted, int FoundByObject, int FoundForImport,
        int WithdrawalCandidates);

    /// <summary>
    /// What the readers that act on Pending Exports see of the call, each on a context of its own as another task would:
    /// the export run's batch selection and its count (Run Profile Safeguards read the same query), the count the portal
    /// shows, the lookups staging and merging make by object, the confirming import's read by Connected System, and the
    /// confirming import's search for queued changes to withdraw (#2915). Beside them, how many of the call's exports are in the table at all, in whatever state.
    /// </summary>
    private async Task<ReaderView> WhatReadersSeeAsync(Seeded s, List<PendingExport> call)
    {
        var callIds = call.Select(pe => pe.Id).ToHashSet();
        var objectIds = call.Where(pe => pe.ConnectedSystemObjectId.HasValue).Select(pe => pe.ConnectedSystemObjectId!.Value).ToList();
        await using var context = NewContext();
        var sync = new PostgresDataRepository(context).Sync;
        var selected = await sync.GetExecutableExportBatchSummaryAsync(s.SystemId, 10_000, null, null);
        var byObject = await sync.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(objectIds);
        var oneByObject = await sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(objectIds[0]);
        var forImport = await sync.GetPendingExportsLightweightByConnectedSystemIdAsync(s.SystemId);
        var withdrawal = await sync.GetUpdatePendingExportsWithQueuedChangesPossiblyWithoutAuthorityAsync(s.SystemId, []);
        return new ReaderView(
            await CountAsync("PendingExports", "Id", callIds),
            selected.Count(x => callIds.Contains(x.Id)),
            await sync.GetExecutableExportCountAsync(s.SystemId),
            await sync.GetPendingExportsCountAsync(s.SystemId),
            byObject.Values.Count(pe => callIds.Contains(pe.Id)) + (oneByObject != null && callIds.Contains(oneByObject.Id) ? 1 : 0),
            forImport.Values.Count(pe => callIds.Contains(pe.Id)),
            withdrawal.Count(pe => callIds.Contains(pe.Id)));
    }

    /// <summary>
    /// Between the steps of a parallel create (every export written, then every change), no reader sees anything of the
    /// call, although its rows are committed; once it returns, every reader sees all of it. An export run between the
    /// steps would otherwise send a Create with none of its attributes, or a group with part of its members.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_ParallelPath_NoReaderSeesTheCallUntilItIsWholeAsync()
    {
        var s = await SeedAsync();
        var exports = BuildBatch(s);
        var seen = new Dictionary<string, ReaderView>();

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, Rows(exports));
            repository.PendingExportCreateStepReached = async step => seen[step] = await WhatReadersSeeAsync(s, exports);
            await repository.CreatePendingExportsAsync(exports);
        }

        var after = await WhatReadersSeeAsync(s, exports);
        using (Assert.EnterMultipleScope())
        {
            foreach (var step in new[] { SyncRepository.StepExportsWritten, SyncRepository.StepChangesWritten })
            {
                Assert.That(seen[step].Written, Is.EqualTo(exports.Count), $"{step}: sanity, the call's exports are committed");
                Assert.That(seen[step] with { Written = 0 }, Is.EqualTo(new ReaderView(0, 0, 0, 0, 0, 0, 0)), $"{step}: no reader sees any of them");
            }

            Assert.That(after.Selected, Is.EqualTo(exports.Count), "afterwards the export run selects every export");
            Assert.That(after.Counted, Is.EqualTo(exports.Count), "the portal counts every export");
            Assert.That(after.FoundByObject, Is.EqualTo(exports.Count + 1), "staging finds every export by its object");
            Assert.That(after.FoundForImport, Is.EqualTo(exports.Count), "the confirming import reads every one");
            Assert.That(after.WithdrawalCandidates, Is.EqualTo(exports.Count(pe => pe.ChangeType == PendingExportChangeType.Update)),
                "and weighs every queued Update for withdrawal (their changes' Synchronisation Rule maps nothing)");
        }
    }

    #endregion

    #region What a Worker that stops part-way leaves

    /// <summary>
    /// A call stopped part-way, as a Worker that dies between its steps leaves it: every export and change committed,
    /// nothing made live, nothing removed. What it leaves is selected for no export and counted nowhere, holds no place
    /// against the next staging for the same objects, and is removed when the Worker next starts. The stopped call, were
    /// it to carry on, finds its rows gone and fails rather than make anything live.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_StoppedPartWay_LeavesNothingAnyReaderActsOnAndIsRemovedAtStartAsync()
    {
        var s = await SeedAsync();
        var stopped = BuildBatch(s);
        var reachedStop = new TaskCompletionSource();
        var carryOn = new TaskCompletionSource();

        await using var stoppedContext = NewContext();
        var stoppedRepository = Repository(stoppedContext, WritePath.Parallel, Rows(stopped));
        stoppedRepository.PendingExportCreateStepReached = async step =>
        {
            if (step != SyncRepository.StepChangesWritten)
                return;
            reachedStop.SetResult();
            await carryOn.Task;
        };
        var stoppedCall = Task.Run(() => stoppedRepository.CreatePendingExportsAsync(stopped));
        await reachedStop.Task.WaitAsync(TimeSpan.FromSeconds(60));

        var leftover = await WhatReadersSeeAsync(s, stopped);

        // The next synchronisation stages the same objects again.
        var next = BuildBatch(s, seed: 3);
        await using (var context = NewContext())
            await Repository(context, WritePath.Parallel, Rows(next)).CreatePendingExportsAsync(next);

        int removedAtStart;
        await using (var context = NewContext())
            removedAtStart = await new PostgresDataRepository(context).Sync.RemoveIncompletePendingExportsAsync();

        carryOn.SetResult();
        Assert.That(async () => await stoppedCall, Throws.InstanceOf<InvalidOperationException>(),
            "the stopped call, carrying on, finds its rows gone and fails rather than making anything live");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(leftover.Written, Is.EqualTo(stopped.Count), "sanity: the stopped call's exports were committed");
            Assert.That(leftover with { Written = 0 }, Is.EqualTo(new ReaderView(0, 0, 0, 0, 0, 0, 0)),
                "no reader sees what it left: nothing is selected for export, counted or found by object");
            Assert.That(removedAtStart, Is.EqualTo(stopped.Count), "the Worker's start removes every export it left");
            Assert.That(await CountAsync("PendingExports", "Id", stopped.Select(pe => pe.Id)), Is.Zero, "none remains");
            Assert.That(await CountAsync("PendingExportAttributeValueChanges", "Id", ChangeIds(stopped)), Is.Zero, "nor any change");
            Assert.That(await CountAsync("PendingExports", "Id", next.Select(pe => pe.Id)), Is.EqualTo(next.Count),
                "the next staging for the same objects was not held back by it, and is left alone");
            Assert.That((await WhatReadersSeeAsync(s, next)).Selected, Is.EqualTo(next.Count), "and is live");
        }
    }

    #endregion

    #region The start-up removal and a call made live at the same moment

    /// <summary>
    /// The Worker's start-up removal runs while a call's exports are being made live: they have been given their status,
    /// and that has not committed. The removal must wait for them, and then leave them, with every one of their changes.
    /// Removing the changes of Incomplete exports in a statement of its own would take them before it waited.
    /// </summary>
    [Test]
    public async Task RemoveIncompletePendingExportsAsync_WhileACallIsMadeLive_LeavesItsExportsWholeAsync()
    {
        var s = await SeedAsync();
        var exports = BuildBatch(s);

        await using var sweepContext = NewContext();
        var sweepPid = await OpenAndGetBackendPidAsync(sweepContext);
        var sweep = new PostgresDataRepository(sweepContext).Sync;
        Task<int>? sweeping = null;
        var sweepWaited = false;

        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, Rows(exports));
            repository.PendingExportCreateStepReached = async step =>
            {
                if (step != SyncRepository.StepMakingLive)
                    return;
                sweeping = Task.Run(() => sweep.RemoveIncompletePendingExportsAsync());
                sweepWaited = await WaitUntilBlockedAsync(sweepPid);
            };
            await repository.CreatePendingExportsAsync(exports);
        }

        var removed = await sweeping!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sweepWaited, Is.True, "sanity: the removal reached the exports being made live and waited for them");
            Assert.That(removed, Is.Zero, "the removal removes no export that was made live");
            Assert.That(await CountAsync("PendingExports", "Id", exports.Select(pe => pe.Id)), Is.EqualTo(exports.Count), "every export remains");
            Assert.That(await CountAsync("PendingExportAttributeValueChanges", "Id", ChangeIds(exports)), Is.EqualTo(ChangeIds(exports).Count),
                "with every one of its changes");
        }
    }

    #endregion

    #region Pending Export batch scope

    /// <summary>
    /// Inside a Pending Export batch (#2492) with a delete queued for every object the call stages again, the parallel
    /// path executes the queued deletes before its exports take their objects, as the single-connection path does: the
    /// replacements are live and the originals gone.
    /// </summary>
    [Test]
    public async Task CreatePendingExportsAsync_ParallelPathInsideABatchWithQueuedDeletes_ReplacesTheQueuedExportsAsync()
    {
        var s = await SeedAsync();
        var originals = BuildBatch(s);
        await using (var context = NewContext())
            await Repository(context, WritePath.SingleConnection, Rows(originals)).CreatePendingExportsAsync(originals);

        var replacements = BuildBatch(s, seed: 3);
        await using (var context = NewContext())
        {
            var repository = Repository(context, WritePath.Parallel, Rows(replacements));
            repository.BeginPendingExportBatch();
            foreach (var original in originals)
                await repository.DeletePendingExportForMergeAsync(original);
            await repository.CreatePendingExportsAsync(replacements);
            await repository.FlushPendingExportBatchAsync();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await CountAsync("PendingExports", "Id", originals.Select(pe => pe.Id)), Is.Zero, "the queued originals are gone");
            Assert.That(await CountAsync("PendingExportAttributeValueChanges", "Id", ChangeIds(originals)), Is.Zero, "with their changes");
            Assert.That((await WhatReadersSeeAsync(s, replacements)).Selected, Is.EqualTo(replacements.Count), "every replacement is live");
            Assert.That(await CountAsync("PendingExportAttributeValueChanges", "Id", ChangeIds(replacements)),
                Is.EqualTo(ChangeIds(replacements).Count), "with every change");
        }
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

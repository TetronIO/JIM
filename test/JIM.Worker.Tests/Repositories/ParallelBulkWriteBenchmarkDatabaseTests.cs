// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
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
/// Measures the bulk Metaverse Object create (<c>SyncRepository.CreateMetaverseObjectsBulkAsync</c>) on one connection
/// against the parallel path at 4, 8 and 16 connections (#2872, #2669), for the pages synchronisation actually writes:
/// one page of 500 groups carrying 650,000 membership values that reference previously written people (the shape of the
/// slowest <c>Mvo.Create</c> calls in the 500k initial load: 649,735, 523,865, 255,770 and 177,897 values), pages of 500
/// people with ten and eleven values each (the 500k Full Synchronisation's people page is the eleven), and a sweep of
/// group pages in between that locates where parallel starts to pay and how many connections pay at each size, which is
/// what <see cref="ParallelBatchWriter.MinimumRowsForParallelWrite"/> and
/// <see cref="ParallelBatchWriter.RowsPerParallelConnection"/> are set from. Every value names its contributing
/// Connected System and Synchronisation Rule, as synchronisation's values do, so the foreign key checks every row pays
/// (the bulk of a COPY's cost here) are the real ones. Each measurement is the median of three, the modes interleaved
/// round by round so drift in the database's state favours none of them; the rows written are deleted, vacuumed and
/// checkpointed between runs. A second test measures the Connected System Object create and the execution item flush
/// at the sizes imports and synchronisation write them, which is what shows those writers share the same rule.
/// </summary>
/// <remarks>
/// Explicit, because it seeds 200,000 people and writes several million rows, and exists to be read, not to guard; the
/// numbers go to the test output. They depend on the host: the development sandbox it was first run on has 4 cores,
/// shared by the test host and PostgreSQL, so 8 and 16 connections there are oversubscribed. The constants are set from
/// a 16-core host (#2669). Each mode forces its connection count, whatever the rows (one row a connection).
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
[Explicit("Benchmark: seeds 200,000 Metaverse Objects and writes several million attribute values; run on demand")]
public class ParallelBulkWriteBenchmarkDatabaseTests
{
    private const int SeededPeople = 200_000;
    private const int Rounds = 3;
    private static readonly int?[] Modes = [null, 4, 8, 16];

    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        var context = new JimDbContext(options);
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        return context;
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the parallel bulk write benchmark.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass};Command Timeout=600";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task CreateMetaverseObjects_SingleConnectionAgainstParallel_ReportsMedianOfThreeAsync()
    {
        var seeded = await SeedAsync();

        // (name, page builder). Group pages are 500 groups; the sweep varies their membership.
        var scenarios = new List<(string Name, Func<List<MetaverseObject>> Build)>
        {
            ("People page: 500 people, 10 values each", () => BuildPeoplePage(seeded)),
            ("People page: 500 people, 11 values each", () => BuildPeoplePage(seeded, extraValue: true)),
            ("Group page: 500 groups, 2 members each", () => BuildGroupPage(seeded, Enumerable.Repeat(2, 500).ToList())),
            ("Group page: 500 groups, 10 members each", () => BuildGroupPage(seeded, Enumerable.Repeat(10, 500).ToList())),
            ("Group page: 500 groups, 40 members each", () => BuildGroupPage(seeded, Enumerable.Repeat(40, 500).ToList())),
            ("Group page: 500 groups, 100 members each", () => BuildGroupPage(seeded, Enumerable.Repeat(100, 500).ToList())),
            ("Group page: 500 groups, 300 members each", () => BuildGroupPage(seeded, Enumerable.Repeat(300, 500).ToList())),
            ("Group page: 500 groups, 650,000 members (200k/150k/100k/50k + 496 small)", () => BuildGroupPage(seeded, LargeGroupPageSizes()))
        };

        var report = new List<string>();
        foreach (var (name, build) in scenarios)
        {
            var timings = Modes.ToDictionary(m => m ?? 1, _ => new List<double>());
            var rows = 0;
            for (var round = 0; round < Rounds; round++)
            {
                // Rotate the order each round so no mode always runs first (or last) after a cleanup.
                var order = Modes.Skip(round % Modes.Length).Concat(Modes.Take(round % Modes.Length)).ToList();
                foreach (var mode in order)
                {
                    var page = build();
                    rows = page.Count + page.Sum(m => m.AttributeValues.Count);
                    await PrepareAsync();

                    await using var context = NewContext();
                    var repository = new PostgresDataRepository(context);
                    var sync = (SyncRepository)repository.Sync;
                    sync.ConnectionStringForParallelWrites = _connectionString;
                    if (mode == null)
                    {
                        sync.ParallelWriteRowThreshold = int.MaxValue;
                    }
                    else
                    {
                        sync.ParallelWriteRowThreshold = 0;
                        sync.ParallelWriteRowsPerConnection = 1;
                        sync.WriteParallelismOverride = mode;
                    }

                    var stopwatch = Stopwatch.StartNew();
                    await sync.CreateMetaverseObjectsAsync(page);
                    stopwatch.Stop();
                    timings[mode ?? 1].Add(stopwatch.Elapsed.TotalMilliseconds);

                    await AssertWrittenAndRemoveAsync(page);
                }
            }

            var single = Median(timings[1]);
            report.Add($"{name}: {rows:N0} rows");
            foreach (var mode in Modes)
            {
                var median = Median(timings[mode ?? 1]);
                var label = mode == null ? "single connection" : $"{mode} connections";
                report.Add($"    {label,-18} median {median,9:N0} ms  (runs: {string.Join(", ", timings[mode ?? 1].Select(t => $"{t:N0}"))})  speed-up {single / median:0.00}x");
            }
        }

        WriteReport(report);
    }

    /// <summary>
    /// The import-shaped writes (#2872): the Connected System Object create and the execution item flush, at the sizes a
    /// Full Import writes them (batches of 2,000 objects of about six values, and of twenty for comparison, which went parallel on a 16-core host under
    /// the old object-count gate), and the execution item flush at a synchronisation page's size. Single connection
    /// against 4 and 16 connections; the timing is the writer's own call (for the import flush, also with the change records
    /// the import writes beside it, which take the single connection either way).
    /// </summary>
    [Test]
    public async Task CreateConnectedSystemObjectsAndBulkInsertRpeis_SingleConnectionAgainstParallel_ReportsMedianOfThreeAsync()
    {
        var seeded = await SeedImportAsync();
        int?[] modes = [null, 4, 16];
        var report = new List<string>();

        // Earlier import batches already in the table, so the indexes are not empty ones.
        await using (var context = NewContext())
        {
            var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
            sync.ParallelWriteRowThreshold = int.MaxValue;
            for (var batch = 0; batch < 10; batch++)
                await sync.CreateConnectedSystemObjectsAsync(BuildUserCsos(seeded, 2_000, 6));
        }

        await ExecuteAsync("""VACUUM ANALYZE "ConnectedSystemObjects", "ConnectedSystemObjectAttributeValues" """);

        foreach (var (count, values) in new[] { (2_000, 6), (1_000, 6), (2_000, 20) })
        {
            report.AddRange(await MeasureAsync($"CSO create: {count:N0} users, {values} values each", modes, async (sync, _) =>
            {
                var batch = BuildUserCsos(seeded, count, values);
                var stopwatch = Stopwatch.StartNew();
                await sync.CreateConnectedSystemObjectsAsync(batch);
                stopwatch.Stop();
                await RemoveCsosAsync(batch);
                return (batch.Count * (values + 1), stopwatch.Elapsed.TotalMilliseconds, null);
            }));
        }

        report.AddRange(await MeasureAsync("RPEI flush, import-shaped: 2,000 items, 1 outcome and a 6-value Added change record each", modes, async (sync, _) =>
        {
            var items = BuildImportRpeis(seeded, 2_000);
            var rows = items.Count + items.Sum(i => i.SyncOutcomes.Count);
            var stopwatch = Stopwatch.StartNew();
            await sync.BulkInsertRpeisAsync(items);
            var writerMs = stopwatch.Elapsed.TotalMilliseconds;
            foreach (var item in items)
                item.ConnectedSystemObjectChange!.ActivityRunProfileExecutionItemId = item.Id;
            await sync.PersistRpeiCsoChangesAsync(items);
            stopwatch.Stop();
            await TruncateRpeisAsync();
            return (rows, writerMs, stopwatch.Elapsed.TotalMilliseconds);
        }));

        report.AddRange(await MeasureAsync("RPEI flush, synchronisation-shaped: 500 items, 3 outcomes each", modes, async (sync, _) =>
        {
            var items = BuildSyncRpeis(seeded, 500);
            var rows = items.Count + items.Sum(i => i.SyncOutcomes.Count);
            var stopwatch = Stopwatch.StartNew();
            await sync.BulkInsertRpeisAsync(items);
            stopwatch.Stop();
            await TruncateRpeisAsync();
            return (rows, stopwatch.Elapsed.TotalMilliseconds, null);
        }));

        WriteReport(report);
    }

    /// <summary>
    /// Runs one write <see cref="Rounds"/> times per mode, interleaving the modes round by round, and reports each mode's
    /// median against the single connection's.
    /// </summary>
    private async Task<List<string>> MeasureAsync(
        string name,
        IReadOnlyList<int?> modes,
        Func<SyncRepository, int?, Task<(int Rows, double WriterMs, double? TotalMs)>> run)
    {
        var writer = modes.ToDictionary(m => m ?? 1, _ => new List<double>());
        var total = modes.ToDictionary(m => m ?? 1, _ => new List<double>());
        var rows = 0;
        for (var round = 0; round < Rounds; round++)
        {
            var order = modes.Skip(round % modes.Count).Concat(modes.Take(round % modes.Count)).ToList();
            foreach (var mode in order)
            {
                await PrepareAsync();
                await using var context = NewContext();
                var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
                sync.ConnectionStringForParallelWrites = _connectionString;
                sync.ParallelWriteRowThreshold = mode == null ? int.MaxValue : 0;
                sync.ParallelWriteRowsPerConnection = 1;
                sync.WriteParallelismOverride = mode;
                var result = await run(sync, mode);
                rows = result.Rows;
                writer[mode ?? 1].Add(result.WriterMs);
                if (result.TotalMs.HasValue)
                    total[mode ?? 1].Add(result.TotalMs.Value);
            }
        }

        var lines = new List<string> { $"{name}: {rows:N0} rows written by the writer" };
        foreach (var (label, timings) in new[] { ("writer", writer), ("with change records", total) })
        {
            if (timings[1].Count == 0)
                continue;
            var single = Median(timings[1]);
            foreach (var mode in modes)
            {
                var median = Median(timings[mode ?? 1]);
                var modeLabel = mode == null ? "single connection" : $"{mode} connections";
                lines.Add($"    {label,-20} {modeLabel,-18} median {median,8:N0} ms  (runs: {string.Join(", ", timings[mode ?? 1].Select(t => $"{t:N0}"))})  speed-up {single / median:0.00}x");
            }
        }

        return lines;
    }

    private static void WriteReport(List<string> report)
    {
        var header = $"ParallelBulkWriteBenchmark: host has {Environment.ProcessorCount} cores; median of {Rounds}";
        TestContext.Out.WriteLine(header);
        Console.WriteLine(header);
        foreach (var line in report)
        {
            TestContext.Out.WriteLine(line);
            Console.WriteLine(line);
        }
    }

    private sealed record ImportSeeded(int SystemId, int TypeId, List<ConnectedSystemObjectTypeAttribute> Attributes, Guid ActivityId);

    private async Task<ImportSeeded> SeedImportAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var attributes = new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "objectGUID", ConnectedSystemObjectType = csType, Type = AttributeDataType.Guid, AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true }
        };
        attributes.AddRange(new[] { "displayName", "givenName", "sn", "mail", "employeeId" }.Concat(Enumerable.Range(1, 14).Select(i => $"extensionAttribute{i}")).Select(n => new ConnectedSystemObjectTypeAttribute
        {
            Name = n, ConnectedSystemObjectType = csType, Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued, Selected = true
        }));
        csType.Attributes.AddRange(attributes);
        var activity = new JIM.Models.Activities.Activity
        {
            Id = Guid.NewGuid(), TargetName = "Full Import", TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.InProgress, InitiatedByType = ActivityInitiatorType.System
        };
        seed.AddRange(connectorDefinition, system, csType, activity);
        await seed.SaveChangesAsync();
        return new ImportSeeded(system.Id, csType.Id, attributes, activity.Id);
    }

    private static int _userSequence;

    private static List<ConnectedSystemObject> BuildUserCsos(ImportSeeded s, int count, int values)
    {
        var csos = new List<ConnectedSystemObject>(count);
        for (var i = 0; i < count; i++)
        {
            var n = Interlocked.Increment(ref _userSequence);
            var cso = new ConnectedSystemObject
            {
                ConnectedSystemId = s.SystemId, TypeId = s.TypeId, ExternalIdAttributeId = s.Attributes[0].Id,
                Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined, Created = DateTime.UtcNow
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = s.Attributes[0].Id, GuidValue = Guid.NewGuid() });
            for (var a = 1; a < values; a++)
                cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { AttributeId = s.Attributes[a].Id, StringValue = $"{s.Attributes[a].Name}-{n}" });
            csos.Add(cso);
        }

        return csos;
    }

    /// <summary>
    /// Execution items as a Full Import writes them for new objects: an Added item naming its object, a CsoAdded outcome,
    /// and the Added change record listing the object's six values.
    /// </summary>
    private static List<ActivityRunProfileExecutionItem> BuildImportRpeis(ImportSeeded s, int count)
    {
        var items = new List<ActivityRunProfileExecutionItem>(count);
        for (var i = 0; i < count; i++)
        {
            var csoId = Guid.NewGuid();
            var item = new ActivityRunProfileExecutionItem
            {
                ActivityId = s.ActivityId, ObjectChangeType = ObjectChangeType.Added, ConnectedSystemObjectId = csoId,
                ObjectTypeSnapshot = "user", DisplayNameSnapshot = $"User {i}", ExternalIdSnapshot = csoId.ToString()
            };
            item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded });
            var change = new ConnectedSystemObjectChange
            {
                ConnectedSystemId = s.SystemId, ConnectedSystemObjectId = csoId, ChangeType = ObjectChangeType.Added,
                ChangeTime = DateTime.UtcNow, InitiatedByType = ActivityInitiatorType.System
            };
            foreach (var attribute in s.Attributes.Take(6))
            {
                var attributeChange = new ConnectedSystemObjectChangeAttribute
                {
                    Attribute = attribute, AttributeName = attribute.Name, AttributeType = attribute.Type, ConnectedSystemChange = change
                };
                attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, $"{attribute.Name}-{i}"));
                change.AttributeChanges.Add(attributeChange);
            }

            item.ConnectedSystemObjectChange = change;
            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// Execution items as a synchronisation page writes them for projected objects: Projected, then Attribute Flow,
    /// then a Pending Export created, as a tree.
    /// </summary>
    private static List<ActivityRunProfileExecutionItem> BuildSyncRpeis(ImportSeeded s, int count)
    {
        var items = new List<ActivityRunProfileExecutionItem>(count);
        for (var i = 0; i < count; i++)
        {
            var item = new ActivityRunProfileExecutionItem
            {
                ActivityId = s.ActivityId, ObjectChangeType = ObjectChangeType.Projected, ConnectedSystemObjectId = Guid.NewGuid(),
                MetaverseObjectId = Guid.NewGuid(), ObjectTypeSnapshot = "user", DisplayNameSnapshot = $"User {i}"
            };
            var projected = new ActivityRunProfileExecutionItemSyncOutcome { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected };
            var flow = new ActivityRunProfileExecutionItemSyncOutcome { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, ParentSyncOutcome = projected };
            var export = new ActivityRunProfileExecutionItemSyncOutcome { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, ParentSyncOutcome = flow };
            projected.Children.Add(flow);
            flow.Children.Add(export);
            item.SyncOutcomes.AddRange([projected, flow, export]);
            items.Add(item);
        }

        return items;
    }

    private async Task RemoveCsosAsync(List<ConnectedSystemObject> csos)
    {
        var ids = csos.Select(c => c.Id).ToArray();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
                 {
                     """DELETE FROM "ConnectedSystemObjectAttributeValues" WHERE "ConnectedSystemObjectId" = ANY(@ids)""",
                     """DELETE FROM "ConnectedSystemObjects" WHERE "Id" = ANY(@ids)""",
                     """VACUUM "ConnectedSystemObjects", "ConnectedSystemObjectAttributeValues" """
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
            if (sql.Contains("@ids"))
                command.Parameters.AddWithValue("ids", ids);
            await command.ExecuteNonQueryAsync();
        }
    }

    private Task TruncateRpeisAsync() => ExecuteAsync(
        """TRUNCATE "ActivityRunProfileExecutionItems", "ActivityRunProfileExecutionItemSyncOutcomes", "ConnectedSystemObjectChanges", "ConnectedSystemObjectChangeAttributes", "ConnectedSystemObjectChangeAttributeValues", "ActivityStatCounters" CASCADE""");

    private static List<int> LargeGroupPageSizes()
    {
        var sizes = new List<int> { 200_000, 150_000, 100_000, 50_000 };
        const int smallGroups = 496;
        const int smallTotal = 150_000;
        for (var i = 0; i < smallGroups; i++)
            sizes.Add(smallTotal / smallGroups + (i < smallTotal % smallGroups ? 1 : 0));
        return sizes;
    }

    private sealed record Seeded(
        MetaverseObjectType PersonType, MetaverseObjectType GroupType, int SystemId, int SyncRuleId,
        int[] PersonTextAttributeIds, int EmployeeNumberAttributeId, int EnabledAttributeId, int ManagerAttributeId,
        int GroupNameAttributeId, int GroupDescriptionAttributeId, int MemberAttributeId, List<Guid> PeopleIds);

    private async Task<Seeded> SeedAsync()
    {
        Seeded seeded;
        await using (var seed = NewContext())
        {
            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
            var textAttributes = new[] { "displayName", "givenName", "sn", "mail", "employeeId", "department", "title" }
                .Select(n => new MetaverseAttribute { Name = n, Type = AttributeDataType.Text }).ToList();
            var employeeNumber = new MetaverseAttribute { Name = "employeeNumber", Type = AttributeDataType.Number };
            var enabled = new MetaverseAttribute { Name = "accountEnabled", Type = AttributeDataType.Boolean };
            var manager = new MetaverseAttribute { Name = "manager", Type = AttributeDataType.Reference };
            var groupName = new MetaverseAttribute { Name = "groupName", Type = AttributeDataType.Text };
            var description = new MetaverseAttribute { Name = "description", Type = AttributeDataType.Text };
            var member = new MetaverseAttribute { Name = "member", Type = AttributeDataType.Reference };

            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
            var syncRule = new SyncRule
            {
                Name = "Directory Import", ConnectedSystem = system, ConnectedSystemObjectType = csType,
                MetaverseObjectType = groupType, Direction = SyncRuleDirection.Import
            };

            seed.AddRange(personType, groupType, employeeNumber, enabled, manager, groupName, description, member,
                connectorDefinition, system, csType, syncRule);
            seed.AddRange(textAttributes);
            await seed.SaveChangesAsync();

            seeded = new Seeded(personType, groupType, system.Id, syncRule.Id, textAttributes.Select(a => a.Id).ToArray(),
                employeeNumber.Id, enabled.Id, manager.Id, groupName.Id, description.Id, member.Id, []);
        }

        // The people the pages reference, written as earlier pages would have written them: on one connection, in
        // chunks, with no values (the references only need the rows).
        for (var offset = 0; offset < SeededPeople; offset += 50_000)
        {
            var people = Enumerable.Range(0, 50_000)
                .Select(_ => new MetaverseObject { Id = Guid.NewGuid(), Type = seeded.PersonType, Created = DateTime.UtcNow })
                .ToList();
            await using var context = NewContext();
            var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
            sync.ParallelWriteRowThreshold = int.MaxValue;
            await sync.CreateMetaverseObjectsAsync(people);
            seeded.PeopleIds.AddRange(people.Select(p => p.Id));
        }

        await ExecuteAsync("""VACUUM ANALYZE "MetaverseObjects", "MetaverseObjectAttributeValues" """);
        return seeded;
    }

    private static MetaverseObjectAttributeValue Value(Seeded s, int attributeId) => new()
    {
        AttributeId = attributeId,
        ContributedBySystemId = s.SystemId,
        ContributedBySyncRuleId = s.SyncRuleId,
        ContributedByKind = ValueContributorKind.SynchronisationRule
    };

    private static List<MetaverseObject> BuildPeoplePage(Seeded s, bool extraValue = false)
    {
        var page = new List<MetaverseObject>(500);
        for (var i = 0; i < 500; i++)
        {
            var person = new MetaverseObject { Type = s.PersonType, Created = DateTime.UtcNow };
            foreach (var attributeId in s.PersonTextAttributeIds)
            {
                var value = Value(s, attributeId);
                value.StringValue = $"value-{attributeId}-{i}";
                person.AttributeValues.Add(value);
            }

            var number = Value(s, s.EmployeeNumberAttributeId);
            number.IntValue = 100_000 + i;
            person.AttributeValues.Add(number);
            var enabled = Value(s, s.EnabledAttributeId);
            enabled.BoolValue = true;
            person.AttributeValues.Add(enabled);
            var manager = Value(s, s.ManagerAttributeId);
            manager.ReferenceValueId = s.PeopleIds[i * 7 % s.PeopleIds.Count];
            person.AttributeValues.Add(manager);
            if (extraValue)
            {
                var extra = Value(s, s.PersonTextAttributeIds[0]);
                extra.StringValue = $"extra-{i}";
                person.AttributeValues.Add(extra);
            }

            page.Add(person);
        }

        return page;
    }

    private static List<MetaverseObject> BuildGroupPage(Seeded s, IReadOnlyList<int> memberCounts)
    {
        var page = new List<MetaverseObject>(memberCounts.Count);
        var nextPerson = 0;
        for (var g = 0; g < memberCounts.Count; g++)
        {
            var group = new MetaverseObject { Type = s.GroupType, Created = DateTime.UtcNow };
            var name = Value(s, s.GroupNameAttributeId);
            name.StringValue = $"group-{g}";
            group.AttributeValues.Add(name);
            var description = Value(s, s.GroupDescriptionAttributeId);
            description.StringValue = $"Group {g}";
            group.AttributeValues.Add(description);

            // Distinct members within a group, wrapping around the seeded people.
            for (var m = 0; m < memberCounts[g]; m++)
            {
                var member = Value(s, s.MemberAttributeId);
                member.ReferenceValueId = s.PeopleIds[(nextPerson + m) % s.PeopleIds.Count];
                group.AttributeValues.Add(member);
            }

            nextPerson = (nextPerson + memberCounts[g]) % s.PeopleIds.Count;
            page.Add(group);
        }

        return page;
    }

    /// <summary>
    /// Settles the database before a timed run: dirty pages flushed, so no run pays for an earlier run's checkpoint.
    /// </summary>
    private Task PrepareAsync() => ExecuteAsync("CHECKPOINT");

    private async Task AssertWrittenAndRemoveAsync(List<MetaverseObject> page)
    {
        var ids = page.Select(m => m.Id).ToArray();
        var expectedValues = page.Sum(m => m.AttributeValues.Count);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var count = new NpgsqlCommand(
            """SELECT count(*) FROM "MetaverseObjectAttributeValues" WHERE "MetaverseObjectId" = ANY(@ids)""", connection))
        {
            count.Parameters.AddWithValue("ids", ids);
            Assert.That(Convert.ToInt32(await count.ExecuteScalarAsync()), Is.EqualTo(expectedValues),
                "every value of the page must have been written");
        }

        await using (var delete = new NpgsqlCommand("""DELETE FROM "MetaverseObjects" WHERE "Id" = ANY(@ids)""", connection))
        {
            delete.CommandTimeout = 600;
            delete.Parameters.AddWithValue("ids", ids);
            Assert.That(await delete.ExecuteNonQueryAsync(), Is.EqualTo(page.Count));
        }

        await using (var vacuum = new NpgsqlCommand("""VACUUM "MetaverseObjects", "MetaverseObjectAttributeValues" """, connection))
        {
            vacuum.CommandTimeout = 600;
            await vacuum.ExecuteNonQueryAsync();
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 600 };
        await command.ExecuteNonQueryAsync();
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}

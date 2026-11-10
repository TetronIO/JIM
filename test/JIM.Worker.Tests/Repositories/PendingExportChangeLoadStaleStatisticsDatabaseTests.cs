// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL regression for the loads that read many Pending Exports' changes at once, under statistics taken
/// while <c>PendingExportAttributeValueChanges</c> held no live row (#2884): an Export removes the changes it executes,
/// autovacuum analyses the table then, and the next synchronisation fills it again before autoanalyze returns.
/// </summary>
/// <remarks>
/// <para>
/// A load that reaches the changes through a join leaves how it reads them to the statistics. On a 500,000-user leaver
/// step the planner, believing the table held one row, read all 41,281 changes through the <c>PendingExportId</c> index
/// with no index condition and probed <c>PendingExports</c>' primary key for each, to keep a batch's 100: 197 ms per
/// batch, 105 times. Each load here must instead seek the changes by their Pending Export's id, whatever the statistics
/// say, so the changes it reads are the changes it returns.
/// </para>
/// <para>
/// The statistics are made stale the way a run leaves them: an earlier run's exports and changes are written and removed,
/// both tables are analysed holding pages but no live row, autovacuum is kept off them for the test, and only then are
/// this run's Pending Exports and their changes written. The planner then estimates any read of the changes at one row,
/// as it did on the leaver step. Each statement the load issues is captured and run again under <c>EXPLAIN ANALYZE</c>
/// with its own parameters. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent. <c>SetUp</c> TRUNCATEs every table.
/// </para>
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class PendingExportChangeLoadStaleStatisticsDatabaseTests
{
    private const string ChangesTable = "PendingExportAttributeValueChanges";
    private const int ExportCount = 3000;
    private const int ChangesPerExport = 3;
    private const int LoadedExportCount = 5;
    private static readonly DateTime T0 = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    // The tables a Pending Export load reads its exports and changes from, analysed together while neither holds a live row.
    private static readonly string[] StaleTables = ["PendingExports", ChangesTable];

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL Pending Export change load stale statistics tests.");
        _connectionString = connectionString!;

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
        await ExecuteAsync(string.Join(" ", StaleTables.Select(t => $"ALTER TABLE \"{t}\" SET (autovacuum_enabled = false);")));
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        if (_connectionString != null)
            await ExecuteAsync(string.Join(" ", StaleTables.Select(t => $"ALTER TABLE \"{t}\" RESET (autovacuum_enabled);")));
    }

    private JimDbContext NewContext(CommandRecordingInterceptor? recorder = null, QueryTrackingBehavior tracking = QueryTrackingBehavior.TrackAll)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (recorder != null)
            builder.AddInterceptors(recorder);
        var context = new JimDbContext(builder.Options);
        context.ChangeTracker.QueryTrackingBehavior = tracking;
        return context;
    }

    // ─── Export execution's batch load ───

    /// <summary>
    /// The export batch load (sequential batches, a parallel worker's re-load, and the deferred pass's chunks) seeks
    /// the batch's changes by their Pending Export's id, reading no change it does not return.
    /// </summary>
    [Test]
    public async Task GetPendingExportsByIdsAsync_StatisticsTakenWhileTheChangesWereEmpty_SeeksTheBatchsChangesByPendingExportIdAsync()
    {
        var seeded = await SeedUnderStaleStatisticsAsync();
        var ids = seeded.Exports.Skip(ExportCount / 2).Take(LoadedExportCount).Select(e => e.Id).ToList();

        var recorder = new CommandRecordingInterceptor();
        await using (var ctx = NewContext(recorder))
            await new PostgresDataRepository(ctx).Sync.GetPendingExportsByIdsAsync(ids);

        await AssertChangesSoughtByPendingExportIdAsync(recorder, LoadedExportCount * ChangesPerExport);
    }

    /// <summary>
    /// The batch load hands execution the graph its single Include load did: the exports in queue order, each change
    /// with its attribute, the object with its Object Type and its values; one instance of each attribute and Object
    /// Type across the batch, the attribute joined to the Object Type that holds it, and a change's attribute the same
    /// instance as a value's of the same attribute. Each export's changes come in id order, as the single-query Includes
    /// returned them.
    /// </summary>
    [Test]
    public async Task GetPendingExportsByIdsAsync_StatisticsTakenWhileTheChangesWereEmpty_LoadsTheGraphTheIncludeLoadDidAsync()
    {
        var seeded = await SeedUnderStaleStatisticsAsync();
        // Out of queue order, so the load's own ordering is what puts them back.
        var ids = seeded.Exports.Skip(ExportCount / 2).Take(LoadedExportCount).Select(e => e.Id).Reverse().ToList();

        List<PendingExport> expected;
        await using (var reference = NewContext())
            expected = await LoadAsTheIncludeLoadDidAsync(reference, ids);
        await using var ctx = NewContext();
        var loaded = await new PostgresDataRepository(ctx).Sync.GetPendingExportsByIdsAsync(ids);

        var changeAttributes = loaded.SelectMany(pe => pe.AttributeValueChanges).Select(c => c.Attribute).ToList();
        var types = loaded.Select(pe => pe.ConnectedSystemObject!.Type).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded.Select(pe => pe.Id), Is.EqualTo(expected.Select(pe => pe.Id)), "the exports, in (CreatedAt, Id) order");
            Assert.That(loaded.Select(DescribeChanges), Is.EqualTo(expected.Select(DescribeChanges)),
                "each export's changes, each with its attribute (compared in id order: the split Include ordered them by their export alone)");
            Assert.That(loaded.All(pe => pe.AttributeValueChanges.Select(c => c.Id).SequenceEqual(pe.AttributeValueChanges.Select(c => c.Id).Order())),
                Is.True, "each export's changes in id order, so a Connector applying them in sequence sees one order every time");
            Assert.That(loaded.Select(DescribeObject), Is.EqualTo(expected.Select(DescribeObject)),
                "each export's object, with its Object Type and its values");
            Assert.That(changeAttributes.Distinct().Count(), Is.EqualTo(changeAttributes.DistinctBy(a => a.Id).Count()),
                "one instance of each attribute across the batch");
            Assert.That(types.Distinct().Count(), Is.EqualTo(1), "one instance of the Object Type across the batch");
            Assert.That(changeAttributes.All(a => ReferenceEquals(a.ConnectedSystemObjectType, types[0]) && types[0].Attributes.Contains(a)), Is.True,
                "each change's attribute is joined to the Object Type that holds it, both ways");
            Assert.That(loaded.All(pe => pe.ConnectedSystemObject!.AttributeValues
                    .Where(v => v.AttributeId == seeded.Schema.MailAttributeId)
                    .All(v => ReferenceEquals(v.Attribute, pe.AttributeValueChanges.Single(c => c.AttributeId == seeded.Schema.MailAttributeId).Attribute))),
                Is.True, "a value's attribute is the same instance as a change's of the same attribute");
            Assert.That(DescribeTypeAttributes(types[0]), Is.EqualTo(DescribeTypeAttributes(expected[0].ConnectedSystemObject!.Type)),
                "the Object Type holds the attributes the Include load joined to it");
            Assert.That(ctx.ChangeTracker.Entries().Any(), Is.False, "the batch is loaded untracked, on a tracking context too");
        }
    }

    // ─── The synchronisation's Pending Export reads ───

    /// <summary>
    /// The synchronisation's per-page read of its objects' Pending Exports (export evaluation, reconciliation's
    /// releases) seeks their changes by Pending Export id, and returns each export with its changes in id order, each
    /// with its attribute.
    /// </summary>
    [Test]
    public async Task GetPendingExportsLightweightByConnectedSystemObjectIdsAsync_StatisticsTakenWhileTheChangesWereEmpty_SeeksTheChangesByPendingExportIdAsync()
    {
        var seeded = await SeedUnderStaleStatisticsAsync();
        var exports = seeded.Exports.Skip(ExportCount / 2).Take(LoadedExportCount).ToList();

        var recorder = new CommandRecordingInterceptor();
        Dictionary<Guid, PendingExport> loaded;
        await using (var ctx = NewContext(recorder, QueryTrackingBehavior.NoTracking))
            loaded = await new PostgresDataRepository(ctx).Sync.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(exports.Select(e => e.ObjectId).ToList());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded.Keys, Is.EquivalentTo(exports.Select(e => e.ObjectId)));
            Assert.That(exports.Select(e => loaded[e.ObjectId].Id), Is.EqualTo(exports.Select(e => e.Id)));
            Assert.That(loaded.Values.All(pe => pe.AttributeValueChanges.Count == ChangesPerExport &&
                                                pe.AttributeValueChanges.Select(c => c.Id).SequenceEqual(pe.AttributeValueChanges.Select(c => c.Id).Order()) &&
                                                pe.AttributeValueChanges.All(c => c.Attribute?.Id == c.AttributeId && c.PendingExportId == pe.Id)),
                Is.True, "each export carries its own changes, in id order, each with its attribute");
        }

        await AssertChangesSoughtByPendingExportIdAsync(recorder, LoadedExportCount * ChangesPerExport);
    }

    /// <summary>
    /// The cross-page reference step's batch prefetch seeks the changes by Pending Export id, and leaves each export
    /// tracked with its tracked changes, as the per-object read it answers for would have.
    /// </summary>
    [Test]
    public async Task PrefetchPendingExportsForBatchAsync_StatisticsTakenWhileTheChangesWereEmpty_SeeksTheChangesByPendingExportIdAsync()
    {
        var seeded = await SeedUnderStaleStatisticsAsync();
        var exports = seeded.Exports.Skip(ExportCount / 2).Take(LoadedExportCount).ToList();

        var recorder = new CommandRecordingInterceptor();
        await using (var ctx = NewContext(recorder))
        {
            var sync = new PostgresDataRepository(ctx).Sync;
            sync.BeginPendingExportBatch();
            await sync.PrefetchPendingExportsForBatchAsync(exports.Select(e => e.ObjectId).ToList());
            var prefetchCommands = recorder.Commands.Count;

            var answered = new List<PendingExport>();
            foreach (var export in exports)
                answered.Add((await sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(export.ObjectId))!);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(recorder.Commands, Has.Count.EqualTo(prefetchCommands), "every object is answered from the prefetch");
                Assert.That(answered.Select(pe => pe.Id), Is.EqualTo(exports.Select(e => e.Id)));
                Assert.That(answered.All(pe => pe.AttributeValueChanges.Count == ChangesPerExport &&
                                               pe.AttributeValueChanges.All(c => c.Attribute?.Id == c.AttributeId && c.PendingExportId == pe.Id)),
                    Is.True, "each export carries its own changes, each with its attribute");
                Assert.That(answered.All(pe => ctx.Entry(pe).State == EntityState.Unchanged &&
                                               pe.AttributeValueChanges.All(c => ctx.Entry(c).State == EntityState.Unchanged)),
                    Is.True, "the exports and their changes are tracked, as the per-object read tracks them");
            }
        }

        await AssertChangesSoughtByPendingExportIdAsync(recorder, LoadedExportCount * ChangesPerExport);
    }

    /// <summary>
    /// The per-object read the prefetch answers for, where an object was not prefetched, stays an Include: with one
    /// Pending Export to join, the planner probes the changes' index by that export's id even believing the table empty.
    /// Held here so a change to its shape cannot quietly bring back the whole-table read.
    /// </summary>
    [Test]
    public async Task GetPendingExportLightweightByConnectedSystemObjectIdAsync_StatisticsTakenWhileTheChangesWereEmpty_SeeksTheChangesByPendingExportIdAsync()
    {
        var seeded = await SeedUnderStaleStatisticsAsync();
        var export = seeded.Exports[ExportCount / 2];

        var recorder = new CommandRecordingInterceptor();
        PendingExport? loaded;
        await using (var ctx = NewContext(recorder))
            loaded = await new PostgresDataRepository(ctx).Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(export.ObjectId);

        Assert.That(loaded?.AttributeValueChanges.Select(c => c.Attribute?.Id).ToList(), Has.Count.EqualTo(ChangesPerExport).And.None.Null);
        await AssertChangesSoughtByPendingExportIdAsync(recorder, ChangesPerExport);
    }

    private static string DescribeChanges(PendingExport export) => string.Join("; ", export.AttributeValueChanges.OrderBy(c => c.Id).Select(c =>
        $"{c.Id} {c.ChangeType} {c.Status} {c.StringValue} {c.AttributeId}={c.Attribute?.Id}:{c.Attribute?.Name}"));

    private static string DescribeObject(PendingExport export)
    {
        var cso = export.ConnectedSystemObject!;
        var values = cso.AttributeValues.OrderBy(v => v.Id)
            .Select(v => $"{v.Id} {v.StringValue} {v.AttributeId}={v.Attribute?.Name} {ReferenceEquals(v.ConnectedSystemObject, cso)}");
        return $"{cso.Id} {cso.TypeId}={cso.Type?.Name} [{string.Join("; ", values)}]";
    }

    private static string DescribeTypeAttributes(ConnectedSystemObjectType type) =>
        string.Join(",", type.Attributes.Select(a => a.Id).Order());

    /// <summary>
    /// The batch as the load read it before #2884, and before #2816 trimmed its values: one query, every change and
    /// value Included, untracked with identity resolution. Nothing in this fixture's schema is trimmed, so it is the
    /// graph the load must still hand over.
    /// </summary>
    private static Task<List<PendingExport>> LoadAsTheIncludeLoadDidAsync(JimDbContext context, IReadOnlyCollection<Guid> ids) =>
        context.PendingExports
            .AsNoTrackingWithIdentityResolution()
            .AsSplitQuery()
            .Include(pe => pe.AttributeValueChanges).ThenInclude(avc => avc.Attribute)
            .Include(pe => pe.ConnectedSystemObject).ThenInclude(cso => cso!.Type)
            .Include(pe => pe.ConnectedSystemObject).ThenInclude(cso => cso!.AttributeValues).ThenInclude(av => av.Attribute)
            .Where(pe => ids.Contains(pe.Id))
            .OrderBy(pe => pe.CreatedAt).ThenBy(pe => pe.Id)
            .ToListAsync();

    // ─── Plan inspection ───

    private sealed record ChangeScan(string NodeType, string? IndexName, string? IndexCondition, long RowsRead);

    /// <summary>
    /// Every scan of the changes table across the recorded statements reads by an index condition on its Pending Export's
    /// id, and together they read exactly the changes the load returns.
    /// </summary>
    private async Task AssertChangesSoughtByPendingExportIdAsync(CommandRecordingInterceptor recorder, int expectedChanges)
    {
        var scans = new List<ChangeScan>();
        var plans = new List<string>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        foreach (var (text, parameters) in recorder.Commands)
        {
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, FORMAT JSON) " + text, connection);
            foreach (var parameter in parameters)
                explain.Parameters.Add(parameter.Clone());
            var plan = (string)(await explain.ExecuteScalarAsync())!;
            plans.Add($"{text}\n{plan}");
            CollectChangeScans(JsonDocument.Parse(plan).RootElement[0].GetProperty("Plan"), scans);
        }

        var described = string.Join("\n", scans.Select(s => $"{s.NodeType} {s.IndexName} [{s.IndexCondition}] rows read {s.RowsRead}"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(scans, Is.Not.Empty, $"the load reads the changes. Plans:\n{string.Join("\n\n", plans)}");
            Assert.That(scans.All(s => s.IndexCondition?.Contains("\"PendingExportId\"", StringComparison.Ordinal) == true), Is.True,
                $"every read of the changes seeks them by Pending Export id, never scanning the table or its index whole:\n{described}\n\nPlans:\n{string.Join("\n\n", plans)}");
            Assert.That(scans.Sum(s => s.RowsRead), Is.EqualTo(expectedChanges),
                $"the load reads only the changes it returns:\n{described}");
        }
    }

    /// <summary>
    /// The scans of the changes table in a plan: an index scan carries its own condition; a bitmap heap scan takes its
    /// condition from the bitmap index scans beneath it; a sequential scan has none.
    /// </summary>
    private static void CollectChangeScans(JsonElement node, List<ChangeScan> scans)
    {
        if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == ChangesTable)
        {
            var nodeType = node.GetProperty("Node Type").GetString()!;
            string? indexName = node.TryGetProperty("Index Name", out var name) ? name.GetString() : null;
            string? condition = node.TryGetProperty("Index Cond", out var cond) ? cond.GetString() : null;
            if (nodeType == "Bitmap Heap Scan" && node.TryGetProperty("Plans", out var bitmaps))
            {
                var bitmapIndexScans = bitmaps.EnumerateArray().Where(b => b.TryGetProperty("Index Cond", out _)).ToList();
                indexName = string.Join(",", bitmapIndexScans.Select(b => b.GetProperty("Index Name").GetString()));
                condition = bitmapIndexScans.Count == 0 ? null : string.Join(" AND ", bitmapIndexScans.Select(b => b.GetProperty("Index Cond").GetString()));
            }

            var loops = node.GetProperty("Actual Loops").GetDouble();
            double Per(string property) => node.TryGetProperty(property, out var value) ? value.GetDouble() : 0;
            var rowsRead = (long)Math.Round(loops * (Per("Actual Rows") + Per("Rows Removed by Filter") + Per("Rows Removed by Index Recheck")));
            scans.Add(new ChangeScan(nodeType, indexName, condition, rowsRead));
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
                CollectChangeScans(child, scans);
        }
    }

    // ─── Seeding ───

    private sealed record Schema(int SystemId, int TypeId, int DnAttributeId, int MailAttributeId, int DescriptionAttributeId, int TitleAttributeId);

    private sealed record SeededExport(Guid Id, Guid ObjectId);

    private sealed record Seeded(Schema Schema, List<SeededExport> Exports);

    /// <summary>
    /// The statistics a run leaves: an earlier run's Pending Exports and changes written and then removed (as the Export
    /// that executes them removes them), and the tables analysed while they hold pages but no live row, as autovacuum
    /// analyses them after that Export. The planner then takes each table to hold no row whatever it grows to, and
    /// estimates any read of it at one row. Then this run's people, each with a DN and a mail value, and an Update
    /// Pending Export per person with its changes, written with no analysis after.
    /// </summary>
    private async Task<Seeded> SeedUnderStaleStatisticsAsync()
    {
        var schema = await SeedSchemaAsync();
        await WritePendingExportsAsync(schema, ExportCount / 3, "earlier");
        await ExecuteAsync($"DELETE FROM \"{ChangesTable}\"; DELETE FROM \"PendingExports\";");
        await ExecuteAsync($"ANALYZE {string.Join(", ", StaleTables.Select(t => $"\"{t}\""))};");

        var exports = await WritePendingExportsAsync(schema, ExportCount, "person");
        return new Seeded(schema, exports);
    }

    /// <summary>
    /// People, each with a DN and a mail value and an Update Pending Export of <see cref="ChangesPerExport"/> changes, in
    /// queue order.
    /// </summary>
    private async Task<List<SeededExport>> WritePendingExportsAsync(Schema schema, int count, string prefix)
    {
        var exports = new List<SeededExport>();
        await using var seed = NewContext();
        for (var n = 0; n < count; n++)
        {
            var person = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(), ConnectedSystemId = schema.SystemId, TypeId = schema.TypeId, ExternalIdAttributeId = schema.DnAttributeId,
                Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.NotJoined
            };
            person.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                { Id = Guid.NewGuid(), AttributeId = schema.DnAttributeId, StringValue = $"uid={prefix}{n},ou=People,dc=glitterband,dc=local" });
            person.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
                { Id = Guid.NewGuid(), AttributeId = schema.MailAttributeId, StringValue = $"{prefix}{n}@glitterband.local" });
            var export = new PendingExport
            {
                Id = Guid.NewGuid(), ConnectedSystemId = schema.SystemId, ConnectedSystemObjectId = person.Id, ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending, CreatedAt = T0.AddSeconds(n)
            };
            foreach (var attributeId in new[] { schema.MailAttributeId, schema.DescriptionAttributeId, schema.TitleAttributeId })
            {
                export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(), AttributeId = attributeId, ChangeType = PendingExportAttributeChangeType.Update,
                    Status = PendingExportAttributeChangeStatus.Pending, StringValue = $"{prefix} {n}.{attributeId}"
                });
            }

            seed.ConnectedSystemObjects.Add(person);
            seed.PendingExports.Add(export);
            exports.Add(new SeededExport(export.Id, person.Id));
        }

        await seed.SaveChangesAsync();
        return exports;
    }

    private async Task<Schema> SeedSchemaAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "inetOrgPerson", ConnectedSystem = system, Selected = true };
        ConnectedSystemObjectTypeAttribute Attribute(string name, bool externalId = false)
        {
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = name, ConnectedSystemObjectType = type, Type = AttributeDataType.Text,
                AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = externalId
            };
            type.Attributes.Add(attribute);
            return attribute;
        }

        var dn = Attribute("distinguishedName", externalId: true);
        var mail = Attribute("mail");
        var description = Attribute("description");
        var title = Attribute("title");
        seed.AddRange(connectorDefinition, system, type);
        await seed.SaveChangesAsync();
        return new Schema(system.Id, type.Id, dn.Id, mail.Id, description.Id, title.Id);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

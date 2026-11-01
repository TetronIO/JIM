// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using JIM.Application;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Utility;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using NUnit.Framework;
using JimActivity = JIM.Models.Activities.Activity;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Measures, on real PostgreSQL, the costs #2669 found in a leaver cohort's confirming Delta Synchronisation, and
/// prints them; it asserts only that each path did its job, not how fast.
/// <list type="bullet">
/// <item>Deleting a page of obsolete Connected System Objects: 500 departed accounts of 25 values each, on a tracking
/// context that also holds two groups' 50,000 member values referencing them and others, and the run's Connected System,
/// as the Worker holds a page (tracking, change detection off). Times the repository deletion through each way in: the Connected System
/// repository's, the quiet deletion that wraps it (the path a leaver cohort actually takes), the execution-item
/// deletion, and the Synchronisation repository's delete by id; each with and without the history rows (execution
/// items, change records and the values in them) the database clears to null when an object goes. The statement
/// count it prints is held constant by <see cref="ConnectedSystemObjectBatchDeletionDatabaseTests"/>; this measures
/// the time.</item>
/// <item>Deleting 500 departed people's Metaverse Objects of 25 values each, among 50,000, through both deleters: the
/// synchronisation flush's bulk deletion on a page context, and the portal's and housekeeping's singular deletion,
/// once per object, on a tracking context as housekeeping loads them. Each departed person is a member of two groups of
/// 25,000, the Manager of another person, and joined to an account in a target system; with history, each also has
/// change records and Activities of its own, and other objects' change records hold Manager and member values naming
/// it.</item>
/// <item>Beside every deletion's time, how many rows of each audit table it updated and deleted, the rewrites the plan
/// for #2669 (<c>engineering/plans/doing/AUDIT_REFERENCES_WITHOUT_FOREIGN_KEYS.md</c>) removes. Every path commits its
/// own transaction (the quiet deletion's change record persistence refuses an ambient one), so the counts are the
/// difference in <c>pg_stat_user_tables</c> before and after, not <c>pg_stat_xact_user_tables</c>. That view lags:
/// a backend flushes its counts to it only when idle, at most once a second. So the deletion runs on one connection,
/// held open, which flushes with <c>pg_stat_force_next_flush()</c> before each read; and the counts must agree across
/// the measured runs, which a late flush would break.</item>
/// <item>Loading a modified group's page, at several membership sizes, in each of the ways the plan for #2669 weighs:
/// today's tracked load through <see cref="TypeOrderedCsoPager.NextPageAsync"/>, split by its spans; the same EF query
/// untracked (option (a')); EF rows with raw values attached to the context (option (b)); a raw read that builds what
/// a raw page reader would (the floor for option (a)); and a raw read of one column per value row, which builds
/// nothing. Each asserts it loaded every value, so a variant that loaded less cannot report a time.</item>
/// </list>
/// </summary>
/// <remarks>
/// Explicit, because it is a measurement: timings depend on the host, so a bound here would be either loose enough to
/// mean nothing or tight enough to fail at random. Run it on demand to compare before and after a change:
/// <c>JIM_TEST_RESET_DB=... dotnet test --filter FullyQualifiedName~LeaverCohortCostDatabaseTests</c>.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
[Explicit("A measurement for #2669: prints timings and statement counts rather than asserting a bound.")]
public class LeaverCohortCostDatabaseTests
{
    private const int Leavers = 500;
    private const int ValuesPerLeaver = 25;
    private const int Users = 50_000;
    private const int MembersPerGroup = 25_000;
    private const int MeasuredRuns = 5;

    // A page load's runs vary widely on a shared host (the first measured run after the warm-up was over twice the
    // median at 25,000 members), so its median is taken over five of them, as a deletion's is.
    private const int PageLoadRuns = 5;

    // History each departed account carries at the moment it is deleted, all of it pointing at the account and so all
    // of it cleared to null by the database as the account goes: execution items from the runs that touched it, its own
    // change records, and member values in groups' change records. A departed person's Metaverse Object carries the
    // same change records and change values (one of them a Manager value on its report's change record), plus
    // Activities naming it, all cleared by the deleters' own statements.
    private const int ExecutionItemsPerLeaver = 5;
    private const int ChangeRecordsPerLeaver = 3;
    private const int ChangeValuesPerLeaver = 15;
    private const int ActivitiesPerLeaver = 2;

    /// <summary>
    /// The audit tables whose rows name a Connected System Object or a Metaverse Object, as the plan's inventory lists
    /// them.
    /// </summary>
    private static readonly string[] AuditTables =
    [
        "ConnectedSystemObjectChanges", "ConnectedSystemObjectChangeAttributeValues", "ActivityRunProfileExecutionItems",
        "MetaverseObjectChanges", "MetaverseObjectChangeAttributeValues", "Activities"
    ];

    private string _connectionString = null!;
    private SpanRecorder _spans = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping the real-PostgreSQL leaver cohort measurements.");
        _connectionString = connectionString!;

        using var ctx = NewContext(null);
        // The repository sets Npgsql's timestamp switch, as the Worker does, before anything is written.
        _ = new PostgresDataRepository(ctx);
        ctx.Database.Migrate();
        _spans = new SpanRecorder();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _spans?.Dispose();

    private JimDbContext NewContext(SqlStatementCounter? counter)
    {
        var builder = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning, RelationalEventId.MultipleCollectionIncludeWarning));
        if (counter != null)
            builder.AddInterceptors(counter);
        return new JimDbContext(builder.Options);
    }

    /// <summary>
    /// A tracking context with change detection off, as the Worker holds it from a page's load to its tracker clear.
    /// </summary>
    private JimDbContext NewPageContext(SqlStatementCounter? counter)
    {
        var context = NewContext(counter);
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        return context;
    }

    public enum DeletionPath
    {
        /// <summary><c>ConnectedSystemRepository.DeleteConnectedSystemObjectsAsync</c>, called directly.</summary>
        ConnectedSystemRepository,

        /// <summary><c>SyncServer.DeleteConnectedSystemObjectsQuietlyAsync</c>: deletion records built, the objects
        /// deleted, the records persisted.</summary>
        QuietDeletion,

        /// <summary><c>SyncServer.DeleteConnectedSystemObjectsAsync</c>: deletion records linked to execution items,
        /// the objects deleted.</summary>
        ExecutionItems,

        /// <summary><c>SyncRepository.DeleteConnectedSystemObjectsByIdsAsync</c>, the export path's.</summary>
        SyncRepositoryByIds
    }

    public enum MetaverseDeletionPath
    {
        /// <summary><c>SyncRepository.DeleteMetaverseObjectsAsync</c>: the synchronisation flush's, every object in one
        /// pass, on a page context, the objects passed detached as the flush passes them.</summary>
        Bulk,

        /// <summary><c>MetaverseRepository.DeleteMetaverseObjectAsync</c>, once per object: the portal's and
        /// housekeeping's, on a tracking context with change detection on, each object loaded tracked with its Type and
        /// Connected System Objects, as housekeeping loads it.</summary>
        Singular
    }

    [TestCase(DeletionPath.ConnectedSystemRepository)]
    [TestCase(DeletionPath.QuietDeletion)]
    [TestCase(DeletionPath.ExecutionItems)]
    [TestCase(DeletionPath.SyncRepositoryByIds)]
    public async Task DeleteConnectedSystemObjectsAsync_LeaverPageOnLargeGroupContext_ReportsThePathsCostAsync(DeletionPath path)
    {
        var report = new List<string>();
        foreach (var withHistory in new[] { false, true })
        {
            // One run discarded for warm-up (JIT, EF's model and query caches), then the measured runs.
            await MeasureDeletionAsync(path, withHistory);
            var runs = new List<DeletionRun>();
            for (var i = 0; i < MeasuredRuns; i++)
                runs.Add(await MeasureDeletionAsync(path, withHistory));

            report.Add(DescribeDeletionRuns($"{path}{(withHistory ? ", with history" : "")}", runs));
            TestContext.Progress.WriteLine(report[^1]);
        }

        TestContext.Out.WriteLine(string.Join(Environment.NewLine + Environment.NewLine, report));
    }

    [TestCase(MetaverseDeletionPath.Bulk)]
    [TestCase(MetaverseDeletionPath.Singular)]
    public async Task DeleteMetaverseObjectsAsync_LeaverPageReferencedByGroupsAndHistory_ReportsThePathsCostAsync(MetaverseDeletionPath path)
    {
        var report = new List<string>();
        foreach (var withHistory in new[] { false, true })
        {
            await MeasureMetaverseDeletionAsync(path, withHistory);
            var runs = new List<DeletionRun>();
            for (var i = 0; i < MeasuredRuns; i++)
                runs.Add(await MeasureMetaverseDeletionAsync(path, withHistory));

            report.Add(DescribeDeletionRuns($"Metaverse Object {path}{(withHistory ? ", with history" : "")}", runs));
            TestContext.Progress.WriteLine(report[^1]);
        }

        TestContext.Out.WriteLine(string.Join(Environment.NewLine + Environment.NewLine, report));
    }

    [TestCase(25_000)]
    [TestCase(50_000)]
    [TestCase(100_000)]
    [TestCase(400_000)]
    public async Task NextPageAsync_ModifiedGroupWithLargeMembership_ReportsPageLoadCostAsync(int members)
    {
        var seeded = await SeedModifiedGroupAsync(members);

        var variants = new List<(string Name, List<PageLoadRun> Runs)>
        {
            ("EF tracked, the Delta loader through NextPageAsync", await MeasureAsync(() => MeasurePageLoadAsync(seeded))),
            ("(a') EF AsNoTracking, the main query alone", await MeasureAsync(() => LoadWithUntrackedQueryAsync(seeded))),
            ("(b) EF rows, raw values attached Unchanged, references joined", await MeasureAsync(() => LoadWithAttachedRawValuesAsync(seeded))),
            ("(c) raw rows and values built into objects, references joined", await MeasureAsync(() => ReadPageRawAsync(seeded))),
            ("raw, one column per value row (the floor)", await MeasureAsync(() => ReadGroupValuesRawAsync(seeded)))
        };

        TestContext.Out.WriteLine(DescribePageLoadRuns(members, variants));
    }

    #region Deletion

    private sealed record DeletionRun(
        double TotalMs, Dictionary<string, double> SpanMs, Dictionary<string, string> SpanTags,
        int Commands, Dictionary<string, int> Statements, Dictionary<string, AuditRowCounts> AuditRows);

    private async Task<DeletionRun> MeasureDeletionAsync(DeletionPath path, bool withHistory)
    {
        var seeded = await SeedLeaverPageAsync(withHistory);
        var counter = new SqlStatementCounter();

        await using var ctx = NewPageContext(counter);
        // One connection for the whole run, so the audit counts read below are this connection's to flush.
        await ctx.Database.OpenConnectionAsync();
        var repository = new PostgresDataRepository(ctx);
        // The two server paths run through the application layer; disposing it disposes the shared context, so it lives
        // as long as the context does.
        using var jim = path is DeletionPath.QuietDeletion or DeletionPath.ExecutionItems
            ? new JimApplication(repository, syncRepository: repository.Sync)
            : null;
        var activity = new JimActivity
        {
            TargetName = "Target confirming synchronisation",
            InitiatedByType = ActivityInitiatorType.System
        };

        // The Connected System first, tracked, as the Worker loads its own at the start of the run: fix-up then adds
        // every object the page loads to its Objects, which the deletion has to take the deleted ones out of.
        var connectedSystem = await ctx.ConnectedSystems.SingleAsync(cs => cs.Id == seeded.SystemId);
        var userPage = await repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(
            seeded.SystemId, seeded.Watermark, 1, 500, Leavers, Guid.Empty, seeded.UserTypeId, null);
        var groupPage = await repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(
            seeded.SystemId, seeded.Watermark, 1, 500, 2, Guid.Empty, seeded.GroupTypeId, null);
        var leavers = userPage.Results;
        var leaverIds = leavers.Select(c => c.Id).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(leavers, Has.Count.EqualTo(Leavers));
            Assert.That(groupPage.Results, Has.Count.EqualTo(2));
            Assert.That(ctx.ChangeTracker.Entries<ConnectedSystemObjectAttributeValue>().Count(),
                Is.EqualTo(Leavers * ValuesPerLeaver + 2 * (MembersPerGroup + 1)), "the page as the Worker tracks it");
            Assert.That(connectedSystem.Objects, Has.Count.EqualTo(Leavers + 2), "fix-up added the page's objects to the tracked Connected System");
        }

        // The execution-item path deletes objects the obsoletion handling gave an execution item each, as the processor
        // queues them.
        var executionItems = leavers.Select(cso =>
        {
            var item = activity.PrepareRunProfileExecutionItem();
            item.ConnectedSystemObject = cso;
            item.ConnectedSystemObjectId = cso.Id;
            item.ObjectChangeType = ObjectChangeType.Deleted;
            return item;
        }).ToList();

        var auditBefore = await ReadAuditRowCountsAsync(ctx);
        _spans.Start();
        counter.Counting = true;
        var stopwatch = Stopwatch.StartNew();
        switch (path)
        {
            case DeletionPath.ConnectedSystemRepository:
                await repository.ConnectedSystems.DeleteConnectedSystemObjectsAsync(leavers);
                break;
            case DeletionPath.QuietDeletion:
                await new SyncServer(jim!).DeleteConnectedSystemObjectsQuietlyAsync(leavers, activity);
                break;
            case DeletionPath.ExecutionItems:
                await new SyncServer(jim!).DeleteConnectedSystemObjectsAsync(leavers, executionItems);
                break;
            case DeletionPath.SyncRepositoryByIds:
                await repository.Sync.DeleteConnectedSystemObjectsByIdsAsync(leaverIds);
                break;
        }
        stopwatch.Stop();
        counter.Counting = false;
        var recorded = _spans.Stop();
        var auditRows = AuditRowCounts.Difference(auditBefore, await ReadAuditRowCountsAsync(ctx));

        // The page carries on after its deletions (the Metaverse Object deletions and the execution items are written
        // on the same context), so whatever a path left tracked must save cleanly.
        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing, $"{path}: the context saves after the deletion");
        await AssertDeletedAsync(path, seeded, leaverIds);
        Assert.That(connectedSystem.Objects, Has.Count.EqualTo(2), $"{path}: the tracked Connected System holds only the groups");
        if (path == DeletionPath.ExecutionItems)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(executionItems.Select(i => i.ConnectedSystemObjectId), Is.All.Null, $"{path}: no execution item still names a deleted object");
                Assert.That(executionItems.Select(i => i.ConnectedSystemObjectChange), Is.All.Not.Null, $"{path}: every execution item carries its deletion record");
            }
        }

        return NewDeletionRun(stopwatch, recorded, counter, auditRows);
    }

    private static DeletionRun NewDeletionRun(Stopwatch stopwatch, List<RecordedSpan> recorded, SqlStatementCounter counter,
        Dictionary<string, AuditRowCounts> auditRows)
        => new(
            stopwatch.Elapsed.TotalMilliseconds,
            recorded.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Sum(s => s.Ms), StringComparer.Ordinal),
            recorded.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => DescribeTags(g), StringComparer.Ordinal),
            counter.Commands,
            new Dictionary<string, int>(counter.Statements, StringComparer.Ordinal),
            auditRows);

    /// <summary>
    /// The tags of every span of one name: a numeric tag summed over them (a deleter run once per object, or a span
    /// reported twice per deletion, adds up), any other tag as the first span gave it.
    /// </summary>
    private static string DescribeTags(IEnumerable<RecordedSpan> spans)
        => string.Join(", ", spans
            .SelectMany(s => s.Tags)
            .GroupBy(t => t.Key, StringComparer.Ordinal)
            .Select(g => g.All(t => t.Value is int or long)
                ? $"{g.Key}={g.Sum(t => Convert.ToInt64(t.Value, CultureInfo.InvariantCulture))}"
                : $"{g.Key}={g.First().Value}"));

    private async Task AssertDeletedAsync(DeletionPath path, SeededLeaverPage seeded, List<Guid> leaverIds)
    {
        await using var verify = NewContext(null);
        var ids = leaverIds.ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.ConnectedSystemObjects.CountAsync(c => ids.Contains(c.Id)), Is.Zero, $"{path}: the departed accounts are gone");
            Assert.That(await verify.ConnectedSystemObjectAttributeValues.CountAsync(v => v.ReferenceValueId.HasValue && ids.Contains(v.ReferenceValueId.Value)),
                Is.Zero, $"{path}: no member value still points at a departed account");
            Assert.That(await verify.ConnectedSystemObjectAttributeValues.CountAsync(v => EF.Property<Guid>(v, "ConnectedSystemObjectId") == seeded.GroupIds[0]
                || EF.Property<Guid>(v, "ConnectedSystemObjectId") == seeded.GroupIds[1]), Is.EqualTo(2 * (MembersPerGroup + 1)), $"{path}: the groups keep every value");
            if (path == DeletionPath.QuietDeletion)
            {
                Assert.That(await verify.ConnectedSystemObjectChanges.CountAsync(c => c.ChangeType == ObjectChangeType.Deleted), Is.EqualTo(Leavers),
                    $"{path}: one deletion record per departed account");
            }
        }
    }

    private static string DescribeDeletionRuns(string label, List<DeletionRun> runs)
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"== {label}: median {Median(runs.Select(r => r.TotalMs)):F1} ms " +
                $"(runs {string.Join(" / ", runs.Select(r => r.TotalMs.ToString("F1", CultureInfo.InvariantCulture)))}), " +
                $"{Leavers} objects, {Median(runs.Select(r => r.TotalMs)) / Leavers:F2} ms each")
        };

        var last = runs[^1];
        foreach (var name in last.SpanMs.Keys.Order(StringComparer.Ordinal))
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"   span {name}: median {Median(runs.Select(r => r.SpanMs.GetValueOrDefault(name))):F1} ms [{last.SpanTags[name]}]"));
        }

        var statements = string.Join(", ", last.Statements.OrderByDescending(s => s.Value).Select(s => $"{s.Key} x{s.Value}"));
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"   EF commands (round trips) {last.Commands}; statements {last.Statements.Values.Sum()}: {statements}"));

        // Every run deletes the same seeded rows, so a count that differs between runs was read before the deleting
        // connection's statistics reached the view: the measurement, not the deletion, would be wrong.
        Assert.That(runs.Select(r => DescribeAuditRows(r.AuditRows)).Distinct().Count(), Is.EqualTo(1),
            $"{label}: every run touches the same audit rows");
        lines.Add($"   audit rows touched (updated, of which HOT; deleted): {DescribeAuditRows(last.AuditRows)}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeAuditRows(Dictionary<string, AuditRowCounts> auditRows)
        => string.Join("; ", AuditTables.Select(table => auditRows.TryGetValue(table, out var counts)
            ? string.Create(CultureInfo.InvariantCulture, $"{table} {counts.Updated} ({counts.HotUpdated} HOT), {counts.Deleted} deleted")
            : $"{table} not in pg_stat_user_tables"));

    private sealed record SeededLeaverPage(int SystemId, int UserTypeId, int GroupTypeId, DateTime Watermark, List<Guid> GroupIds);

    /// <summary>
    /// <see cref="Users"/> accounts, the first <see cref="Leavers"/> of them departed (obsolete, already disconnected,
    /// marked for synchronisation) with <see cref="ValuesPerLeaver"/> values each; two groups marked for
    /// synchronisation, each with <see cref="MembersPerGroup"/> members, both holding every departed account.
    /// </summary>
    private async Task<SeededLeaverPage> SeedLeaverPageAsync(bool withHistory)
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);

        int systemId, userTypeId, groupTypeId, userDnId, groupDnId, memberId;
        var textAttributeIds = new List<int>();
        await using (var seed = NewContext(null))
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Yellowstone Directory", ConnectorDefinition = connectorDefinition };
            var userType = new ConnectedSystemObjectType { Name = "inetOrgPerson", ConnectedSystem = system, Selected = true };
            var userDn = Attribute(userType, "distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued);
            var textAttributes = Enumerable.Range(1, ValuesPerLeaver - 1)
                .Select(i => Attribute(userType, $"attribute{i:D2}", AttributeDataType.Text, AttributePlurality.SingleValued))
                .ToList();
            var groupType = new ConnectedSystemObjectType { Name = "groupOfNames", ConnectedSystem = system, Selected = true };
            var groupDn = Attribute(groupType, "distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued);
            var member = Attribute(groupType, "member", AttributeDataType.Reference, AttributePlurality.MultiValued);
            seed.AddRange(connectorDefinition, system, userType, groupType);
            await seed.SaveChangesAsync();
            (systemId, userTypeId, groupTypeId, userDnId, groupDnId, memberId) = (system.Id, userType.Id, groupType.Id, userDn.Id, groupDn.Id, member.Id);
            textAttributeIds.AddRange(textAttributes.Select(a => a.Id));
        }

        var userIds = Enumerable.Range(0, Users).Select(_ => Guid.NewGuid()).ToList();
        var groupIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var created = DateTime.UtcNow.AddDays(-30);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""ConnectedSystemObjects"" (""Id"", ""Created"", ""TypeId"", ""ConnectedSystemId"", ""ExternalIdAttributeId"", ""Status"", ""JoinType"", ""SynchronisationPending"") FROM STDIN (FORMAT BINARY)"))
        {
            for (var i = 0; i < Users; i++)
            {
                var departed = i < Leavers;
                await WriteObjectRowAsync(writer, userIds[i], created, userTypeId, systemId, userDnId,
                    departed ? ConnectedSystemObjectStatus.Obsolete : ConnectedSystemObjectStatus.Normal, departed);
            }
            foreach (var groupId in groupIds)
                await WriteObjectRowAsync(writer, groupId, created, groupTypeId, systemId, groupDnId, ConnectedSystemObjectStatus.Normal, true);
            await writer.CompleteAsync();
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""ConnectedSystemObjectAttributeValues"" (""Id"", ""ConnectedSystemObjectId"", ""AttributeId"", ""StringValue"", ""ReferenceValueId"") FROM STDIN (FORMAT BINARY)"))
        {
            for (var i = 0; i < Leavers; i++)
            {
                await WriteValueRowAsync(writer, userIds[i], userDnId, $"uid=person{i:D6},ou=People,dc=yellowstone,dc=local", null);
                foreach (var attributeId in textAttributeIds)
                    await WriteValueRowAsync(writer, userIds[i], attributeId, $"value {attributeId} of person {i:D6}", null);
            }

            // Both groups hold every departed account; the first holds the first block of accounts, the second the last.
            var firstGroupMembers = userIds.Take(MembersPerGroup);
            var secondGroupMembers = userIds.Take(Leavers).Concat(userIds.Skip(Users - (MembersPerGroup - Leavers)));
            foreach (var (groupId, members) in new[] { (groupIds[0], firstGroupMembers), (groupIds[1], secondGroupMembers) })
            {
                await WriteValueRowAsync(writer, groupId, groupDnId, $"cn={groupId:N},ou=Groups,dc=yellowstone,dc=local", null);
                foreach (var memberCsoId in members)
                    await WriteValueRowAsync(writer, groupId, memberId, null, memberCsoId);
            }
            await writer.CompleteAsync();
        }

        if (withHistory)
            await SeedHistoryAsync(connection, systemId, userIds.Take(Leavers).ToList(), groupIds);

        await using (var analyse = new NpgsqlCommand(
            @"ANALYZE ""ConnectedSystemObjects""; ANALYZE ""ConnectedSystemObjectAttributeValues""; ANALYZE ""ActivityRunProfileExecutionItems""; " +
            @"ANALYZE ""ConnectedSystemObjectChanges""; ANALYZE ""ConnectedSystemObjectChangeAttributes""; ANALYZE ""ConnectedSystemObjectChangeAttributeValues"";", connection))
        {
            await analyse.ExecuteNonQueryAsync();
        }

        // Far enough ahead that no object qualifies by its timestamps, whatever the session time zone: the marks select.
        return new SeededLeaverPage(systemId, userTypeId, groupTypeId, DateTime.UtcNow.AddDays(1), groupIds);
    }

    private async Task SeedHistoryAsync(NpgsqlConnection connection, int systemId, List<Guid> leaverIds, List<Guid> groupIds)
    {
        Guid activityId;
        await using (var seed = NewContext(null))
        {
            var activity = new JimActivity { TargetName = "Earlier run", InitiatedByType = ActivityInitiatorType.System, Created = DateTime.UtcNow };
            seed.Activities.Add(activity);
            await seed.SaveChangesAsync();
            activityId = activity.Id;
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""ActivityRunProfileExecutionItems"" (""Id"", ""ActivityId"", ""ObjectChangeType"", ""ConnectedSystemObjectId"") FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var leaverId in leaverIds)
            {
                for (var i = 0; i < ExecutionItemsPerLeaver; i++)
                {
                    await writer.StartRowAsync();
                    await writer.WriteAsync(Guid.NewGuid(), NpgsqlDbType.Uuid);
                    await writer.WriteAsync(activityId, NpgsqlDbType.Uuid);
                    await writer.WriteAsync((int)ObjectChangeType.Updated, NpgsqlDbType.Integer);
                    await writer.WriteAsync(leaverId, NpgsqlDbType.Uuid);
                }
            }
            await writer.CompleteAsync();
        }

        // Each departed account's own change records, then two membership change records per group whose member values
        // name the departed accounts.
        var groupChangeIds = groupIds.SelectMany(id => new[] { (Id: Guid.NewGuid(), GroupId: id), (Id: Guid.NewGuid(), GroupId: id) }).ToList();
        await using (var changeWriter = await connection.BeginBinaryImportAsync(
            @"COPY ""ConnectedSystemObjectChanges"" (""Id"", ""ConnectedSystemId"", ""ChangeTime"", ""ChangeType"", ""InitiatedByType"", ""ConnectedSystemObjectId"") FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var objectId in leaverIds.SelectMany(id => Enumerable.Repeat(id, ChangeRecordsPerLeaver)))
                await WriteChangeRowAsync(changeWriter, Guid.NewGuid(), systemId, objectId);
            foreach (var (changeId, groupId) in groupChangeIds)
                await WriteChangeRowAsync(changeWriter, changeId, systemId, groupId);
            await changeWriter.CompleteAsync();
        }

        var groupChangeAttributeIds = new List<Guid>();
        await using (var attributeWriter = await connection.BeginBinaryImportAsync(
            @"COPY ""ConnectedSystemObjectChangeAttributes"" (""Id"", ""ConnectedSystemChangeId"", ""AttributeName"", ""AttributeType"") FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var (changeId, _) in groupChangeIds)
            {
                var attributeChangeId = Guid.NewGuid();
                groupChangeAttributeIds.Add(attributeChangeId);
                await attributeWriter.StartRowAsync();
                await attributeWriter.WriteAsync(attributeChangeId, NpgsqlDbType.Uuid);
                await attributeWriter.WriteAsync(changeId, NpgsqlDbType.Uuid);
                await attributeWriter.WriteAsync("member", NpgsqlDbType.Text);
                await attributeWriter.WriteAsync((int)AttributeDataType.Reference, NpgsqlDbType.Integer);
            }
            await attributeWriter.CompleteAsync();
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""ConnectedSystemObjectChangeAttributeValues"" (""Id"", ""ConnectedSystemObjectChangeAttributeId"", ""ValueChangeType"", ""IsPendingExportStub"", ""ReferenceValueId"") FROM STDIN (FORMAT BINARY)"))
        {
            var row = 0;
            foreach (var leaverId in leaverIds.SelectMany(id => Enumerable.Repeat(id, ChangeValuesPerLeaver)))
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(Guid.NewGuid(), NpgsqlDbType.Uuid);
                await writer.WriteAsync(groupChangeAttributeIds[row++ % groupChangeAttributeIds.Count], NpgsqlDbType.Uuid);
                await writer.WriteAsync((int)ValueChangeType.Add, NpgsqlDbType.Integer);
                await writer.WriteAsync(false, NpgsqlDbType.Boolean);
                await writer.WriteAsync(leaverId, NpgsqlDbType.Uuid);
            }
            await writer.CompleteAsync();
        }
    }

    #endregion

    #region Metaverse Object deletion

    private sealed record SeededMetaverseLeaverPage(
        List<Guid> LeaverIds, List<Guid> GroupIds, int ManagerAttributeId, int AccountSystemId, bool WithHistory);

    private async Task<DeletionRun> MeasureMetaverseDeletionAsync(MetaverseDeletionPath path, bool withHistory)
    {
        var seeded = await SeedMetaverseLeaverPageAsync(withHistory);
        var counter = new SqlStatementCounter();
        var ids = seeded.LeaverIds.ToArray();

        // The flush deletes on the page's context; housekeeping on a tracking context whose changes are detected.
        await using var ctx = path == MetaverseDeletionPath.Bulk ? NewPageContext(counter) : NewContext(counter);
        ctx.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        await ctx.Database.OpenConnectionAsync();
        var repository = new PostgresDataRepository(ctx);

        // Each departed person's account in the target system, tracked, as export evaluation leaves it on either path:
        // the detach has to clear the tracked join as well as the row's.
        List<MetaverseObject> leavers;
        if (path == MetaverseDeletionPath.Bulk)
        {
            await ctx.ConnectedSystemObjects.Where(c => c.ConnectedSystemId == seeded.AccountSystemId).LoadAsync();
            leavers = await ctx.MetaverseObjects.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync();
        }
        else
        {
            leavers = await ctx.MetaverseObjects
                .AsSplitQuery()
                .Include(m => m.Type)
                .Include(m => m.ConnectedSystemObjects)
                .Where(m => ids.Contains(m.Id))
                .ToListAsync();
        }
        var accounts = ctx.ChangeTracker.Entries<ConnectedSystemObject>().Select(e => e.Entity).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(leavers, Has.Count.EqualTo(Leavers));
            Assert.That(accounts, Has.Count.EqualTo(Leavers), "every departed person's account is tracked");
            Assert.That(accounts.Select(a => a.MetaverseObjectId), Is.All.Not.Null, "every account is joined");
        }

        var auditBefore = await ReadAuditRowCountsAsync(ctx);
        _spans.Start();
        counter.Counting = true;
        var stopwatch = Stopwatch.StartNew();
        if (path == MetaverseDeletionPath.Bulk)
        {
            await repository.Sync.DeleteMetaverseObjectsAsync(leavers);
        }
        else
        {
            foreach (var leaver in leavers)
                await repository.Metaverse.DeleteMetaverseObjectAsync(leaver);
        }
        stopwatch.Stop();
        counter.Counting = false;
        var recorded = _spans.Stop();
        var auditRows = AuditRowCounts.Difference(auditBefore, await ReadAuditRowCountsAsync(ctx));

        // Either context carries on after its deletions, so whatever a path left tracked must save cleanly.
        Assert.That(async () => await ctx.SaveChangesAsync(), Throws.Nothing, $"{path}: the context saves after the deletion");
        Assert.That(accounts.Select(a => a.MetaverseObjectId), Is.All.Null, $"{path}: no tracked account is still joined");
        await AssertMetaverseObjectsDeletedAsync(path, seeded);

        return NewDeletionRun(stopwatch, recorded, counter, auditRows);
    }

    /// <summary>
    /// The departed people are gone and no live value names them; everyone else, and every row of history, is kept.
    /// </summary>
    private async Task AssertMetaverseObjectsDeletedAsync(MetaverseDeletionPath path, SeededMetaverseLeaverPage seeded)
    {
        await using var verify = NewContext(null);
        var ids = seeded.LeaverIds.ToArray();
        var (firstGroup, secondGroup) = (seeded.GroupIds[0], seeded.GroupIds[1]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.MetaverseObjects.CountAsync(m => ids.Contains(m.Id)), Is.Zero, $"{path}: the departed people are gone");
            Assert.That(await verify.MetaverseObjects.CountAsync(), Is.EqualTo(Users - Leavers + 2), $"{path}: everyone else is kept");
            Assert.That(await verify.MetaverseObjectAttributeValues.CountAsync(v => v.ReferenceValueId.HasValue && ids.Contains(v.ReferenceValueId.Value)),
                Is.Zero, $"{path}: no value still names a departed person");
            Assert.That(await verify.MetaverseObjectAttributeValues.CountAsync(v => EF.Property<Guid>(v, "MetaverseObjectId") == firstGroup),
                Is.EqualTo(MembersPerGroup - Leavers), $"{path}: the first group keeps its other members");
            Assert.That(await verify.MetaverseObjectAttributeValues.CountAsync(v => EF.Property<Guid>(v, "MetaverseObjectId") == secondGroup),
                Is.EqualTo(MembersPerGroup - Leavers), $"{path}: the second group keeps its other members");
            Assert.That(await verify.MetaverseObjectAttributeValues.CountAsync(v => v.AttributeId == seeded.ManagerAttributeId), Is.Zero,
                $"{path}: the departed people's reports have no Manager left");
            Assert.That(await verify.ConnectedSystemObjects.CountAsync(c => c.ConnectedSystemId == seeded.AccountSystemId && c.MetaverseObjectId == null),
                Is.EqualTo(Leavers), $"{path}: every account is kept, disconnected");

            var history = seeded.WithHistory ? 1 : 0;
            Assert.That(await verify.Activities.CountAsync(), Is.EqualTo(history * Leavers * ActivitiesPerLeaver), $"{path}: every Activity is kept");
            Assert.That(await verify.MetaverseObjectChanges.CountAsync(), Is.EqualTo(history * (Leavers * ChangeRecordsPerLeaver + Leavers + 4)),
                $"{path}: every change record is kept");
            Assert.That(await verify.MetaverseObjectChangeAttributeValues.CountAsync(), Is.EqualTo(history * Leavers * ChangeValuesPerLeaver),
                $"{path}: every change value is kept");
        }
    }

    /// <summary>
    /// <see cref="Users"/> people's Metaverse Objects, the first <see cref="Leavers"/> departed, with
    /// <see cref="ValuesPerLeaver"/> values each and each joined to an account in a target system; two groups of
    /// <see cref="MembersPerGroup"/> members, both holding every departed person, as the Connected System Object page
    /// does; and the next <see cref="Leavers"/> people each managed by one of them. With history, each departed person
    /// also has <see cref="ChangeRecordsPerLeaver"/> change records and <see cref="ActivitiesPerLeaver"/> Activities of
    /// its own, and <see cref="ChangeValuesPerLeaver"/> reference values naming it in other objects' change records:
    /// one in its report's Manager change, the rest in the groups' member changes.
    /// </summary>
    private async Task<SeededMetaverseLeaverPage> SeedMetaverseLeaverPageAsync(bool withHistory)
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);

        int systemId, accountTypeId, accountIdAttributeId, personTypeId, groupTypeId, memberId, managerId;
        var textAttributeIds = new List<int>();
        await using (var seed = NewContext(null))
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Panoply Directory", ConnectorDefinition = connectorDefinition };
            var accountType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var accountId = Attribute(accountType, "objectGUID", AttributeDataType.Guid, AttributePlurality.SingleValued);
            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            var groupType = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
            var member = new MetaverseAttribute { Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
            var manager = new MetaverseAttribute { Name = "Manager", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.SingleValued };
            var textAttributes = Enumerable.Range(1, ValuesPerLeaver)
                .Select(i => new MetaverseAttribute { Name = $"Attribute {i:D2}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued })
                .ToList();
            seed.AddRange(connectorDefinition, system, accountType, personType, groupType, member, manager);
            seed.AddRange(textAttributes);
            await seed.SaveChangesAsync();
            (systemId, accountTypeId, accountIdAttributeId) = (system.Id, accountType.Id, accountId.Id);
            (personTypeId, groupTypeId, memberId, managerId) = (personType.Id, groupType.Id, member.Id, manager.Id);
            textAttributeIds.AddRange(textAttributes.Select(a => a.Id));
        }

        var personIds = Enumerable.Range(0, Users).Select(_ => Guid.NewGuid()).ToList();
        var leaverIds = personIds.Take(Leavers).ToList();
        var groupIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var created = DateTime.UtcNow.AddDays(-30);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""MetaverseObjects"" (""Id"", ""Created"", ""TypeId"", ""Status"", ""DeletionInitiatedByType"", ""Origin"") FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var (mvoId, typeId) in personIds.Select(id => (id, personTypeId)).Concat(groupIds.Select(id => (id, groupTypeId))))
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(mvoId, NpgsqlDbType.Uuid);
                await writer.WriteAsync(created, NpgsqlDbType.TimestampTz);
                await writer.WriteAsync(typeId, NpgsqlDbType.Integer);
                await writer.WriteAsync((int)MetaverseObjectStatus.Normal, NpgsqlDbType.Integer);
                await writer.WriteAsync(0, NpgsqlDbType.Integer);
                await writer.WriteAsync((int)MetaverseObjectOrigin.Projected, NpgsqlDbType.Integer);
            }
            await writer.CompleteAsync();
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""MetaverseObjectAttributeValues"" (""Id"", ""MetaverseObjectId"", ""AttributeId"", ""StringValue"", ""ReferenceValueId"") FROM STDIN (FORMAT BINARY)"))
        {
            for (var i = 0; i < Leavers; i++)
            {
                foreach (var attributeId in textAttributeIds)
                    await WriteValueRowAsync(writer, leaverIds[i], attributeId, $"value {attributeId} of person {i:D6}", null);

                // The next block of people report to the departed ones.
                await WriteValueRowAsync(writer, personIds[Leavers + i], managerId, null, leaverIds[i]);
            }

            // Both groups hold every departed person; the first holds the first block of people, the second the last.
            var firstGroupMembers = personIds.Take(MembersPerGroup);
            var secondGroupMembers = leaverIds.Concat(personIds.Skip(Users - (MembersPerGroup - Leavers)));
            foreach (var (groupId, members) in new[] { (groupIds[0], firstGroupMembers), (groupIds[1], secondGroupMembers) })
            {
                foreach (var memberMvoId in members)
                    await WriteValueRowAsync(writer, groupId, memberId, null, memberMvoId);
            }
            await writer.CompleteAsync();
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""ConnectedSystemObjects"" (""Id"", ""Created"", ""TypeId"", ""ConnectedSystemId"", ""ExternalIdAttributeId"", ""Status"", ""JoinType"", ""SynchronisationPending"", ""MetaverseObjectId"") FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var leaverId in leaverIds)
            {
                await WriteObjectRowAsync(writer, Guid.NewGuid(), created, accountTypeId, systemId, accountIdAttributeId,
                    ConnectedSystemObjectStatus.Normal, false, ConnectedSystemObjectJoinType.Provisioned);
                await writer.WriteAsync(leaverId, NpgsqlDbType.Uuid);
            }
            await writer.CompleteAsync();
        }

        if (withHistory)
            await SeedMetaverseHistoryAsync(connection, leaverIds, personIds.Skip(Leavers).Take(Leavers).ToList(), groupIds, memberId, managerId);

        await using (var analyse = new NpgsqlCommand(
            @"ANALYZE ""MetaverseObjects""; ANALYZE ""MetaverseObjectAttributeValues""; ANALYZE ""ConnectedSystemObjects""; ANALYZE ""Activities""; " +
            @"ANALYZE ""MetaverseObjectChanges""; ANALYZE ""MetaverseObjectChangeAttributes""; ANALYZE ""MetaverseObjectChangeAttributeValues"";", connection))
        {
            await analyse.ExecuteNonQueryAsync();
        }

        return new SeededMetaverseLeaverPage(leaverIds, groupIds, managerId, systemId, withHistory);
    }

    private async Task SeedMetaverseHistoryAsync(NpgsqlConnection connection, List<Guid> leaverIds, List<Guid> reportIds,
        List<Guid> groupIds, int memberId, int managerId)
    {
        await using (var seed = NewContext(null))
        {
            seed.Activities.AddRange(leaverIds.SelectMany(id => Enumerable.Range(0, ActivitiesPerLeaver).Select(_ => new JimActivity
            {
                TargetName = "Earlier change to a person",
                TargetType = ActivityTargetType.MetaverseObject,
                TargetOperationType = ActivityTargetOperationType.Update,
                InitiatedByType = ActivityInitiatorType.System,
                Created = DateTime.UtcNow.AddDays(-2),
                MetaverseObjectId = id
            })));
            await seed.SaveChangesAsync();
        }

        // Each departed person's own change records; then a Manager change record for each report and two membership
        // change records per group, whose reference values name the departed people.
        var managerChanges = reportIds.Select(id => (Id: Guid.NewGuid(), ObjectId: id)).ToList();
        var groupChanges = groupIds.SelectMany(id => new[] { (Id: Guid.NewGuid(), ObjectId: id), (Id: Guid.NewGuid(), ObjectId: id) }).ToList();
        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""MetaverseObjectChanges"" (""Id"", ""MetaverseObjectId"", ""ChangeTime"", ""InitiatedByType"", ""ChangeInitiatorType"", ""ChangeType"") FROM STDIN (FORMAT BINARY)"))
        {
            var ownChanges = leaverIds.SelectMany(id => Enumerable.Repeat(id, ChangeRecordsPerLeaver)).Select(id => (Id: Guid.NewGuid(), ObjectId: id));
            foreach (var (changeId, objectId) in ownChanges.Concat(managerChanges).Concat(groupChanges))
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(changeId, NpgsqlDbType.Uuid);
                await writer.WriteAsync(objectId, NpgsqlDbType.Uuid);
                await writer.WriteAsync(DateTime.UtcNow.AddDays(-1), NpgsqlDbType.TimestampTz);
                await writer.WriteAsync((int)ActivityInitiatorType.System, NpgsqlDbType.Integer);
                await writer.WriteAsync((int)MetaverseObjectChangeInitiatorType.NotSet, NpgsqlDbType.Integer);
                await writer.WriteAsync((int)ObjectChangeType.Updated, NpgsqlDbType.Integer);
            }
            await writer.CompleteAsync();
        }

        var managerAttributeChangeIds = new List<Guid>();
        var groupAttributeChangeIds = new List<Guid>();
        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""MetaverseObjectChangeAttributes"" (""Id"", ""MetaverseObjectChangeId"", ""AttributeId"", ""AttributeName"", ""AttributeType"") FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var (changeIds, changes, attributeId, name) in new[]
                     {
                         (managerAttributeChangeIds, managerChanges, managerId, "Manager"),
                         (groupAttributeChangeIds, groupChanges, memberId, "Static Members")
                     })
            {
                foreach (var (changeId, _) in changes)
                {
                    var attributeChangeId = Guid.NewGuid();
                    changeIds.Add(attributeChangeId);
                    await writer.StartRowAsync();
                    await writer.WriteAsync(attributeChangeId, NpgsqlDbType.Uuid);
                    await writer.WriteAsync(changeId, NpgsqlDbType.Uuid);
                    await writer.WriteAsync(attributeId, NpgsqlDbType.Integer);
                    await writer.WriteAsync(name, NpgsqlDbType.Text);
                    await writer.WriteAsync((int)AttributeDataType.Reference, NpgsqlDbType.Integer);
                }
            }
            await writer.CompleteAsync();
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""MetaverseObjectChangeAttributeValues"" (""Id"", ""MetaverseObjectChangeAttributeId"", ""ValueChangeType"", ""ReferenceValueId"") FROM STDIN (FORMAT BINARY)"))
        {
            // One value in the departed person's report's Manager change; the rest across the groups' member changes.
            var row = 0;
            for (var i = 0; i < leaverIds.Count; i++)
            {
                await WriteChangeValueRowAsync(writer, managerAttributeChangeIds[i], leaverIds[i]);
                for (var j = 1; j < ChangeValuesPerLeaver; j++)
                    await WriteChangeValueRowAsync(writer, groupAttributeChangeIds[row++ % groupAttributeChangeIds.Count], leaverIds[i]);
            }
            await writer.CompleteAsync();
        }
    }

    private static async Task WriteChangeValueRowAsync(NpgsqlBinaryImporter writer, Guid attributeChangeId, Guid referenceValueId)
    {
        await writer.StartRowAsync();
        await writer.WriteAsync(Guid.NewGuid(), NpgsqlDbType.Uuid);
        await writer.WriteAsync(attributeChangeId, NpgsqlDbType.Uuid);
        await writer.WriteAsync((int)ValueChangeType.Add, NpgsqlDbType.Integer);
        await writer.WriteAsync(referenceValueId, NpgsqlDbType.Uuid);
    }

    #endregion

    #region Audit rows

    /// <summary>
    /// What a table's cumulative statistics say was done to its rows: updated (of which heap-only, so no index entry
    /// was written) and deleted, whether by a statement or by a foreign key's action.
    /// </summary>
    private sealed record AuditRowCounts(long Updated, long HotUpdated, long Deleted)
    {
        public static Dictionary<string, AuditRowCounts> Difference(
            Dictionary<string, AuditRowCounts> before, Dictionary<string, AuditRowCounts> after)
            => after.ToDictionary(
                a => a.Key,
                a => before.TryGetValue(a.Key, out var b)
                    ? new AuditRowCounts(a.Value.Updated - b.Updated, a.Value.HotUpdated - b.HotUpdated, a.Value.Deleted - b.Deleted)
                    : a.Value,
                StringComparer.Ordinal);
    }

    /// <summary>
    /// The audit tables' cumulative row counts, read on the context's open connection after flushing that connection's
    /// own pending statistics: a backend publishes them only when idle and at most once a second, so without the flush
    /// the read would miss what the deletion just did. The flush lands before the server reports the statement done,
    /// so the next statement sees it.
    /// </summary>
    private static async Task<Dictionary<string, AuditRowCounts>> ReadAuditRowCountsAsync(JimDbContext context)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        Assert.That(connection.State, Is.EqualTo(System.Data.ConnectionState.Open), "the measured connection is held open");

        await using (var flush = new NpgsqlCommand("SELECT pg_stat_force_next_flush()", connection))
            await flush.ExecuteNonQueryAsync();

        await using var command = new NpgsqlCommand(
            @"SELECT ""relname"", ""n_tup_upd"", ""n_tup_hot_upd"", ""n_tup_del"" FROM ""pg_stat_user_tables"" " +
            @"WHERE ""schemaname"" = 'public' AND ""relname"" = ANY(@tables)", connection);
        command.Parameters.Add(new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = AuditTables });

        var counts = new Dictionary<string, AuditRowCounts>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            counts[reader.GetString(0)] = new AuditRowCounts(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        return counts;
    }

    #endregion

    #region Page load

    /// <summary>
    /// The page size every variant loads with: the one the measured pager passes the Delta loader.
    /// </summary>
    private const int SyncPageSize = 500;

    private const string MainQuerySpan = "Cso.Page.LoadModifiedWithAttributeValues";
    private const string ReferenceLookupSpan = "Cso.PopulateReferenceValues";

    private sealed record PageLoadRun(double Ms, int ValueCount, Dictionary<string, double> SpanMs);

    private sealed record SeededModifiedGroup(
        int SystemId, int UserTypeId, int GroupTypeId, Guid GroupCsoId, DateTime Watermark,
        HashSet<Guid> MemberCsoIds, HashSet<Guid> MemberMvoIds);

    /// <summary>
    /// One run discarded for warm-up (JIT, EF's model and query caches), then the measured runs, each after a full
    /// collection so that no run pays for the garbage the one before it left.
    /// </summary>
    private static async Task<List<PageLoadRun>> MeasureAsync(Func<Task<PageLoadRun>> run)
    {
        var runs = new List<PageLoadRun>();
        for (var i = 0; i <= PageLoadRuns; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var measured = await run();
            if (i > 0)
                runs.Add(measured);
        }
        return runs;
    }

    /// <summary>
    /// Today's load, as the Worker runs it: the Delta loader through the pager, on a tracking context with change
    /// detection off. Its spans split it into the main query, the reference lookup and the Metaverse Object read.
    /// </summary>
    private async Task<PageLoadRun> MeasurePageLoadAsync(SeededModifiedGroup seeded)
    {
        await using var ctx = NewPageContext(null);
        var repository = new PostgresDataRepository(ctx);
        var pager = new TypeOrderedCsoPager(
            (afterId, objectTypeId, createdOnOrAfter) => repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(
                seeded.SystemId, seeded.Watermark, 1, SyncPageSize, 1, afterId, objectTypeId, createdOnOrAfter),
            [seeded.UserTypeId, seeded.GroupTypeId],
            seeded.Watermark);

        _spans.Start();
        var stopwatch = Stopwatch.StartNew();
        var page = await pager.NextPageAsync();
        stopwatch.Stop();
        var recorded = _spans.Stop();

        Assert.That(page, Is.Not.Null, "EF tracked: a page");
        AssertGroupPage("EF tracked", page!.Results, seeded, referencesResolved: true);
        Assert.That(await pager.NextPageAsync(), Is.Null, "nothing else is modified");

        return new PageLoadRun(
            stopwatch.Elapsed.TotalMilliseconds,
            page!.Results[0].AttributeValues.Count,
            recorded.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Sum(s => s.Ms), StringComparer.Ordinal));
    }

    /// <summary>
    /// Option (a'): the Delta loader's main query, its Includes and selection unchanged, untracked. It reads no
    /// references; the Delta loader's reference lookup would follow it.
    /// </summary>
    private async Task<PageLoadRun> LoadWithUntrackedQueryAsync(SeededModifiedGroup seeded)
    {
        await using var ctx = NewPageContext(null);
        var stopwatch = Stopwatch.StartNew();
        var page = await SelectModifiedPage(ctx.ConnectedSystemObjects
                .AsNoTracking()
                .Include(cso => cso.Type)
                .Include(cso => cso.AttributeValues)
                    .ThenInclude(av => av.Attribute), seeded)
            .ToListAsync();
        stopwatch.Stop();

        AssertGroupPage("(a')", page, seeded, referencesResolved: false);
        return new PageLoadRun(stopwatch.Elapsed.TotalMilliseconds, page[0].AttributeValues.Count, []);
    }

    /// <summary>
    /// Option (b): EF loads the Connected System Object rows, tracked, then the values are read raw and each is attached
    /// to the context as Unchanged and added to its object's values. The attach is the cost #2598 Phase 0 measured.
    /// </summary>
    private async Task<PageLoadRun> LoadWithAttachedRawValuesAsync(SeededModifiedGroup seeded)
    {
        await using var ctx = NewPageContext(null);
        var stopwatch = Stopwatch.StartNew();
        var page = await SelectModifiedPage(ctx.ConnectedSystemObjects.AsTracking().Include(cso => cso.Type), seeded).ToListAsync();
        var objectsById = page.ToDictionary(cso => cso.Id);
        var valuesByOwner = await ReadValueRowsAsync(ctx, [.. objectsById.Keys]);
        var attributesById = await LoadAttributesAsync(ctx, valuesByOwner);
        foreach (var (owner, values) in valuesByOwner.Select(o => (objectsById[o.Key], o.Value)))
        {
            foreach (var value in values)
            {
                value.ConnectedSystemObject = owner;
                value.Attribute = attributesById[value.AttributeId];
                owner.AttributeValues.Add(value);
                ctx.Entry(value).State = EntityState.Unchanged;
            }
        }
        stopwatch.Stop();

        AssertGroupPage("(b)", page, seeded, referencesResolved: true);
        Assert.That(ctx.ChangeTracker.Entries<ConnectedSystemObjectAttributeValue>().Count(e => e.State == EntityState.Unchanged),
            Is.EqualTo(seeded.MemberCsoIds.Count + 1), "(b): every value is tracked, unchanged");
        return new PageLoadRun(stopwatch.Elapsed.TotalMilliseconds, page[0].AttributeValues.Count, []);
    }

    /// <summary>
    /// The floor for option (a), as its reader would build the page: every column of both tables (the bulk writers'
    /// lists) plus <c>xmin</c>, into untracked objects; Type and Attribute resolved through the context, as
    /// <c>MetaverseObjectRawReader</c> resolves its own; one <see cref="ReferenceIndexedList{T}"/> per object; the three
    /// resolved-reference fields from a join to the referenced object; and a reference to an object on the page wired
    /// to that object.
    /// </summary>
    private async Task<PageLoadRun> ReadPageRawAsync(SeededModifiedGroup seeded)
    {
        await using var ctx = NewPageContext(null);
        var stopwatch = Stopwatch.StartNew();
        var page = await ReadObjectRowsAsync(ctx, seeded);
        var objectsById = page.ToDictionary(cso => cso.Id);
        var typeIds = page.Select(cso => cso.TypeId).Distinct().ToList();
        var typesById = await ctx.ConnectedSystemObjectTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        foreach (var cso in page)
            cso.Type = typesById[cso.TypeId];

        var valuesByOwner = await ReadValueRowsAsync(ctx, [.. objectsById.Keys]);
        var attributesById = await LoadAttributesAsync(ctx, valuesByOwner);
        foreach (var (owner, values) in valuesByOwner.Select(o => (objectsById[o.Key], o.Value)))
        {
            foreach (var value in values)
            {
                value.ConnectedSystemObject = owner;
                value.Attribute = attributesById[value.AttributeId];
                if (value.ReferenceValueId is { } referenceId && objectsById.TryGetValue(referenceId, out var target))
                    value.ReferenceValue = target;
            }
            owner.AttributeValues = new ReferenceIndexedList<ConnectedSystemObjectAttributeValue>(values);
        }
        stopwatch.Stop();

        AssertGroupPage("(c)", page, seeded, referencesResolved: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.ChangeTracker.Entries<ConnectedSystemObject>(), Is.Empty, "(c): the objects are untracked");
            Assert.That(ctx.ChangeTracker.Entries<ConnectedSystemObjectAttributeValue>(), Is.Empty, "(c): the values are untracked");
            Assert.That(ctx.Entry(page[0].Type).State, Is.EqualTo(EntityState.Unchanged), "(c): the Type is the context's tracked instance");
            Assert.That(ctx.Entry(page[0].AttributeValues[0].Attribute).State, Is.EqualTo(EntityState.Unchanged),
                "(c): each Attribute is the context's tracked instance");
        }
        return new PageLoadRun(stopwatch.Elapsed.TotalMilliseconds, page[0].AttributeValues.Count, []);
    }

    /// <summary>
    /// The floor: the group's value rows with their attribute's name, read raw, one column taken per row and nothing
    /// built.
    /// </summary>
    private async Task<PageLoadRun> ReadGroupValuesRawAsync(SeededModifiedGroup seeded)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var stopwatch = Stopwatch.StartNew();
        await using var command = new NpgsqlCommand(
            @"SELECT v.*, a.""Name"" FROM ""ConnectedSystemObjectAttributeValues"" v JOIN ""ConnectedSystemAttributes"" a ON a.""Id"" = v.""AttributeId"" " +
            @"WHERE v.""ConnectedSystemObjectId"" = @id ORDER BY v.""Id""", connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = seeded.GroupCsoId });
        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            _ = reader.GetGuid(0);
            rows++;
        }
        stopwatch.Stop();
        Assert.That(rows, Is.EqualTo(seeded.MemberCsoIds.Count + 1), "the floor: every value row");
        return new PageLoadRun(stopwatch.Elapsed.TotalMilliseconds, rows, []);
    }

    /// <summary>
    /// The Delta loader's selection as <c>GetConnectedSystemObjectsModifiedSinceAsync</c> composes it for the group's
    /// Object Type from an empty keyset cursor: the marks or the timestamps, the type, the cursor, ordered by id.
    /// </summary>
    private static IQueryable<ConnectedSystemObject> SelectModifiedPage(IQueryable<ConnectedSystemObject> source, SeededModifiedGroup seeded)
    {
        var (systemId, modifiedSince, typeId, afterId) = (seeded.SystemId, seeded.Watermark, seeded.GroupTypeId, Guid.Empty);
        return source
            .Where(cso => cso.ConnectedSystemId == systemId &&
                          (cso.SynchronisationPending ||
                           cso.DerivedInputChangePending ||
                           cso.ScopeReviewPending ||
                           cso.Created > modifiedSince ||
                           (cso.LastUpdated.HasValue && cso.LastUpdated.Value > modifiedSince)))
            .Where(cso => cso.TypeId == typeId)
            .OrderBy(cso => cso.Id)
            .Where(cso => cso.Id.CompareTo(afterId) > 0)
            .Take(SyncPageSize);
    }

    /// <summary>
    /// Statement 1 of option (a)'s reader: the Delta loader's selection over every column of the rows, and xmin.
    /// </summary>
    private static async Task<List<ConnectedSystemObject>> ReadObjectRowsAsync(JimDbContext context, SeededModifiedGroup seeded)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var columns = CsoBulkColumns.ConnectedSystemObjects;
        await using var command = new NpgsqlCommand(
            $"""
             SELECT {BulkSqlHelpers.ToQuotedList(columns)}, "xmin"
             FROM "ConnectedSystemObjects"
             WHERE "ConnectedSystemId" = @systemId
               AND ("SynchronisationPending" OR "DerivedInputChangePending" OR "ScopeReviewPending"
                    OR "Created" > @modifiedSince OR "LastUpdated" > @modifiedSince)
               AND "TypeId" = @typeId
               AND "Id" > @afterId
             ORDER BY "Id"
             LIMIT @pageSize
             """, connection);
        command.Parameters.Add(new NpgsqlParameter("systemId", NpgsqlDbType.Integer) { Value = seeded.SystemId });
        command.Parameters.Add(new NpgsqlParameter("modifiedSince", NpgsqlDbType.TimestampTz) { Value = seeded.Watermark });
        command.Parameters.Add(new NpgsqlParameter("typeId", NpgsqlDbType.Integer) { Value = seeded.GroupTypeId });
        command.Parameters.Add(new NpgsqlParameter("afterId", NpgsqlDbType.Uuid) { Value = Guid.Empty });
        command.Parameters.Add(new NpgsqlParameter("pageSize", NpgsqlDbType.Integer) { Value = SyncPageSize });

        var result = new List<ConnectedSystemObject>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var cso = new ConnectedSystemObject();
            for (var ordinal = 0; ordinal < columns.Length; ordinal++)
                SetObjectColumn(cso, columns[ordinal], reader, ordinal);
            cso.xmin = reader.GetFieldValue<uint>(columns.Length);
            result.Add(cso);
        }
        return result;
    }

    /// <summary>
    /// Statement 2 of option (a)'s reader, and option (b)'s value read: every column of the objects' values, in the
    /// order EF's Include reads them, each with its referenced object's Metaverse Object, Type and creation time from a
    /// join, in place of the Delta loader's second lookup. Grouped by owner.
    /// </summary>
    private static async Task<Dictionary<Guid, List<ConnectedSystemObjectAttributeValue>>> ReadValueRowsAsync(JimDbContext context, Guid[] ownerIds)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        var columns = CsoBulkColumns.ConnectedSystemObjectAttributeValues;
        var valueColumns = string.Join(", ", columns.Select(c => $"v.\"{c}\""));
        await using var command = new NpgsqlCommand(
            $"""
             SELECT {valueColumns}, r."MetaverseObjectId", r."TypeId", r."Created"
             FROM "ConnectedSystemObjectAttributeValues" v
             LEFT JOIN "ConnectedSystemObjects" r ON r."Id" = v."ReferenceValueId"
             WHERE v."ConnectedSystemObjectId" = ANY(@ownerIds)
             ORDER BY v."ConnectedSystemObjectId", v."Id"
             """, connection);
        command.Parameters.Add(new NpgsqlParameter("ownerIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ownerIds });

        var result = new Dictionary<Guid, List<ConnectedSystemObjectAttributeValue>>(ownerIds.Length);
        var referenced = columns.Length;
        List<ConnectedSystemObjectAttributeValue>? ownerValues = null;
        var currentOwnerId = Guid.Empty;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var value = new ConnectedSystemObjectAttributeValue();
            var ownerId = Guid.Empty;
            for (var ordinal = 0; ordinal < columns.Length; ordinal++)
                SetValueColumn(value, columns[ordinal], reader, ordinal, ref ownerId);

            // A referenced row always has a Type, so a null one means the reference names no row.
            if (!reader.IsDBNull(referenced + 1))
            {
                value.ResolvedReferenceMetaverseObjectId = reader.IsDBNull(referenced) ? null : reader.GetGuid(referenced);
                value.ResolvedReferenceTypeId = reader.GetInt32(referenced + 1);
                value.ResolvedReferenceCreated = reader.GetDateTime(referenced + 2);
            }

            // Rows arrive by owner, so each owner's values are one run; an owner seen twice fails the read.
            if (ownerValues == null || ownerId != currentOwnerId)
            {
                ownerValues = [];
                result.Add(ownerId, ownerValues);
                currentOwnerId = ownerId;
            }
            ownerValues.Add(value);
        }
        return result;
    }

    /// <summary>
    /// The values' Attributes through the context, so on a tracking context they are its tracked instances, the ones
    /// the run's Synchronisation Rules hold.
    /// </summary>
    private static Task<Dictionary<int, ConnectedSystemObjectTypeAttribute>> LoadAttributesAsync(
        JimDbContext context, Dictionary<Guid, List<ConnectedSystemObjectAttributeValue>> valuesByOwner)
    {
        var attributeIds = valuesByOwner.Values.SelectMany(values => values).Select(value => value.AttributeId).Distinct().ToList();
        return context.ConnectedSystemAttributes.Where(a => attributeIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id);
    }

    /// <summary>
    /// Sets the property a <c>ConnectedSystemObjects</c> column maps to, as <c>MetaverseObjectRawReader</c> does for its
    /// own table; a column with no case fails the read.
    /// </summary>
    private static void SetObjectColumn(ConnectedSystemObject cso, string column, NpgsqlDataReader reader, int ordinal)
    {
        var isNull = reader.IsDBNull(ordinal);
        switch (column)
        {
            case "Id": cso.Id = reader.GetGuid(ordinal); break;
            case "ConnectedSystemId": cso.ConnectedSystemId = reader.GetInt32(ordinal); break;
            case "Created": cso.Created = reader.GetDateTime(ordinal); break;
            case "LastUpdated": cso.LastUpdated = isNull ? null : reader.GetDateTime(ordinal); break;
            case "TypeId": cso.TypeId = reader.GetInt32(ordinal); break;
            case "ExternalIdAttributeId": cso.ExternalIdAttributeId = reader.GetInt32(ordinal); break;
            case "SecondaryExternalIdAttributeId": cso.SecondaryExternalIdAttributeId = isNull ? null : reader.GetInt32(ordinal); break;
            case "Status": cso.Status = (ConnectedSystemObjectStatus)reader.GetInt32(ordinal); break;
            case "MetaverseObjectId": cso.MetaverseObjectId = isNull ? null : reader.GetGuid(ordinal); break;
            case "JoinType": cso.JoinType = (ConnectedSystemObjectJoinType)reader.GetInt32(ordinal); break;
            case "DateJoined": cso.DateJoined = isNull ? null : reader.GetDateTime(ordinal); break;
            case "PartitionId": cso.PartitionId = isNull ? null : reader.GetInt32(ordinal); break;
            case "ScopeReviewPending": cso.ScopeReviewPending = reader.GetBoolean(ordinal); break;
            case "LastScopeEvaluatedAt": cso.LastScopeEvaluatedAt = isNull ? null : reader.GetDateTime(ordinal); break;
            case "ImportStateHash": cso.ImportStateHash = isNull ? null : reader.GetGuid(ordinal); break;
            case "ImportStateFingerprint": cso.ImportStateFingerprint = isNull ? null : reader.GetGuid(ordinal); break;
            case "DerivedInputChangePending": cso.DerivedInputChangePending = reader.GetBoolean(ordinal); break;
            case "JoinMethod": cso.JoinMethod = isNull ? null : (ConnectedSystemObjectJoinMethod)reader.GetInt32(ordinal); break;
            case "JoinSyncRuleId": cso.JoinSyncRuleId = isNull ? null : reader.GetInt32(ordinal); break;
            case "JoinSyncRuleName": cso.JoinSyncRuleName = isNull ? null : reader.GetString(ordinal); break;
            case "JoinObjectMatchingRuleId": cso.JoinObjectMatchingRuleId = isNull ? null : reader.GetInt32(ordinal); break;
            case "JoinMatchValue": cso.JoinMatchValue = isNull ? null : reader.GetString(ordinal); break;
            case "SynchronisationPending": cso.SynchronisationPending = reader.GetBoolean(ordinal); break;
            default: throw new InvalidOperationException($"ConnectedSystemObjects column '{column}' is not read; map it in {nameof(SetObjectColumn)}.");
        }
    }

    /// <summary>
    /// Sets the property a <c>ConnectedSystemObjectAttributeValues</c> column maps to; the owner's id, a shadow
    /// property, goes to <paramref name="ownerId"/>. See <see cref="SetObjectColumn"/>.
    /// </summary>
    private static void SetValueColumn(ConnectedSystemObjectAttributeValue value, string column, NpgsqlDataReader reader, int ordinal, ref Guid ownerId)
    {
        var isNull = reader.IsDBNull(ordinal);
        switch (column)
        {
            case "Id": value.Id = reader.GetGuid(ordinal); break;
            case "ConnectedSystemObjectId": ownerId = reader.GetGuid(ordinal); break;
            case "AttributeId": value.AttributeId = reader.GetInt32(ordinal); break;
            case "StringValue": value.StringValue = isNull ? null : reader.GetString(ordinal); break;
            case "DateTimeValue": value.DateTimeValue = isNull ? null : JIM.Utilities.DateTimeAttributeValue.ToCanonical(reader.GetDateTime(ordinal)); break;
            case "IntValue": value.IntValue = isNull ? null : reader.GetInt32(ordinal); break;
            case "LongValue": value.LongValue = isNull ? null : reader.GetInt64(ordinal); break;
            case "DecimalValue": value.DecimalValue = isNull ? null : reader.GetDecimal(ordinal); break;
            case "ByteValue": value.ByteValue = isNull ? null : reader.GetFieldValue<byte[]>(ordinal); break;
            case "GuidValue": value.GuidValue = isNull ? null : reader.GetGuid(ordinal); break;
            case "BoolValue": value.BoolValue = isNull ? null : reader.GetBoolean(ordinal); break;
            case "ReferenceValueId": value.ReferenceValueId = isNull ? null : reader.GetGuid(ordinal); break;
            case "UnresolvedReferenceValue": value.UnresolvedReferenceValue = isNull ? null : reader.GetString(ordinal); break;
            default: throw new InvalidOperationException($"ConnectedSystemObjectAttributeValues column '{column}' is not read; map it in {nameof(SetValueColumn)}.");
        }
    }

    /// <summary>
    /// Every variant loaded the one modified group with all its values, each carrying its Attribute and, where the
    /// variant reads references, each member's Metaverse Object; so a variant that loaded less cannot report a time.
    /// </summary>
    private static void AssertGroupPage(string variant, IReadOnlyList<ConnectedSystemObject> page, SeededModifiedGroup seeded, bool referencesResolved)
    {
        Assert.That(page.Select(cso => cso.Id), Is.EqualTo(new[] { seeded.GroupCsoId }), $"{variant}: the modified group's page");
        var values = page[0].AttributeValues;
        var references = values.Where(v => v.ReferenceValueId.HasValue).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(values, Has.Count.EqualTo(seeded.MemberCsoIds.Count + 1), $"{variant}: every value");
            Assert.That(references.Select(v => v.ReferenceValueId!.Value).ToHashSet().SetEquals(seeded.MemberCsoIds), Is.True,
                $"{variant}: every member");
            Assert.That(values.All(v => v.Attribute?.Id == v.AttributeId), Is.True, $"{variant}: each value carries its Attribute");
            if (referencesResolved)
            {
                Assert.That(references.Select(v => v.ResolvedReferenceMetaverseObjectId ?? Guid.Empty).ToHashSet().SetEquals(seeded.MemberMvoIds),
                    Is.True, $"{variant}: each member's Metaverse Object");
            }
        }
    }

    private static string DescribePageLoadRuns(int members, List<(string Name, List<PageLoadRun> Runs)> variants)
    {
        var valueCount = variants[0].Runs[^1].ValueCount;
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"== One modified group of {members} members ({valueCount} values): median of {PageLoadRuns} runs after a warm-up")
        };
        foreach (var (name, runs) in variants)
        {
            lines.Add(DescribeTimings(name, runs.Select(r => r.Ms), valueCount));
            foreach (var span in runs[^1].SpanMs.Keys.Order(StringComparer.Ordinal))
                lines.Add(DescribeTimings($"   span {span}", runs.Select(r => r.SpanMs.GetValueOrDefault(span)), valueCount));
            if (runs[^1].SpanMs.ContainsKey(MainQuerySpan))
            {
                lines.Add(DescribeTimings("   main query and reference lookup together, which (b) and (c) replace",
                    runs.Select(r => r.SpanMs.GetValueOrDefault(MainQuerySpan) + r.SpanMs.GetValueOrDefault(ReferenceLookupSpan)), valueCount));
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeTimings(string name, IEnumerable<double> runMs, int valueCount)
    {
        var ms = runMs.ToList();
        var median = Median(ms);
        return string.Create(CultureInfo.InvariantCulture,
            $"   {name}: {median:F1} ms, {median * 1000 / valueCount:F2} µs per value " +
            $"(runs {string.Join(" / ", ms.Select(m => m.ToString("F1", CultureInfo.InvariantCulture)))})");
    }

    /// <summary>
    /// One modified group, joined to its Metaverse Object, whose <paramref name="members"/> member values reference that
    /// many joined accounts; the Metaverse Object holds the same membership. Nothing else is marked for synchronisation.
    /// </summary>
    private async Task<SeededModifiedGroup> SeedModifiedGroupAsync(int members)
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);

        int systemId, userTypeId, groupTypeId, userDnId, groupDnId, memberId, personTypeId, groupMvTypeId, mvMemberId;
        await using (var seed = NewContext(null))
        {
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Yellowstone Directory", ConnectorDefinition = connectorDefinition };
            var userType = new ConnectedSystemObjectType { Name = "inetOrgPerson", ConnectedSystem = system, Selected = true };
            var userDn = Attribute(userType, "distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued);
            var groupType = new ConnectedSystemObjectType { Name = "groupOfNames", ConnectedSystem = system, Selected = true };
            var groupDn = Attribute(groupType, "distinguishedName", AttributeDataType.Text, AttributePlurality.SingleValued);
            var member = Attribute(groupType, "member", AttributeDataType.Reference, AttributePlurality.MultiValued);
            var personType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            var groupMvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
            var mvMember = new MetaverseAttribute { Name = "Static Members", Type = AttributeDataType.Reference, AttributePlurality = AttributePlurality.MultiValued };
            seed.AddRange(connectorDefinition, system, userType, groupType, personType, groupMvType, mvMember);
            await seed.SaveChangesAsync();
            (systemId, userTypeId, groupTypeId, userDnId, groupDnId, memberId) = (system.Id, userType.Id, groupType.Id, userDn.Id, groupDn.Id, member.Id);
            (personTypeId, groupMvTypeId, mvMemberId) = (personType.Id, groupMvType.Id, mvMember.Id);
        }

        var people = Enumerable.Range(0, members).Select(_ => (Mvo: Guid.NewGuid(), Cso: Guid.NewGuid())).ToList();
        var groupMvoId = Guid.NewGuid();
        var groupCsoId = Guid.NewGuid();
        var created = DateTime.UtcNow.AddDays(-30);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""MetaverseObjects"" (""Id"", ""Created"", ""TypeId"", ""Status"", ""DeletionInitiatedByType"", ""Origin"") FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var (mvoId, typeId) in people.Select(p => (p.Mvo, personTypeId)).Append((groupMvoId, groupMvTypeId)))
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(mvoId, NpgsqlDbType.Uuid);
                await writer.WriteAsync(created, NpgsqlDbType.TimestampTz);
                await writer.WriteAsync(typeId, NpgsqlDbType.Integer);
                await writer.WriteAsync((int)MetaverseObjectStatus.Normal, NpgsqlDbType.Integer);
                await writer.WriteAsync(0, NpgsqlDbType.Integer);
                await writer.WriteAsync((int)MetaverseObjectOrigin.Projected, NpgsqlDbType.Integer);
            }
            await writer.CompleteAsync();
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""MetaverseObjectAttributeValues"" (""Id"", ""MetaverseObjectId"", ""AttributeId"", ""ReferenceValueId"") FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var person in people)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(Guid.NewGuid(), NpgsqlDbType.Uuid);
                await writer.WriteAsync(groupMvoId, NpgsqlDbType.Uuid);
                await writer.WriteAsync(mvMemberId, NpgsqlDbType.Integer);
                await writer.WriteAsync(person.Mvo, NpgsqlDbType.Uuid);
            }
            await writer.CompleteAsync();
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""ConnectedSystemObjects"" (""Id"", ""Created"", ""TypeId"", ""ConnectedSystemId"", ""ExternalIdAttributeId"", ""Status"", ""JoinType"", ""SynchronisationPending"", ""MetaverseObjectId"") FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var (csoId, mvoId, typeId, dnId, pending) in people.Select(p => (p.Cso, p.Mvo, userTypeId, userDnId, false))
                .Append((groupCsoId, groupMvoId, groupTypeId, groupDnId, true)))
            {
                await WriteObjectRowAsync(writer, csoId, created, typeId, systemId, dnId, ConnectedSystemObjectStatus.Normal, pending,
                    ConnectedSystemObjectJoinType.Projected);
                await writer.WriteAsync(mvoId, NpgsqlDbType.Uuid);
            }
            await writer.CompleteAsync();
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            @"COPY ""ConnectedSystemObjectAttributeValues"" (""Id"", ""ConnectedSystemObjectId"", ""AttributeId"", ""StringValue"", ""ReferenceValueId"") FROM STDIN (FORMAT BINARY)"))
        {
            await WriteValueRowAsync(writer, groupCsoId, groupDnId, "cn=All Staff,ou=Groups,dc=yellowstone,dc=local", null);
            foreach (var person in people)
                await WriteValueRowAsync(writer, groupCsoId, memberId, null, person.Cso);
            await writer.CompleteAsync();
        }

        await using (var analyse = new NpgsqlCommand(
            @"ANALYZE ""ConnectedSystemObjects""; ANALYZE ""ConnectedSystemObjectAttributeValues""; ANALYZE ""MetaverseObjects""; ANALYZE ""MetaverseObjectAttributeValues"";", connection))
        {
            await analyse.ExecuteNonQueryAsync();
        }

        return new SeededModifiedGroup(systemId, userTypeId, groupTypeId, groupCsoId, DateTime.UtcNow.AddDays(1),
            people.Select(p => p.Cso).ToHashSet(), people.Select(p => p.Mvo).ToHashSet());
    }

    #endregion

    #region Helpers

    private static ConnectedSystemObjectTypeAttribute Attribute(ConnectedSystemObjectType type, string name, AttributeDataType dataType, AttributePlurality plurality)
    {
        var attribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = name, ConnectedSystemObjectType = type, Type = dataType, AttributePlurality = plurality, Selected = true
        };
        type.Attributes.Add(attribute);
        return attribute;
    }

    private static async Task WriteObjectRowAsync(NpgsqlBinaryImporter writer, Guid id, DateTime created, int typeId, int systemId,
        int externalIdAttributeId, ConnectedSystemObjectStatus status, bool synchronisationPending,
        ConnectedSystemObjectJoinType joinType = ConnectedSystemObjectJoinType.NotJoined)
    {
        await writer.StartRowAsync();
        await writer.WriteAsync(id, NpgsqlDbType.Uuid);
        await writer.WriteAsync(created, NpgsqlDbType.TimestampTz);
        await writer.WriteAsync(typeId, NpgsqlDbType.Integer);
        await writer.WriteAsync(systemId, NpgsqlDbType.Integer);
        await writer.WriteAsync(externalIdAttributeId, NpgsqlDbType.Integer);
        await writer.WriteAsync((int)status, NpgsqlDbType.Integer);
        await writer.WriteAsync((int)joinType, NpgsqlDbType.Integer);
        await writer.WriteAsync(synchronisationPending, NpgsqlDbType.Boolean);
    }

    private static async Task WriteValueRowAsync(NpgsqlBinaryImporter writer, Guid objectId, int attributeId, string? stringValue, Guid? referenceValueId)
    {
        await writer.StartRowAsync();
        await writer.WriteAsync(Guid.NewGuid(), NpgsqlDbType.Uuid);
        await writer.WriteAsync(objectId, NpgsqlDbType.Uuid);
        await writer.WriteAsync(attributeId, NpgsqlDbType.Integer);
        if (stringValue == null)
            await writer.WriteNullAsync();
        else
            await writer.WriteAsync(stringValue, NpgsqlDbType.Text);
        if (referenceValueId is { } reference)
            await writer.WriteAsync(reference, NpgsqlDbType.Uuid);
        else
            await writer.WriteNullAsync();
    }

    private static async Task WriteChangeRowAsync(NpgsqlBinaryImporter writer, Guid id, int systemId, Guid objectId)
    {
        await writer.StartRowAsync();
        await writer.WriteAsync(id, NpgsqlDbType.Uuid);
        await writer.WriteAsync(systemId, NpgsqlDbType.Integer);
        await writer.WriteAsync(DateTime.UtcNow.AddDays(-1), NpgsqlDbType.TimestampTz);
        await writer.WriteAsync((int)ObjectChangeType.Updated, NpgsqlDbType.Integer);
        await writer.WriteAsync((int)ActivityInitiatorType.System, NpgsqlDbType.Integer);
        await writer.WriteAsync(objectId, NpgsqlDbType.Uuid);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
            return 0;
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>
    /// Every JIM span completed while recording, with its duration and tags.
    /// </summary>
    private sealed class SpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        private ConcurrentQueue<RecordedSpan> _spans = new();
        private volatile bool _recording;

        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name.StartsWith("JIM.", StringComparison.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = span =>
                {
                    if (_recording)
                        _spans.Enqueue(new RecordedSpan(span.DisplayName, span.Duration.TotalMilliseconds, span.TagObjects.ToList()));
                }
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Start()
        {
            _spans = new ConcurrentQueue<RecordedSpan>();
            _recording = true;
        }

        public List<RecordedSpan> Stop()
        {
            _recording = false;
            return [.. _spans];
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed record RecordedSpan(string Name, double Ms, List<KeyValuePair<string, object?>> Tags);

    #endregion
}

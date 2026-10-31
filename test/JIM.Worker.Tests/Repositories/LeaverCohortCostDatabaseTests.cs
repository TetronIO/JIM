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
using JIM.PostgresData;
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
/// Measures, on real PostgreSQL, the two costs #2669 found in a leaver cohort's confirming Delta Synchronisation, and
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
/// <item>Loading a modified group's page through <see cref="TypeOrderedCsoPager.NextPageAsync"/>, at several
/// membership sizes, against a raw read of the same rows.</item>
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
    private const int MeasuredRuns = 3;

    // History each departed account carries at the moment it is deleted, all of it pointing at the account and so all
    // of it cleared to null by the database as the account goes: execution items from the runs that touched it, its own
    // change records, and member values in groups' change records.
    private const int ExecutionItemsPerLeaver = 5;
    private const int ChangeRecordsPerLeaver = 3;
    private const int ChangeValuesPerLeaver = 15;

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

            report.Add(DescribeDeletionRuns(path, withHistory, runs));
            TestContext.Progress.WriteLine(report[^1]);
        }

        TestContext.Out.WriteLine(string.Join(Environment.NewLine + Environment.NewLine, report));
    }

    [TestCase(25_000)]
    [TestCase(50_000)]
    [TestCase(100_000)]
    public async Task NextPageAsync_ModifiedGroupWithLargeMembership_ReportsPageLoadCostAsync(int members)
    {
        var seeded = await SeedModifiedGroupAsync(members);

        var runs = new List<PageLoadRun>();
        for (var i = 0; i <= MeasuredRuns; i++)
        {
            var run = await MeasurePageLoadAsync(seeded);
            if (i > 0)
                runs.Add(run);
        }

        var rawMs = new List<double>();
        for (var i = 0; i <= MeasuredRuns; i++)
        {
            var ms = await ReadGroupValuesRawAsync(seeded.GroupCsoId);
            if (i > 0)
                rawMs.Add(ms);
        }

        TestContext.Out.WriteLine(DescribePageLoadRuns(members, runs, Median(rawMs)));
    }

    #region Deletion

    private sealed record DeletionRun(
        double TotalMs, Dictionary<string, double> SpanMs, Dictionary<string, string> SpanTags,
        int Commands, Dictionary<string, int> Statements);

    private async Task<DeletionRun> MeasureDeletionAsync(DeletionPath path, bool withHistory)
    {
        var seeded = await SeedLeaverPageAsync(withHistory);
        var counter = new SqlStatementCounter();

        await using var ctx = NewPageContext(counter);
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

        return new DeletionRun(
            stopwatch.Elapsed.TotalMilliseconds,
            recorded.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Sum(s => s.Ms), StringComparer.Ordinal),
            recorded.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => string.Join(", ", g.First().Tags.Select(t => $"{t.Key}={t.Value}")), StringComparer.Ordinal),
            counter.Commands,
            new Dictionary<string, int>(counter.Statements, StringComparer.Ordinal));
    }

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

    private static string DescribeDeletionRuns(DeletionPath path, bool withHistory, List<DeletionRun> runs)
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"== {path}{(withHistory ? ", with history" : "")}: median {Median(runs.Select(r => r.TotalMs)):F1} ms " +
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
        return string.Join(Environment.NewLine, lines);
    }

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

    #region Page load

    private sealed record PageLoadRun(double GroupPageMs, int ValueCount, Dictionary<string, double> SpanMs);

    private sealed record SeededModifiedGroup(int SystemId, int UserTypeId, int GroupTypeId, Guid GroupCsoId, DateTime Watermark);

    private async Task<PageLoadRun> MeasurePageLoadAsync(SeededModifiedGroup seeded)
    {
        await using var ctx = NewPageContext(null);
        var repository = new PostgresDataRepository(ctx);
        var pager = new TypeOrderedCsoPager(
            (afterId, objectTypeId, createdOnOrAfter) => repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(
                seeded.SystemId, seeded.Watermark, 1, 500, 1, afterId, objectTypeId, createdOnOrAfter),
            [seeded.UserTypeId, seeded.GroupTypeId],
            seeded.Watermark);

        _spans.Start();
        var stopwatch = Stopwatch.StartNew();
        var page = await pager.NextPageAsync();
        stopwatch.Stop();
        var recorded = _spans.Stop();

        Assert.That(page?.Results.Select(c => c.Id), Is.EqualTo(new[] { seeded.GroupCsoId }), "the modified group's page");
        Assert.That(await pager.NextPageAsync(), Is.Null, "nothing else is modified");

        return new PageLoadRun(
            stopwatch.Elapsed.TotalMilliseconds,
            page!.Results.Single().AttributeValues.Count,
            recorded.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Sum(s => s.Ms), StringComparer.Ordinal));
    }

    /// <summary>
    /// The floor: the group's value rows with their attribute, read raw.
    /// </summary>
    private async Task<double> ReadGroupValuesRawAsync(Guid groupCsoId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        var stopwatch = Stopwatch.StartNew();
        await using var command = new NpgsqlCommand(
            @"SELECT v.*, a.""Name"" FROM ""ConnectedSystemObjectAttributeValues"" v JOIN ""ConnectedSystemAttributes"" a ON a.""Id"" = v.""AttributeId"" " +
            @"WHERE v.""ConnectedSystemObjectId"" = @id ORDER BY v.""Id""", connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = groupCsoId });
        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            _ = reader.GetGuid(0);
            rows++;
        }
        stopwatch.Stop();
        Assert.That(rows, Is.GreaterThan(0));
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static string DescribePageLoadRuns(int members, List<PageLoadRun> runs, double rawMs)
    {
        var median = Median(runs.Select(r => r.GroupPageMs));
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"== NextPageAsync, one modified group of {members} members ({runs[^1].ValueCount} values): median {median:F1} ms " +
                $"(runs {string.Join(" / ", runs.Select(r => r.GroupPageMs.ToString("F1", CultureInfo.InvariantCulture)))}), " +
                $"{median * 1000 / runs[^1].ValueCount:F2} µs per value; raw read of the same rows {rawMs:F1} ms")
        };
        foreach (var name in runs[^1].SpanMs.Keys.Order(StringComparer.Ordinal))
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"   span {name}: median {Median(runs.Select(r => r.SpanMs.GetValueOrDefault(name))):F1} ms"));
        return string.Join(Environment.NewLine, lines);
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

        return new SeededModifiedGroup(systemId, userTypeId, groupTypeId, groupCsoId, DateTime.UtcNow.AddDays(1));
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

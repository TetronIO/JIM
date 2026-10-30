// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
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
/// Real-PostgreSQL verification of the Causality Explorer's reads (#2100). An object's causal timeline is one raw SQL
/// statement over every place an object's items are recorded, live and deleted alike, so which sources it takes, how it
/// bounds the ones keyed on something other than the object's id, and that each source is read through an index are
/// the feature. None of it can run on the in-memory provider.
///
/// Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class CausalTimelineReadDatabaseTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL causal timeline read tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    // ─── Connected System Object timelines ───

    [Test]
    public async Task GetCausalTimelinePageAsync_LiveConnectedSystemObject_ListsItsItemsNewestFirstWithTheirRunsAsync()
    {
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var tina = await SeedCsoAsync(hr);
        var mia = await SeedCsoAsync(hr);
        var import = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0, "Delta Import", "Yellowstone APAC");
        var imported = await SeedItemAsync(import, ObjectChangeType.Updated, "Tina Adams", "person", csoId: tina, externalId: "E1001");
        await SeedItemAsync(import, ObjectChangeType.Updated, "Mia Young", "person", csoId: mia, externalId: "E1002");
        var sync = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddMinutes(5), "Delta Sync", "Yellowstone APAC");
        var synchronised = await SeedItemAsync(sync, ObjectChangeType.AttributeFlow, "Tina Adams", "person", csoId: tina,
            externalId: "E1001", outcomeSummary: "AttributeFlow:3,PendingExportCreated:1");
        var export = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.Export, T0.AddMinutes(10), "Export", "Yellowstone APAC");
        var exported = await SeedItemAsync(export, ObjectChangeType.Exported, "Tina Adams", "person", csoId: tina, externalId: "E1001",
            errorType: ActivityRunProfileExecutionItemErrorType.UnhandledError);

        var (entries, total) = await ReadTimelineAsync(CsoSubject(tina));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Select(e => e.RunProfileExecutionItemId), Is.EqualTo(new[] { exported, synchronised, imported }),
                "newest first, and only this object's items");
            Assert.That(total, Is.EqualTo(3));
            var syncEntry = entries[1];
            Assert.That(syncEntry.ActivityId, Is.EqualTo(sync));
            Assert.That(syncEntry.Executed, Is.EqualTo(T0.AddMinutes(5)));
            Assert.That(syncEntry.Executed.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(syncEntry.ActivityTargetType, Is.EqualTo(ActivityTargetType.ConnectedSystemRunProfile));
            Assert.That(syncEntry.ActivityTargetName, Is.EqualTo("Delta Sync"), "the run as the Activity recorded it");
            Assert.That(syncEntry.ActivityTargetContext, Is.EqualTo("Yellowstone APAC"));
            Assert.That(syncEntry.ConnectedSystemId, Is.EqualTo(hr.SystemId));
            Assert.That(syncEntry.RunType, Is.EqualTo(ConnectedSystemRunType.DeltaSynchronisation));
            Assert.That(syncEntry.ObjectChangeType, Is.EqualTo(ObjectChangeType.AttributeFlow));
            Assert.That(syncEntry.OutcomeSummary, Is.EqualTo("AttributeFlow:3,PendingExportCreated:1"));
            Assert.That(syncEntry.DisplayName, Is.EqualTo("Tina Adams"));
            Assert.That(syncEntry.ObjectType, Is.EqualTo("person"));
            Assert.That(syncEntry.ConnectedSystemObjectId, Is.EqualTo(tina));
            Assert.That(syncEntry.ExternalId, Is.EqualTo("E1001"));
            Assert.That(entries[0].ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnhandledError));
        }
    }

    [Test]
    public async Task CausalTimelineReads_ItemRecordedNoObjectType_TakeItsConnectedSystemObjectsTypeAsync()
    {
        // Not every item records the object's type: an import updating a record it already holds records only the name.
        // The record's own type stands in, so the explorer's trail and focus header name every item alike. A type the
        // item did record is kept, since it can name the Metaverse Object the item was about rather than the record.
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var tina = await SeedCsoAsync(hr);
        var import = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0, "Delta Import", "Yellowstone APAC");
        var untyped = await SeedItemAsync(import, ObjectChangeType.Updated, "Tina Adams", objectType: null, csoId: tina);
        var sync = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddMinutes(5), "Delta Sync", "Yellowstone APAC");
        var typed = await SeedItemAsync(sync, ObjectChangeType.AttributeFlow, "Tina Adams", "User", csoId: tina);

        var (page, _) = await ReadTimelineAsync(CsoSubject(tina));
        await using var ctx = NewContext();
        var byId = await new PostgresDataRepository(ctx).Activity.GetCausalTimelineEntriesAsync([untyped, typed]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Single(e => e.RunProfileExecutionItemId == untyped).ObjectType, Is.EqualTo("person"), "a timeline page");
            Assert.That(byId[untyped].ObjectType, Is.EqualTo("person"), "items read by id");
            Assert.That(page.Single(e => e.RunProfileExecutionItemId == typed).ObjectType, Is.EqualTo("User"), "a recorded type is kept");
            Assert.That(byId[typed].ObjectType, Is.EqualTo("User"));
        }
    }

    [Test]
    public async Task GetCausalTimelinePageAsync_LongerThanAPage_PagesNewestFirstAndCountsEveryItemAsync()
    {
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var tina = await SeedCsoAsync(hr);
        var items = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var activity = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0.AddHours(i));
            items.Add(await SeedItemAsync(activity, ObjectChangeType.Updated, "Tina Adams", "person", csoId: tina));
        }

        var (page, total) = await ReadTimelineAsync(CsoSubject(tina), skip: 2, take: 2);
        var (pastTheEnd, totalPastTheEnd) = await ReadTimelineAsync(CsoSubject(tina), skip: 10, take: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Select(e => e.RunProfileExecutionItemId), Is.EqualTo(new[] { items[2], items[1] }));
            Assert.That(total, Is.EqualTo(5));
            Assert.That(pastTheEnd, Is.Empty);
            Assert.That(totalPastTheEnd, Is.EqualTo(5), "a page past the end still says how many there are");
        }
    }

    [Test]
    public async Task GetCausalTimelinePageAsync_ItemsOfOneRun_AreOrderedByIdWithinItAsync()
    {
        // Everything one run did shares its Activity's time, so the id decides between them: arbitrary, but the same
        // on every read, so paging never repeats or skips an item.
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var tina = await SeedCsoAsync(hr);
        var sync = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullSynchronisation, T0);
        var first = await SeedItemAsync(sync, ObjectChangeType.DriftCorrection, "Tina Adams", "person", csoId: tina);
        var second = await SeedItemAsync(sync, ObjectChangeType.AttributeFlow, "Tina Adams", "person", csoId: tina);

        var (entries, _) = await ReadTimelineAsync(CsoSubject(tina));

        Assert.That(entries.Select(e => e.RunProfileExecutionItemId),
            Is.EqualTo(new[] { first, second }.OrderByDescending(id => id)));
    }

    [Test]
    public async Task GetCausalTimelinePageAsync_DeletedConnectedSystemObject_ListsTheItemsItsDeletionLeftByExternalIdAsync()
    {
        // Deleting a Connected System Object nulls its id on every item that processed it, and only its deletion record
        // keeps the id. That record names the deleting item and keeps the external ID and system, which find the rest:
        // within that system, with the id nulled, and no later than the deletion. Each bound excludes a real neighbour.
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var other = await SeedConnectedSystemAsync("Glitterband EMEA");
        var tina = await SeedCsoAsync(hr);
        var added = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullImport, T0),
            ObjectChangeType.Added, "Tina Adams", "person", csoId: tina, externalId: "E1001");
        var projected = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullSynchronisation, T0.AddHours(1)),
            ObjectChangeType.Projected, "Tina Adams", "person", csoId: tina, externalId: "E1001");
        var deletedInSource = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0.AddDays(1)),
            ObjectChangeType.Deleted, "Tina Adams", "person", csoId: tina, externalId: "E1001");
        var deletionSync = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddDays(1).AddMinutes(5));
        var removed = await SeedItemAsync(deletionSync, ObjectChangeType.Disconnected, "Tina Adams", "person", csoId: tina, externalId: "E1001");
        await DeleteCsoAsync(tina);
        await SeedCsoDeletionRecordAsync(hr.SystemId, tina, "E1001", removed, T0.AddDays(1).AddMinutes(5).AddSeconds(1));

        // The same external ID in another system, on a live record created later, and on a record deleted later still.
        await SeedItemAsync(await SeedActivityAsync(other.SystemId, ConnectedSystemRunType.FullImport, T0.AddMinutes(30)),
            ObjectChangeType.Added, "Tina Adams", "person", externalId: "E1001");
        var rehired = await SeedCsoAsync(hr);
        await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0.AddDays(30)),
            ObjectChangeType.Added, "Tina Adams", "person", csoId: rehired, externalId: "E1001");
        await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0.AddDays(60)),
            ObjectChangeType.Added, "Tina Adams", "person", externalId: "E1001");

        var (entries, total) = await ReadTimelineAsync(CsoSubject(tina));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Select(e => e.RunProfileExecutionItemId), Is.EqualTo(new[] { removed, deletedInSource, projected, added }));
            Assert.That(total, Is.EqualTo(4), "the deleting item is found both ways and counted once");
            Assert.That(entries.Select(e => e.ConnectedSystemObjectId), Is.All.Null, "the deletion nulled every one");
        }
    }

    [Test]
    public async Task GetCausalTimelinePageAsync_DeletedConnectedSystemObjectWithNoExternalId_ListsTheItemItsDeletionRecordNamesAsync()
    {
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var tina = await SeedCsoAsync(hr);
        var removed = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0),
            ObjectChangeType.Disconnected, "Tina Adams", "person", csoId: tina);
        await DeleteCsoAsync(tina);
        await SeedCsoDeletionRecordAsync(hr.SystemId, tina, externalId: null, removed, T0.AddSeconds(1));

        var (entries, total) = await ReadTimelineAsync(CsoSubject(tina));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Select(e => e.RunProfileExecutionItemId), Is.EqualTo(new[] { removed }));
            Assert.That(total, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GetCausalTimelinePageAsync_UnknownObject_IsEmptyAsync()
    {
        var (entries, total) = await ReadTimelineAsync(CsoSubject(Guid.NewGuid()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries, Is.Empty);
            Assert.That(total, Is.Zero);
        }
    }

    // ─── Metaverse Object timelines ───

    [Test]
    public async Task GetCausalTimelinePageAsync_LiveMetaverseObject_ListsItemsFromEverySourceOnceAsync()
    {
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var target = await SeedConnectedSystemAsync("Glitterband EMEA");
        var tina = await SeedMvoAsync();
        var someoneElse = await SeedMvoAsync();
        var hrRecord = await SeedCsoAsync(hr, joinedTo: tina);
        var targetRecord = await SeedCsoAsync(target, joinedTo: tina);
        var formerRecord = await SeedCsoAsync(target);
        var unrelated = await SeedCsoAsync(hr, joinedTo: someoneElse);

        // A joined record's import, before anything reached the Metaverse Object.
        var imported = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullImport, T0),
            ObjectChangeType.Added, "Tina Adams", "person", csoId: hrRecord);
        // Its projection, named both by a change record and as a joined record's item: counted once.
        var projected = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullSynchronisation, T0.AddHours(1)),
            ObjectChangeType.Projected, "Tina Adams", "person", csoId: hrRecord);
        await SeedMvoChangeAsync(projected, metaverseObjectId: tina, changeTime: T0.AddHours(1));
        var exported = await SeedItemAsync(await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.Export, T0.AddHours(2)),
            ObjectChangeType.Exported, "Tina Adams", "user", csoId: targetRecord);
        // An export scope review records the Metaverse Object itself.
        var reviewed = await SeedItemAsync(await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.FullSynchronisation, T0.AddHours(3)),
            ObjectChangeType.NoChange, "Tina Adams", "user", metaverseObjectId: tina);
        // A record no longer joined, whose synchronisation left a change record.
        var leftScope = await SeedItemAsync(await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddHours(4)),
            ObjectChangeType.DisconnectedOutOfScope, "Tina Adams", "user", csoId: formerRecord);
        await SeedMvoChangeAsync(leftScope, metaverseObjectId: tina, changeTime: T0.AddHours(4));
        // The disconnection that scheduled the deletion left no change record; only the schedule names it.
        var scheduled = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddHours(5)),
            ObjectChangeType.Disconnected, "Tina Adams", "person");
        await ScheduleDeletionAsync(tina, scheduled);

        // Someone else's history, through every source.
        var elsewhere = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullImport, T0.AddHours(6)),
            ObjectChangeType.Added, "Mia Young", "person", csoId: unrelated);
        await SeedMvoChangeAsync(elsewhere, metaverseObjectId: someoneElse, changeTime: T0.AddHours(6));
        await SeedItemAsync(await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.FullSynchronisation, T0.AddHours(6)),
            ObjectChangeType.NoChange, "Mia Young", "user", metaverseObjectId: someoneElse);

        var (entries, total) = await ReadTimelineAsync(MvoSubject(tina));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Select(e => e.RunProfileExecutionItemId),
                Is.EqualTo(new[] { scheduled, leftScope, reviewed, exported, projected, imported }));
            Assert.That(total, Is.EqualTo(6));
            Assert.That(entries.Single(e => e.RunProfileExecutionItemId == reviewed).MetaverseObjectId, Is.EqualTo(tina));
        }
    }

    [Test]
    public async Task GetCausalTimelinePageAsync_DeletedMetaverseObject_ListsItemsItsReKeyedChangeRecordsNameAsync()
    {
        // Deleting a Metaverse Object moves its change records from its id to the deleted id, and its records'
        // joins are gone; what names it now is those records and any item that recorded it directly.
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var tina = Guid.NewGuid();
        var projected = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullSynchronisation, T0),
            ObjectChangeType.Projected, "Tina Adams", "person");
        await SeedMvoChangeAsync(projected, deletedMetaverseObjectId: tina, changeTime: T0);
        var reviewed = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullSynchronisation, T0.AddHours(1)),
            ObjectChangeType.NoChange, "Tina Adams", "person", metaverseObjectId: tina);
        var deleted = await SeedItemAsync(await SeedActivityAsync(null, ConnectedSystemRunType.NotSet, T0.AddDays(7), "Housekeeping",
                targetType: ActivityTargetType.HistoryRetentionCleanup),
            ObjectChangeType.Deleted, "Tina Adams", "User");
        await SeedMvoChangeAsync(deleted, deletedMetaverseObjectId: tina, changeTime: T0.AddDays(7));

        var (entries, total) = await ReadTimelineAsync(MvoSubject(tina));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Select(e => e.RunProfileExecutionItemId), Is.EqualTo(new[] { deleted, reviewed, projected }));
            Assert.That(total, Is.EqualTo(3));
            Assert.That(entries[0].ConnectedSystemId, Is.Null, "housekeeping runs against no Connected System");
        }
    }

    [Test]
    public async Task GetCausalTimelinePageAsync_DeletedMetaverseObject_ListsTheItemThatDeletedItAsync()
    {
        // An object deleted at once by a disconnection: the disconnection's item is about the Connected System Object,
        // whose join is gone, and it carries no change record of its own. Only the deletion record names it (#2460).
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var tina = Guid.NewGuid();
        var projected = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullSynchronisation, T0),
            ObjectChangeType.Projected, "Tina Adams", "person");
        await SeedMvoChangeAsync(projected, deletedMetaverseObjectId: tina, changeTime: T0);
        var disconnected = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddDays(3)),
            ObjectChangeType.Disconnected, "Tina Adams", "person");
        await SeedMvoChangeAsync(null, deletedMetaverseObjectId: tina, changeTime: T0.AddDays(3), deletedByItemId: disconnected);

        var (entries, total) = await ReadTimelineAsync(MvoSubject(tina));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Select(e => e.RunProfileExecutionItemId), Is.EqualTo(new[] { disconnected, projected }));
            Assert.That(total, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task GetCausalTimelinePageAsync_SourcesNamingAPurgedItem_AreNotCountedAsync()
    {
        // History retention deletes old items. A change record's link to one is nulled with it; the deletion schedule
        // is a plain id and outlives it. Neither is an item to list, and the count must not include them.
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var tina = await SeedMvoAsync();
        var retained = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullSynchronisation, T0),
            ObjectChangeType.Projected, "Tina Adams", "person");
        await SeedMvoChangeAsync(retained, metaverseObjectId: tina, changeTime: T0);
        await SeedMvoChangeAsync(null, metaverseObjectId: tina, changeTime: T0.AddHours(1));
        await ScheduleDeletionAsync(tina, Guid.NewGuid());

        var (entries, total) = await ReadTimelineAsync(MvoSubject(tina));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Select(e => e.RunProfileExecutionItemId), Is.EqualTo(new[] { retained }));
            Assert.That(total, Is.EqualTo(1));
        }
    }

    // ─── Items as timeline entries ───

    [Test]
    public async Task GetCausalTimelineEntriesAsync_ItemsById_ArriveWithTheirRunsAndPurgedOnesAreAbsentAsync()
    {
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var sync = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0, "Delta Sync", "Yellowstone APAC");
        var first = await SeedItemAsync(sync, ObjectChangeType.AttributeFlow, "Tina Adams", "person");
        var second = await SeedItemAsync(sync, ObjectChangeType.Projected, "Mia Young", "person");
        var purged = Guid.NewGuid();

        await using var ctx = NewContext();
        var entries = await new PostgresDataRepository(ctx).Activity.GetCausalTimelineEntriesAsync([first, second, purged]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Keys, Is.EquivalentTo(new[] { first, second }));
            Assert.That(entries[second].ObjectChangeType, Is.EqualTo(ObjectChangeType.Projected));
            Assert.That(entries[second].DisplayName, Is.EqualTo("Mia Young"));
            Assert.That(entries[second].ActivityTargetName, Is.EqualTo("Delta Sync"));
            Assert.That(entries[second].Executed, Is.EqualTo(T0));
        }
    }

    [Test]
    public async Task GetCausalTimelineEntriesAsync_NoIds_IsEmptyAsync()
    {
        await using var ctx = NewContext();
        var entries = await new PostgresDataRepository(ctx).Activity.GetCausalTimelineEntriesAsync([]);

        Assert.That(entries, Is.Empty);
    }

    // ─── Queued by ───

    [Test]
    public async Task QueuedByRead_PendingExportStagedBySeveralRuns_ReadsEveryRunAsATimelineEntryAsync()
    {
        // Queued by is the Pending Export's own row (its stagers, Phase 3-0) and one read of those items: no index on
        // the synchronisation outcomes, and nothing per stager.
        var target = await SeedConnectedSystemAsync("Glitterband EMEA");
        var firstRun = await SeedItemAsync(await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0, "Delta Sync"),
            ObjectChangeType.AttributeFlow, "Project Pulse", "group");
        var secondRun = await SeedItemAsync(await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddHours(1), "Delta Sync"),
            ObjectChangeType.AttributeFlow, "Project Pulse", "group");
        var staged = await SeedPendingExportAsync(target.SystemId, firstRun, secondRun);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var stagers = (await repository.ConnectedSystems.GetPendingExportCoreAsync(staged))!.Stagers();
        var entries = await repository.Activity.GetCausalTimelineEntriesAsync([.. stagers]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stagers, Is.EqualTo(new[] { firstRun, secondRun }));
            Assert.That(entries.Keys, Is.EquivalentTo(new[] { firstRun, secondRun }));
            Assert.That(entries[secondRun].Executed, Is.EqualTo(T0.AddHours(1)));
        }
    }

    [Test]
    public async Task QueuedByRead_PendingExportStagedBeforeTheListWasRecorded_FallsBackToItsLatestStagerAsync()
    {
        var target = await SeedConnectedSystemAsync("Glitterband EMEA");
        var run = await SeedItemAsync(await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0),
            ObjectChangeType.AttributeFlow, "Project Pulse", "group");
        var staged = await SeedPendingExportAsync(target.SystemId, run);
        await using (var clear = NewContext())
        {
            await clear.Database.ExecuteSqlRawAsync(
                """UPDATE "PendingExports" SET "StagedByRunProfileExecutionItemIds" = '{{}}' WHERE "Id" = {0}""", staged);
        }

        await using var ctx = NewContext();
        var pendingExport = await new PostgresDataRepository(ctx).ConnectedSystems.GetPendingExportCoreAsync(staged);

        Assert.That(pendingExport!.Stagers(), Is.EqualTo(new[] { run }));
    }

    [Test]
    public async Task GetPendingExportCoreAsync_UnknownPendingExport_IsNullAsync()
    {
        await using var ctx = NewContext();
        var pendingExport = await new PostgresDataRepository(ctx).ConnectedSystems.GetPendingExportCoreAsync(Guid.NewGuid());

        Assert.That(pendingExport, Is.Null);
    }

    // ─── Source-import hop, in batch ───

    [Test]
    public async Task GetLatestImportEventsForItemsAsync_SynchronisationItems_EachGetsItsOwnRecordsLatestImportAtOrBeforeItAsync()
    {
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var tina = await SeedCsoAsync(hr);
        var mia = await SeedCsoAsync(hr);
        await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullImport, T0),
            ObjectChangeType.Added, "Tina Adams", "person", csoId: tina);
        var miaAdded = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullImport, T0.AddMinutes(10)),
            ObjectChangeType.Added, "Mia Young", "person", csoId: mia);
        var tinaUpdated = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0.AddMinutes(20)),
            ObjectChangeType.Updated, "Tina Adams (Finance)", "person", csoId: tina);
        var sync = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddMinutes(30));
        var tinaSync = await SeedItemAsync(sync, ObjectChangeType.AttributeFlow, "Tina Adams", "person", csoId: tina);
        var miaSync = await SeedItemAsync(sync, ObjectChangeType.Projected, "Mia Young", "person", csoId: mia);
        // An import after the synchronisation is not where its data came from.
        await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0.AddMinutes(40)),
            ObjectChangeType.Updated, "Tina Adams (Sales)", "person", csoId: tina);

        var events = await ReadLatestImportsAsync([tinaSync, miaSync]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(events.Keys, Is.EquivalentTo(new[] { tinaSync, miaSync }));
            Assert.That(events[tinaSync].RunProfileExecutionItemId, Is.EqualTo(tinaUpdated), "the latest at or before, not after");
            Assert.That(events[tinaSync].ChangeType, Is.EqualTo(ObjectChangeType.Updated));
            Assert.That(events[tinaSync].DisplayName, Is.EqualTo("Tina Adams (Finance)"));
            Assert.That(events[tinaSync].Occurred, Is.EqualTo(T0.AddMinutes(20)));
            Assert.That(events[tinaSync].ConnectedSystemId, Is.EqualTo(hr.SystemId));
            Assert.That(events[tinaSync].ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(events[miaSync].RunProfileExecutionItemId, Is.EqualTo(miaAdded), "each item its own record");
        }
    }

    [Test]
    public async Task GetLatestImportEventsForItemsAsync_RecordDeleted_FallsBackToTheExternalIdWithinItsConnectedSystemAsync()
    {
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var other = await SeedConnectedSystemAsync("Glitterband EMEA");
        var tina = await SeedCsoAsync(hr);
        var deletedInSource = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0),
            ObjectChangeType.Deleted, "Tina Adams", "person", csoId: tina, externalId: "E1001");
        // The same external ID in another system, closer in time: not this record.
        await SeedItemAsync(await SeedActivityAsync(other.SystemId, ConnectedSystemRunType.DeltaImport, T0.AddMinutes(1)),
            ObjectChangeType.Updated, "Tina Adams", "person", externalId: "E1001");
        var removed = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddMinutes(5)),
            ObjectChangeType.Disconnected, "Tina Adams", "person", csoId: tina, externalId: "E1001");
        await DeleteCsoAsync(tina);

        var events = await ReadLatestImportsAsync([removed]);

        Assert.That(events[removed].RunProfileExecutionItemId, Is.EqualTo(deletedInSource));
    }

    [Test]
    public async Task GetLatestImportEventsForItemsAsync_NothingRetained_IsAbsentAndTheItemIsNeverItsOwnSourceAsync()
    {
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var tina = await SeedCsoAsync(hr);
        var mia = await SeedCsoAsync(hr);
        var tinaImport = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullImport, T0),
            ObjectChangeType.Added, "Tina Adams", "person", csoId: tina);
        var miaSync = await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullSynchronisation, T0.AddMinutes(5)),
            ObjectChangeType.Projected, "Mia Young", "person", csoId: mia);

        var events = await ReadLatestImportsAsync([tinaImport, miaSync, Guid.NewGuid()]);

        Assert.That(events, Is.Empty, "an import with no earlier import, a record never imported, and a purged item");
    }

    [Test]
    public async Task GetLatestImportEventsForItemsAsync_AgreesWithThePerItemReadsAsync()
    {
        // The batch replaces the walk's per-item reads on the explorer's pages, so the two must find the same import
        // for the same item, by id and by the external ID fallback alike.
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var live = await SeedCsoAsync(hr);
        var gone = await SeedCsoAsync(hr);
        await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullImport, T0),
            ObjectChangeType.Added, "Tina Adams", "person", csoId: live, externalId: "E1001");
        await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullImport, T0.AddMinutes(1)),
            ObjectChangeType.Added, "Mia Young", "person", csoId: gone, externalId: "E1002");
        await SeedItemAsync(await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0.AddMinutes(2)),
            ObjectChangeType.Updated, "Tina Adams", "person", csoId: live, externalId: "E1001");
        var sync = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddMinutes(10));
        var liveSync = await SeedItemAsync(sync, ObjectChangeType.AttributeFlow, "Tina Adams", "person", csoId: live, externalId: "E1001");
        var goneSync = await SeedItemAsync(sync, ObjectChangeType.Disconnected, "Mia Young", "person", csoId: gone, externalId: "E1002");
        await DeleteCsoAsync(gone);

        var batch = await ReadLatestImportsAsync([liveSync, goneSync]);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Activity;
        var perItemLive = await repository.GetLatestImportItemForCsoAsync(live, T0.AddMinutes(10), liveSync);
        var perItemGone = await repository.GetLatestImportItemForExternalIdAsync(hr.SystemId, "E1002", T0.AddMinutes(10), goneSync);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch[liveSync].RunProfileExecutionItemId, Is.EqualTo(perItemLive!.RunProfileExecutionItemId));
            Assert.That(batch[goneSync].RunProfileExecutionItemId, Is.EqualTo(perItemGone!.RunProfileExecutionItemId));
            Assert.That(batch[liveSync].ConnectedSystemName, Is.EqualTo(perItemLive!.ConnectedSystemName));
        }
    }

    // ─── Indexes and cost ───

    [Test]
    public async Task GetCausalTimelinePageAsync_ConnectedSystemObjectTimeline_IsReadThroughIndexesAsync()
    {
        // Sequential scans are switched off so a tiny test table cannot make the planner prefer one regardless: what
        // is left is whether each source's predicate can use an index at all. A deleted record's items are found by
        // external ID among the items whose record id is null, and in production those are many (every item of every
        // deleted record), so the statistics are made to say so; on empty tables the planner reaches them through the
        // null record ids instead, a path that scans every such item.
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        await SeedItemsOfDeletedRecordsAsync(hr.SystemId, 5_000);
        var tina = Guid.NewGuid();
        await SeedCsoDeletionRecordAsync(hr.SystemId, tina, "E1001", itemId: null, T0);
        await AnalyseAsync();

        var plan = await ExplainAsync(command =>
            ActivityRepository.PrepareCausalTimelinePageCommand(command, CsoSubject(tina), 0, 50));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan, Does.Contain("IX_ActivityRunProfileExecutionItems_ConnectedSystemObjectId"), "the live object's items");
            Assert.That(plan, Does.Contain("IX_ConnectedSystemObjectChanges_DeletedConnectedSystemObjectId"), "its deletion record");
            Assert.That(plan, Does.Contain("IX_ActivityRunProfileExecutionItems_ExternalIdSnapshot"), "the deleted object's items");
            Assert.That(plan, Does.Not.Contain("Seq Scan"));
        }
    }

    [Test]
    public async Task GetCausalTimelinePageAsync_MetaverseObjectTimeline_IsReadThroughIndexesAsync()
    {
        var plan = await ExplainAsync(command =>
            ActivityRepository.PrepareCausalTimelinePageCommand(command, MvoSubject(Guid.NewGuid()), 0, 50));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan, Does.Contain("IX_MetaverseObjectChanges_MetaverseObjectId"), "its change records");
            Assert.That(plan, Does.Contain("IX_MetaverseObjectChanges_DeletedMetaverseObjectId"), "its re-keyed change records");
            Assert.That(plan, Does.Contain("IX_ActivityRunProfileExecutionItems_MetaverseObjectId"), "items recording it directly");
            Assert.That(plan, Does.Contain("IX_ActivityRunProfileExecutionItems_ConnectedSystemObjectId"), "its joined records' items");
            Assert.That(plan, Does.Not.Contain("Seq Scan"));
        }
    }

    [Test]
    public async Task GetLatestImportEventsForItemsAsync_IsReadThroughIndexesAsync()
    {
        var plan = await ExplainAsync(command =>
            ActivityRepository.PrepareLatestImportEventsCommand(command, [Guid.NewGuid(), Guid.NewGuid()]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan, Does.Contain("IX_ActivityRunProfileExecutionItems_ConnectedSystemObjectId"), "by the record's id");
            Assert.That(plan, Does.Contain("IX_ActivityRunProfileExecutionItems_ExternalIdSnapshot"), "by its external ID");
            Assert.That(plan, Does.Not.Contain("Seq Scan"));
        }
    }

    [Test]
    public async Task GetCausalTimelinePageAsync_TenThousandItemObject_OpensWithinTheBudgetOfATenItemOneAsync()
    {
        // A long-lived record (a Group changed daily for years) can collect thousands of items. Its first page must open
        // as quickly as a new joiner's: the read gathers, orders and counts the object's items in the database through
        // its indexes, and returns fifty.
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var longLived = await SeedCsoAsync(hr);
        var joiner = await SeedCsoAsync(hr);
        await SeedItemsInTheirOwnRunsAsync(hr.SystemId, longLived, 10_000);
        await SeedItemsInTheirOwnRunsAsync(hr.SystemId, joiner, 10);
        await AnalyseAsync();

        var (longLivedPage, longLivedTotal, longLivedElapsed) = await TimeFirstPageAsync(CsoSubject(longLived));
        var (joinerPage, joinerTotal, joinerElapsed) = await TimeFirstPageAsync(CsoSubject(joiner));

        TestContext.Out.WriteLine($"10,000-item first page: {longLivedElapsed.TotalMilliseconds:F1} ms; 10-item: {joinerElapsed.TotalMilliseconds:F1} ms");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(longLivedPage, Has.Count.EqualTo(50));
            Assert.That(longLivedTotal, Is.EqualTo(10_000));
            Assert.That(joinerPage, Has.Count.EqualTo(10));
            Assert.That(joinerTotal, Is.EqualTo(10));
            Assert.That(longLivedPage.Select(e => e.Executed), Is.Ordered.Descending);
            Assert.That(longLivedElapsed, Is.LessThan(TimelinePageBudget), "the long history's first page");
            Assert.That(joinerElapsed, Is.LessThan(TimelinePageBudget), "the short history's first page");
        }
    }

    /// <summary>
    /// What one page of a timeline may cost, wall clock, against the test database: generous for a shared CI runner,
    /// and still an order of magnitude below what reading an unindexed history would take.
    /// </summary>
    private static readonly TimeSpan TimelinePageBudget = TimeSpan.FromMilliseconds(300);

    // ─── Helpers ───

    private static CausalTimelineSubject CsoSubject(Guid id) => new(CausalTimelineSubjectKind.ConnectedSystemObject, id);

    private static CausalTimelineSubject MvoSubject(Guid id) => new(CausalTimelineSubjectKind.MetaverseObject, id);

    private async Task<(List<CausalTimelineEntry> Entries, int TotalCount)> ReadTimelineAsync(
        CausalTimelineSubject subject, int skip = 0, int take = 50)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).Activity.GetCausalTimelinePageAsync(subject, skip, take);
    }

    private async Task<Dictionary<Guid, CausalSourceImportEvent>> ReadLatestImportsAsync(IReadOnlyCollection<Guid> itemIds)
    {
        await using var ctx = NewContext();
        return await new PostgresDataRepository(ctx).Activity.GetLatestImportEventsForItemsAsync(itemIds);
    }

    /// <summary>
    /// The plan PostgreSQL would use for the statement <paramref name="prepare"/> builds, with sequential scans switched
    /// off for the session.
    /// </summary>
    private async Task<string> ExplainAsync(Action<NpgsqlCommand> prepare)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
            await off.ExecuteNonQueryAsync();

        await using var explain = new NpgsqlCommand { Connection = connection };
        prepare(explain);
        explain.CommandText = "EXPLAIN " + explain.CommandText;
        var plan = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            plan.Add(reader.GetString(0));
        return string.Join("\n", plan);
    }

    /// <summary>
    /// Times the first page of <paramref name="subject"/>'s timeline: the best of three reads after one to warm the
    /// connection pool and the plan, so a single scheduling hiccup on a shared runner cannot fail the test.
    /// </summary>
    private async Task<(List<CausalTimelineEntry> Page, int Total, TimeSpan Elapsed)> TimeFirstPageAsync(CausalTimelineSubject subject)
    {
        var result = await ReadTimelineAsync(subject);
        var best = TimeSpan.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var ctx = NewContext();
            var repository = new PostgresDataRepository(ctx).Activity;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            result = await repository.GetCausalTimelinePageAsync(subject, 0, 50);
            stopwatch.Stop();
            if (stopwatch.Elapsed < best)
                best = stopwatch.Elapsed;
        }

        return (result.Entries, result.TotalCount, best);
    }

    /// <summary>
    /// Seeds <paramref name="count"/> items for one Connected System Object, each in a run of its own an hour apart,
    /// as a record touched once per run accumulates them. Two statements whatever the count: the runs are copies of
    /// one seeded Activity, the items one per run.
    /// </summary>
    private async Task SeedItemsInTheirOwnRunsAsync(int connectedSystemId, Guid csoId, int count)
    {
        var template = await SeedActivityAsync(connectedSystemId, ConnectedSystemRunType.DeltaSynchronisation, T0, $"bulk {csoId:N}");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        // LATERAL, not "(jsonb_populate_record(...)).*": the latter calls the function once per column of the wide
        // Activities row, which made 10,000 copies take over 30 seconds instead of under one.
        await using var runs = new NpgsqlCommand("""
            INSERT INTO "Activities"
            SELECT copy.*
            FROM "Activities" t
            CROSS JOIN generate_series(1, @count - 1) AS g
            CROSS JOIN LATERAL jsonb_populate_record(NULL::"Activities",
                to_jsonb(t) || jsonb_build_object('Id', gen_random_uuid(), 'Executed', t."Executed" + make_interval(hours => g))) AS copy
            WHERE t."Id" = @template
            """, connection);
        runs.Parameters.AddWithValue("count", count);
        runs.Parameters.AddWithValue("template", template);
        await runs.ExecuteNonQueryAsync();

        await using var items = new NpgsqlCommand("""
            INSERT INTO "ActivityRunProfileExecutionItems" ("Id", "ActivityId", "ObjectChangeType", "ConnectedSystemObjectId", "DisplayNameSnapshot")
            SELECT gen_random_uuid(), a."Id", @change_type, @cso, 'Tina Adams'
            FROM "Activities" a
            WHERE a."TargetName" = @name
            """, connection);
        items.Parameters.AddWithValue("change_type", (int)ObjectChangeType.AttributeFlow);
        items.Parameters.AddWithValue("cso", csoId);
        items.Parameters.AddWithValue("name", $"bulk {csoId:N}");
        await items.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Seeds <paramref name="count"/> items left behind by deleted records: no record id, each its own external ID, in
    /// one run.
    /// </summary>
    private async Task SeedItemsOfDeletedRecordsAsync(int connectedSystemId, int count)
    {
        var run = await SeedActivityAsync(connectedSystemId, ConnectedSystemRunType.DeltaImport, T0.AddDays(-1));
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var items = new NpgsqlCommand("""
            INSERT INTO "ActivityRunProfileExecutionItems" ("Id", "ActivityId", "ObjectChangeType", "ExternalIdSnapshot")
            SELECT gen_random_uuid(), @run, @change_type, 'X' || g
            FROM generate_series(1, @count) AS g
            """, connection);
        items.Parameters.AddWithValue("run", run);
        items.Parameters.AddWithValue("change_type", (int)ObjectChangeType.Deleted);
        items.Parameters.AddWithValue("count", count);
        await items.ExecuteNonQueryAsync();
    }

    private async Task AnalyseAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var analyse = new NpgsqlCommand(
            """ANALYZE "Activities", "ActivityRunProfileExecutionItems", "ConnectedSystemObjectChanges" """, connection);
        await analyse.ExecuteNonQueryAsync();
    }

    private async Task<(int SystemId, int TypeId, int ExternalIdAttributeId)> SeedConnectedSystemAsync(string name)
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = $"Test Connector {Guid.NewGuid():N}", BuiltIn = true };
        var system = new ConnectedSystem { Name = name, ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = system, Selected = true };
        var idAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "id", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text, IsExternalId = true, Selected = true
        };
        csType.Attributes.Add(idAttribute);
        seed.AddRange(connectorDefinition, system, csType);
        await seed.SaveChangesAsync();
        return (system.Id, csType.Id, idAttribute.Id);
    }

    private async Task<Guid> SeedCsoAsync((int SystemId, int TypeId, int ExternalIdAttributeId) system, Guid? joinedTo = null)
    {
        await using var seed = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.SystemId,
            TypeId = system.TypeId,
            ExternalIdAttributeId = system.ExternalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow,
            MetaverseObjectId = joinedTo
        };
        seed.ConnectedSystemObjects.Add(cso);
        await seed.SaveChangesAsync();
        return cso.Id;
    }

    private async Task DeleteCsoAsync(Guid csoId)
    {
        await using var ctx = NewContext();
        await ctx.ConnectedSystemObjects.Where(c => c.Id == csoId).ExecuteDeleteAsync();
    }

    private async Task<Guid> SeedActivityAsync(int? connectedSystemId, ConnectedSystemRunType runType, DateTime executed,
        string? targetName = null, string? targetContext = null,
        ActivityTargetType targetType = ActivityTargetType.ConnectedSystemRunProfile)
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = targetType,
            TargetName = targetName ?? runType.ToString(),
            TargetContext = targetContext,
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemRunType = runType,
            Executed = executed
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        return activity.Id;
    }

    private async Task<Guid> SeedItemAsync(Guid activityId, ObjectChangeType changeType, string displayName, string? objectType,
        Guid? csoId = null, string? externalId = null, string? outcomeSummary = null, Guid? metaverseObjectId = null,
        ActivityRunProfileExecutionItemErrorType? errorType = null)
    {
        await using var ctx = NewContext();
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = changeType,
            DisplayNameSnapshot = displayName,
            ObjectTypeSnapshot = objectType,
            ConnectedSystemObjectId = csoId,
            ExternalIdSnapshot = externalId,
            OutcomeSummary = outcomeSummary,
            MetaverseObjectId = metaverseObjectId,
            ErrorType = errorType ?? ActivityRunProfileExecutionItemErrorType.NotSet
        };
        ctx.ActivityRunProfileExecutionItems.Add(item);
        await ctx.SaveChangesAsync();
        return item.Id;
    }

    private async Task<Guid> SeedMvoAsync()
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType { Name = $"User {Guid.NewGuid():N}", PluralName = "Users" };
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = type, Origin = MetaverseObjectOrigin.Projected };
        ctx.AddRange(type, mvo);
        await ctx.SaveChangesAsync();
        return mvo.Id;
    }

    private async Task ScheduleDeletionAsync(Guid mvoId, Guid scheduledByItemId)
    {
        await using var ctx = NewContext();
        await ctx.MetaverseObjects.Where(m => m.Id == mvoId).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.LastConnectorDisconnectedDate, T0)
            .SetProperty(m => m.DeletionScheduledByRunProfileExecutionItemId, scheduledByItemId));
    }

    /// <summary>
    /// A change record as the synchronisation or deletion wrote it: under the object's id while it exists, under the
    /// deleted id once its deletion has re-keyed it. Raw SQL because the object's own key is a shadow column.
    /// </summary>
    private async Task SeedMvoChangeAsync(Guid? itemId, DateTime changeTime, Guid? metaverseObjectId = null,
        Guid? deletedMetaverseObjectId = null, Guid? deletedByItemId = null)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO "MetaverseObjectChanges"
                ("Id", "ChangeTime", "InitiatedByType", "ChangeInitiatorType", "ChangeType",
                 "MetaverseObjectId", "DeletedMetaverseObjectId", "ActivityRunProfileExecutionItemId",
                 "DeletedByRunProfileExecutionItemId")
            VALUES (@id, @time, @initiated_by, @initiator, @change_type, @mvo, @deleted_mvo, @item, @deleted_by)
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("time", changeTime);
        command.Parameters.AddWithValue("initiated_by", (int)ActivityInitiatorType.System);
        command.Parameters.AddWithValue("initiator", (int)MetaverseObjectChangeInitiatorType.SynchronisationRule);
        command.Parameters.AddWithValue("change_type", (int)ObjectChangeType.AttributeFlow);
        command.Parameters.Add(new NpgsqlParameter("mvo", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)metaverseObjectId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("deleted_mvo", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)deletedMetaverseObjectId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("item", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)itemId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("deleted_by", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)deletedByItemId ?? DBNull.Value });
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> SeedPendingExportAsync(int connectedSystemId, params Guid[] stagers)
    {
        await using var ctx = NewContext();
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ChangeType = PendingExportChangeType.Update,
            Status = PendingExportStatus.Pending,
            CreatedAt = T0
        };
        foreach (var stager in stagers)
            pendingExport.RecordStager(stager);
        ctx.PendingExports.Add(pendingExport);
        await ctx.SaveChangesAsync();
        return pendingExport.Id;
    }

    private async Task SeedCsoDeletionRecordAsync(int connectedSystemId, Guid deletedCsoId, string? externalId, Guid? itemId, DateTime changeTime)
    {
        await using var ctx = NewContext();
        ctx.ConnectedSystemObjectChanges.Add(new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ChangeType = ObjectChangeType.Deleted,
            ChangeTime = changeTime,
            InitiatedByType = ActivityInitiatorType.System,
            ActivityRunProfileExecutionItemId = itemId,
            DeletedConnectedSystemObjectId = deletedCsoId,
            DeletedObjectExternalId = externalId,
            DeletedObjectDisplayName = "Tina Adams"
        });
        await ctx.SaveChangesAsync();
    }
}

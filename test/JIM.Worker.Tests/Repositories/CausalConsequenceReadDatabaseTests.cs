// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the downward causal walk's reads (#1811). Each level is one raw SQL statement
/// that finds every hop out of the frontier and groups the results, so the shape of the SQL is the feature: which
/// hop is taken, what splits a cohort, what is counted exactly and what is capped, and which outstanding work is
/// read live. None of it can run on the in-memory provider.
///
/// Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class CausalConsequenceReadDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL causal consequence read tests.");

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

    // ─── Edge hops ───

    [Test]
    public async Task GetCausalConsequenceLevelAsync_EdgesFromTheFrontier_GroupIntoCohortsCountedExactlyAsync()
    {
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var housekeeping = await SeedActivityAsync(null, ConnectedSystemRunType.NotSet, T0);
        var deletion = await SeedItemAsync(housekeeping, ObjectChangeType.Deleted, "Tina Adams", "User");
        var removals = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var removal = await SeedItemAsync(housekeeping, ObjectChangeType.PendingExport, $"Project {i}", "Group");
            await SeedEdgeAsync(removal, CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval, causeItemId: deletion,
                reasonCode: CausalReasonCode.AuthoritativeSourceDisconnected, connectedSystemId: hr.SystemId,
                connectedSystemName: "Yellowstone APAC", attributeName: "Static Members", created: T0.AddSeconds(i));
            removals.Add(removal);
        }

        var level = await ReadLevelAsync([new(deletion, 0)], memberPageSize: 2, continuingItemLimit: 2);

        var cohort = level.Cohorts.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cohort.Tuple.HopKind, Is.EqualTo(CausalConsequenceHopKind.Edge));
            Assert.That(cohort.Tuple.EdgeType, Is.EqualTo(CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval));
            Assert.That(cohort.Tuple.ReasonCode, Is.EqualTo(CausalReasonCode.AuthoritativeSourceDisconnected));
            Assert.That(cohort.Tuple.ConnectedSystemId, Is.EqualTo(hr.SystemId));
            Assert.That(cohort.Tuple.ObjectTypeName, Is.EqualTo("Group"));
            Assert.That(cohort.Tuple.AttributeName, Is.EqualTo("Static Members"));
            Assert.That(cohort.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(cohort.ParentCohortOrdinal, Is.Zero);
            Assert.That(cohort.MemberCount, Is.EqualTo(3), "the count is exact whatever the page size");
            Assert.That(cohort.Members.Select(m => m.RunProfileExecutionItemId), Is.EqualTo(removals.Take(2)),
                "the first page, earliest first and then by name");
            Assert.That(cohort.ContinuingMemberCount, Is.EqualTo(3));
            Assert.That(cohort.ContinuingItemIds, Has.Count.EqualTo(2), "continuing items are capped, not the count");
            Assert.That(level.Leaves, Is.Empty, "the deletion led somewhere");
        }
    }

    [Test]
    public async Task GetCausalConsequenceLevelAsync_DifferingOutcomeSummaries_SplitIntoSeparateCohortsAsync()
    {
        var sync = await SeedActivityAsync(null, ConnectedSystemRunType.NotSet, T0);
        var cause = await SeedItemAsync(sync, ObjectChangeType.AttributeFlow, "Tina Adams", "User");
        var target = await SeedConnectedSystemAsync("Glitterband EMEA");
        var export = await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.Export, T0.AddHours(1));
        for (var i = 0; i < 3; i++)
        {
            var exported = await SeedItemAsync(export, ObjectChangeType.Exported, $"Group {i}", "group", outcomeSummary: "Exported:1");
            await SeedEdgeAsync(exported, CausalEdgeType.PendingExportQueueingCausedExportExecution, causeItemId: cause,
                pendingExportId: Guid.NewGuid(), reasonCode: CausalReasonCode.ExportUpdateStaged, connectedSystemId: target.SystemId);
        }
        var failed = await SeedItemAsync(export, ObjectChangeType.Exported, "Group 9", "group", outcomeSummary: "ExportFailed:1");
        await SeedEdgeAsync(failed, CausalEdgeType.PendingExportQueueingCausedExportExecution, causeItemId: cause,
            pendingExportId: Guid.NewGuid(), reasonCode: CausalReasonCode.ExportUpdateStaged, connectedSystemId: target.SystemId);

        var level = await ReadLevelAsync([new(cause, 0)]);

        Assert.That(level.Cohorts.Select(c => (c.Tuple.OutcomeSummary, c.MemberCount)),
            Is.EquivalentTo(new[] { ("Exported:1", 3), ("ExportFailed:1", 1) }));
    }

    [Test]
    public async Task GetCausalConsequenceLevelAsync_FrontierItemsFromDifferentParents_GroupUnderTheirOwnParentAsync()
    {
        var housekeeping = await SeedActivityAsync(null, ConnectedSystemRunType.NotSet, T0);
        var first = await SeedItemAsync(housekeeping, ObjectChangeType.Deleted, "Tina Adams", "User");
        var second = await SeedItemAsync(housekeeping, ObjectChangeType.Deleted, "Mia Young", "User");
        foreach (var cause in new[] { first, second })
        {
            var removal = await SeedItemAsync(housekeeping, ObjectChangeType.PendingExport, "Project Pulse", "Group");
            await SeedEdgeAsync(removal, CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval, causeItemId: cause);
        }

        var level = await ReadLevelAsync([new(first, 4), new(second, 7)]);

        Assert.That(level.Cohorts.Select(c => (c.ParentCohortOrdinal, c.MemberCount)),
            Is.EquivalentTo(new[] { (4, 1), (7, 1) }), "the same values under two parents are two cohorts");
        Assert.That(level.Cohorts.Select(c => c.Ordinal).Distinct().Count(), Is.EqualTo(2));
    }

    // ─── Confirmation hop ───

    [Test]
    public async Task GetCausalConsequenceLevelAsync_ExportFrontierItem_ReachesItsConfirmingImportAndEndsThereAsync()
    {
        var target = await SeedConnectedSystemAsync("Glitterband EMEA");
        var sync = await SeedActivityAsync(null, ConnectedSystemRunType.NotSet, T0);
        var cause = await SeedItemAsync(sync, ObjectChangeType.AttributeFlow, "Tina Adams", "User");
        var export = await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.Export, T0.AddHours(1));
        var exported = await SeedItemAsync(export, ObjectChangeType.Exported, "Tina Adams", "user");
        var pendingExportId = Guid.NewGuid();
        await SeedEdgeAsync(exported, CausalEdgeType.PendingExportQueueingCausedExportExecution, causeItemId: cause,
            pendingExportId: pendingExportId);
        var import = await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.FullImport, T0.AddHours(2));
        var confirming = await SeedItemAsync(import, ObjectChangeType.Updated, "Tina Adams", "user");
        await SeedEdgeAsync(confirming, CausalEdgeType.ExportCausedImportConfirmation, pendingExportId: pendingExportId,
            connectedSystemId: target.SystemId, connectedSystemName: "Glitterband EMEA");

        var level = await ReadLevelAsync([new(exported, 0)]);

        var cohort = level.Cohorts.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cohort.Tuple.HopKind, Is.EqualTo(CausalConsequenceHopKind.Confirmation));
            Assert.That(cohort.Members.Single().RunProfileExecutionItemId, Is.EqualTo(confirming));
            Assert.That(cohort.ContinuingItemIds, Is.Empty, "a confirmation ends its branch");
            Assert.That(cohort.ContinuingMemberCount, Is.Zero);
        }
    }

    // ─── Next synchronisation hop ───

    [Test]
    public async Task GetCausalConsequenceLevelAsync_ImportFrontierItem_ReachesTheFirstSynchronisationAfterItAsync()
    {
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var csoId = await SeedCsoAsync(hr);
        var earlierSync = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddMinutes(-30));
        await SeedItemAsync(earlierSync, ObjectChangeType.AttributeFlow, "Tina Adams", "person", csoId: csoId);
        var importActivity = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0);
        var import = await SeedItemAsync(importActivity, ObjectChangeType.Updated, "Tina Adams", "person", csoId: csoId);
        var nextSyncActivity = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddMinutes(5));
        var nextSync = await SeedItemAsync(nextSyncActivity, ObjectChangeType.AttributeFlow, "Tina Adams", "person", csoId: csoId);
        var laterSync = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.FullSynchronisation, T0.AddHours(1));
        await SeedItemAsync(laterSync, ObjectChangeType.AttributeFlow, "Tina Adams", "person", csoId: csoId);

        var level = await ReadLevelAsync([new(import, 0)]);

        var cohort = level.Cohorts.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cohort.Tuple.HopKind, Is.EqualTo(CausalConsequenceHopKind.NextSynchronisation));
            Assert.That(cohort.Tuple.ConnectedSystemId, Is.EqualTo(hr.SystemId));
            Assert.That(cohort.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(cohort.Members.Single().RunProfileExecutionItemId, Is.EqualTo(nextSync),
                "the first synchronisation after the import, not an earlier or a later one");
            Assert.That(cohort.ContinuingItemIds, Is.EqualTo(new[] { nextSync }));
        }
    }

    [Test]
    public async Task GetCausalConsequenceLevelAsync_ImportWhoseRecordWasDeleted_ReachesTheSynchronisationByExternalIdAsync()
    {
        // A deletion nulls the Connected System Object id on every item that processed it; the external ID
        // snapshotted on each item survives, as it does for Phase 1's upward hop.
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var importActivity = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0);
        var import = await SeedItemAsync(importActivity, ObjectChangeType.Deleted, "Tina Adams", "person", externalId: "S8-99");
        var syncActivity = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddMinutes(5));
        var sync = await SeedItemAsync(syncActivity, ObjectChangeType.Disconnected, "Tina Adams", "person", externalId: "S8-99");

        var level = await ReadLevelAsync([new(import, 0)]);

        Assert.That(level.Cohorts.Single().Members.Single().RunProfileExecutionItemId, Is.EqualTo(sync));
    }

    // ─── Leaves ───

    [Test]
    public async Task GetCausalConsequenceLevelAsync_ImportNotYetSynchronised_EndsWaitingForThatSystemAsync()
    {
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var csoId = await SeedCsoAsync(hr);
        var importActivity = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0);
        var import = await SeedItemAsync(importActivity, ObjectChangeType.Added, "Tina Adams", "person", csoId: csoId);

        var level = await ReadLevelAsync([new(import, 3)]);

        var leaf = level.Leaves.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(level.Cohorts, Is.Empty);
            Assert.That(leaf.Ending, Is.EqualTo(CausalConsequenceEnding.WaitingForSynchronisation));
            Assert.That(leaf.ParentCohortOrdinal, Is.EqualTo(3));
            Assert.That(leaf.ConnectedSystemId, Is.EqualTo(hr.SystemId));
            Assert.That(leaf.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(leaf.MemberCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GetCausalConsequenceLevelAsync_ImportSynchronisedSinceWithNothingRecorded_EndsAsNothingFollowedAsync()
    {
        var hr = await SeedConnectedSystemAsync("Yellowstone APAC");
        var csoId = await SeedCsoAsync(hr);
        var importActivity = await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaImport, T0);
        var import = await SeedItemAsync(importActivity, ObjectChangeType.Updated, "Tina Adams", "person", csoId: csoId);
        await SeedActivityAsync(hr.SystemId, ConnectedSystemRunType.DeltaSynchronisation, T0.AddMinutes(5));

        var level = await ReadLevelAsync([new(import, 0)]);

        Assert.That(level.Leaves.Single().Ending, Is.EqualTo(CausalConsequenceEnding.NothingFollowed));
    }

    [Test]
    public async Task GetCausalConsequenceLevelAsync_ImportThatOnlyConfirmedAnExport_CausesNothingAsync()
    {
        // A confirming import found what JIM exported, so there is nothing for a Synchronisation to act on: it ends with
        // no further consequences rather than waiting on, or being "settled" by, a run that can never follow from it.
        var glitterband = await SeedConnectedSystemAsync("Glitterband EMEA");
        var csoId = await SeedCsoAsync(glitterband);
        var importActivity = await SeedActivityAsync(glitterband.SystemId, ConnectedSystemRunType.DeltaImport, T0);
        var confirming = await SeedItemAsync(importActivity, ObjectChangeType.Updated, "Project-Pulse", "group", csoId: csoId);
        await SeedOutcomeAsync(confirming, ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed, csoId);
        var changed = await SeedItemAsync(importActivity, ObjectChangeType.Updated, "Project-Atlas", "group", csoId: await SeedCsoAsync(glitterband));
        await SeedOutcomeAsync(changed, ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed, csoId);
        await SeedOutcomeAsync(changed, ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, csoId);

        var level = await ReadLevelAsync([new(confirming, 0), new(changed, 1)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(level.Leaves.Single(l => l.ParentCohortOrdinal == 0).Ending, Is.EqualTo(CausalConsequenceEnding.NoFurtherConsequences));
            Assert.That(level.Leaves.Single(l => l.ParentCohortOrdinal == 1).Ending, Is.EqualTo(CausalConsequenceEnding.WaitingForSynchronisation),
                "an import that also brought a change of its own is still one a Synchronisation acts on");
        }
    }

    [Test]
    public async Task GetCausalConsequenceLevelAsync_ItemWithNothingRecordedAfterIt_EndsWithNoFurtherConsequencesAsync()
    {
        var sync = await SeedActivityAsync(null, ConnectedSystemRunType.NotSet, T0);
        var item = await SeedItemAsync(sync, ObjectChangeType.AttributeFlow, "Tina Adams", "User");

        var level = await ReadLevelAsync([new(item, 0)]);

        Assert.That(level.Leaves.Single().Ending, Is.EqualTo(CausalConsequenceEnding.NoFurtherConsequences));
    }

    // ─── Outstanding work, read live ───

    [Test]
    public async Task GetCausalConsequenceLevelAsync_StagedPendingExportsStillQueued_AreOutstandingByLiveStatusAsync()
    {
        var target = await SeedConnectedSystemAsync("Glitterband EMEA");
        var sync = await SeedActivityAsync(null, ConnectedSystemRunType.NotSet, T0);
        var item = await SeedItemAsync(sync, ObjectChangeType.AttributeFlow, "Tina Adams", "User");
        var waiting = await SeedPendingExportAsync(target.SystemId, PendingExportStatus.Pending);
        var retryAt = T0.AddHours(3);
        var retrying = await SeedPendingExportAsync(target.SystemId, PendingExportStatus.ExportNotConfirmed, retryAt);
        var executed = await SeedPendingExportAsync(target.SystemId, PendingExportStatus.Exported);
        foreach (var pendingExportId in new[] { waiting, retrying, executed })
            await SeedOutcomeAsync(item, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, pendingExportId);
        // The third has already been carried out by an export run, which is the real consequence; it must not
        // also read as outstanding from the item that staged it.
        var export = await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.Export, T0.AddHours(1));
        var exportItem = await SeedItemAsync(export, ObjectChangeType.Exported, "Tina Adams", "user");
        await SeedEdgeAsync(exportItem, CausalEdgeType.PendingExportQueueingCausedExportExecution, causeItemId: item,
            pendingExportId: executed);

        var level = await ReadLevelAsync([new(item, 0)]);

        var outstanding = level.Cohorts.Where(c => c.Tuple.HopKind == CausalConsequenceHopKind.OutstandingPendingExport).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outstanding.Select(c => c.Tuple.PendingExportStatus),
                Is.EquivalentTo(new[] { PendingExportStatus.Pending, PendingExportStatus.ExportNotConfirmed }));
            Assert.That(outstanding.All(c => c.Tuple.ConnectedSystemId == target.SystemId), Is.True);
            Assert.That(outstanding.Single(c => c.Tuple.PendingExportStatus == PendingExportStatus.ExportNotConfirmed).DueAt,
                Is.EqualTo(retryAt));
            Assert.That(outstanding.SelectMany(c => c.Members).Select(m => m.PendingExportId),
                Is.EquivalentTo(new[] { waiting, retrying }));
            Assert.That(outstanding.All(c => c.ContinuingItemIds.Count == 0), Is.True);
            Assert.That(level.Cohorts.Count(c => c.Tuple.HopKind == CausalConsequenceHopKind.Edge), Is.EqualTo(1),
                "the executed one is the export run");
        }
    }

    [Test]
    public async Task GetCausalConsequenceLevelAsync_ExportAwaitingConfirmation_IsOutstandingAsExportedAsync()
    {
        var target = await SeedConnectedSystemAsync("Glitterband EMEA");
        var export = await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.Export, T0);
        var exportItem = await SeedItemAsync(export, ObjectChangeType.Exported, "Tina Adams", "user");
        var pendingExportId = await SeedPendingExportAsync(target.SystemId, PendingExportStatus.Exported);
        await SeedEdgeAsync(exportItem, CausalEdgeType.PendingExportQueueingCausedExportExecution, pendingExportId: pendingExportId);

        var level = await ReadLevelAsync([new(exportItem, 0)]);

        var cohort = level.Cohorts.Single();
        Assert.That(cohort.Tuple.HopKind, Is.EqualTo(CausalConsequenceHopKind.OutstandingPendingExport));
        Assert.That(cohort.Tuple.PendingExportStatus, Is.EqualTo(PendingExportStatus.Exported));
    }

    [Test]
    public async Task GetCausalConsequenceLevelAsync_DeletionStillScheduledByTheItem_IsOutstandingWithItsDueDateAsync()
    {
        var sync = await SeedActivityAsync(null, ConnectedSystemRunType.NotSet, T0);
        var item = await SeedItemAsync(sync, ObjectChangeType.Disconnected, "Tina Adams", "User");
        var due = T0.AddDays(7);
        await SetDeletionPolicySnapshotAsync(item, new MvoDeletionPolicySnapshot { DeletionEligibleDate = due }.ToJson());
        var mvoId = await SeedMvoAsync(scheduledByItemId: item);
        await SeedOutcomeAsync(item, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled, mvoId);

        var level = await ReadLevelAsync([new(item, 0)]);

        var cohort = level.Cohorts.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cohort.Tuple.HopKind, Is.EqualTo(CausalConsequenceHopKind.ScheduledDeletion));
            Assert.That(cohort.Members.Single().MetaverseObjectId, Is.EqualTo(mvoId));
            Assert.That(cohort.DueAt, Is.EqualTo(due));
        }
    }

    [Test]
    public async Task GetCausalConsequenceLevelAsync_DeletionScheduledButSinceCancelled_IsNotOutstandingAsync()
    {
        var sync = await SeedActivityAsync(null, ConnectedSystemRunType.NotSet, T0);
        var item = await SeedItemAsync(sync, ObjectChangeType.Disconnected, "Tina Adams", "User");
        var mvoId = await SeedMvoAsync(scheduledByItemId: null);
        await SeedOutcomeAsync(item, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled, mvoId);

        var level = await ReadLevelAsync([new(item, 0)]);

        Assert.That(level.Cohorts, Is.Empty);
        Assert.That(level.Leaves.Single().Ending, Is.EqualTo(CausalConsequenceEnding.NoFurtherConsequences));
    }

    // ─── Member paging ───

    [Test]
    public async Task GetCausalConsequenceCohortMembersAsync_OneCohortsMembers_PagesAndFiltersByNameAsync()
    {
        var housekeeping = await SeedActivityAsync(null, ConnectedSystemRunType.NotSet, T0);
        var deletion = await SeedItemAsync(housekeeping, ObjectChangeType.Deleted, "Tina Adams", "User");
        var removals = new List<Guid>();
        foreach (var name in new[] { "Project Pulse", "Project Diamond", "Team Alpha" })
        {
            var removal = await SeedItemAsync(housekeeping, ObjectChangeType.PendingExport, name, "Group");
            await SeedEdgeAsync(removal, CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval, causeItemId: deletion,
                attributeName: "Static Members", created: T0.AddSeconds(removals.Count));
            removals.Add(removal);
        }
        // A removal through a different attribute is a different cohort, and must not page in with these.
        var other = await SeedItemAsync(housekeeping, ObjectChangeType.PendingExport, "Project Zebra", "Group");
        await SeedEdgeAsync(other, CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval, causeItemId: deletion,
            attributeName: "Owners");
        var tuple = (await ReadLevelAsync([new(deletion, 0)])).Cohorts.Single(c => c.Tuple.AttributeName == "Static Members").Tuple;

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var page = await repository.Activity.GetCausalConsequenceCohortMembersAsync([deletion], tuple, skip: 1, take: 1, search: null);
        var found = await repository.Activity.GetCausalConsequenceCohortMembersAsync([deletion], tuple, skip: 0, take: 10, search: "project");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.TotalCount, Is.EqualTo(3));
            Assert.That(page.Members.Select(m => m.RunProfileExecutionItemId), Is.EqualTo(new[] { removals[0] }),
                "one run's members share its time, so they page by name: Project Diamond, Project Pulse, Team Alpha");
            Assert.That(found.TotalCount, Is.EqualTo(2), "the find box narrows within the cohort, case-insensitively");
            Assert.That(found.Members.Select(m => m.DisplayName), Is.EqualTo(new[] { "Project Diamond", "Project Pulse" }));
        }
    }

    // ─── The walk over real reads ───

    [Test]
    public async Task GetCausalConsequencesAsync_DeletionCascade_WalksToTheConfirmationAndPagesANestedCohortAsync()
    {
        // The application walk over the real level reads: a deletion removes a reference from two Groups, each removal
        // is exported, one export is confirmed and the other is still awaiting its confirming import.
        var target = await SeedConnectedSystemAsync("Glitterband EMEA");
        var housekeeping = await SeedActivityAsync(null, ConnectedSystemRunType.NotSet, T0);
        var deletion = await SeedItemAsync(housekeeping, ObjectChangeType.Deleted, "Tina Adams", "User");
        var export = await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.Export, T0.AddHours(1));
        var import = await SeedActivityAsync(target.SystemId, ConnectedSystemRunType.FullImport, T0.AddHours(2));
        var awaitingId = await SeedPendingExportAsync(target.SystemId, PendingExportStatus.Exported);
        foreach (var (name, confirmed) in new[] { ("Project Diamond", true), ("Project Pulse", false) })
        {
            var removal = await SeedItemAsync(housekeeping, ObjectChangeType.PendingExport, name, "Group");
            await SeedEdgeAsync(removal, CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval, causeItemId: deletion,
                attributeName: "Static Members");
            var exported = await SeedItemAsync(export, ObjectChangeType.Exported, name, "group", outcomeSummary: "Exported:1");
            var pendingExportId = confirmed ? Guid.NewGuid() : awaitingId;
            await SeedEdgeAsync(exported, CausalEdgeType.PendingExportQueueingCausedExportExecution, causeItemId: removal,
                pendingExportId: pendingExportId, connectedSystemId: target.SystemId);
            if (!confirmed)
                continue;
            var confirming = await SeedItemAsync(import, ObjectChangeType.Updated, name, "group");
            await SeedEdgeAsync(confirming, CausalEdgeType.ExportCausedImportConfirmation, pendingExportId: pendingExportId,
                connectedSystemId: target.SystemId);
        }

        await using var ctx = NewContext();
        using var jim = new JIM.Application.JimApplication(new PostgresDataRepository(ctx));
        var consequences = await jim.Activities.GetCausalConsequencesAsync(deletion);

        var removals = consequences.Cohorts.Single();
        var exports = removals.Consequences.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(removals.MemberCount, Is.EqualTo(2));
            Assert.That(exports.MemberCount, Is.EqualTo(2));
            Assert.That(exports.Consequences.Select(c => (c.HopKind, c.Ending)), Is.EquivalentTo(new (CausalConsequenceHopKind, CausalConsequenceEnding?)[]
            {
                (CausalConsequenceHopKind.Confirmation, CausalConsequenceEnding.Confirmed),
                (CausalConsequenceHopKind.OutstandingPendingExport, CausalConsequenceEnding.WaitingForConfirmingImport)
            }));
            Assert.That(consequences.HasWaitingBranches, Is.True);
        }

        var page = await jim.Activities.GetCausalConsequenceCohortMembersAsync(deletion, exports.CohortKey, page: 2, pageSize: 1);
        Assert.That(page!.TotalResults, Is.EqualTo(2));
        Assert.That(page!.Results.Single().DisplayName, Is.EqualTo("Project Pulse"));
    }

    // ─── Representatives ───

    [Test]
    public async Task GetCausalConsequenceRepresentativesAsync_ItemsByIdWithTheirOutcomesAndActivityAsync()
    {
        // The portal places each cohort's cards from one item's outcomes and names the run from its Activity, so both
        // must arrive with the item; a purged item is simply absent rather than an error.
        var glitterband = await SeedConnectedSystemAsync("Glitterband EMEA");
        var sync = await SeedActivityAsync(glitterband.SystemId, ConnectedSystemRunType.FullSynchronisation, T0);
        var item = await SeedItemAsync(sync, ObjectChangeType.Disconnected, "Tina Adams", "person");
        await SeedOutcomeAsync(item, ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected, Guid.NewGuid());
        await SeedOutcomeAsync(item, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted, Guid.NewGuid());
        var purged = Guid.NewGuid();

        await using var ctx = NewContext();
        var representatives = await new PostgresDataRepository(ctx).Activity.GetCausalConsequenceRepresentativesAsync([item, purged]);

        var representative = representatives[item];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(representatives.Keys, Is.EquivalentTo(new[] { item }));
            Assert.That(representative.SyncOutcomes.Select(o => o.OutcomeType), Is.EquivalentTo(new[]
            {
                ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
            }));
            Assert.That(representative.Activity.ConnectedSystemRunType, Is.EqualTo(ConnectedSystemRunType.FullSynchronisation));
            Assert.That(representative.Activity.ConnectedSystemId, Is.EqualTo(glitterband.SystemId));
        }
    }

    // ─── Index ───

    [Test]
    public async Task CausalEdges_LookupByCausePendingExportId_UsesItsIndexAsync()
    {
        // The confirmation hop looks edges up by the Pending Export they name. Without the index Phase 1's upward
        // read scanned the table too; with it the planner can seek. Sequential scans are switched off so a tiny
        // test table cannot make the planner prefer one regardless.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
            await off.ExecuteNonQueryAsync();
        await using var explain = new NpgsqlCommand(
            @"EXPLAIN SELECT ""Id"" FROM ""CausalEdges"" WHERE ""CausePendingExportId"" = ANY(@ids)", connection);
        explain.Parameters.AddWithValue("ids", new[] { Guid.NewGuid() });
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
        }

        Assert.That(string.Join("\n", plan), Does.Contain("IX_CausalEdges_CausePendingExportId"));
    }

    // ─── Helpers ───

    private async Task<CausalConsequenceLevel> ReadLevelAsync(
        IReadOnlyList<CausalConsequenceFrontierMember> frontier, int memberPageSize = 50, int continuingItemLimit = 10_000)
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        return await repository.Activity.GetCausalConsequenceLevelAsync(frontier, memberPageSize, continuingItemLimit);
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

    private async Task<Guid> SeedCsoAsync((int SystemId, int TypeId, int ExternalIdAttributeId) system)
    {
        await using var seed = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.SystemId,
            TypeId = system.TypeId,
            ExternalIdAttributeId = system.ExternalIdAttributeId,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow
        };
        seed.ConnectedSystemObjects.Add(cso);
        await seed.SaveChangesAsync();
        return cso.Id;
    }

    private async Task<Guid> SeedActivityAsync(int? connectedSystemId, ConnectedSystemRunType runType, DateTime executed)
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = runType.ToString(),
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

    private async Task<Guid> SeedItemAsync(Guid activityId, ObjectChangeType changeType, string displayName, string objectType,
        Guid? csoId = null, string? externalId = null, string? outcomeSummary = null)
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
            OutcomeSummary = outcomeSummary
        };
        ctx.ActivityRunProfileExecutionItems.Add(item);
        await ctx.SaveChangesAsync();
        return item.Id;
    }

    private async Task SetDeletionPolicySnapshotAsync(Guid itemId, string snapshotJson)
    {
        await using var ctx = NewContext();
        var item = await ctx.ActivityRunProfileExecutionItems.AsTracking().SingleAsync(i => i.Id == itemId);
        item.DeletionPolicySnapshotJson = snapshotJson;
        await ctx.SaveChangesAsync();
    }

    private async Task SeedEdgeAsync(Guid effectItemId, CausalEdgeType edgeType, Guid? causeItemId = null, Guid? pendingExportId = null,
        CausalReasonCode reasonCode = CausalReasonCode.NotSet, int? connectedSystemId = null, string? connectedSystemName = null,
        string? attributeName = null, DateTime? created = null)
    {
        await using var ctx = NewContext();
        ctx.CausalEdges.Add(new CausalEdge
        {
            Id = Guid.NewGuid(),
            EffectRunProfileExecutionItemId = effectItemId,
            CauseRunProfileExecutionItemId = causeItemId,
            CausePendingExportId = pendingExportId,
            EdgeType = edgeType,
            ReasonCode = reasonCode,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemName = connectedSystemName,
            EffectAttributeName = attributeName,
            Created = created ?? T0
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedOutcomeAsync(Guid itemId, ActivityRunProfileExecutionItemSyncOutcomeType outcomeType, Guid targetEntityId)
    {
        await using var ctx = NewContext();
        ctx.ActivityRunProfileExecutionItemSyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            ActivityRunProfileExecutionItemId = itemId,
            OutcomeType = outcomeType,
            TargetEntityId = targetEntityId
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<Guid> SeedPendingExportAsync(int connectedSystemId, PendingExportStatus status, DateTime? nextRetryAt = null)
    {
        await using var ctx = NewContext();
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ChangeType = PendingExportChangeType.Update,
            Status = status,
            NextRetryAt = nextRetryAt,
            CreatedAt = T0.AddMinutes(1)
        };
        ctx.PendingExports.Add(pendingExport);
        await ctx.SaveChangesAsync();
        return pendingExport.Id;
    }

    private async Task<Guid> SeedMvoAsync(Guid? scheduledByItemId)
    {
        await using var ctx = NewContext();
        var type = new MetaverseObjectType { Name = $"User {Guid.NewGuid():N}", PluralName = "Users" };
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = type,
            Origin = MetaverseObjectOrigin.Projected,
            LastConnectorDisconnectedDate = scheduledByItemId.HasValue ? T0 : null,
            DeletionScheduledByRunProfileExecutionItemId = scheduledByItemId
        };
        ctx.AddRange(type, mvo);
        await ctx.SaveChangesAsync();
        return mvo.Id;
    }
}

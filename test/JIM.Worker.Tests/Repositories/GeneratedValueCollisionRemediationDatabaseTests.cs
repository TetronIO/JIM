// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL cover for Collision Remediation's writes (Unique Value Generation, #242, release 4, PR 8b):
/// <see cref="SyncRepository.ApplyGeneratedValueRevisionAsync"/> in both modes (the compare-and-swap, the provenance,
/// the change record, the <see cref="RetiredGeneratedValueReason.Regenerated"/> retirement, the assignment and the
/// revision-pending record, all in one transaction, and all rolled back together when the swap or the assignment's
/// unique index refuses), the revision drain's reads and deletes, the release of Parked exports, and the
/// revision-pending table's behaviour on every deletion path that could reach it.
/// <para>
/// Only a real provider can see any of this: the revision is hand-written SQL inside an explicit transaction, the
/// <c>ValueTaken</c> exit depends on PostgreSQL's unique-violation code, and the deletion paths depend on real
/// foreign-key cascades. The in-memory provider runs none of it.
/// </para>
/// <para>
/// Opt-in via the <c>JIM_TEST_RESET_*</c> environment variables, as every <c>RequiresPostgres</c> fixture is.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class GeneratedValueCollisionRemediationDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Collision Remediation tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    private static SyncRepository NewSyncRepository(JimDbContext ctx) => new(new PostgresDataRepository(ctx));

    // ---- Import mode ----

    [Test]
    public async Task ApplyGeneratedValueRevisionAsync_ImportMode_WritesEveryPartInOneTransactionAsync()
    {
        var estate = await SeedEstateAsync();
        var assignmentId = await AddImportAssignmentAsync(estate, "jbloggs");
        var executionItemId = Guid.NewGuid();
        var revision = await ImportRevisionAsync(estate, assignmentId, "jbloggs", "jbloggs2", executionItemId, withChange: true);

        GeneratedValueRevisionResult result;
        await using (var ctx = NewContext())
            result = await NewSyncRepository(ctx).ApplyGeneratedValueRevisionAsync(revision);

        await using var check = NewContext();
        var value = await MetaverseValueAsync(check, estate);
        var assignment = await check.GeneratedValueAssignments.SingleAsync(a => a.Id == assignmentId);
        var retired = await check.RetiredGeneratedValues.SingleAsync(r => r.MetaverseAttributeId == estate.MvAttributeId);
        var pending = await check.GeneratedValueRevisionsPending.SingleAsync();
        var change = await check.MetaverseObjectChanges
            .Include(c => c.AttributeChanges).ThenInclude(a => a.ValueChanges)
            .SingleAsync(c => c.MetaverseObject!.Id == estate.MvoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(GeneratedValueRevisionResult.Applied));

            Assert.That(value.StringValue, Is.EqualTo("jbloggs2"), "the compare-and-swap wrote the new value");
            Assert.That(value.ContributedBySyncRuleId, Is.EqualTo(estate.ImportRuleId), "the generated mapping's provenance is written with it");
            Assert.That(value.ContributedBySystemId, Is.EqualTo(estate.ConnectedSystemId));

            Assert.That(assignment.Value, Is.EqualTo("jbloggs2"));
            Assert.That(assignment.NormalisedValue, Is.EqualTo("jbloggs2"));
            Assert.That(assignment.PreviousValue, Is.EqualTo("jbloggs"));
            Assert.That(assignment.State, Is.EqualTo(GeneratedValueAssignmentState.Remediated));
            Assert.That(assignment.RemediationCount, Is.EqualTo(1));
            Assert.That(assignment.RejectedByConnectedSystemId, Is.EqualTo(estate.ConnectedSystemId));
            Assert.That(assignment.RemediatedByActivityRunProfileExecutionItemId, Is.EqualTo(executionItemId));
            Assert.That(assignment.BaseValue, Is.EqualTo("jbloggs"));

            Assert.That(retired.Value, Is.EqualTo("jbloggs"), "the refused value is retired, read before the assignment is overwritten");
            Assert.That(retired.Reason, Is.EqualTo(RetiredGeneratedValueReason.Regenerated));
            Assert.That(retired.FromObjectId, Is.EqualTo(estate.MvoId));

            Assert.That(pending.MetaverseObjectId, Is.EqualTo(estate.MvoId));
            Assert.That(pending.MetaverseAttributeId, Is.EqualTo(estate.MvAttributeId));
            Assert.That(pending.RemediatingActivityRunProfileExecutionItemId, Is.EqualTo(executionItemId));
            Assert.That(pending.ReasonCode, Is.EqualTo(CausalReasonCode.GeneratedValueAlreadyInUse));
            Assert.That(pending.RejectedByConnectedSystemId, Is.EqualTo(estate.ConnectedSystemId));
            Assert.That(pending.RejectedByConnectedSystemName, Is.EqualTo(estate.ConnectedSystemName));

            Assert.That(change.InitiatedByName, Is.EqualTo("Collision Remediation"));
            Assert.That(change.ChangeType, Is.EqualTo(ObjectChangeType.Updated));
            var valueChanges = change.AttributeChanges.Single(a => a.Attribute?.Id == estate.MvAttributeId || a.AttributeName == estate.MvAttributeName).ValueChanges;
            Assert.That(valueChanges.Single(v => v.ValueChangeType == ValueChangeType.Remove).StringValue, Is.EqualTo("jbloggs"));
            Assert.That(valueChanges.Single(v => v.ValueChangeType == ValueChangeType.Add).StringValue, Is.EqualTo("jbloggs2"));
        }
    }

    [Test]
    public async Task ApplyGeneratedValueRevisionAsync_ImportMode_ValueChangedUnderneath_RollsEverythingBackAsync()
    {
        var estate = await SeedEstateAsync();
        var assignmentId = await AddImportAssignmentAsync(estate, "jbloggs");
        var revision = await ImportRevisionAsync(estate, assignmentId, "jbloggs", "jbloggs2", Guid.NewGuid(), withChange: true);

        // Something else changed the value after the export run read it: the optimistic concurrency check must refuse.
        await using (var ctx = NewContext())
            await ctx.Database.ExecuteSqlRawAsync(
                @"UPDATE ""MetaverseObjectAttributeValues"" SET ""StringValue"" = 'joe.bloggs' WHERE ""MetaverseObjectId"" = {0} AND ""AttributeId"" = {1}",
                estate.MvoId, estate.MvAttributeId);

        GeneratedValueRevisionResult result;
        await using (var ctx = NewContext())
            result = await NewSyncRepository(ctx).ApplyGeneratedValueRevisionAsync(revision);

        await AssertNothingWrittenAsync(estate, assignmentId, result, GeneratedValueRevisionResult.ValueChanged, expectedMetaverseValue: "joe.bloggs");
    }

    [Test]
    public async Task ApplyGeneratedValueRevisionAsync_ImportMode_NewValueClaimedByAnotherAssignment_RollsEverythingBackAsync()
    {
        var estate = await SeedEstateAsync();
        var assignmentId = await AddImportAssignmentAsync(estate, "jbloggs");
        var revision = await ImportRevisionAsync(estate, assignmentId, "jbloggs", "jbloggs2", Guid.NewGuid(), withChange: true);

        // Another object's live assignment took "JBloggs2" first (the unique index is case-insensitive).
        var otherMvoId = await AddMetaverseObjectAsync(estate, "JBloggs2");
        await AddImportAssignmentAsync(estate, "JBloggs2", otherMvoId);

        GeneratedValueRevisionResult result;
        await using (var ctx = NewContext())
            result = await NewSyncRepository(ctx).ApplyGeneratedValueRevisionAsync(revision);

        await AssertNothingWrittenAsync(estate, assignmentId, result, GeneratedValueRevisionResult.ValueTaken, expectedMetaverseValue: "jbloggs");
    }

    [Test]
    public async Task ApplyGeneratedValueRevisionAsync_ImportMode_ValueTaken_LeavesTheContextUsableForTheNextSaveAsync()
    {
        // The export run's batch context is long-lived: a refused revision must leave nothing tracked that a later,
        // unrelated save would resend (and fail on again).
        var estate = await SeedEstateAsync();
        var assignmentId = await AddImportAssignmentAsync(estate, "jbloggs");
        var revision = await ImportRevisionAsync(estate, assignmentId, "jbloggs", "jbloggs2", Guid.NewGuid(), withChange: false);
        var otherMvoId = await AddMetaverseObjectAsync(estate, "jbloggs2");
        await AddImportAssignmentAsync(estate, "jbloggs2", otherMvoId);

        await using var ctx = NewContext();
        var repository = NewSyncRepository(ctx);
        Assert.That(await repository.ApplyGeneratedValueRevisionAsync(revision), Is.EqualTo(GeneratedValueRevisionResult.ValueTaken));

        ctx.GeneratedValueRevisionsPending.Add(new GeneratedValueRevisionPending
        {
            Id = Guid.NewGuid(), MetaverseObjectId = estate.MvoId, MetaverseAttributeId = estate.MvAttributeId,
            ReasonCode = CausalReasonCode.GeneratedValueAlreadyInUse
        });
        Assert.DoesNotThrowAsync(async () => await ctx.SaveChangesAsync());
    }

    [Test]
    public async Task ApplyGeneratedValueRevisionAsync_ImportMode_NeverReuseOff_RetiresNothingAsync()
    {
        var estate = await SeedEstateAsync();
        var assignmentId = await AddImportAssignmentAsync(estate, "jbloggs");
        var revision = await ImportRevisionAsync(estate, assignmentId, "jbloggs", "jbloggs2", Guid.NewGuid(), withChange: false) with
        {
            RetirePreviousValue = false
        };

        await using (var ctx = NewContext())
            Assert.That(await NewSyncRepository(ctx).ApplyGeneratedValueRevisionAsync(revision), Is.EqualTo(GeneratedValueRevisionResult.Applied));

        await using var check = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await MetaverseValueAsync(check, estate)).StringValue, Is.EqualTo("jbloggs2"));
            Assert.That(await check.RetiredGeneratedValues.AnyAsync(), Is.False);
            Assert.That(await check.MetaverseObjectChanges.AnyAsync(), Is.False, "no change record when change tracking is off");
            Assert.That(await check.GeneratedValueRevisionsPending.CountAsync(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ApplyGeneratedValueRevisionAsync_ImportMode_NumberAttribute_SwapsTheIntValueAsync()
    {
        var estate = await SeedEstateAsync();
        var assignmentId = await AddImportAssignmentAsync(estate, "1001", attributeId: estate.MvNumberAttributeId, intValue: 1001);
        var revision = await ImportRevisionAsync(estate, assignmentId, "1001", "1002", Guid.NewGuid(), withChange: false) with
        {
            PreviousNumericValue = 1001,
            NewNumericValue = 1002,
            IsLongNumber = false,
            RevisionPending = null
        };

        await using (var ctx = NewContext())
            Assert.That(await NewSyncRepository(ctx).ApplyGeneratedValueRevisionAsync(revision), Is.EqualTo(GeneratedValueRevisionResult.Applied));

        await using var check = NewContext();
        var value = await check.Set<MetaverseObjectAttributeValue>()
            .SingleAsync(v => v.MetaverseObject.Id == estate.MvoId && v.AttributeId == estate.MvNumberAttributeId);
        Assert.That(value.IntValue, Is.EqualTo(1002));
    }

    // ---- Export mode ----

    [Test]
    public async Task ApplyGeneratedValueRevisionAsync_ExportMode_RewritesTheQueuedChangeAndTheAssignmentAsync()
    {
        var estate = await SeedEstateAsync();
        var assignmentId = await AddExportAssignmentAsync(estate, "jbloggs");
        var (_, changeId) = await AddPendingExportAsync(estate, "jbloggs", PendingExportStatus.Pending);
        var executionItemId = Guid.NewGuid();
        var revision = await ExportRevisionAsync(assignmentId, "jbloggs", "jbloggs2", executionItemId, changeId);

        await using (var ctx = NewContext())
            Assert.That(await NewSyncRepository(ctx).ApplyGeneratedValueRevisionAsync(revision), Is.EqualTo(GeneratedValueRevisionResult.Applied));

        await using var check = NewContext();
        var change = await check.Set<PendingExportAttributeValueChange>().SingleAsync(c => c.Id == changeId);
        var assignment = await check.GeneratedValueAssignments.SingleAsync(a => a.Id == assignmentId);
        var retired = await check.RetiredGeneratedValues.SingleAsync(r => r.ConnectedSystemObjectTypeAttributeId == estate.CsAttributeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(change.StringValue, Is.EqualTo("jbloggs2"));
            Assert.That(assignment.Value, Is.EqualTo("jbloggs2"));
            Assert.That(assignment.PreviousValue, Is.EqualTo("jbloggs"));
            Assert.That(assignment.State, Is.EqualTo(GeneratedValueAssignmentState.Remediated));
            Assert.That(assignment.RemediatedByActivityRunProfileExecutionItemId, Is.EqualTo(executionItemId));
            Assert.That(retired.Value, Is.EqualTo("jbloggs"));
            Assert.That(retired.Reason, Is.EqualTo(RetiredGeneratedValueReason.Regenerated));
            Assert.That(await check.GeneratedValueRevisionsPending.AnyAsync(), Is.False, "an export-mode revision needs no synchronisation to carry it");
            Assert.That(await check.MetaverseObjectChanges.AnyAsync(), Is.False);
        }
    }

    [Test]
    public async Task ApplyGeneratedValueRevisionAsync_ExportMode_QueuedValueChangedUnderneath_RollsEverythingBackAsync()
    {
        var estate = await SeedEstateAsync();
        var assignmentId = await AddExportAssignmentAsync(estate, "jbloggs");
        var (_, changeId) = await AddPendingExportAsync(estate, "joe.bloggs", PendingExportStatus.Pending);
        var revision = await ExportRevisionAsync(assignmentId, "jbloggs", "jbloggs2", Guid.NewGuid(), changeId);

        GeneratedValueRevisionResult result;
        await using (var ctx = NewContext())
            result = await NewSyncRepository(ctx).ApplyGeneratedValueRevisionAsync(revision);

        await using var check = NewContext();
        var change = await check.Set<PendingExportAttributeValueChange>().SingleAsync(c => c.Id == changeId);
        var assignment = await check.GeneratedValueAssignments.SingleAsync(a => a.Id == assignmentId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(GeneratedValueRevisionResult.ValueChanged));
            Assert.That(change.StringValue, Is.EqualTo("joe.bloggs"));
            Assert.That(assignment.Value, Is.EqualTo("jbloggs"));
            Assert.That(assignment.State, Is.EqualTo(GeneratedValueAssignmentState.Committed));
            Assert.That(await check.RetiredGeneratedValues.AnyAsync(), Is.False, "the retirement rolled back with the refused swap");
        }
    }

    // ---- The drain's reads and deletes ----

    [Test]
    public async Task GetGeneratedValueRevisionsPendingAsync_ReturnsOldestFirstUpToTheLimitAsync()
    {
        var estate = await SeedEstateAsync();
        var now = DateTime.UtcNow;
        var oldest = await AddRevisionPendingAsync(estate, now.AddMinutes(-3));
        var middle = await AddRevisionPendingAsync(estate, now.AddMinutes(-2));
        await AddRevisionPendingAsync(estate, now.AddMinutes(-1));

        await using var ctx = NewContext();
        var page = await NewSyncRepository(ctx).GetGeneratedValueRevisionsPendingAsync(2);

        Assert.That(page.Select(r => r.Id), Is.EqualTo(new[] { oldest, middle }));
        Assert.That(page[0].RejectedByConnectedSystemName, Is.EqualTo(estate.ConnectedSystemName), "every column round-trips");
    }

    [Test]
    public async Task AnyAndHasGeneratedValueRevisionPendingAsync_AnswerFromTheTableAsync()
    {
        var estate = await SeedEstateAsync();

        await using (var ctx = NewContext())
        {
            var repository = NewSyncRepository(ctx);
            Assert.That(await repository.AnyGeneratedValueRevisionsPendingAsync(), Is.False);
            Assert.That(await repository.HasGeneratedValueRevisionPendingAsync(estate.MvoId, estate.MvAttributeId), Is.False);
        }

        await AddRevisionPendingAsync(estate, DateTime.UtcNow);

        await using (var ctx = NewContext())
        {
            var repository = NewSyncRepository(ctx);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await repository.AnyGeneratedValueRevisionsPendingAsync(), Is.True);
                Assert.That(await repository.HasGeneratedValueRevisionPendingAsync(estate.MvoId, estate.MvAttributeId), Is.True);
                Assert.That(await repository.HasGeneratedValueRevisionPendingAsync(estate.MvoId, estate.MvNumberAttributeId), Is.False,
                    "scoped to the attribute");
            }
        }
    }

    [Test]
    public async Task DeleteGeneratedValueRevisionsPendingAsync_DeletesOnlyTheNamedRecordsAsync()
    {
        var estate = await SeedEstateAsync();
        var first = await AddRevisionPendingAsync(estate, DateTime.UtcNow.AddMinutes(-2));
        var second = await AddRevisionPendingAsync(estate, DateTime.UtcNow.AddMinutes(-1));
        var kept = await AddRevisionPendingAsync(estate, DateTime.UtcNow);

        await using (var ctx = NewContext())
        {
            var repository = NewSyncRepository(ctx);
            await repository.DeleteGeneratedValueRevisionsPendingAsync([first, second]);
            await repository.DeleteGeneratedValueRevisionsPendingAsync([]);
        }

        await using var check = NewContext();
        Assert.That(await check.GeneratedValueRevisionsPending.Select(r => r.Id).ToListAsync(), Is.EqualTo(new[] { kept }));
    }

    [Test]
    public async Task ReleaseParkedPendingExportsAsync_ReturnsOnlyParkedExportsOfTheNamedObjectsToPendingAsync()
    {
        var estate = await SeedEstateAsync();
        var (parkedId, _) = await AddPendingExportAsync(estate, "jbloggs", PendingExportStatus.Parked);

        int released;
        await using (var ctx = NewContext())
        {
            var repository = NewSyncRepository(ctx);
            Assert.That(await repository.ReleaseParkedPendingExportsAsync([Guid.NewGuid()]), Is.Zero, "another object's exports are untouched");
            released = await repository.ReleaseParkedPendingExportsAsync([estate.CsoId]);
        }

        await using var check = NewContext();
        var export = await check.PendingExports.SingleAsync(p => p.Id == parkedId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(released, Is.EqualTo(1));
            Assert.That(export.Status, Is.EqualTo(PendingExportStatus.Pending));
            Assert.That(export.NextRetryAt, Is.Null);
        }
    }

    // ---- Deletion paths ----

    [Test]
    public async Task RevisionPending_DeletingTheMetaverseObjectThroughTheSyncRepository_TakesTheRecordWithItAsync()
    {
        var estate = await SeedEstateAsync();
        await AddRevisionPendingAsync(estate, DateTime.UtcNow);

        await using (var ctx = NewContext())
        {
            var mvo = await ctx.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == estate.MvoId);
            await NewSyncRepository(ctx).DeleteMetaverseObjectsAsync([mvo]);
        }

        await AssertNoRevisionsPendingAsync();
    }

    [Test]
    public async Task RevisionPending_DeletingTheMetaverseObjectThroughTheMetaverseRepository_TakesTheRecordWithItAsync()
    {
        var estate = await SeedEstateAsync();
        await AddRevisionPendingAsync(estate, DateTime.UtcNow);

        await using (var ctx = NewContext())
        {
            var mvo = await ctx.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == estate.MvoId);
            await new PostgresDataRepository(ctx).Metaverse.DeleteMetaverseObjectAsync(mvo);
        }

        await AssertNoRevisionsPendingAsync();
    }

    [Test]
    public async Task RevisionPending_DeletingTheMetaverseAttribute_TakesTheRecordWithItAsync()
    {
        var estate = await SeedEstateAsync();
        await AddRevisionPendingAsync(estate, DateTime.UtcNow);

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Metaverse.CascadeDeleteMetaverseAttributeAsync(estate.MvAttributeId);

        await AssertNoRevisionsPendingAsync();
    }

    [Test]
    public async Task RevisionPending_DeletingTheRejectingConnectedSystem_IsNotBlockedAndKeepsTheRecordForItsObjectAsync()
    {
        // The record belongs to the Metaverse Object, which outlives the system; the rejecting system is held by id and
        // name only, so the drain can still carry the revision to the remaining targets and name the system on the edge.
        var estate = await SeedEstateAsync();
        await AddExportAssignmentAsync(estate, "jbloggs");
        await AddPendingExportAsync(estate, "jbloggs", PendingExportStatus.Parked);
        await AddRevisionPendingAsync(estate, DateTime.UtcNow);

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.DeleteConnectedSystemAsync(estate.ConnectedSystemId);

        await using var check = NewContext();
        var pending = await check.GeneratedValueRevisionsPending.SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await check.ConnectedSystems.AnyAsync(s => s.Id == estate.ConnectedSystemId), Is.False);
            Assert.That(pending.MetaverseObjectId, Is.EqualTo(estate.MvoId));
            Assert.That(pending.RejectedByConnectedSystemName, Is.EqualTo(estate.ConnectedSystemName));
        }
    }

    [Test]
    public async Task RevisionPending_ClearingTheConnectorSpace_IsNotBlockedAndKeepsTheRecordAsync()
    {
        var estate = await SeedEstateAsync();
        await AddExportAssignmentAsync(estate, "jbloggs");
        await AddPendingExportAsync(estate, "jbloggs", PendingExportStatus.Parked);
        await AddRevisionPendingAsync(estate, DateTime.UtcNow);

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).ConnectedSystems.DeleteAllConnectedSystemObjectsAndDependenciesAsync(
                estate.ConnectedSystemId, deleteChangeHistory: true, recordJoinsForReconciliation: true);

        await using var check = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await check.ConnectedSystemObjects.AnyAsync(c => c.Id == estate.CsoId), Is.False);
            Assert.That(await check.PendingExports.AnyAsync(), Is.False);
            Assert.That(await check.GeneratedValueRevisionsPending.CountAsync(), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task RevisionPending_DeletingTheGeneratingSynchronisationRule_IsNotBlockedAndKeepsTheRecordAsync()
    {
        var estate = await SeedEstateAsync();
        await AddImportAssignmentAsync(estate, "jbloggs");
        await AddRevisionPendingAsync(estate, DateTime.UtcNow);

        await using (var ctx = NewContext())
        {
            var rule = await ctx.SyncRules.AsTracking().SingleAsync(r => r.Id == estate.ImportRuleId);
            await new PostgresDataRepository(ctx).ConnectedSystems.DeleteSyncRuleAsync(rule);
        }

        await using var check = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await check.SyncRules.AnyAsync(r => r.Id == estate.ImportRuleId), Is.False);
            Assert.That(await check.GeneratedValueRevisionsPending.CountAsync(), Is.EqualTo(1));
        }
    }

    // ---- Helpers ----

    private async Task AssertNothingWrittenAsync(
        Estate estate, Guid assignmentId, GeneratedValueRevisionResult result, GeneratedValueRevisionResult expected, string expectedMetaverseValue)
    {
        await using var check = NewContext();
        var value = await MetaverseValueAsync(check, estate);
        var assignment = await check.GeneratedValueAssignments.SingleAsync(a => a.Id == assignmentId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(value.StringValue, Is.EqualTo(expectedMetaverseValue), "the Metaverse value is left alone");
            Assert.That(value.ContributedBySyncRuleId, Is.Null, "no provenance written");
            Assert.That(assignment.Value, Is.EqualTo("jbloggs"));
            Assert.That(assignment.State, Is.EqualTo(GeneratedValueAssignmentState.Committed));
            Assert.That(assignment.RemediationCount, Is.Zero);
            Assert.That(await check.RetiredGeneratedValues.AnyAsync(), Is.False, "the retirement rolled back with the rest");
            Assert.That(await check.GeneratedValueRevisionsPending.AnyAsync(), Is.False, "no revision-pending record");
            Assert.That(await check.MetaverseObjectChanges.AnyAsync(), Is.False, "no change record");
        }
    }

    private async Task AssertNoRevisionsPendingAsync()
    {
        await using var check = NewContext();
        Assert.That(await check.GeneratedValueRevisionsPending.AnyAsync(), Is.False);
    }

    private static Task<MetaverseObjectAttributeValue> MetaverseValueAsync(JimDbContext ctx, Estate estate) =>
        ctx.Set<MetaverseObjectAttributeValue>().SingleAsync(v => v.MetaverseObject.Id == estate.MvoId && v.AttributeId == estate.MvAttributeId);

    /// <summary>
    /// The revision <c>CollisionRemediationRun</c> builds in import mode, routed through
    /// <c>MetaverseServer.ReviseGeneratedValueAsync</c>'s change record so the round trip covers the real shape.
    /// </summary>
    private async Task<GeneratedValueRevision> ImportRevisionAsync(
        Estate estate, Guid assignmentId, string previous, string next, Guid executionItemId, bool withChange)
    {
        await using var ctx = NewContext();
        var assignment = await ctx.GeneratedValueAssignments.SingleAsync(a => a.Id == assignmentId);
        var attribute = await ctx.MetaverseAttributes.SingleAsync(a => a.Id == assignment.MetaverseAttributeId);
        Revise(assignment, previous, next, executionItemId, estate.ConnectedSystemId);

        MetaverseObjectChange? change = null;
        if (withChange)
        {
            var mvo = new MetaverseObject { Id = estate.MvoId };
            change = new MetaverseObjectChange
            {
                MetaverseObject = mvo,
                ChangeType = ObjectChangeType.Updated,
                ChangeTime = DateTime.UtcNow,
                InitiatedByType = ActivityInitiatorType.System,
                InitiatedByName = "Collision Remediation",
                ChangeInitiatorType = MetaverseObjectChangeInitiatorType.System
            };
            change.AddAttributeValueChange(new MetaverseObjectAttributeValue { Attribute = attribute, AttributeId = attribute.Id, MetaverseObject = mvo, StringValue = previous }, ValueChangeType.Remove);
            change.AddAttributeValueChange(new MetaverseObjectAttributeValue { Attribute = attribute, AttributeId = attribute.Id, MetaverseObject = mvo, StringValue = next }, ValueChangeType.Add);
        }

        return new GeneratedValueRevision
        {
            Assignment = assignment,
            PreviousValue = previous,
            RetirePreviousValue = true,
            ContributedBySyncRuleId = estate.ImportRuleId,
            ContributedBySystemId = estate.ConnectedSystemId,
            MetaverseObjectChange = change,
            RevisionPending = new GeneratedValueRevisionPending
            {
                Id = Guid.NewGuid(),
                MetaverseObjectId = estate.MvoId,
                MetaverseAttributeId = attribute.Id,
                RemediatingActivityRunProfileExecutionItemId = executionItemId,
                ReasonCode = CausalReasonCode.GeneratedValueAlreadyInUse,
                RejectedByConnectedSystemId = estate.ConnectedSystemId,
                RejectedByConnectedSystemName = estate.ConnectedSystemName,
                Created = DateTime.UtcNow
            }
        };
    }

    private async Task<GeneratedValueRevision> ExportRevisionAsync(Guid assignmentId, string previous, string next, Guid executionItemId, Guid changeId)
    {
        await using var ctx = NewContext();
        var assignment = await ctx.GeneratedValueAssignments.SingleAsync(a => a.Id == assignmentId);
        Revise(assignment, previous, next, executionItemId, rejectedBy: null);
        return new GeneratedValueRevision
        {
            Assignment = assignment,
            PreviousValue = previous,
            RetirePreviousValue = true,
            PendingExportAttributeValueChangeId = changeId
        };
    }

    private static void Revise(GeneratedValueAssignment assignment, string previous, string next, Guid executionItemId, int? rejectedBy)
    {
        assignment.BaseValue ??= previous;
        assignment.PreviousValue = previous;
        assignment.Value = next;
        assignment.NormalisedValue = next.ToLowerInvariant();
        assignment.State = GeneratedValueAssignmentState.Remediated;
        assignment.RemediationCount++;
        assignment.RejectedByConnectedSystemId = rejectedBy;
        assignment.RemediatedByActivityRunProfileExecutionItemId = executionItemId;
    }

    private async Task<Guid> AddRevisionPendingAsync(Estate estate, DateTime created)
    {
        await using var ctx = NewContext();
        var record = new GeneratedValueRevisionPending
        {
            Id = Guid.NewGuid(),
            MetaverseObjectId = estate.MvoId,
            MetaverseAttributeId = estate.MvAttributeId,
            RemediatingActivityRunProfileExecutionItemId = Guid.NewGuid(),
            ReasonCode = CausalReasonCode.GeneratedValueAlreadyInUse,
            RejectedByConnectedSystemId = estate.ConnectedSystemId,
            RejectedByConnectedSystemName = estate.ConnectedSystemName,
            Created = created
        };
        ctx.GeneratedValueRevisionsPending.Add(record);
        await ctx.SaveChangesAsync();
        return record.Id;
    }

    private async Task<Guid> AddMetaverseObjectAsync(Estate estate, string accountName)
    {
        await using var ctx = NewContext();
        var type = await ctx.MetaverseObjectTypes.AsTracking().SingleAsync(t => t.Id == estate.MvoTypeId);
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = type, Created = DateTime.UtcNow };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = estate.MvAttributeId, StringValue = accountName });
        ctx.MetaverseObjects.Add(mvo);
        await ctx.SaveChangesAsync();
        return mvo.Id;
    }

    private async Task<Guid> AddImportAssignmentAsync(Estate estate, string value, Guid? mvoId = null, int? attributeId = null, int? intValue = null)
    {
        await using var ctx = NewContext();
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), MetaverseObjectId = mvoId ?? estate.MvoId, MetaverseAttributeId = attributeId ?? estate.MvAttributeId,
            Value = value, NormalisedValue = value.ToLowerInvariant(), State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = estate.ImportGenerationId, Created = DateTime.UtcNow, LastUpdated = DateTime.UtcNow,
            CommittedAt = DateTime.UtcNow
        };
        ctx.GeneratedValueAssignments.Add(assignment);
        if (intValue.HasValue)
        {
            var mvo = await ctx.MetaverseObjects.AsTracking().SingleAsync(m => m.Id == assignment.MetaverseObjectId);
            ctx.Set<MetaverseObjectAttributeValue>().Add(new MetaverseObjectAttributeValue
            {
                Id = Guid.NewGuid(), MetaverseObject = mvo, AttributeId = assignment.MetaverseAttributeId!.Value, IntValue = intValue
            });
        }
        await ctx.SaveChangesAsync();
        return assignment.Id;
    }

    private async Task<Guid> AddExportAssignmentAsync(Estate estate, string value)
    {
        await using var ctx = NewContext();
        var assignment = new GeneratedValueAssignment
        {
            Id = Guid.NewGuid(), ConnectedSystemObjectId = estate.CsoId, ConnectedSystemObjectTypeAttributeId = estate.CsAttributeId,
            Value = value, NormalisedValue = value.ToLowerInvariant(), State = GeneratedValueAssignmentState.Committed,
            SyncRuleMappingGenerationId = estate.ExportGenerationId, Created = DateTime.UtcNow, LastUpdated = DateTime.UtcNow,
            CommittedAt = DateTime.UtcNow
        };
        ctx.GeneratedValueAssignments.Add(assignment);
        await ctx.SaveChangesAsync();
        return assignment.Id;
    }

    private async Task<(Guid ExportId, Guid ChangeId)> AddPendingExportAsync(Estate estate, string value, PendingExportStatus status)
    {
        await using var ctx = NewContext();
        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(), AttributeId = estate.CsAttributeId, StringValue = value,
            ChangeType = PendingExportAttributeChangeType.Update
        };
        var export = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = estate.ConnectedSystemId, ConnectedSystemObjectId = estate.CsoId,
            ChangeType = PendingExportChangeType.Update, Status = status, SourceMetaverseObjectId = estate.MvoId,
            NextRetryAt = status == PendingExportStatus.Parked ? DateTime.UtcNow.AddHours(1) : null
        };
        export.AttributeValueChanges.Add(change);
        ctx.PendingExports.Add(export);
        await ctx.SaveChangesAsync();
        return (export.Id, change.Id);
    }

    /// <summary>
    /// One Connected System with an import-mode generated mapping onto a Metaverse Text attribute ("jbloggs" held on
    /// the seeded Metaverse Object) and an export-mode generated mapping onto a Connected System Text attribute, plus
    /// a Number Metaverse attribute for the numeric swap. Generation never reuses values.
    /// </summary>
    private async Task<Estate> SeedEstateAsync()
    {
        await using var ctx = NewContext();

        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var mvAttribute = new MetaverseAttribute { Name = "Account Name", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        var mvNumberAttribute = new MetaverseAttribute { Name = "Employee Number", Type = AttributeDataType.Number, AttributePlurality = AttributePlurality.SingleValued };
        ctx.MetaverseObjectTypes.Add(mvoType);
        ctx.MetaverseAttributes.AddRange(mvAttribute, mvNumberAttribute);

        var connectorDefinition = new ConnectorDefinition { Name = "def" };
        var system = new ConnectedSystem { Name = "Directory", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var csAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "sAMAccountName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        csType.Attributes.Add(csAttribute);
        ctx.AddRange(connectorDefinition, system, csType);
        await ctx.SaveChangesAsync();

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = DateTime.UtcNow, CachedDisplayName = "Joe Bloggs" };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), Attribute = mvAttribute, StringValue = "jbloggs" });
        var cso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            DateJoined = DateTime.UtcNow,
            ExternalIdAttributeId = csAttribute.Id,
            MetaverseObject = mvo
        };
        ctx.MetaverseObjects.Add(mvo);
        ctx.ConnectedSystemObjects.Add(cso);
        await ctx.SaveChangesAsync();

        var importRule = new SyncRule
        {
            Name = "import", Direction = SyncRuleDirection.Import, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        var exportRule = new SyncRule
        {
            Name = "export", Direction = SyncRuleDirection.Export, ConnectedSystemId = system.Id,
            ConnectedSystemObjectTypeId = csType.Id, MetaverseObjectTypeId = mvoType.Id
        };
        ctx.SyncRules.AddRange(importRule, exportRule);
        await ctx.SaveChangesAsync();

        var importMapping = new SyncRuleMapping { SyncRuleId = importRule.Id, TargetMetaverseAttributeId = mvAttribute.Id };
        var exportMapping = new SyncRuleMapping { SyncRuleId = exportRule.Id, TargetConnectedSystemAttributeId = csAttribute.Id };
        ctx.SyncRuleMappings.AddRange(importMapping, exportMapping);
        await ctx.SaveChangesAsync();

        var importGeneration = new SyncRuleMappingGeneration { SyncRuleMappingId = importMapping.Id, TokenKind = GeneratedValueTokenKind.OnlyIfTaken, NeverReuse = true };
        var exportGeneration = new SyncRuleMappingGeneration { SyncRuleMappingId = exportMapping.Id, TokenKind = GeneratedValueTokenKind.OnlyIfTaken, NeverReuse = true };
        ctx.SyncRuleMappingGenerations.AddRange(importGeneration, exportGeneration);
        await ctx.SaveChangesAsync();

        return new Estate(mvo.Id, mvoType.Id, mvAttribute.Id, mvAttribute.Name, mvNumberAttribute.Id, cso.Id, system.Id, system.Name,
            csAttribute.Id, importRule.Id, importGeneration.Id, exportGeneration.Id);
    }

    private sealed record Estate(
        Guid MvoId,
        int MvoTypeId,
        int MvAttributeId,
        string MvAttributeName,
        int MvNumberAttributeId,
        Guid CsoId,
        int ConnectedSystemId,
        string ConnectedSystemName,
        int CsAttributeId,
        int ImportRuleId,
        int ImportGenerationId,
        int ExportGenerationId);
}

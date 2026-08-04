// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that causal edges buffered on an RPEI actually reach the database when that
/// RPEI is flushed (#1223), on <b>both</b> of the flush's two paths.
///
/// BulkInsertRpeisAsync takes a single-connection transactional path for small batches and a parallel COPY
/// path for large ones. Wiring only the first would drop every edge on exactly the large cascades this
/// feature exists to explain, and no test running under the batch threshold would notice: the small-batch
/// tests would stay green while a ten-thousand-object deletion cascade recorded nothing at all. Hence the
/// parallel-path test below deliberately crosses the threshold.
///
/// The outcome-id resolution is the other thing only a real flush can prove. Sync outcomes have no id when
/// the seam creates them; ids are assigned at flush time. An edge therefore cannot be given its
/// EffectSyncOutcomeId where it is written, and the flush has to resolve the transient reference. Getting
/// that wrong leaves every edge pointing at no outcome, which reads as "correct" everywhere except the one
/// case that matters: an RPEI carrying more than one outcome, where the cohort can no longer be computed.
///
/// Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class CausalEdgeFlushDatabaseTests
{
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL causal edge flush tests.");

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
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
    }

    /// <summary>
    /// The small-batch path: an edge buffered on an RPEI is written inside the same flush, and its transient
    /// outcome reference is resolved to the id the flush assigned that outcome.
    /// </summary>
    [Test]
    public async Task BulkInsertRpeisAsync_SingleConnectionPath_WritesTheEdgeAndResolvesTheOutcomeIdAsync()
    {
        var activityId = await SeedActivityAsync();
        var rpei = NewRpei(activityId);
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
        };
        rpei.SyncOutcomes.Add(outcome);

        var edge = NewEdge();
        edge.EffectSyncOutcome = outcome;
        rpei.CausalEdges.Add(edge);

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.Sync.BulkInsertRpeisAsync([rpei]);
        }

        await using var verifyCtx = NewContext();
        var persisted = await verifyCtx.CausalEdges.AsNoTracking().SingleOrDefaultAsync();

        Assert.That(persisted, Is.Not.Null, "an edge buffered on a flushed RPEI must be written by that flush");
        Assert.That(persisted!.EffectRunProfileExecutionItemId, Is.EqualTo(rpei.Id));
        Assert.That(persisted!.EffectSyncOutcomeId, Is.EqualTo(outcome.Id),
            "the flush assigns outcome ids, so it must resolve the edge's transient outcome reference to the assigned id");
        Assert.That(outcome.Id, Is.Not.EqualTo(Guid.Empty), "sanity: the flush must have assigned the outcome an id");
    }

    /// <summary>
    /// The parallel COPY path, which engages once the batch crosses parallelism * 50 items. This is the path
    /// a real deletion cascade takes, so an edge insert wired only into the small-batch path would lose
    /// provenance precisely when it is most needed.
    /// </summary>
    [Test]
    public async Task BulkInsertRpeisAsync_ParallelPath_WritesEveryEdgeAsync()
    {
        var activityId = await SeedActivityAsync();

        // Force the parallel COPY path, and prove it engaged rather than assuming it: without the JIM_DB_*
        // variables this scope sets, the repository cannot build its own connection string and falls back to
        // the single-connection path with every assertion below still passing.
        using var parallelPath = ParallelWritePathScope.Enter();
        var count = ParallelWritePathScope.Threshold;
        ParallelWritePathScope.AssertEngaged(count);

        var rpeis = new List<ActivityRunProfileExecutionItem>(count);
        for (var i = 0; i < count; i++)
        {
            var rpei = NewRpei(activityId);
            var outcome = new ActivityRunProfileExecutionItemSyncOutcome
            {
                OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
            };
            rpei.SyncOutcomes.Add(outcome);

            var edge = NewEdge();
            edge.EffectSyncOutcome = outcome;
            rpei.CausalEdges.Add(edge);
            rpeis.Add(rpei);
        }

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.Sync.BulkInsertRpeisAsync(rpeis);
        }

        await using var verifyCtx = NewContext();
        Assert.That(await verifyCtx.CausalEdges.CountAsync(), Is.EqualTo(count),
            "the parallel path must persist edges too; a cascade large enough to need it is exactly the one needing explanation");
        Assert.That(await verifyCtx.CausalEdges.CountAsync(e => e.EffectSyncOutcomeId == null), Is.Zero,
            "outcome ids are resolved before either path runs, so no edge should reach the database without one");
    }

    /// <summary>
    /// The buffer is emptied once written. RPEI objects outlive their flush (confirming imports revisit them),
    /// so leaving written edges in place would either re-insert them on a later flush and fail on the primary
    /// key, or force every future flush path to work out for itself which edges were already persisted.
    /// </summary>
    [Test]
    public async Task BulkInsertRpeisAsync_AfterWritingEdges_ClearsTheBufferAsync()
    {
        var activityId = await SeedActivityAsync();
        var rpei = NewRpei(activityId);
        rpei.CausalEdges.Add(NewEdge());

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        await repository.Sync.BulkInsertRpeisAsync([rpei]);

        Assert.That(rpei.CausalEdges, Is.Empty,
            "the buffer hands edges to the flush; leaving them behind would duplicate them on any later flush of the same RPEI");
    }

    /// <summary>
    /// The EF-based persistence path, which is what Metaverse Object Housekeeping uses. It is a separate path
    /// from the sync engine's bulk flush, and the edge buffer is deliberately unmapped, so <c>AddRange</c> does
    /// not reach the edges: without an explicit drain here, every edge written by the grace-period deletion
    /// path would be dropped with nothing failing anywhere.
    ///
    /// This is also the only path where cause and effect are persisted in the <b>same</b> batch, so it is the
    /// one that proves the cause-side references resolve. Housekeeping deletes an object and records the
    /// removals that deletion caused in one Activity; the causing outcome has no id when the edge is built, so
    /// an implementation that resolved the cause eagerly would store an edge naming no cause at all.
    /// </summary>
    [Test]
    public async Task CreateActivityRunProfileExecutionItemsAsync_ItemCarryingAnEdge_WritesItAndResolvesBothSidesAsync()
    {
        var activityId = await SeedActivityAsync();

        var causeItem = NewRpei(activityId);
        var causeOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
        };
        causeItem.SyncOutcomes.Add(causeOutcome);

        var effectItem = NewRpei(activityId);
        var effectOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated
        };
        effectItem.SyncOutcomes.Add(effectOutcome);
        effectItem.CausalEdges.Add(new CausalCause
        {
            RunProfileExecutionItem = causeItem,
            SyncOutcome = causeOutcome,
            MetaverseObjectId = Guid.NewGuid(),
            DisplayName = "Lena Leaver",
            ReasonCode = CausalReasonCode.AuthoritativeSourceDisconnected,
            ConnectedSystemId = 9,
            ConnectedSystemName = "Yellowstone APAC"
        }.ToEdge(CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval, effectOutcome));

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.Activity.CreateActivityRunProfileExecutionItemsAsync([causeItem, effectItem]);
        }

        await using var verifyCtx = NewContext();
        var persisted = await verifyCtx.CausalEdges.AsNoTracking().SingleOrDefaultAsync();

        Assert.That(persisted, Is.Not.Null,
            "the EF path must drain the edge buffer too; the buffer is unmapped, so AddRange cannot reach it");
        Assert.Multiple(() =>
        {
            Assert.That(persisted!.EffectRunProfileExecutionItemId, Is.EqualTo(effectItem.Id));
            Assert.That(persisted!.EffectSyncOutcomeId, Is.EqualTo(effectOutcome.Id));
            Assert.That(persisted!.CauseRunProfileExecutionItemId, Is.EqualTo(causeItem.Id),
                "the causing item was persisted in this same batch, so its id existed only after the save");
            Assert.That(persisted!.CauseSyncOutcomeId, Is.EqualTo(causeOutcome.Id),
                "an implementation resolving the cause eagerly would have stored null here and named no cause");
            Assert.That(persisted!.CauseDisplayName, Is.EqualTo("Lena Leaver"));
            Assert.That(persisted!.ReasonCode, Is.EqualTo(CausalReasonCode.AuthoritativeSourceDisconnected));
        });
        Assert.That(effectItem.CausalEdges, Is.Empty, "the buffer is emptied once written on this path too");
    }

    private static CausalEdge NewEdge()
    {
        return new CausalEdge
        {
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
            ReasonCode = CausalReasonCode.AuthoritativeSourceDisconnected,
            CauseDisplayName = "Tina Adams (S8-99)",
            ConnectedSystemId = 7,
            ConnectedSystemName = "Yellowstone APAC"
        };
    }

    private static ActivityRunProfileExecutionItem NewRpei(Guid activityId)
    {
        return new ActivityRunProfileExecutionItem
        {
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.DisconnectedOutOfScope,
            ObjectTypeSnapshot = "user"
        };
    }

    private async Task<Guid> SeedActivityAsync()
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "Full Sync",
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        return activity.Id;
    }
}

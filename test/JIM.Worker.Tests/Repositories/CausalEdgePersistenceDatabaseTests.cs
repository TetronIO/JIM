// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the raw-SQL causal edge insert path (#1223), and of the asymmetric
/// retention behaviour the whole feature depends on.
///
/// Two things here are structurally invisible to the in-memory provider. It stores the object graph
/// verbatim, so a column omitted from hand-written SQL, a value written in the wrong position, or a
/// wrong NpgsqlDbType all round-trip perfectly; and it enforces no foreign keys or cascades, so it
/// cannot tell a cause-side reference (which must survive its target being purged) from an
/// effect-side one (which must not). Getting the second wrong is worse than losing the feature: an
/// edge that cascaded away with its cause would take the explanation of a still-retained effect with
/// it, which is precisely the "this change has no cause whatsoever" bug the feature exists to fix.
///
/// Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class CausalEdgePersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL causal edge persistence tests.");

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
    /// Every field on a fully populated edge must survive the round trip. The column-completeness unit test
    /// proves the list matches the model, but cannot catch a writer emitting values in the wrong order or with
    /// the wrong NpgsqlDbType, which would silently swap two same-typed columns (CauseMetaverseObjectId and
    /// CauseConnectedSystemObjectId are both nullable Guids, and transposing them would attribute a cascade to
    /// the wrong object).
    /// </summary>
    [Test]
    public async Task BulkInsertCausalEdgesAsync_FullyPopulatedEdge_PersistsEveryFieldAsync()
    {
        var activityId = await SeedActivityAsync();
        var effectRpeiId = await SeedRpeiAsync(activityId, "Project-Pulse");
        var causeRpeiId = await SeedRpeiAsync(activityId, "Tina Adams (S8-99)");

        var causeMvoId = Guid.NewGuid();
        var causeCsoId = Guid.NewGuid();
        var causePendingExportId = Guid.NewGuid();
        var effectOutcomeId = Guid.NewGuid();
        var causeOutcomeId = Guid.NewGuid();
        var created = new DateTime(2026, 8, 4, 9, 12, 2, DateTimeKind.Utc);

        var edge = new CausalEdge
        {
            Id = Guid.NewGuid(),
            EffectRunProfileExecutionItemId = effectRpeiId,
            EffectSyncOutcomeId = effectOutcomeId,
            CauseRunProfileExecutionItemId = causeRpeiId,
            CauseSyncOutcomeId = causeOutcomeId,
            CauseMetaverseObjectId = causeMvoId,
            CauseConnectedSystemObjectId = causeCsoId,
            CausePendingExportId = causePendingExportId,
            CauseDisplayName = "Tina Adams (S8-99)",
            CauseObjectTypeName = "User",
            CauseObjectTypePluralName = "Users",
            EffectAttributeName = "Static Members",
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
            ReasonCode = CausalReasonCode.AuthoritativeSourceDisconnected,
            ConnectedSystemId = 7,
            ConnectedSystemName = "Yellowstone APAC",
            SyncRuleId = 12,
            SyncRuleName = "APAC Identities Inbound",
            Created = created
        };

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.Sync.BulkInsertCausalEdgesAsync([edge]);
        }

        await using var verifyCtx = NewContext();
        var persisted = await verifyCtx.CausalEdges.AsNoTracking().SingleAsync(e => e.Id == edge.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.EffectRunProfileExecutionItemId, Is.EqualTo(effectRpeiId));
            Assert.That(persisted.EffectSyncOutcomeId, Is.EqualTo(effectOutcomeId));
            Assert.That(persisted.CauseRunProfileExecutionItemId, Is.EqualTo(causeRpeiId));
            Assert.That(persisted.CauseSyncOutcomeId, Is.EqualTo(causeOutcomeId));
            Assert.That(persisted.CauseMetaverseObjectId, Is.EqualTo(causeMvoId),
                "a transposed Guid parameter would attribute the cascade to the wrong object");
            Assert.That(persisted.CauseConnectedSystemObjectId, Is.EqualTo(causeCsoId));
            Assert.That(persisted.CausePendingExportId, Is.EqualTo(causePendingExportId),
                "the Pending Export identifies which export cycle a confirmation confirms; losing it reintroduces the wrong-cycle attribution");
            Assert.That(persisted.CauseDisplayName, Is.EqualTo("Tina Adams (S8-99)"));
            Assert.That(persisted.CauseObjectTypeName, Is.EqualTo("User"));
            Assert.That(persisted.CauseObjectTypePluralName, Is.EqualTo("Users"),
                "both nouns are snapshotted because the edge cannot know whether it lands in a cohort of one or ten");
            Assert.That(persisted.EffectAttributeName, Is.EqualTo("Static Members"),
                "the relationship noun comes from the schema, so the chain can name which reference was lost");
            Assert.That(persisted.EdgeType, Is.EqualTo(CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval));
            Assert.That(persisted.ReasonCode, Is.EqualTo(CausalReasonCode.AuthoritativeSourceDisconnected));
            Assert.That(persisted.ConnectedSystemId, Is.EqualTo(7));
            Assert.That(persisted.ConnectedSystemName, Is.EqualTo("Yellowstone APAC"));
            Assert.That(persisted.SyncRuleId, Is.EqualTo(12));
            Assert.That(persisted.SyncRuleName, Is.EqualTo("APAC Identities Inbound"));
            Assert.That(persisted.Created, Is.EqualTo(created));
        }
    }

    /// <summary>
    /// A minimally populated edge must persist NULLs rather than spurious values. Several seams have no
    /// Synchronisation Rule or Connected System to name, and a writer that defaulted those to zero would put
    /// every such edge into a cohort attributed to a Connected System that does not exist.
    /// </summary>
    [Test]
    public async Task BulkInsertCausalEdgesAsync_MinimalEdge_PersistsNullsAsync()
    {
        var activityId = await SeedActivityAsync();
        var effectRpeiId = await SeedRpeiAsync(activityId, "Project-Pulse");

        var edge = new CausalEdge
        {
            Id = Guid.NewGuid(),
            EffectRunProfileExecutionItemId = effectRpeiId,
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedDeprovision,
            ReasonCode = CausalReasonCode.NotSet
        };

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.Sync.BulkInsertCausalEdgesAsync([edge]);
        }

        await using var verifyCtx = NewContext();
        var persisted = await verifyCtx.CausalEdges.AsNoTracking().SingleAsync(e => e.Id == edge.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.EffectSyncOutcomeId, Is.Null);
            Assert.That(persisted.CauseRunProfileExecutionItemId, Is.Null);
            Assert.That(persisted.CauseSyncOutcomeId, Is.Null);
            Assert.That(persisted.CauseMetaverseObjectId, Is.Null);
            Assert.That(persisted.CauseConnectedSystemObjectId, Is.Null);
            Assert.That(persisted.CausePendingExportId, Is.Null);
            Assert.That(persisted.CauseDisplayName, Is.Null);
            Assert.That(persisted.CauseObjectTypeName, Is.Null);
            Assert.That(persisted.CauseObjectTypePluralName, Is.Null);
            Assert.That(persisted.EffectAttributeName, Is.Null);
            Assert.That(persisted.ConnectedSystemId, Is.Null);
            Assert.That(persisted.ConnectedSystemName, Is.Null);
            Assert.That(persisted.SyncRuleId, Is.Null);
            Assert.That(persisted.SyncRuleName, Is.Null);
        }
    }

    /// <summary>
    /// Deleting the Activity holding the <b>effect</b> must cascade to the edge: an edge whose effect is gone is
    /// garbage nothing will ever query, and leaving it would accumulate orphans in a table written on the
    /// deletion hot path.
    /// </summary>
    [Test]
    public async Task DeletingTheEffectActivity_CascadesToTheEdgeAsync()
    {
        var activityId = await SeedActivityAsync();
        var effectRpeiId = await SeedRpeiAsync(activityId, "Project-Pulse");
        var edgeId = await InsertEdgeAsync(effectRpeiId, causeRpeiId: null);

        await using (var ctx = NewContext())
        {
            var activity = await ctx.Activities.SingleAsync(a => a.Id == activityId);
            ctx.Activities.Remove(activity);
            await ctx.SaveChangesAsync();
        }

        await using var verifyCtx = NewContext();
        Assert.That(await verifyCtx.CausalEdges.AnyAsync(e => e.Id == edgeId), Is.False,
            "purging the effect's Activity must take its causal edges with it, leaving no orphaned rows");
    }

    /// <summary>
    /// Deleting the Activity holding the <b>cause</b> must NOT delete the edge. Causes are always older than
    /// their effects, so once a deployment has been live longer than one retention window this is the normal
    /// state; the edge has to survive so the chain can render "cause no longer retained" instead of showing the
    /// effect as uncaused.
    /// </summary>
    [Test]
    public async Task DeletingTheCauseActivity_LeavesTheEdgeIntactAsync()
    {
        var effectActivityId = await SeedActivityAsync();
        var causeActivityId = await SeedActivityAsync();
        var effectRpeiId = await SeedRpeiAsync(effectActivityId, "Project-Pulse");
        var causeRpeiId = await SeedRpeiAsync(causeActivityId, "Tina Adams (S8-99)");
        var edgeId = await InsertEdgeAsync(effectRpeiId, causeRpeiId);

        await using (var ctx = NewContext())
        {
            var causeActivity = await ctx.Activities.SingleAsync(a => a.Id == causeActivityId);
            ctx.Activities.Remove(causeActivity);
            await ctx.SaveChangesAsync();
        }

        await using var verifyCtx = NewContext();
        var persisted = await verifyCtx.CausalEdges.AsNoTracking().SingleOrDefaultAsync(e => e.Id == edgeId);

        Assert.That(persisted, Is.Not.Null,
            "purging a cause must never delete the edge recording that it was once the cause; that would " +
            "reintroduce the 'this change has no cause whatsoever' bug on a still-retained effect");
        Assert.That(persisted!.CauseRunProfileExecutionItemId, Is.EqualTo(causeRpeiId),
            "the cause reference is a snapshot scalar, so it must survive verbatim rather than being nulled out");
        Assert.That(persisted!.CauseDisplayName, Is.EqualTo("Tina Adams (S8-99)"),
            "the snapshot name is what lets a truncated chain still say what the lost cause was");
    }

    private async Task<Guid> InsertEdgeAsync(Guid effectRpeiId, Guid? causeRpeiId)
    {
        var edge = new CausalEdge
        {
            Id = Guid.NewGuid(),
            EffectRunProfileExecutionItemId = effectRpeiId,
            CauseRunProfileExecutionItemId = causeRpeiId,
            CauseDisplayName = "Tina Adams (S8-99)",
            EdgeType = CausalEdgeType.MetaverseObjectDeletionCausedReferenceRemoval,
            ReasonCode = CausalReasonCode.AuthoritativeSourceDisconnected
        };

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        await repository.Sync.BulkInsertCausalEdgesAsync([edge]);
        return edge.Id;
    }

    private async Task<Guid> SeedRpeiAsync(Guid activityId, string displayName)
    {
        await using var ctx = NewContext();
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = JIM.Models.Enums.ObjectChangeType.PendingExport,
            DisplayNameSnapshot = displayName
        };
        ctx.ActivityRunProfileExecutionItems.Add(rpei);
        await ctx.SaveChangesAsync();
        return rpei.Id;
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

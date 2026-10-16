// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that the bulk sync outcome insert paths persist the Synchronisation Rule
/// attribution columns added by issue #1085 (<c>SyncRuleId</c> and <c>SyncRuleName</c>). The raw-SQL
/// insert paths (parameterised multi-row INSERT and the parallel COPY) enumerate columns by hand, so a
/// missing column silently persists NULL; the in-memory provider stores the object graph verbatim and
/// cannot catch that, making this fixture the regression guard the raw-SQL-write rules require.
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncOutcomeSyncRuleAttributionDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL sync outcome Synchronisation Rule attribution tests.");

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

    [Test]
    public async Task BulkInsertRpeisAsync_OutcomeWithSyncRuleAttribution_PersistsTheColumnsAsync()
    {
        var activityId = await SeedActivityAsync();

        // SyncRuleId is a loose scalar snapshot (no FK constraint), so no SyncRule row needs seeding;
        // that is deliberate, matching the TargetEntityId snapshot approach.
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.DisconnectedOutOfScope,
            DisplayNameSnapshot = "Some User"
        };
        var rootOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
            SyncRuleId = 42,
            SyncRuleName = "HR Import"
        };
        // A child outcome without attribution must still round-trip NULLs.
        var childOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            ParentSyncOutcome = rootOutcome,
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            DetailCount = 3
        };
        rootOutcome.Children.Add(childOutcome);
        rpei.SyncOutcomes.Add(rootOutcome);
        rpei.SyncOutcomes.Add(childOutcome);

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.Sync.BulkInsertRpeisAsync([rpei]);
        }

        await using var verifyCtx = NewContext();
        var persistedRoot = await verifyCtx.ActivityRunProfileExecutionItemSyncOutcomes
            .AsNoTracking().SingleAsync(o => o.ActivityRunProfileExecutionItemId == rpei.Id && o.ParentSyncOutcomeId == null);
        var persistedChild = await verifyCtx.ActivityRunProfileExecutionItemSyncOutcomes
            .AsNoTracking().SingleAsync(o => o.ActivityRunProfileExecutionItemId == rpei.Id && o.ParentSyncOutcomeId != null);

        Assert.That(persistedRoot.SyncRuleId, Is.EqualTo(42),
            "The scoping Synchronisation Rule id must be persisted by the raw outcome insert");
        Assert.That(persistedRoot.SyncRuleName, Is.EqualTo("HR Import"),
            "The Synchronisation Rule name snapshot must be persisted by the raw outcome insert");
        Assert.That(persistedChild.SyncRuleId, Is.Null,
            "An outcome with no attributed Synchronisation Rule must persist a NULL SyncRuleId");
        Assert.That(persistedChild.SyncRuleName, Is.Null,
            "An outcome with no attributed Synchronisation Rule must persist a NULL SyncRuleName");
    }

    /// <summary>
    /// Round-trip for the Export queued staged kind (#1561 follow-up): every Pending Export outcome
    /// records the kind of change staged (Create/Update/Delete), and an outcome recorded before this
    /// existed persists a NULL rather than a guess. Exercises the same raw INSERT writer
    /// (<c>BulkInsertSyncOutcomesRawAsync</c>) as the Synchronisation Rule attribution test above.
    /// </summary>
    [Test]
    public async Task BulkInsertRpeisAsync_OutcomesWithStagedChangeType_PersistTheColumnAsync()
    {
        var activityId = await SeedActivityAsync();

        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.PendingExport,
            DisplayNameSnapshot = "Some User"
        };
        var createOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            StagedChangeType = PendingExportChangeType.Create
        };
        var updateOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            StagedChangeType = PendingExportChangeType.Update
        };
        var deleteOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
            StagedChangeType = PendingExportChangeType.Delete
        };
        // Outcomes recorded before this feature existed carry no staged kind at all.
        var legacyOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated
        };
        rpei.SyncOutcomes.Add(createOutcome);
        rpei.SyncOutcomes.Add(updateOutcome);
        rpei.SyncOutcomes.Add(deleteOutcome);
        rpei.SyncOutcomes.Add(legacyOutcome);

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.Sync.BulkInsertRpeisAsync([rpei]);
        }

        await using var verifyCtx = NewContext();
        var persisted = await verifyCtx.ActivityRunProfileExecutionItemSyncOutcomes
            .AsNoTracking()
            .Where(o => o.ActivityRunProfileExecutionItemId == rpei.Id)
            .ToListAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.Single(o => o.Id == createOutcome.Id).StagedChangeType,
                Is.EqualTo(PendingExportChangeType.Create));
            Assert.That(persisted.Single(o => o.Id == updateOutcome.Id).StagedChangeType,
                Is.EqualTo(PendingExportChangeType.Update));
            Assert.That(persisted.Single(o => o.Id == deleteOutcome.Id).StagedChangeType,
                Is.EqualTo(PendingExportChangeType.Delete));
            Assert.That(persisted.Single(o => o.Id == legacyOutcome.Id).StagedChangeType, Is.Null,
                "An outcome recorded before this feature existed must persist NULL, not a guessed kind");
        }
    }

    /// <summary>
    /// The cross-page merge read (<c>GetRpeisWithMvoChangeIdsForCrossPageMergeAsync</c>) is a second,
    /// independent raw-SQL projection of the same table; a column added to the insert writers but not
    /// to this SELECT and reader would silently drop it whenever a reference resolved at page end
    /// merges into an outcome tree built earlier in the same page (#1428's cross-page merge path).
    /// </summary>
    [Test]
    public async Task GetRpeisWithMvoChangeIdsForCrossPageMergeAsync_OutcomeWithStagedChangeType_ReturnsTheColumnAsync()
    {
        var activityId = await SeedActivityAsync();
        var csoId = await SeedConnectedSystemObjectAsync();

        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.PendingExport,
            ConnectedSystemObjectId = csoId,
            DisplayNameSnapshot = "Some User"
        };
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            StagedChangeType = PendingExportChangeType.Update
        };
        rpei.SyncOutcomes.Add(outcome);

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.Sync.BulkInsertRpeisAsync([rpei]);
        }

        await using var readCtx = NewContext();
        var repositoryForRead = new PostgresDataRepository(readCtx);
        var merged = await repositoryForRead.Sync.GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(activityId, [csoId]);

        var mergedOutcome = merged.Single().Rpei.SyncOutcomes.Single();
        Assert.That(mergedOutcome.StagedChangeType, Is.EqualTo(PendingExportChangeType.Update),
            "The cross-page merge read must carry the staged kind, or a same-page merge would silently drop it");
    }

    /// <summary>
    /// A Joined outcome made by matching records which Object Matching Rule found the Metaverse Object and on what
    /// value (#2052). The update path writes new outcomes through the parameterised multi-row INSERT
    /// (<c>BulkInsertSyncOutcomesRawAsync</c>), a writer separate from the create path's COPY, so it needs its own
    /// round trip.
    /// </summary>
    [Test]
    public async Task BulkUpdateRpeiOutcomesAsync_JoinedOutcomeWithMatchSource_PersistsTheColumnsAsync()
    {
        var activityId = await SeedActivityAsync();
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.Joined,
            DisplayNameSnapshot = "Ann Bloggs"
        };

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.Sync.BulkInsertRpeisAsync([rpei]);

            var joined = new ActivityRunProfileExecutionItemSyncOutcome
            {
                Id = Guid.NewGuid(),
                ActivityRunProfileExecutionItemId = rpei.Id,
                OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Joined,
                ObjectMatchingRuleId = 12,
                ObjectMatchingRuleDescriptionSnapshot = "Employee ID equals \"E\" + Trim(cs[\"employeeId\"])",
                MatchedValue = "E1007"
            };
            await repository.Sync.BulkUpdateRpeiOutcomesAsync([rpei], [joined]);
        }

        await using var verifyCtx = NewContext();
        var persisted = await verifyCtx.ActivityRunProfileExecutionItemSyncOutcomes
            .AsNoTracking().SingleAsync(o => o.ActivityRunProfileExecutionItemId == rpei.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.ObjectMatchingRuleId, Is.EqualTo(12));
            Assert.That(persisted.ObjectMatchingRuleDescriptionSnapshot, Is.EqualTo("Employee ID equals \"E\" + Trim(cs[\"employeeId\"])"));
            Assert.That(persisted.MatchedValue, Is.EqualTo("E1007"));
        }
    }

    /// <summary>
    /// The cross-page merge read must carry a Joined outcome's match source too (#2052), or a reference resolved at
    /// page end would merge into a tree that has lost which rule made the join.
    /// </summary>
    [Test]
    public async Task GetRpeisWithMvoChangeIdsForCrossPageMergeAsync_JoinedOutcomeWithMatchSource_ReturnsTheColumnsAsync()
    {
        var activityId = await SeedActivityAsync();
        var csoId = await SeedConnectedSystemObjectAsync();
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.Joined,
            ConnectedSystemObjectId = csoId,
            DisplayNameSnapshot = "Ann Bloggs"
        };
        rpei.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Joined,
            ObjectMatchingRuleId = 12,
            ObjectMatchingRuleDescriptionSnapshot = "Employee ID equals employeeId",
            MatchedValue = "1007"
        });

        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            await repository.Sync.BulkInsertRpeisAsync([rpei]);
        }

        await using var readCtx = NewContext();
        var merged = await new PostgresDataRepository(readCtx).Sync.GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(activityId, [csoId]);

        var mergedOutcome = merged.Single().Rpei.SyncOutcomes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mergedOutcome.ObjectMatchingRuleId, Is.EqualTo(12));
            Assert.That(mergedOutcome.ObjectMatchingRuleDescriptionSnapshot, Is.EqualTo("Employee ID equals employeeId"));
            Assert.That(mergedOutcome.MatchedValue, Is.EqualTo("1007"));
        }
    }

    /// <summary>
    /// The Run Profile Execution Item detail endpoint reads an item's Joined outcome on its own (#2052), to say which
    /// rule made the join and on what value, without loading the item's whole tree.
    /// </summary>
    [Test]
    public async Task GetJoinedOutcomeAsync_ItemThatJoinedByMatching_ReturnsTheJoinedOutcomeWithItsMatchSourceAsync()
    {
        var activityId = await SeedActivityAsync();
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.Joined,
            DisplayNameSnapshot = "Ann Bloggs"
        };
        var joined = new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Joined,
            ObjectMatchingRuleId = 12,
            ObjectMatchingRuleDescriptionSnapshot = "Employee ID equals employeeId",
            MatchedValue = "1007"
        };
        joined.Children.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            ParentSyncOutcome = joined,
            DetailCount = 3
        });
        rpei.SyncOutcomes.Add(joined);
        rpei.SyncOutcomes.Add(joined.Children[0]);

        await using (var ctx = NewContext())
        {
            await new PostgresDataRepository(ctx).Sync.BulkInsertRpeisAsync([rpei]);
        }

        await using var readCtx = NewContext();
        var outcome = await new PostgresDataRepository(readCtx).Activity.GetJoinedOutcomeAsync(rpei.Id);

        Assert.That(outcome, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcome!.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Joined));
            Assert.That(outcome.ObjectMatchingRuleId, Is.EqualTo(12));
            Assert.That(outcome.ObjectMatchingRuleDescriptionSnapshot, Is.EqualTo("Employee ID equals employeeId"));
            Assert.That(outcome.MatchedValue, Is.EqualTo("1007"));
        }
    }

    [Test]
    public async Task GetJoinedOutcomeAsync_ItemWithNoJoinedOutcome_ReturnsNullAsync()
    {
        var activityId = await SeedActivityAsync();
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.Projected,
            DisplayNameSnapshot = "Ann Bloggs"
        };
        rpei.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected });

        await using (var ctx = NewContext())
        {
            await new PostgresDataRepository(ctx).Sync.BulkInsertRpeisAsync([rpei]);
        }

        await using var readCtx = NewContext();
        Assert.That(await new PostgresDataRepository(readCtx).Activity.GetJoinedOutcomeAsync(rpei.Id), Is.Null);
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

    /// <summary>
    /// Seeds a minimal Connected System Object, so an RPEI's ConnectedSystemObjectId foreign key resolves.
    /// </summary>
    private async Task<Guid> SeedConnectedSystemObjectAsync()
    {
        await using var ctx = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Yellowstone HR", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "USER", ConnectedSystem = system, Selected = true };
        ctx.AddRange(connectorDefinition, system, csType);
        await ctx.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            Type = csType,
            ConnectedSystem = system,
            Status = ConnectedSystemObjectStatus.Normal
        };
        ctx.Add(cso);
        await ctx.SaveChangesAsync();
        return cso.Id;
    }
}

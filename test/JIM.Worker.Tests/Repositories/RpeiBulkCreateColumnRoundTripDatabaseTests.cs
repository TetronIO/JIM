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
/// Real-PostgreSQL characterisation of <c>SyncRepository.BulkInsertRpeisAsync</c>'s single-connection
/// branch (page-sized batches, below the parallel COPY threshold): every column of a newly created
/// Run Profile Execution Item and its sync outcomes must round-trip exactly, and a failure part-way
/// through must leave no row behind. Written before converting that branch's two parameterised
/// multi-row INSERTs (RPEI rows, then outcome rows) to COPY binary import on the EF connection's own
/// transaction, so this fixture proves the write path's observable behaviour is unchanged by the
/// switch. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class RpeiBulkCreateColumnRoundTripDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL RPEI bulk create round-trip tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
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

    [Test]
    public async Task BulkInsertRpeisAsync_PageSizedBatchEveryColumnPopulated_RoundTripsExactlyAsync()
    {
        var activityId = await SeedActivityAsync();
        var pendingExportId = Guid.NewGuid();
        var snapshotJson = """{"deletionRule":"WhenAuthoritativeSourceDisconnected"}""";

        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.Updated,
            NoChangeReason = NoChangeReason.MvoNoAttributeChanges,
            ConnectedSystemObjectId = null,
            ExternalIdSnapshot = "cn=Jo Bloggs,ou=People,dc=corp",
            DisplayNameSnapshot = "Jo Bloggs",
            ObjectTypeSnapshot = "user",
            ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError,
            ErrorMessage = "the connector rejected the change",
            ErrorStackTrace = "at Foo.Bar()",
            AttributeFlowCount = 3,
            OutcomeSummary = "Updated 3 attributes",
            PendingExportId = pendingExportId,
            DeletionPolicySnapshotJson = snapshotJson
        };
        var rootOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            TargetEntityId = Guid.NewGuid(),
            TargetEntityDescription = "displayName",
            DetailCount = 1,
            DetailMessage = "Jo Bloggs",
            ConnectedSystemObjectChangeId = null,
            SyncRuleId = 42,
            SyncRuleName = "HR Import",
            StagedChangeType = null
        };
        var childOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            ParentSyncOutcome = rootOutcome,
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            TargetEntityId = Guid.NewGuid(),
            TargetEntityDescription = "employeeNumber",
            DetailCount = null,
            DetailMessage = null,
            SyncRuleId = null,
            SyncRuleName = null,
            StagedChangeType = PendingExportChangeType.Update
        };
        rootOutcome.Children.Add(childOutcome);
        rpei.SyncOutcomes.Add(rootOutcome);
        rpei.SyncOutcomes.Add(childOutcome);

        // Act: persist through the single-connection create path (1 RPEI is well below the parallel threshold)
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.Sync.BulkInsertRpeisAsync([rpei]);
        }

        // Assert: read back on a fresh context
        await using var readContext = NewContext();
        var storedRpei = await readContext.ActivityRunProfileExecutionItems.AsNoTracking().SingleAsync(r => r.Id == rpei.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedRpei.ActivityId, Is.EqualTo(activityId));
            Assert.That(storedRpei.ObjectChangeType, Is.EqualTo(ObjectChangeType.Updated));
            Assert.That(storedRpei.NoChangeReason, Is.EqualTo(NoChangeReason.MvoNoAttributeChanges));
            Assert.That(storedRpei.ConnectedSystemObjectId, Is.Null);
            Assert.That(storedRpei.ExternalIdSnapshot, Is.EqualTo("cn=Jo Bloggs,ou=People,dc=corp"));
            Assert.That(storedRpei.DisplayNameSnapshot, Is.EqualTo("Jo Bloggs"));
            Assert.That(storedRpei.ObjectTypeSnapshot, Is.EqualTo("user"));
            Assert.That(storedRpei.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnhandledError));
            Assert.That(storedRpei.ErrorMessage, Is.EqualTo("the connector rejected the change"));
            Assert.That(storedRpei.ErrorStackTrace, Is.EqualTo("at Foo.Bar()"));
            Assert.That(storedRpei.AttributeFlowCount, Is.EqualTo(3));
            Assert.That(storedRpei.OutcomeSummary, Is.EqualTo("Updated 3 attributes"));
            Assert.That(storedRpei.PendingExportId, Is.EqualTo(pendingExportId));
            Assert.That(storedRpei.DeletionPolicySnapshotJson, Is.EqualTo(snapshotJson));
        }

        var storedOutcomes = await readContext.ActivityRunProfileExecutionItemSyncOutcomes
            .AsNoTracking()
            .Where(o => o.ActivityRunProfileExecutionItemId == rpei.Id)
            .ToListAsync();
        Assert.That(storedOutcomes, Has.Count.EqualTo(2));

        using (Assert.EnterMultipleScope())
        {
            var storedRoot = storedOutcomes.Single(o => o.Id == rootOutcome.Id);
            Assert.That(storedRoot.ParentSyncOutcomeId, Is.Null);
            Assert.That(storedRoot.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow));
            Assert.That(storedRoot.TargetEntityId, Is.EqualTo(rootOutcome.TargetEntityId));
            Assert.That(storedRoot.TargetEntityDescription, Is.EqualTo("displayName"));
            Assert.That(storedRoot.DetailCount, Is.EqualTo(1));
            Assert.That(storedRoot.DetailMessage, Is.EqualTo("Jo Bloggs"));
            Assert.That(storedRoot.Ordinal, Is.EqualTo(0));
            Assert.That(storedRoot.ConnectedSystemObjectChangeId, Is.Null);
            Assert.That(storedRoot.SyncRuleId, Is.EqualTo(42));
            Assert.That(storedRoot.SyncRuleName, Is.EqualTo("HR Import"));
            Assert.That(storedRoot.StagedChangeType, Is.Null);

            var storedChild = storedOutcomes.Single(o => o.Id == childOutcome.Id);
            Assert.That(storedChild.ParentSyncOutcomeId, Is.EqualTo(rootOutcome.Id));
            Assert.That(storedChild.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
            Assert.That(storedChild.TargetEntityId, Is.EqualTo(childOutcome.TargetEntityId));
            Assert.That(storedChild.TargetEntityDescription, Is.EqualTo("employeeNumber"));
            Assert.That(storedChild.DetailCount, Is.Null);
            Assert.That(storedChild.DetailMessage, Is.Null);
            Assert.That(storedChild.SyncRuleId, Is.Null);
            Assert.That(storedChild.SyncRuleName, Is.Null);
            Assert.That(storedChild.StagedChangeType, Is.EqualTo(PendingExportChangeType.Update));
        }
    }

    /// <summary>
    /// A batch whose outcomes collide on a duplicate primary key must fail the whole batch: the
    /// single-connection path writes the RPEI row then the outcome rows in one transaction, so a
    /// unique-constraint violation on the outcome write must roll back the RPEI row already written
    /// earlier in the same transaction.
    /// </summary>
    /// <remarks>
    /// Cannot use a dangling <c>ParentSyncOutcomeId</c> for this (the more obvious foreign-key shape):
    /// <c>SyncRepository.FlattenOutcomesRecursive</c> derives every outcome's <c>ParentSyncOutcomeId</c>
    /// from its actual position in the <c>Children</c> tree and overwrites whatever the caller set, and
    /// <c>FlattenSyncOutcomes</c> only walks from roots where <c>ParentSyncOutcomeId</c> is null, so an
    /// outcome given a non-null, non-tree-derived parent id is both overwritten and (if not reachable
    /// from a root) silently dropped rather than ever reaching the write. A duplicate <c>Id</c> across
    /// two root outcomes survives both and reaches the COPY/INSERT unchanged.
    /// </remarks>
    [Test]
    public async Task BulkInsertRpeisAsync_OutcomesWithDuplicatePrimaryKey_LeavesNoRowsBehindAsync()
    {
        var activityId = await SeedActivityAsync();
        var duplicateOutcomeId = Guid.NewGuid();

        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.Updated,
            DisplayNameSnapshot = "Jo Bloggs"
        };
        rpei.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = duplicateOutcomeId,
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow
        });
        rpei.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = duplicateOutcomeId, // same id as the first root outcome: a primary key collision
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected
        });

        await using var writeContext = NewContext();
        var repository = new PostgresDataRepository(writeContext);
        Assert.That(async () => await repository.Sync.BulkInsertRpeisAsync([rpei]),
            Throws.Exception, "a primary key violation on the outcome write must propagate, not be swallowed");

        await using var verify = NewContext();
        var survivingCount = await verify.ActivityRunProfileExecutionItems.CountAsync(r => r.Id == rpei.Id);
        Assert.That(survivingCount, Is.Zero,
            "the RPEI must not survive: the outcome failure must roll back the whole single-connection transaction, including the RPEI row already COPY'd earlier in it");
    }
}

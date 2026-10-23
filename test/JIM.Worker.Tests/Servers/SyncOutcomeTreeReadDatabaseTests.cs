// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of how an Activity item's outcome tree is read back for the REST API (#2033): the
/// item's own outcomes only, nested by parent and ordered by ordinal. Run on a context that does not track, as
/// JIM.Web's does, so nothing relies on EF fixing up the Children navigation.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class SyncOutcomeTreeReadDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL sync outcome tree read tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task GetSyncOutcomeTreeAsync_NestedOutcomes_ReadsTheItemsTreeInOrdinalOrderAsync()
    {
        var activityId = await SeedActivityAsync();
        var projected = Outcome(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, ordinal: 0);
        var provisioned = Outcome(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, ordinal: 1, projected);
        var flow = Outcome(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, ordinal: 0, projected);
        var staged = Outcome(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, ordinal: 0, provisioned);
        staged.StagedChangeType = PendingExportChangeType.Create;
        var item = Item(activityId, projected, provisioned, flow, staged);
        var other = Item(activityId, Outcome(ActivityRunProfileExecutionItemSyncOutcomeType.Joined, ordinal: 0));
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.BulkInsertRpeisAsync([item, other]);

        var tree = await NewJim().Activities.GetSyncOutcomeTreeAsync(item.Id);

        var root = tree.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Projected), "only this item's outcomes");
            Assert.That(root.Children.Select(c => c.OutcomeType), Is.EqualTo(new[]
            {
                ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned
            }), "children nested under their parent, in ordinal order");
            Assert.That(root.Children[1].Children.Single().StagedChangeType, Is.EqualTo(PendingExportChangeType.Create));
        }
    }

    [Test]
    public async Task GetSyncOutcomeTreeAsync_ItemWithNoOutcomes_ReadsNoneAsync()
    {
        var item = Item(await SeedActivityAsync());
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.BulkInsertRpeisAsync([item]);

        Assert.That(await NewJim().Activities.GetSyncOutcomeTreeAsync(item.Id), Is.Empty);
    }

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    private static ActivityRunProfileExecutionItem Item(Guid activityId, params ActivityRunProfileExecutionItemSyncOutcome[] outcomes) => new()
    {
        Id = Guid.NewGuid(),
        ActivityId = activityId,
        ObjectChangeType = ObjectChangeType.Projected,
        SyncOutcomes = [.. outcomes]
    };

    private static ActivityRunProfileExecutionItemSyncOutcome Outcome(
        ActivityRunProfileExecutionItemSyncOutcomeType type, int ordinal, ActivityRunProfileExecutionItemSyncOutcome? parent = null)
    {
        var outcome = new ActivityRunProfileExecutionItemSyncOutcome { OutcomeType = type, Ordinal = ordinal, ParentSyncOutcome = parent };
        parent?.Children.Add(outcome);
        return outcome;
    }

    private async Task<Guid> SeedActivityAsync()
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "Full Synchronisation",
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        return activity.Id;
    }
}

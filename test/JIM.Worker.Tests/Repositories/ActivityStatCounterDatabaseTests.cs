// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the Activity stat counter table (#1078): incremental counter
/// upserts from the bulk RPEI/outcome insert paths, counter-backed stats reads for in-progress
/// Activities, completion-time finalisation, and the lazy legacy fallback.
/// </summary>
/// <remarks>
/// The counter maintenance is raw SQL (multi-row INSERT ... ON CONFLICT) reachable from the sync
/// path, so per the raw-SQL testing rule it needs real-PostgreSQL coverage; the in-memory
/// provider cannot exercise it. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables
/// as the other <c>RequiresPostgres</c> fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ActivityStatCounterDatabaseTests
{
    private string _connectionString = null!;
    private readonly Dictionary<string, string?> _savedDbEnvVars = new();

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Activity stat counter tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        // The bulk RPEI insert switches to parallel COPY connections above a size threshold, and
        // those connections are built from the JIM_DB_* environment variables, not the test
        // context's connection string. Point them at the scratch database for the duration of
        // this fixture so the parallel path cannot write anywhere else.
        SetDbEnvVar(Constants.Config.DatabaseHostname, host);
        SetDbEnvVar(Constants.Config.DatabaseName, dbName);
        SetDbEnvVar(Constants.Config.DatabaseUsername, user);
        SetDbEnvVar(Constants.Config.DatabasePassword, pass);

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        foreach (var (name, value) in _savedDbEnvVars)
            Environment.SetEnvironmentVariable(name, value);
    }

    private void SetDbEnvVar(string name, string value)
    {
        _savedDbEnvVars[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
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

    private static string EnumKey<T>(T value) where T : Enum =>
        Convert.ToInt32(value).ToString(CultureInfo.InvariantCulture);

    private async Task<Activity> SeedActivityAsync(ActivityStatus status = ActivityStatus.InProgress)
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = status,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System",
            Executed = DateTime.UtcNow
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        return activity;
    }

    private static ActivityRunProfileExecutionItem NewRpei(
        Guid activityId,
        ObjectChangeType changeType,
        string? objectTypeSnapshot = "user",
        ActivityRunProfileExecutionItemErrorType? errorType = null,
        NoChangeReason? noChangeReason = null,
        params ActivityRunProfileExecutionItemSyncOutcomeType[] outcomeTypes)
    {
        var rpei = new ActivityRunProfileExecutionItem
        {
            ActivityId = activityId,
            ObjectChangeType = changeType,
            ObjectTypeSnapshot = objectTypeSnapshot,
            ErrorType = errorType,
            NoChangeReason = noChangeReason
        };
        foreach (var outcomeType in outcomeTypes)
            rpei.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome { OutcomeType = outcomeType });
        return rpei;
    }

    private async Task<Dictionary<(ActivityStatDimension Dimension, string Key), long>> GetCountersAsync(Guid activityId)
    {
        await using var ctx = NewContext();
        return await ctx.ActivityStatCounters
            .Where(c => c.ActivityId == activityId)
            .ToDictionaryAsync(c => (c.Dimension, c.Key), c => c.Count);
    }

    [Test]
    public async Task BulkInsertRpeisAsync_SingleConnectionPath_WritesCounterRows()
    {
        var activity = await SeedActivityAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var rpeis = new List<ActivityRunProfileExecutionItem>
        {
            NewRpei(activity.Id, ObjectChangeType.Added, outcomeTypes: ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded),
            NewRpei(activity.Id, ObjectChangeType.Added, outcomeTypes: ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded),
            NewRpei(activity.Id, ObjectChangeType.Updated, objectTypeSnapshot: "group",
                errorType: ActivityRunProfileExecutionItemErrorType.UnhandledError,
                outcomeTypes: [ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow]),
            NewRpei(activity.Id, ObjectChangeType.NoChange, noChangeReason: NoChangeReason.CsoAlreadyCurrent)
        };

        await repository.Sync.BulkInsertRpeisAsync(rpeis);

        var counters = await GetCountersAsync(activity.Id);
        Assert.That(counters[(ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Added))], Is.EqualTo(2));
        Assert.That(counters[(ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Updated))], Is.EqualTo(1));
        Assert.That(counters[(ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.NoChange))], Is.EqualTo(1));
        Assert.That(counters[(ActivityStatDimension.ObjectTypeName, "user")], Is.EqualTo(3));
        Assert.That(counters[(ActivityStatDimension.ObjectTypeName, "group")], Is.EqualTo(1));
        Assert.That(counters[(ActivityStatDimension.ErrorType, EnumKey(ActivityRunProfileExecutionItemErrorType.UnhandledError))], Is.EqualTo(1));
        Assert.That(counters[(ActivityStatDimension.NoChangeReason, EnumKey(NoChangeReason.CsoAlreadyCurrent))], Is.EqualTo(1));
        Assert.That(counters[(ActivityStatDimension.OutcomeType, EnumKey(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded))], Is.EqualTo(2));
        Assert.That(counters[(ActivityStatDimension.OutcomeType, EnumKey(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated))], Is.EqualTo(1));
        Assert.That(counters[(ActivityStatDimension.OutcomeType, EnumKey(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow))], Is.EqualTo(1));
    }

    [Test]
    public async Task BulkInsertRpeisAsync_SecondFlush_AccumulatesCounts()
    {
        var activity = await SeedActivityAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        await repository.Sync.BulkInsertRpeisAsync([NewRpei(activity.Id, ObjectChangeType.Added)]);
        await repository.Sync.BulkInsertRpeisAsync(
            [NewRpei(activity.Id, ObjectChangeType.Added), NewRpei(activity.Id, ObjectChangeType.Added)]);

        var counters = await GetCountersAsync(activity.Id);
        Assert.That(counters[(ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Added))], Is.EqualTo(3));
    }

    [Test]
    public async Task BulkInsertRpeisAsync_ParallelPath_WritesCounterRows()
    {
        var activity = await SeedActivityAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Force the parallel COPY path: it engages at parallelism * 50 items.
        var count = ParallelBatchWriter.GetWriteParallelism() * 50;
        var rpeis = new List<ActivityRunProfileExecutionItem>(count);
        for (var i = 0; i < count; i++)
            rpeis.Add(NewRpei(activity.Id, ObjectChangeType.Added, outcomeTypes: ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded));

        await repository.Sync.BulkInsertRpeisAsync(rpeis);

        var counters = await GetCountersAsync(activity.Id);
        Assert.That(counters[(ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Added))], Is.EqualTo(count));
        Assert.That(counters[(ActivityStatDimension.OutcomeType, EnumKey(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded))], Is.EqualTo(count));

        await using var verify = NewContext();
        Assert.That(await verify.ActivityRunProfileExecutionItems.CountAsync(r => r.ActivityId == activity.Id), Is.EqualTo(count),
            "sanity: the parallel path must actually have persisted the RPEIs");
    }

    [Test]
    public async Task BulkUpdateRpeiOutcomesAsync_NewOutcomes_IncrementsOutcomeCounters()
    {
        var activity = await SeedActivityAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var rpei = NewRpei(activity.Id, ObjectChangeType.Updated, outcomeTypes: ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated);
        await repository.Sync.BulkInsertRpeisAsync([rpei]);

        // Reconciliation-style follow-up: an already-persisted RPEI gains new outcome rows.
        var newOutcome = new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            ActivityRunProfileExecutionItemId = rpei.Id,
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed
        };
        await repository.Sync.BulkUpdateRpeiOutcomesAsync([rpei], [newOutcome]);

        var counters = await GetCountersAsync(activity.Id);
        Assert.That(counters[(ActivityStatDimension.OutcomeType, EnumKey(ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed))], Is.EqualTo(1));
        Assert.That(counters[(ActivityStatDimension.OutcomeType, EnumKey(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated))], Is.EqualTo(1),
            "pre-existing outcome counters must be unaffected by the update path");
        Assert.That(counters[(ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Updated))], Is.EqualTo(1),
            "the update path must not recount RPEI dimensions");
    }

    [Test]
    public async Task GetActivityRunProfileExecutionStats_InProgress_ReadsFromCounterRows()
    {
        var activity = await SeedActivityAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // No outcomes, so the stats derivation uses the ObjectChangeType branch.
        await repository.Sync.BulkInsertRpeisAsync(
            [NewRpei(activity.Id, ObjectChangeType.Added), NewRpei(activity.Id, ObjectChangeType.Added)]);

        // Skew the counter deliberately: if the stats read reflects the skew, it is provably
        // serving from the counter rows and not re-aggregating the RPEI table.
        await using (var skew = NewContext())
        {
            await skew.Database.ExecuteSqlRawAsync(
                @"UPDATE ""ActivityStatCounters"" SET ""Count"" = 999 WHERE ""ActivityId"" = {0} AND ""Dimension"" = {1} AND ""Key"" = {2}",
                activity.Id, (int)ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Added));
        }

        var stats = await repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);

        Assert.That(stats.TotalCsoAdds, Is.EqualTo(999));
    }

    [Test]
    public async Task FinaliseActivityRunProfileExecutionStats_ReplacesSkewedCountersAndSetsFlag()
    {
        var activity = await SeedActivityAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        await repository.Sync.BulkInsertRpeisAsync(
        [
            NewRpei(activity.Id, ObjectChangeType.Added, outcomeTypes: ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded),
            NewRpei(activity.Id, ObjectChangeType.Updated, outcomeTypes: ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated)
        ]);

        // Simulate in-flight drift, then prove finalisation reconciles to exact values.
        await using (var skew = NewContext())
        {
            await skew.Database.ExecuteSqlRawAsync(
                @"UPDATE ""ActivityStatCounters"" SET ""Count"" = 42 WHERE ""ActivityId"" = {0}", activity.Id);
        }

        activity.Status = ActivityStatus.Complete;
        await repository.Activity.FinaliseActivityRunProfileExecutionStatsAsync(activity);
        await repository.Activity.UpdateActivityAsync(activity);

        Assert.That(activity.RunProfileExecutionStatsFinalised, Is.True);

        var counters = await GetCountersAsync(activity.Id);
        Assert.That(counters[(ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Added))], Is.EqualTo(1));
        Assert.That(counters[(ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Updated))], Is.EqualTo(1));
        Assert.That(counters[(ActivityStatDimension.OutcomeType, EnumKey(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded))], Is.EqualTo(1));

        await using var verify = NewContext();
        var persisted = await verify.Activities.SingleAsync(a => a.Id == activity.Id);
        Assert.That(persisted.RunProfileExecutionStatsFinalised, Is.True);

        var stats = await repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);
        Assert.That(stats.TotalCsoAdds, Is.EqualTo(1));
        Assert.That(stats.TotalCsoUpdates, Is.EqualTo(1));
    }

    [Test]
    public async Task GetActivityRunProfileExecutionStats_CompletedWithoutCounters_AggregatesAndLazilyFinalises()
    {
        var activity = await SeedActivityAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        await repository.Sync.BulkInsertRpeisAsync(
        [
            NewRpei(activity.Id, ObjectChangeType.Added),
            NewRpei(activity.Id, ObjectChangeType.Added),
            NewRpei(activity.Id, ObjectChangeType.Updated, errorType: ActivityRunProfileExecutionItemErrorType.UnhandledError)
        ]);

        // Simulate an Activity persisted before the counter table existed: completed, no
        // counter rows, flag unset.
        await using (var legacy = NewContext())
        {
            await legacy.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""ActivityStatCounters"" WHERE ""ActivityId"" = {0}", activity.Id);
            await legacy.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Activities"" SET ""Status"" = {1}, ""RunProfileExecutionStatsFinalised"" = FALSE WHERE ""Id"" = {0}",
                activity.Id, (int)ActivityStatus.Complete);
        }

        var stats = await repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);

        Assert.That(stats.TotalCsoAdds, Is.EqualTo(2), "legacy fallback must aggregate correctly");
        Assert.That(stats.TotalObjectErrors, Is.EqualTo(1));

        await using var verify = NewContext();
        var persisted = await verify.Activities.SingleAsync(a => a.Id == activity.Id);
        Assert.That(persisted.RunProfileExecutionStatsFinalised, Is.True, "first read of a completed legacy Activity must finalise it");
        var counters = await GetCountersAsync(activity.Id);
        Assert.That(counters[(ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Added))], Is.EqualTo(2));

        // Second read must serve from the now-materialised counters and agree.
        var secondRead = await repository.Activity.GetActivityRunProfileExecutionStatsAsync(activity.Id);
        Assert.That(secondRead.TotalCsoAdds, Is.EqualTo(2));
        Assert.That(secondRead.TotalObjectErrors, Is.EqualTo(1));
    }

    [Test]
    public async Task CreateActivityRunProfileExecutionItemsAsync_EfPath_WritesCounterRows()
    {
        var activity = await SeedActivityAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // The Metaverse Object Housekeeping small-batch path persists RPEIs via EF rather than
        // the bulk raw SQL path; it must maintain counters too.
        var rpei = NewRpei(activity.Id, ObjectChangeType.Deleted, objectTypeSnapshot: "person",
            outcomeTypes: ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        await repository.Activity.CreateActivityRunProfileExecutionItemsAsync([rpei]);

        var counters = await GetCountersAsync(activity.Id);
        Assert.That(counters[(ActivityStatDimension.ObjectChangeType, EnumKey(ObjectChangeType.Deleted))], Is.EqualTo(1));
        Assert.That(counters[(ActivityStatDimension.ObjectTypeName, "person")], Is.EqualTo(1));
        Assert.That(counters[(ActivityStatDimension.OutcomeType, EnumKey(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted))], Is.EqualTo(1));
    }
}

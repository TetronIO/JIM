// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that history retention can always remove an expired Activity, whatever change history
/// still names it (#2460). Change records and Activities are trimmed on their own clocks, so a change record can
/// outlive the Activity it was made in; the in-memory provider enforces no foreign keys and cannot see a refusal.
/// Opt-in via <c>JIM_TEST_RESET_*</c>.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ChangeHistoryRetentionDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.TrackAll)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(tracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL change history retention tests.");

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

    [Test]
    public async Task DeleteExpiredActivitiesAsync_ActivityItemNamedByRetainedChange_DeletesActivityAndKeepsChangeUnlinkedAsync()
    {
        // A run that started before the cutoff and changed the object after it: the Activity has expired, its change
        // has not.
        var cutoff = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var activityId = Guid.NewGuid();
        var changeId = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            var type = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = type, Created = cutoff.AddDays(-1) };
            var activity = new Activity
            {
                Id = activityId,
                Created = cutoff.AddMinutes(-5),
                TargetType = ActivityTargetType.ConnectedSystemRunProfile,
                TargetOperationType = ActivityTargetOperationType.Execute,
                InitiatedByType = ActivityInitiatorType.System
            };
            var rpei = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid(), ActivityId = activityId, ObjectChangeType = ObjectChangeType.AttributeFlow };
            seed.AddRange(type, mvo, activity, rpei);
            seed.Add(new MetaverseObjectChange
            {
                Id = changeId,
                MetaverseObject = mvo,
                ChangeTime = cutoff.AddMinutes(5),
                ChangeType = ObjectChangeType.AttributeFlow,
                ActivityRunProfileExecutionItemId = rpei.Id
            });
            await seed.SaveChangesAsync();
        }

        await using (var ctx = NewContext())
        {
            var deleted = await new ChangeHistoryRepository(ctx).DeleteExpiredActivitiesAsync(cutoff, 100);
            Assert.That(deleted, Is.EqualTo(1));
        }

        await using var verify = NewContext(QueryTrackingBehavior.NoTracking);
        var change = await verify.MetaverseObjectChanges.SingleOrDefaultAsync(c => c.Id == changeId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await verify.Activities.AnyAsync(a => a.Id == activityId), Is.False, "the expired Activity is removed");
            Assert.That(change, Is.Not.Null, "change history has its own retention period and outlives the Activity");
            Assert.That(change!.ActivityRunProfileExecutionItemId, Is.Null, "the change lets go of the removed item");
        }
    }
}

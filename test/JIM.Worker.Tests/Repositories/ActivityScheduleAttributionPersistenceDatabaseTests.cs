// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that an Activity's denormalised Schedule attribution (#1196) persists and
/// reads back. The two columns are the permanent record of which Schedule produced an Activity, deliberately
/// carrying no foreign key so they survive the Schedule's deletion; the in-memory provider stores the object
/// graph verbatim and so proves nothing about the mapped columns or the migration that adds them.
/// Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ActivityScheduleAttributionPersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Activity Schedule attribution tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [Test]
    public async Task Activity_WithScheduleAttribution_RoundTripsBothColumnsAsync()
    {
        var scheduleId = Guid.NewGuid();
        var activityId = Guid.NewGuid();

        await using (var write = NewContext())
        {
            write.Activities.Add(new Activity
            {
                Id = activityId,
                TargetType = ActivityTargetType.ConnectedSystemRunProfile,
                TargetOperationType = ActivityTargetOperationType.Execute,
                InitiatedByType = ActivityInitiatorType.System,
                InitiatedByName = "System",
                ScheduleExecutionId = Guid.NewGuid(),
                ScheduleStepIndex = 3,
                ScheduledByScheduleId = scheduleId,
                ScheduledByScheduleName = "Nightly Sync"
            });
            await write.SaveChangesAsync();
        }

        await using var read = NewContext();
        var persisted = await read.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.ScheduledByScheduleId, Is.EqualTo(scheduleId), "the Schedule id column must persist");
            Assert.That(persisted.ScheduledByScheduleName, Is.EqualTo("Nightly Sync"), "the Schedule name column must persist");
        }
    }

    /// <summary>
    /// The Schedule filter options group the Activity history by Schedule id and take each group's most recently
    /// recorded name. That is a correlated ordering inside a group projection, which the in-memory provider is
    /// happy to evaluate client-side but Npgsql must actually translate to SQL; only a real database proves it.
    /// </summary>
    [Test]
    public async Task GetWorkerTaskActivityFilterOptionsAsync_RenamedSchedule_TranslatesAndReturnsTheLatestNameAsync()
    {
        var scheduleId = Guid.NewGuid();

        await using (var write = NewContext())
        {
            write.Activities.AddRange(
                NewScheduledActivity(scheduleId, "Nightly Sync", daysOld: 10),
                NewScheduledActivity(scheduleId, "Overnight Synchronisation", daysOld: 1));
            await write.SaveChangesAsync();
        }

        await using var read = NewContext();
        var repository = new PostgresDataRepository(read);
        var options = await repository.Activity.GetWorkerTaskActivityFilterOptionsAsync();

        var schedule = options.Schedules.SingleOrDefault(s => s.Id == scheduleId);
        Assert.That(schedule, Is.Not.Null, "the Schedule must appear exactly once despite the rename");
        Assert.That(schedule!.Name, Is.EqualTo("Overnight Synchronisation"),
            "the most recently recorded name is the one an administrator recognises");
    }

    private static Activity NewScheduledActivity(Guid scheduleId, string scheduleName, int daysOld) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        TargetOperationType = ActivityTargetOperationType.Execute,
        InitiatedByType = ActivityInitiatorType.System,
        InitiatedByName = "System",
        Created = DateTime.UtcNow.AddDays(-daysOld),
        ScheduledByScheduleId = scheduleId,
        ScheduledByScheduleName = scheduleName
    };

    [Test]
    public async Task Activity_WithoutScheduleAttribution_RoundTripsBothColumnsAsNullAsync()
    {
        var activityId = Guid.NewGuid();

        await using (var write = NewContext())
        {
            write.Activities.Add(new Activity
            {
                Id = activityId,
                TargetType = ActivityTargetType.ConnectedSystemRunProfile,
                TargetOperationType = ActivityTargetOperationType.Execute,
                InitiatedByType = ActivityInitiatorType.User,
                InitiatedByName = "Test User"
            });
            await write.SaveChangesAsync();
        }

        await using var read = NewContext();
        var persisted = await read.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.ScheduledByScheduleId, Is.Null, "an Activity no Schedule produced carries no attribution");
            Assert.That(persisted.ScheduledByScheduleName, Is.Null, "an Activity no Schedule produced carries no attribution");
        }
    }
}

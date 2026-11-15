// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL round trip of the progress writer a paged import's throttle writes through (#2521): the Activity's message
/// and counters, and its steps inserted or updated in place, written on a connection of its own. Only a round trip catches a
/// parameter in the wrong position or a wrong type. Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ActivityProgressWriterDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Activity progress writer tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [Test]
    public async Task WriteAsync_MessageCountersAndSteps_AreWrittenAndStepsUpdatedInPlaceAsync()
    {
        // Arrange
        var activityId = Guid.NewGuid();
        await using (var seed = NewContext())
        {
            seed.Activities.Add(new Activity
            {
                Id = activityId,
                TargetType = ActivityTargetType.ConnectedSystemRunProfile,
                Status = ActivityStatus.InProgress,
                InitiatedByType = ActivityInitiatorType.System
            });
            await seed.SaveChangesAsync();
        }

        var started = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        var fetch = new ActivityPhase
        {
            Id = Guid.NewGuid(), ActivityId = activityId, Order = 1, Key = ActivityPhase.QualifyConnectorKey("fetch"),
            Name = "Fetching objects", ParentKey = RunPhaseKeys.ImportFetch, Status = ActivityPhaseStatus.Active, Started = started
        };

        await using var context = NewContext();
        // Pointed at this test's database, which the environment's connection string does not name.
        var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
        sync.ConnectionStringForParallelWrites = _connectionString;
        var writer = sync.CreateActivityProgressWriter();
        Assert.That(writer, Is.Not.Null, "The repository knows its connection string, so it can write on a connection of its own");

        // Act: the first write inserts the step; the second moves it on.
        await writer!.WriteAsync(new ActivityProgressWrite(activityId, "Reading page 1", 100, 0, [fetch]));
        var completed = new ActivityPhase
        {
            Id = fetch.Id, ActivityId = activityId, Order = 1, Key = fetch.Key, Name = fetch.Name, ParentKey = fetch.ParentKey,
            Status = ActivityPhaseStatus.Completed, Started = started, Ended = started.AddSeconds(42)
        };
        await writer.WriteAsync(new ActivityProgressWrite(activityId, "Reading page 4,200", 420_000, 525_000, [completed]));
        await writer.WriteAsync(new ActivityProgressWrite(activityId, null, 420_100, 525_000, []));

        // Assert
        await using var read = NewContext();
        var activity = await read.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
        var phase = await read.ActivityPhases.AsNoTracking().SingleAsync(p => p.Id == fetch.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.Message, Is.Null, "The last write's message is written as given");
            Assert.That(activity.ObjectsProcessed, Is.EqualTo(420_100));
            Assert.That(activity.ObjectsToProcess, Is.EqualTo(525_000));
            Assert.That(phase.Key, Is.EqualTo(ActivityPhase.QualifyConnectorKey("fetch")));
            Assert.That(phase.ParentKey, Is.EqualTo(RunPhaseKeys.ImportFetch));
            Assert.That(phase.Order, Is.EqualTo(1));
            Assert.That(phase.Status, Is.EqualTo(ActivityPhaseStatus.Completed));
            Assert.That(phase.Started, Is.EqualTo(started));
            Assert.That(phase.Ended, Is.EqualTo(started.AddSeconds(42)));
        }
    }
}

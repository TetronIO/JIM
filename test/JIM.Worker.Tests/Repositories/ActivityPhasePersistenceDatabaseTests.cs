// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the raw-SQL upsert that records Run Profile execution phases
/// (#454). The column completeness test proves the column list matches the model, but it cannot
/// catch values written in the wrong order, a wrong NpgsqlDbType, or an ON CONFLICT clause that
/// updates the wrong columns; only a round trip can. The in-memory provider stores the object
/// graph verbatim, so it cannot catch any of them either. Opt-in via JIM_TEST_RESET_*; ignored
/// when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ActivityPhasePersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Activity phase persistence tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private async Task<Guid> SeedActivityAsync()
    {
        await using var seed = NewContext();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Status = ActivityStatus.InProgress,
            InitiatedByType = ActivityInitiatorType.System
        };
        seed.Activities.Add(activity);
        await seed.SaveChangesAsync();
        return activity.Id;
    }

    [Test]
    public async Task SaveActivityPhasesAsync_NewPhases_PersistsEveryFieldAsync()
    {
        // Arrange
        var activityId = await SeedActivityAsync();
        var started = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var phases = new List<ActivityPhase>
        {
            new()
            {
                Id = Guid.NewGuid(), ActivityId = activityId, Order = 0, Key = RunPhaseKeys.ImportFetch,
                Name = "Importing objects", ParentKey = null, Status = ActivityPhaseStatus.Active, Started = started
            },
            new()
            {
                Id = Guid.NewGuid(), ActivityId = activityId, Order = 1,
                Key = ActivityPhase.QualifyConnectorKey("read"), Name = "Reading file",
                ParentKey = RunPhaseKeys.ImportFetch, Status = ActivityPhaseStatus.Pending
            }
        };

        // Act
        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.Sync.SaveActivityPhasesAsync(phases);
        }

        // Assert: every column, because a value written into the wrong position would still insert
        await using var readContext = NewContext();
        var persisted = await readContext.ActivityPhases.AsNoTracking()
            .Where(p => p.ActivityId == activityId).OrderBy(p => p.Order).ToListAsync();

        Assert.That(persisted, Has.Count.EqualTo(2));
        Assert.That(persisted[0].Id, Is.EqualTo(phases[0].Id));
        Assert.That(persisted[0].Key, Is.EqualTo(RunPhaseKeys.ImportFetch));
        Assert.That(persisted[0].Name, Is.EqualTo("Importing objects"));
        Assert.That(persisted[0].ParentKey, Is.Null);
        Assert.That(persisted[0].Status, Is.EqualTo(ActivityPhaseStatus.Active));
        Assert.That(persisted[0].Started, Is.EqualTo(started));
        Assert.That(persisted[0].Ended, Is.Null);
        Assert.That(persisted[1].Order, Is.EqualTo(1));
        Assert.That(persisted[1].Key, Is.EqualTo(ActivityPhase.QualifyConnectorKey("read")));
        Assert.That(persisted[1].ParentKey, Is.EqualTo(RunPhaseKeys.ImportFetch));
        Assert.That(persisted[1].Status, Is.EqualTo(ActivityPhaseStatus.Pending));
        Assert.That(persisted[1].Started, Is.Null);
    }

    [Test]
    public async Task SaveActivityPhasesAsync_PhaseSavedAgain_UpdatesItsStateInPlaceAsync()
    {
        // Arrange: the shape every transition takes - the row exists, its state moves on
        var activityId = await SeedActivityAsync();
        var started = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        var phase = new ActivityPhase
        {
            Id = Guid.NewGuid(), ActivityId = activityId, Order = 0, Key = RunPhaseKeys.ImportSave,
            Name = "Saving changes", Status = ActivityPhaseStatus.Active, Started = started
        };

        await using (var writeContext = NewContext())
            await new PostgresDataRepository(writeContext).Sync.SaveActivityPhasesAsync([phase]);

        // Act
        phase.Status = ActivityPhaseStatus.Completed;
        phase.Ended = started.AddMinutes(22);
        await using (var writeContext = NewContext())
            await new PostgresDataRepository(writeContext).Sync.SaveActivityPhasesAsync([phase]);

        // Assert
        await using var readContext = NewContext();
        var persisted = await readContext.ActivityPhases.AsNoTracking().SingleAsync(p => p.Id == phase.Id);
        Assert.That(persisted.Status, Is.EqualTo(ActivityPhaseStatus.Completed));
        Assert.That(persisted.Ended, Is.EqualTo(started.AddMinutes(22)));
        Assert.That(persisted.Started, Is.EqualTo(started), "Reopening or completing a step must not disturb when it began.");
        Assert.That(persisted.Duration, Is.EqualTo(TimeSpan.FromMinutes(22)));
    }

    [Test]
    public async Task SaveActivityPhasesAsync_DeclaredThenTransitioned_ReadsBackInRunOrderAsync()
    {
        // Arrange: the real sequence, seeded from the catalogue and driven through two transitions
        var activityId = await SeedActivityAsync();
        var set = ActivityPhaseSet.Declare(activityId, JIM.Models.Staging.ConnectedSystemRunType.FullImport,
            [new JIM.Models.Staging.ConnectorPhase("read", "Reading file")]);

        await using (var writeContext = NewContext())
        {
            var repository = new PostgresDataRepository(writeContext);
            await repository.Sync.SaveActivityPhasesAsync(set.Phases);
            await repository.Sync.SaveActivityPhasesAsync(set.Enter(RunPhaseKeys.ImportFetch, DateTime.UtcNow));
            await repository.Sync.SaveActivityPhasesAsync(set.Enter(ActivityPhase.QualifyConnectorKey("read"), DateTime.UtcNow));
        }

        // Assert
        await using var readContext = NewContext();
        var persisted = await readContext.ActivityPhases.AsNoTracking()
            .Where(p => p.ActivityId == activityId).OrderBy(p => p.Order).ToListAsync();

        Assert.That(persisted.Select(p => p.Key), Is.EqualTo(set.Phases.Select(p => p.Key)));
        Assert.That(persisted.Single(p => p.Key == RunPhaseKeys.ImportConnect).Status, Is.EqualTo(ActivityPhaseStatus.Skipped),
            "A file-based import opens no connection, so that step is recorded as skipped rather than left pending.");
        Assert.That(persisted.Single(p => p.Key == RunPhaseKeys.ImportFetch).Status, Is.EqualTo(ActivityPhaseStatus.Active));
        Assert.That(persisted.Single(p => p.Key == ActivityPhase.QualifyConnectorKey("read")).Status, Is.EqualTo(ActivityPhaseStatus.Active));
    }

    [Test]
    public async Task SaveActivityPhasesAsync_ActivityDeleted_RemovesItsPhasesAsync()
    {
        // Arrange
        var activityId = await SeedActivityAsync();
        var phase = new ActivityPhase
        {
            Id = Guid.NewGuid(), ActivityId = activityId, Order = 0, Key = RunPhaseKeys.ExportPrepare,
            Name = "Preparing export", Status = ActivityPhaseStatus.Completed
        };
        await using (var writeContext = NewContext())
            await new PostgresDataRepository(writeContext).Sync.SaveActivityPhasesAsync([phase]);

        // Act: history retention deletes Activities; their phases must not outlive them
        await using (var deleteContext = NewContext())
        {
            var activity = await deleteContext.Activities.SingleAsync(a => a.Id == activityId);
            deleteContext.Activities.Remove(activity);
            await deleteContext.SaveChangesAsync();
        }

        // Assert
        await using var readContext = NewContext();
        Assert.That(await readContext.ActivityPhases.AnyAsync(p => p.ActivityId == activityId), Is.False);
    }
}

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
/// Real-PostgreSQL verification that the bulk RPEI insert (BulkInsertRpeisAsync), a raw COPY with its own column list,
/// persists the Metaverse Object an export scope review item is about (#1971). A column the COPY list misses is
/// written as NULL for every bulk-written row with no error, and the item page then has no Metaverse Object to show.
/// Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class RpeiMetaverseObjectBulkInsertDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL RPEI Metaverse Object bulk insert tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [Test]
    public async Task BulkInsertRpeisAsync_ExportScopeReviewItem_PersistsTheMetaverseObjectItIsAboutAsync()
    {
        Guid activityId;
        await using (var seed = NewContext())
        {
            var activity = new Activity
            {
                Id = Guid.NewGuid(),
                TargetName = "Delta Synchronisation",
                TargetOperationType = ActivityTargetOperationType.Execute,
                Status = ActivityStatus.Complete,
                InitiatedByType = ActivityInitiatorType.System
            };
            seed.Activities.Add(activity);
            await seed.SaveChangesAsync();
            activityId = activity.Id;
        }

        // No Metaverse Object row is needed: the id is history, not a foreign key, so it outlives the object.
        var metaverseObjectId = Guid.NewGuid();
        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.ExportScopeReview,
            MetaverseObjectId = metaverseObjectId,
            DisplayNameSnapshot = "Carol Ng",
            ObjectTypeSnapshot = "Person"
        };
        await using (var insertContext = NewContext())
        {
            var repository = new PostgresDataRepository(insertContext);
            await repository.Sync.BulkInsertRpeisAsync([rpei]);
        }

        await using var readContext = NewContext();
        var persisted = await readContext.ActivityRunProfileExecutionItems.AsNoTracking().SingleAsync(r => r.Id == rpei.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.ObjectChangeType, Is.EqualTo(ObjectChangeType.ExportScopeReview));
            Assert.That(persisted.MetaverseObjectId, Is.EqualTo(metaverseObjectId));
        }
    }
}

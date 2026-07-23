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
/// Real-PostgreSQL verification that the bulk RPEI field update (BulkUpdateRpeiOutcomesAsync)
/// persists all three co-mutated error fields. The worker's error sites always set ErrorType,
/// ErrorMessage and ErrorStackTrace together as a unit, but the bulk update's SET list carried
/// only the first two, silently dropping the stack trace whenever an error is attached to an
/// already-persisted RPEI. Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class RpeiErrorFieldsBulkUpdateDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL RPEI error-field bulk update tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [Test]
    public async Task BulkUpdateRpeiOutcomesAsync_ErrorAttachedToExistingRpei_PersistsAllThreeErrorFieldsAsync()
    {
        // Arrange: an RPEI persisted without an error, as the sync page flush leaves it
        Guid activityId;
        await using (var seed = NewContext())
        {
            var activity = new Activity
            {
                Id = Guid.NewGuid(),
                TargetName = "Full Sync",
                TargetOperationType = ActivityTargetOperationType.Execute,
                Status = ActivityStatus.Complete,
                InitiatedByType = ActivityInitiatorType.System
            };
            seed.Activities.Add(activity);
            await seed.SaveChangesAsync();
            activityId = activity.Id;
        }

        var rpei = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.Updated,
            DisplayNameSnapshot = "Jo Bloggs"
        };
        await using (var insertContext = NewContext())
        {
            var repository = new PostgresDataRepository(insertContext);
            await repository.Sync.BulkInsertRpeisAsync([rpei]);
        }

        // Act: a later stage (e.g. cross-page reference resolution) attaches an error; the three
        // error fields are always set together as a unit
        rpei.OutcomeSummary = "Reference resolution failed";
        rpei.ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError;
        rpei.ErrorMessage = "The referenced object could not be resolved.";
        rpei.ErrorStackTrace = "at JIM.Application.Servers.SyncEngine...";
        await using (var updateContext = NewContext())
        {
            var repository = new PostgresDataRepository(updateContext);
            await repository.Sync.BulkUpdateRpeiOutcomesAsync([rpei], []);
        }

        // Assert
        await using var readContext = NewContext();
        var persisted = await readContext.ActivityRunProfileExecutionItems.AsNoTracking().SingleAsync(r => r.Id == rpei.Id);
        Assert.That(persisted.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.UnhandledError));
        Assert.That(persisted.ErrorMessage, Is.EqualTo("The referenced object could not be resolved."));
        Assert.That(persisted.ErrorStackTrace, Is.EqualTo("at JIM.Application.Servers.SyncEngine..."),
            "ErrorStackTrace is co-mutated with ErrorType/ErrorMessage and must be persisted with them.");
    }
}

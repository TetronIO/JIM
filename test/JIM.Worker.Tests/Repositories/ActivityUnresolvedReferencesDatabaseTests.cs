// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage for whether a run left references unresolved (#959), which decides whether the Activity's
/// page offers View Affected Objects. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other
/// RequiresPostgres fixtures.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ActivityUnresolvedReferencesDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Activity unresolved reference tests.");

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
    public async Task HasUnresolvedReferencesAsync_AnItemLeftReferencesUnresolved_IsTrueAsync()
    {
        var activityId = await SeedActivityAsync(null, 0, 3);
        await SeedActivityAsync(5);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        Assert.That(await repository.Activity.HasUnresolvedReferencesAsync(activityId), Is.True);
    }

    [Test]
    public async Task HasUnresolvedReferencesAsync_NoItemLeftAny_IsFalseEvenWhenAnotherRunDidAsync()
    {
        var activityId = await SeedActivityAsync(null, 0);
        await SeedActivityAsync(5);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        Assert.That(await repository.Activity.HasUnresolvedReferencesAsync(activityId), Is.False,
            "An item recorded before the count existed (null) or with none left (0) is not unresolved, and another run's items do not count.");
    }

    /// <summary>One Activity with an item per count given.</summary>
    private async Task<Guid> SeedActivityAsync(params int?[] unresolvedReferenceCounts)
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "Full Import",
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.CompleteWithWarning,
            InitiatedByType = ActivityInitiatorType.System
        };
        ctx.Activities.Add(activity);
        ctx.ActivityRunProfileExecutionItems.AddRange(unresolvedReferenceCounts.Select(count => new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            Activity = activity,
            ObjectChangeType = ObjectChangeType.Added,
            UnresolvedReferenceCount = count
        }));
        await ctx.SaveChangesAsync();
        return activity.Id;
    }
}

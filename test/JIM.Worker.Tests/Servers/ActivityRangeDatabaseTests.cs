// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the offset/count Activities read
/// (<see cref="JIM.Application.Servers.ActivityServer.GetActivitiesRangeAsync"/>) that backs the virtualised
/// (infinite-scroll) Activity list. The query core is EF Core LINQ shared with the paged read, so the in-memory
/// tier (<c>ActivityRangeTests</c>) covers the windowing semantics; this fixture proves the same contract against
/// real SQL translation (case-insensitive search via ToLower, the correlated child-Activity subquery, enum
/// Contains) which the in-memory provider evaluates in memory rather than translates.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ActivityRangeDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Activity-range tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
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

    [Test]
    public async Task Range_FirstWindow_ReturnsLeadingSliceAndFullTotalAsync()
    {
        await SeedSequentialActivitiesAsync(10);
        var jim = NewJim();

        var result = await jim.Activities.GetActivitiesRangeAsync(
            startIndex: 0, count: 3, sortBy: "target", sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(a => a.TargetName),
                Is.EqualTo(new[] { "Activity 001", "Activity 002", "Activity 003" }));
        }
    }

    [Test]
    public async Task Range_MidWindow_ReturnsCorrectSliceAtAbsoluteOffsetAsync()
    {
        await SeedSequentialActivitiesAsync(10);
        var jim = NewJim();

        var result = await jim.Activities.GetActivitiesRangeAsync(
            startIndex: 3, count: 3, sortBy: "target", sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(10));
            Assert.That(result.Results.Select(a => a.TargetName),
                Is.EqualTo(new[] { "Activity 004", "Activity 005", "Activity 006" }));
        }
    }

    [Test]
    public async Task Range_TotalCountNotRequested_ReturnsWindowWithNoTotalAsync()
    {
        await SeedSequentialActivitiesAsync(10);
        var jim = NewJim();

        var result = await jim.Activities.GetActivitiesRangeAsync(
            startIndex: 3, count: 3, sortBy: "target", sortDescending: false, includeTotalCount: false);

        using (Assert.EnterMultipleScope())
        {
            // Null rather than zero: the count query did not run, and zero would read as "nothing matches".
            Assert.That(result.TotalResults, Is.Null);
            Assert.That(result.Results.Select(a => a.TargetName),
                Is.EqualTo(new[] { "Activity 004", "Activity 005", "Activity 006" }));
        }
    }

    [Test]
    public async Task Range_CountAboveCap_ClampsToFiveHundredAsync()
    {
        await SeedSequentialActivitiesAsync(505);
        var jim = NewJim();

        var result = await jim.Activities.GetActivitiesRangeAsync(
            startIndex: 0, count: 1000, sortBy: "target", sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            // The window is clamped to bound latency while the total still reflects every matching Activity;
            // see MaxActivityWindowSize in ActivitiesRepository for how the 500 cap was derived.
            Assert.That(result.TotalResults, Is.EqualTo(505));
            Assert.That(result.Results, Has.Count.EqualTo(500));
        }
    }

    [Test]
    public async Task Range_SearchQuery_MatchesTargetAndInitiatorNamesCaseInsensitivelyAsync()
    {
        await using (var ctx = NewContext())
        {
            var byTargetName = NewActivity("Contoso Full Import");
            var byInitiator = NewActivity("Other");
            byInitiator.InitiatedByName = "Connie Contoso";
            var noMatch = NewActivity("Fabrikam Export");
            ctx.Activities.AddRange(byTargetName, byInitiator, noMatch);
            await ctx.SaveChangesAsync();
        }

        var jim = NewJim();
        var result = await jim.Activities.GetActivitiesRangeAsync(
            startIndex: 0, count: 10, searchQuery: "CONTOSO");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(2));
            Assert.That(result.Results.Select(a => a.TargetName), Is.EquivalentTo(new[] { "Contoso Full Import", "Other" }));
        }
    }

    [Test]
    public async Task Range_HasChildActivitiesFilter_ReturnsOnlyParentsWithChildrenAsync()
    {
        await using (var ctx = NewContext())
        {
            var withChild = NewActivity("With Child");
            var child = NewActivity("Child");
            child.ParentActivityId = withChild.Id;
            var withoutChild = NewActivity("Without Child");
            ctx.Activities.AddRange(withChild, child, withoutChild);
            await ctx.SaveChangesAsync();
        }

        var jim = NewJim();
        var result = await jim.Activities.GetActivitiesRangeAsync(
            startIndex: 0, count: 10, hasChildActivities: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(a => a.TargetName), Is.EqualTo(new[] { "With Child" }));
        }
    }

    [Test]
    public async Task Range_FullWindow_MatchesPagedReaderAsync()
    {
        await SeedSequentialActivitiesAsync(10);
        var jim = NewJim();

        var range = await jim.Activities.GetActivitiesRangeAsync(
            startIndex: 0, count: 10, sortBy: "target", sortDescending: false);
        var paged = await jim.Activities.GetActivitiesAsync(
            page: 1, pageSize: 10, sortBy: "target", sortDescending: false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(range.TotalResults, Is.EqualTo(paged.TotalResults));
            Assert.That(range.Results.Select(a => a.TargetName),
                Is.EqualTo(paged.Results.Select(a => a.TargetName)));
        }
    }

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    /// <summary>
    /// Seeds <paramref name="count"/> top-level Activities named "Activity 001", "Activity 002", ...
    /// (zero-padded so lexical order matches numeric order under the target-name sort).
    /// </summary>
    private async Task SeedSequentialActivitiesAsync(int count)
    {
        await using var ctx = NewContext();
        var baseline = DateTime.UtcNow.AddDays(-1);
        for (var i = 1; i <= count; i++)
        {
            var activity = NewActivity($"Activity {i:D3}");
            activity.Created = baseline.AddSeconds(i);
            ctx.Activities.Add(activity);
        }

        await ctx.SaveChangesAsync();
    }

    private static Activity NewActivity(string targetName) => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystem,
        TargetOperationType = ActivityTargetOperationType.Update,
        TargetName = targetName,
        InitiatedByType = ActivityInitiatorType.User,
        InitiatedByName = "Test User",
        Created = DateTime.UtcNow
    };
}

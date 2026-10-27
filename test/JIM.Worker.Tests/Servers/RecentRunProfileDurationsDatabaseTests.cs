// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the two reads behind the Operations page's Activity tab (#2549): the median recent
/// duration per Run Profile, whose per-group <c>Take</c> must translate to SQL rather than evaluate in memory, and the
/// next due Schedule. The in-memory tier (<c>RecentRunProfileDurationsTests</c>, <c>NextDueScheduleTests</c>) covers the
/// semantics; this fixture proves the translation.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class RecentRunProfileDurationsDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL recent-duration tests.");

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
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public async Task GetMedianRecentRunDurationsAsync_SeveralRunProfiles_UsesEachProfilesFiveMostRecentSuccessfulRunsAsync()
    {
        var (first, second, neverRun) = await SeedRunProfilesAsync();
        await using (var ctx = NewContext())
        {
            // First Run Profile: seven successful runs; only the five newest (1..5 minutes) count, median 3.
            for (var i = 1; i <= 7; i++)
                ctx.Activities.Add(NewExecution(first, minutes: i, hoursAgo: i, ActivityStatus.Complete));
            // A newer failed run must not displace any of them.
            ctx.Activities.Add(NewExecution(first, minutes: 500, hoursAgo: 0, ActivityStatus.FailedWithError));

            // Second Run Profile: two runs, median is their mean.
            ctx.Activities.Add(NewExecution(second, minutes: 10, hoursAgo: 1, ActivityStatus.Complete));
            ctx.Activities.Add(NewExecution(second, minutes: 20, hoursAgo: 2, ActivityStatus.CompleteWithWarning));
            await ctx.SaveChangesAsync();
        }

        var result = await NewJim().Activities.GetMedianRecentRunDurationsAsync([first, second, neverRun]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Keys, Is.EquivalentTo(new[] { first, second }));
            Assert.That(result[first], Is.EqualTo(TimeSpan.FromMinutes(3)));
            Assert.That(result[second], Is.EqualTo(TimeSpan.FromMinutes(15)));
        }
    }

    [Test]
    public async Task GetNextDueScheduleAsync_ReturnsEarliestEnabledScheduleAsync()
    {
        var soon = DateTime.UtcNow.AddHours(2);
        Guid expectedId;
        await using (var ctx = NewContext())
        {
            ctx.Schedules.Add(new Schedule { Id = Guid.NewGuid(), Name = "Disabled", IsEnabled = false, NextRunTime = DateTime.UtcNow.AddMinutes(1) });
            ctx.Schedules.Add(new Schedule { Id = Guid.NewGuid(), Name = "Later", IsEnabled = true, NextRunTime = DateTime.UtcNow.AddDays(1) });
            var expected = new Schedule { Id = Guid.NewGuid(), Name = "Soon", IsEnabled = true, NextRunTime = soon };
            ctx.Schedules.Add(expected);
            expectedId = expected.Id;
            await ctx.SaveChangesAsync();
        }

        var result = await NewJim().Scheduler.GetNextDueScheduleAsync();

        Assert.That(result?.Id, Is.EqualTo(expectedId));
    }

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    private async Task<(int First, int Second, int NeverRun)> SeedRunProfilesAsync()
    {
        await using var ctx = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Test System", ConnectorDefinition = connectorDefinition };
        ctx.ConnectorDefinitions.Add(connectorDefinition);
        ctx.ConnectedSystems.Add(system);
        await ctx.SaveChangesAsync();

        var profiles = new[] { "Full Import", "Full Sync", "Export" }
            .Select(name => new ConnectedSystemRunProfile { Name = name, ConnectedSystemId = system.Id, RunType = ConnectedSystemRunType.FullImport })
            .ToList();
        ctx.ConnectedSystemRunProfiles.AddRange(profiles);
        await ctx.SaveChangesAsync();
        return (profiles[0].Id, profiles[1].Id, profiles[2].Id);
    }

    private static Activity NewExecution(int runProfileId, int minutes, int hoursAgo, ActivityStatus status)
    {
        var executed = DateTime.UtcNow.AddHours(-hoursAgo).AddMinutes(-1);
        return new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            TargetName = $"Run Profile {runProfileId}",
            ConnectedSystemRunProfileId = runProfileId,
            Status = status,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedByName = "Test User",
            Created = executed,
            Executed = executed,
            ExecutionTime = TimeSpan.FromMinutes(minutes)
        };
    }
}

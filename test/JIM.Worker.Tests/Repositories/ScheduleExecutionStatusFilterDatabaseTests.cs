// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the Schedule Execution list's status filter: <c>GET /api/v1/schedule-executions
/// ?status=</c> and <c>Get-JIMScheduleExecution -Status</c> used to return every status, because the filter was never
/// passed below the controller. Proves EF Core translates the filter to SQL against the stored status column and that
/// the total count, which drives paging, is taken over the filtered set rather than every execution.
/// </summary>
/// <remarks>
/// The in-memory fixture (<c>ScheduleExecutionRangeTests</c>) proves the LINQ; only a real database proves the
/// translation. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other real-database fixtures;
/// ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScheduleExecutionStatusFilterDatabaseTests
{
    private static readonly DateTime BaseTime = new(2026, 3, 1, 3, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;
    private Guid _scheduleId;

    /// <summary>
    /// A context configured the way JIM.Web configures its own: queries untracked by default. JIM.Web is the host that
    /// serves the Schedule Execution list.
    /// </summary>
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Schedule Execution status filter tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    /// <summary>
    /// Seeds one Schedule with seven executions across five statuses: three Complete With Error, one Complete, one
    /// Failed, one Cancelled and one Queued. Every query below is scoped to this Schedule, so rows left by other
    /// fixtures sharing the database cannot skew the counts.
    /// </summary>
    [SetUp]
    public async Task SetUpAsync()
    {
        await using var ctx = NewContext();
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = "Status Filter Schedule",
            Created = BaseTime,
            IsEnabled = true,
            TriggerType = ScheduleTriggerType.Manual
        };

        var statuses = new[]
        {
            ScheduleExecutionStatus.Complete,
            ScheduleExecutionStatus.CompleteWithError,
            ScheduleExecutionStatus.Failed,
            ScheduleExecutionStatus.CompleteWithError,
            ScheduleExecutionStatus.Cancelled,
            ScheduleExecutionStatus.CompleteWithError,
            ScheduleExecutionStatus.Queued
        };

        for (var i = 0; i < statuses.Length; i++)
        {
            schedule.Executions.Add(new ScheduleExecution
            {
                Id = Guid.NewGuid(),
                ScheduleId = schedule.Id,
                ScheduleName = schedule.Name,
                Status = statuses[i],
                QueuedAt = BaseTime.AddMinutes(i),
                TotalSteps = 1
            });
        }

        ctx.Schedules.Add(schedule);
        await ctx.SaveChangesAsync();
        _scheduleId = schedule.Id;
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await using var ctx = NewContext();
        await ctx.ScheduleExecutions.Where(e => e.ScheduleId == _scheduleId).ExecuteDeleteAsync();
        await ctx.Schedules.Where(s => s.Id == _scheduleId).ExecuteDeleteAsync();
    }

    [Test]
    public async Task GetScheduleExecutionsAsync_StatusFilter_ReturnsOnlyThatStatusWithAFilteredTotalAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Scheduling.GetScheduleExecutionsAsync(
            _scheduleId, page: 1, pageSize: 20, status: ScheduleExecutionStatus.CompleteWithError);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(3));
            Assert.That(result.Results, Has.Count.EqualTo(3));
            Assert.That(result.Results.Select(e => e.Status), Is.All.EqualTo(ScheduleExecutionStatus.CompleteWithError));
        }
    }

    [Test]
    public async Task GetScheduleExecutionsAsync_StatusFilterAcrossPages_PagesOverTheFilteredSetAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        // Three matches at two per page: the second page must hold the one remaining match, which it only does if
        // the total and the window are both taken over the filtered set.
        var first = await repository.Scheduling.GetScheduleExecutionsAsync(
            _scheduleId, page: 1, pageSize: 2, status: ScheduleExecutionStatus.CompleteWithError);
        var second = await repository.Scheduling.GetScheduleExecutionsAsync(
            _scheduleId, page: 2, pageSize: 2, status: ScheduleExecutionStatus.CompleteWithError);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.TotalResults, Is.EqualTo(3));
            Assert.That(first.Results, Has.Count.EqualTo(2));
            Assert.That(second.TotalResults, Is.EqualTo(3));
            Assert.That(second.Results, Has.Count.EqualTo(1));
            Assert.That(first.Results.Concat(second.Results).Select(e => e.Status),
                Is.All.EqualTo(ScheduleExecutionStatus.CompleteWithError));
        }
    }

    [Test]
    public async Task GetScheduleExecutionsAsync_NoStatusFilter_ReturnsEveryStatusAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Scheduling.GetScheduleExecutionsAsync(_scheduleId, page: 1, pageSize: 20);

        Assert.That(result.TotalResults, Is.EqualTo(7));
    }

    [Test]
    public async Task GetScheduleExecutionsRangeAsync_StatusFilter_RestrictsWindowAndTotalAsync()
    {
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var result = await repository.Scheduling.GetScheduleExecutionsRangeAsync(
            _scheduleId, offset: 0, count: 10, status: ScheduleExecutionStatus.Failed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TotalResults, Is.EqualTo(1));
            Assert.That(result.Results.Select(e => e.Status), Is.EqualTo(new[] { ScheduleExecutionStatus.Failed }));
        }
    }
}

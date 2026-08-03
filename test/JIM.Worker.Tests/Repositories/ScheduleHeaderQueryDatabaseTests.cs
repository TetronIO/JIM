// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using JIM.Models.Scheduling;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the Schedules list query (issue #1196). The list projects each Schedule's most
/// recent execution via correlated subqueries; the whole page must cost ONE round trip, never one query per Schedule.
/// </summary>
/// <remarks>
/// The unit-level fixture (<c>SchedulingRepositoryHeaderTests</c>) runs on MockQueryable, which is LINQ-to-Objects: it
/// proves the projection's shape but cannot prove EF Core translates it, and structurally cannot count round trips. A
/// regression to a client-evaluated or per-row query would pass every unit test and quietly turn the Schedules tab
/// into an N+1. Only a real database can catch that, which is what this fixture does by counting the commands the
/// provider actually executes.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other real-database fixtures; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ScheduleHeaderQueryDatabaseTests
{
    private string _connectionString = null!;
    private readonly CommandCountingInterceptor _interceptor = new();

    private JimDbContext NewContext(bool countCommands = false)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

        if (countCommands)
            options.AddInterceptors(_interceptor);

        return new JimDbContext(options.Options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Schedule header query tests.");

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
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            TRUNCATE TABLE ""ScheduleExecutions"", ""ScheduleSteps"", ""Schedules"" RESTART IDENTITY CASCADE;");
        _interceptor.Reset();
    }

    /// <summary>
    /// Seeds the given number of Schedules, each with three executions (the newest of which failed) and two steps, so
    /// that a per-row last-execution query would be plainly visible in the command count.
    /// </summary>
    private async Task<List<Guid>> SeedSchedulesAsync(int scheduleCount)
    {
        await using var ctx = NewContext();
        var ids = new List<Guid>();

        for (var i = 0; i < scheduleCount; i++)
        {
            var schedule = new Schedule
            {
                Id = Guid.NewGuid(),
                Name = $"Schedule {i:D3}",
                Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                IsEnabled = true,
                TriggerType = ScheduleTriggerType.Cron,
                CronExpression = "0 3 * * *"
            };

            schedule.Steps.Add(new ScheduleStep { Id = Guid.NewGuid(), ScheduleId = schedule.Id, StepIndex = 0, Name = "First" });
            schedule.Steps.Add(new ScheduleStep { Id = Guid.NewGuid(), ScheduleId = schedule.Id, StepIndex = 1, Name = "Second" });

            schedule.Executions.Add(NewExecution(schedule, ScheduleExecutionStatus.Completed, new DateTime(2026, 2, 1, 3, 0, 0, DateTimeKind.Utc), 1, 2));
            schedule.Executions.Add(NewExecution(schedule, ScheduleExecutionStatus.Cancelled, new DateTime(2026, 2, 2, 3, 0, 0, DateTimeKind.Utc), 0, 2));
            schedule.Executions.Add(NewExecution(schedule, ScheduleExecutionStatus.Failed, new DateTime(2026, 2, 3, 3, 0, 0, DateTimeKind.Utc), 1, 2, "Connected System unreachable"));

            ctx.Schedules.Add(schedule);
            ids.Add(schedule.Id);
        }

        await ctx.SaveChangesAsync();
        return ids;
    }

    private static ScheduleExecution NewExecution(
        Schedule schedule,
        ScheduleExecutionStatus status,
        DateTime queuedAt,
        int currentStepIndex,
        int totalSteps,
        string? errorMessage = null)
    {
        return new ScheduleExecution
        {
            Id = Guid.NewGuid(),
            ScheduleId = schedule.Id,
            ScheduleName = schedule.Name,
            Status = status,
            QueuedAt = queuedAt,
            StartedAt = queuedAt,
            CompletedAt = queuedAt.AddMinutes(5),
            CurrentStepIndex = currentStepIndex,
            TotalSteps = totalSteps,
            ErrorMessage = errorMessage
        };
    }

    [Test]
    public async Task GetScheduleHeadersAsync_PageOfSchedules_CostsOneQueryForThePageAsync()
    {
        await SeedSchedulesAsync(25);

        await using var ctx = NewContext(countCommands: true);
        var repository = new PostgresDataRepository(ctx);
        var result = await repository.Scheduling.GetScheduleHeadersAsync(1, 25);

        Assert.That(result.Results, Has.Count.EqualTo(25));

        // One command for the count, one for the page. Anything more means the last-execution projection or the step
        // count has become a per-Schedule query.
        Assert.That(_interceptor.CommandCount, Is.EqualTo(2),
            $"the Schedules list must be one count plus one page query; executed:{Environment.NewLine}{string.Join(Environment.NewLine + "---" + Environment.NewLine, _interceptor.CommandTexts)}");
    }

    [Test]
    public async Task GetScheduleHeadersAsync_AgainstRealProvider_ProjectsLastExecutionAndStepCountAsync()
    {
        var ids = await SeedSchedulesAsync(1);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var result = await repository.Scheduling.GetScheduleHeadersAsync(1, 10);

        var header = result.Results.Single(h => h.Id == ids[0]);
        Assert.Multiple(() =>
        {
            Assert.That(header.StepCount, Is.EqualTo(2));
            Assert.That(header.LastExecutionStatus, Is.EqualTo(ScheduleExecutionStatus.Failed), "the newest execution by QueuedAt is the failed one");
            Assert.That(header.LastExecutionCurrentStepIndex, Is.EqualTo(1));
            Assert.That(header.LastExecutionTotalSteps, Is.EqualTo(2));
            Assert.That(header.LastExecutionErrorMessage, Is.EqualTo("Connected System unreachable"));
            Assert.That(header.LastExecutionCompletedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task GetScheduleHeadersAsync_ScheduleWithNoExecutions_ProjectsNullsAgainstRealProviderAsync()
    {
        await using (var seedContext = NewContext())
        {
            seedContext.Schedules.Add(new Schedule
            {
                Id = Guid.NewGuid(),
                Name = "Never Run",
                Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TriggerType = ScheduleTriggerType.Manual
            });
            await seedContext.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var result = await repository.Scheduling.GetScheduleHeadersAsync(1, 10);

        var header = result.Results.Single();
        Assert.Multiple(() =>
        {
            Assert.That(header.StepCount, Is.EqualTo(0));
            Assert.That(header.LastExecutionId, Is.Null);
            Assert.That(header.LastExecutionStatus, Is.Null);
            Assert.That(header.LastExecutionCurrentStepIndex, Is.Null);
            Assert.That(header.LastExecutionTotalSteps, Is.Null);
            Assert.That(header.LastExecutionCompletedAt, Is.Null);
            Assert.That(header.LastExecutionErrorMessage, Is.Null);
        });
    }

    /// <summary>
    /// Counts the commands the provider executes, which is how the "one round trip per page" guarantee is asserted.
    /// </summary>
    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commandTexts = new();

        public int CommandCount => _commandTexts.Count;

        public IReadOnlyList<string> CommandTexts => _commandTexts;

        public void Reset() => _commandTexts.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            _commandTexts.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commandTexts.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            _commandTexts.Add(command.CommandText);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            _commandTexts.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

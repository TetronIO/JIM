// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using JIM.TestSupport;
using Npgsql;

namespace JIM.Worker.Tests.Migrations;

/// <summary>
/// Proves the <c>AddScheduleFailureHandling</c> migration's data mapping (#1787, PRD Scenario 4): after the upgrade
/// every existing Schedule is set to stop when a step fails, a step whose Continue on failure was on is set to continue
/// the Schedule, and a step whose setting was off follows the Schedule, so every run behaves exactly as it did before.
/// <para>
/// Runs against its own scratch database, created and dropped per fixture, migrated in two phases exactly as
/// <see cref="ReplaceStrandedValueSweepPendingWithArmedAtMigrationDatabaseTests"/> does: first to the previous migration
/// (so the boolean column exists and can be seeded), then to head (so the migration under test actually runs). Opt-in
/// via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c> fixtures.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class AddScheduleFailureHandlingMigrationDatabaseTests
{
    private const string ScratchDatabaseName = "jim_1787_migration_test";
    private const string PreviousMigrationId = "20260923224211_AddScheduleStepIdToActivitiesAndWorkerTasks";

    private string _adminConnectionString = null!;
    private string _scratchConnectionString = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL migration data-mapping tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";

        _adminConnectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";
        _scratchConnectionString = $"Host={host};Port={port};Database={ScratchDatabaseName};Username={user};Password={pass}";

        // WITH (FORCE) terminates any connection a previous aborted run left behind.
        await ExecuteAdminSqlAsync($"DROP DATABASE IF EXISTS {ScratchDatabaseName} WITH (FORCE)");
        await ExecuteAdminSqlAsync($"CREATE DATABASE {ScratchDatabaseName}");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_adminConnectionString != null)
            await ExecuteAdminSqlAsync($"DROP DATABASE IF EXISTS {ScratchDatabaseName} WITH (FORCE)");
    }

    [Test]
    public async Task Migration_MapsContinueOnFailureToTheThreeWaySettingAndLeavesSchedulesStoppingAsync()
    {
        var scheduleId = Guid.NewGuid();
        var stepOneId = Guid.NewGuid();
        var stepTwoId = Guid.NewGuid();
        var stepThreeId = Guid.NewGuid();

        // Phase one: bring the scratch database to just before the migration under test, so the old boolean column
        // still exists and can be seeded exactly as an upgrading customer's data would be.
        await using (var priorContext = NewScratchContext())
        {
            await priorContext.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);

            await ExecuteAsync(priorContext,
                @"INSERT INTO ""Schedules""
                    (""Id"", ""Name"", ""BuiltIn"", ""Created"", ""CreatedByType"", ""IsEnabled"", ""LastUpdatedByType"",
                     ""PatternType"", ""TriggerType"")
                  VALUES (@id, '1787-nightly-hr-sync', false, @created, 0, false, 0, 0, 1)",
                ("id", scheduleId));

            await InsertStepAsync(priorContext, stepOneId, scheduleId, 0, continueOnFailure: false);
            await InsertStepAsync(priorContext, stepTwoId, scheduleId, 1, continueOnFailure: true);
            await InsertStepAsync(priorContext, stepThreeId, scheduleId, 2, continueOnFailure: false);
        }

        // Phase two: a fresh context (a new app process, as an upgrade is) applies the migration under test.
        await using (var headContext = NewScratchContext())
        {
            await headContext.Database.MigrateAsync();
        }

        await using var verifyContext = NewScratchContext();
        var schedule = await verifyContext.Schedules.Include(s => s.Steps).SingleAsync(s => s.Id == scheduleId);
        var stepsById = schedule.Steps.ToDictionary(s => s.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(schedule.OnStepFailure, Is.EqualTo(ScheduleFailureBehaviour.Stop),
                "every existing Schedule stops when a step fails, as it did before");
            Assert.That(stepsById[stepOneId].OnFailure, Is.EqualTo(ScheduleStepFailureBehaviour.FollowSchedule),
                "a step whose Continue on failure was off follows the Schedule, which stops");
            Assert.That(stepsById[stepTwoId].OnFailure, Is.EqualTo(ScheduleStepFailureBehaviour.Continue),
                "a step whose Continue on failure was on continues the Schedule");
            Assert.That(stepsById[stepThreeId].OnFailure, Is.EqualTo(ScheduleStepFailureBehaviour.FollowSchedule));
            Assert.That(schedule.Steps.Select(s => ScheduleFailureHandling.ContinuesOnFailure(s, schedule)),
                Is.EqualTo(new[] { false, true, false }), "every step's effective behaviour is unchanged");
        }
    }

    private static Task InsertStepAsync(JimDbContext context, Guid id, Guid scheduleId, int stepIndex, bool continueOnFailure) =>
        // Raw SQL rather than the EF model: at this point in the phased migration the model in this assembly (which no
        // longer declares ContinueOnFailure) does not match the database's actual schema.
        ExecuteAsync(context,
            @"INSERT INTO ""ScheduleSteps""
                (""Id"", ""ScheduleId"", ""StepIndex"", ""StepType"", ""ExecutionMode"", ""ContinueOnFailure"", ""Created"",
                 ""CreatedByType"", ""LastUpdatedByType"")
              VALUES (@id, @scheduleId, @stepIndex, 0, 0, @continueOnFailure, @created, 0, 0)",
            ("id", id), ("scheduleId", scheduleId), ("stepIndex", stepIndex), ("continueOnFailure", continueOnFailure));

    private static async Task ExecuteAsync(JimDbContext context, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.Add(new NpgsqlParameter(name, value));
        command.Parameters.Add(new NpgsqlParameter("created", DateTime.UtcNow));

        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();

        await command.ExecuteNonQueryAsync();
    }

    private JimDbContext NewScratchContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_scratchConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    // CREATE/DROP DATABASE cannot be parameterised or run in a transaction; the name is a constant above, never input.
    private Task ExecuteAdminSqlAsync(string sql) => PostgresTestDatabase.ExecuteDatabaseCreateDropAsync(_adminConnectionString, sql);
}

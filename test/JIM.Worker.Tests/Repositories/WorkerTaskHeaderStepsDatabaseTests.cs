// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that a queue row carries its own run's steps (#1162). Phases are
/// their own table keyed by ActivityId rather than a navigation off the Activity, so the header
/// read batches them and matches them up itself; getting that wrong shows every task another
/// task's steps, which reads as plausible progress rather than as an error. The in-memory provider
/// resolves the object graph for free and so cannot fail this. Opt-in via JIM_TEST_RESET_*;
/// ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class WorkerTaskHeaderStepsDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Worker Task header step tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task ClearQueueAsync()
    {
        // The header read returns every queued Worker Task, so a row left behind by another test
        // would be indistinguishable from one of this test's own.
        await using var ctx = NewContext();
        await ctx.WorkerTasks.ExecuteDeleteAsync();
    }

    private async Task<Guid> SeedRunAsync(string taskName, params ActivityPhase[] phases)
    {
        await using var seed = NewContext();

        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetName = taskName,
            Status = ActivityStatus.InProgress,
            InitiatedByType = ActivityInitiatorType.System
        };
        seed.Activities.Add(activity);

        foreach (var phase in phases)
            phase.ActivityId = activity.Id;
        seed.ActivityPhases.AddRange(phases);

        seed.WorkerTasks.Add(new SynchronisationWorkerTask(0, 0)
        {
            Id = Guid.NewGuid(),
            Status = WorkerTaskStatus.Processing,
            InitiatedByType = ActivityInitiatorType.System,
            Activity = activity
        });

        await seed.SaveChangesAsync();
        return activity.Id;
    }

    private static ActivityPhase Phase(string key, string name, int order, ActivityPhaseStatus status, string? parentKey = null) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Name = name,
        Order = order,
        Status = status,
        ParentKey = parentKey
    };

    [Test]
    public async Task GetWorkerTaskHeadersAsync_SeveralRunsInFlight_EachRowCarriesItsOwnStepsAsync()
    {
        var importActivityId = await SeedRunAsync("HR Database: Full Import",
            Phase(RunPhaseKeys.ImportConnect, "Connecting to Connected System", 0, ActivityPhaseStatus.Completed),
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 1, ActivityPhaseStatus.Active));

        var exportActivityId = await SeedRunAsync("Active Directory: Export",
            Phase(RunPhaseKeys.ExportPrepare, "Preparing export", 0, ActivityPhaseStatus.Completed),
            Phase(RunPhaseKeys.ExportExecute, "Exporting", 1, ActivityPhaseStatus.Completed),
            Phase(RunPhaseKeys.ExportDeferred, "Exporting deferred changes", 2, ActivityPhaseStatus.Active));

        await using var ctx = NewContext();
        var headers = await new PostgresDataRepository(ctx).Tasking.GetWorkerTaskHeadersAsync();

        // Matched by Activity rather than by name: a header's name is derived by looking up the
        // Connected System and Run Profile, which this fixture deliberately does not seed.
        var import = headers.Single(h => h.ActivityId == importActivityId);
        var export = headers.Single(h => h.ActivityId == exportActivityId);

        Assert.Multiple(() =>
        {
            Assert.That(import.Steps, Is.Not.Null);
            Assert.That(import.Steps!.TotalSteps, Is.EqualTo(2));
            Assert.That(import.Steps.CurrentStepName, Is.EqualTo("Importing objects"));

            Assert.That(export.Steps, Is.Not.Null);
            Assert.That(export.Steps!.TotalSteps, Is.EqualTo(3));
            Assert.That(export.Steps.CurrentStepName, Is.EqualTo("Exporting deferred changes"));
        });
    }

    [Test]
    public async Task GetWorkerTaskHeadersAsync_ConnectorReportingItsOwnStep_CountsOnlyTheRunsStepsAsync()
    {
        await SeedRunAsync("HR Database: Full Import",
            Phase(RunPhaseKeys.ImportFetch, "Importing objects", 0, ActivityPhaseStatus.Active),
            Phase(ActivityPhase.QualifyConnectorKey("read"), "Reading the file", 1, ActivityPhaseStatus.Active, RunPhaseKeys.ImportFetch));

        await using var ctx = NewContext();
        var headers = await new PostgresDataRepository(ctx).Tasking.GetWorkerTaskHeadersAsync();

        var header = headers.Single();

        Assert.Multiple(() =>
        {
            Assert.That(header.Steps!.TotalSteps, Is.EqualTo(1),
                "A Connector's step is detail inside the step that called it, so the same Run Profile must not read as a different number of steps per Connector");
            Assert.That(header.Steps.CurrentStepName, Is.EqualTo("Importing objects"));
        });
    }

    [Test]
    public async Task GetWorkerTaskHeadersAsync_TaskThatIsNotARunProfileExecution_CarriesNoStepsAsync()
    {
        // Temporal Scope Reconciliation is not a Run Profile execution and records no phases, like
        // clearing Connected System Objects, example data generation and factory reset. It must
        // degrade to the progress bar it shows today rather than to an empty rail. Chosen over the
        // others here because its header name needs no Connected System to look up, so the fixture
        // stays about phases.
        await using (var seed = NewContext())
        {
            var activity = new Activity
            {
                Id = Guid.NewGuid(),
                TargetType = ActivityTargetType.TemporalScopeReconciliation,
                TargetName = "Temporal Scope Reconciliation",
                Status = ActivityStatus.InProgress,
                InitiatedByType = ActivityInitiatorType.System
            };
            seed.Activities.Add(activity);
            seed.WorkerTasks.Add(new TemporalScopeReconciliationWorkerTask
            {
                Id = Guid.NewGuid(),
                Status = WorkerTaskStatus.Processing,
                InitiatedByType = ActivityInitiatorType.System,
                Activity = activity
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = NewContext();
        var headers = await new PostgresDataRepository(ctx).Tasking.GetWorkerTaskHeadersAsync();

        Assert.That(headers.Single().Steps, Is.Null);
    }
}

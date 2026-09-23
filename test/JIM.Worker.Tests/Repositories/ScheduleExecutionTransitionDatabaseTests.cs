// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the conditional Schedule Execution transitions behind #1768: starting an execution
/// (releasing its first step group), advancing it, and ending it, each of which must only take effect while the
/// execution is still in the status the caller saw, so a cancellation or a competing finish is never overwritten.
/// These are set-based updates inside a transaction; the in-memory provider supports neither, so only a real database
/// proves the SQL, the atomicity and the change-tracker fix-up. Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ScheduleExecutionTransitionDatabaseTests
{
    private string _connectionString = null!;

    /// <summary>
    /// A context configured the way JIM.Web and JIM.Scheduler configure theirs: queries untracked by default, so only
    /// what the code explicitly adds or attaches is tracked. Those are the hosts that start Schedules.
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Schedule Execution transition tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // TryStartScheduleExecutionAsync
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task TryStartScheduleExecutionAsync_QueuedExecution_StartsItAndReleasesOnlyTheFirstStepGroupAsync()
    {
        var (executionId, _) = await SeedExecutionAsync(ScheduleExecutionStatus.Queued, waitingStepIndices: [0, 1, 1]);

        bool started;
        ScheduleExecution execution;
        await using (var ctx = NewContext())
        {
            execution = await ctx.ScheduleExecutions.SingleAsync(e => e.Id == executionId);
            started = await new PostgresDataRepository(ctx).Scheduling.TryStartScheduleExecutionAsync(execution, 0);
        }

        var (stored, tasks) = await ReadAsync(executionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(started, Is.True);
            Assert.That(stored.Status, Is.EqualTo(ScheduleExecutionStatus.InProgress));
            Assert.That(stored.CurrentStepIndex, Is.EqualTo(0));
            Assert.That(stored.StartedAt, Is.Not.Null, "an execution has started once its first step is released");
            Assert.That(tasks.Where(t => t.ScheduleStepIndex == 0).Select(t => t.Status), Is.All.EqualTo(WorkerTaskStatus.Queued));
            Assert.That(tasks.Where(t => t.ScheduleStepIndex == 1).Select(t => t.Status), Is.All.EqualTo(WorkerTaskStatus.WaitingForPreviousStep),
                "only the first step group is released; the rest wait for the Worker to advance to them");

            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.InProgress), "the caller's instance reflects the new state");
            Assert.That(execution.CurrentStepIndex, Is.EqualTo(0));
            Assert.That(execution.StartedAt, Is.Not.Null);
        }
    }

    [Test]
    public async Task TryStartScheduleExecutionAsync_FirstStepGroupIsNotIndexZero_ReleasesThatGroupAndPointsAtItAsync()
    {
        // Step 1 could not be queued and was set to continue, so the first group with anything to run is step 2.
        var (executionId, _) = await SeedExecutionAsync(ScheduleExecutionStatus.Queued, waitingStepIndices: [1, 2]);

        await using (var ctx = NewContext())
        {
            var execution = await ctx.ScheduleExecutions.SingleAsync(e => e.Id == executionId);
            await new PostgresDataRepository(ctx).Scheduling.TryStartScheduleExecutionAsync(execution, 1);
        }

        var (stored, tasks) = await ReadAsync(executionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.CurrentStepIndex, Is.EqualTo(1));
            Assert.That(tasks.Single(t => t.ScheduleStepIndex == 1).Status, Is.EqualTo(WorkerTaskStatus.Queued));
            Assert.That(tasks.Single(t => t.ScheduleStepIndex == 2).Status, Is.EqualTo(WorkerTaskStatus.WaitingForPreviousStep));
        }
    }

    [Test]
    public async Task TryStartScheduleExecutionAsync_CancelledWhileStarting_ChangesNothingAsync()
    {
        // An administrator cancelled the execution while its steps were still being queued. Starting it now would
        // overwrite the cancellation and release work nobody wants.
        var (executionId, _) = await SeedExecutionAsync(ScheduleExecutionStatus.Cancelled, waitingStepIndices: [0, 1]);

        bool started;
        ScheduleExecution execution;
        await using (var ctx = NewContext())
        {
            execution = await ctx.ScheduleExecutions.SingleAsync(e => e.Id == executionId);
            started = await new PostgresDataRepository(ctx).Scheduling.TryStartScheduleExecutionAsync(execution, 0);
        }

        var (stored, tasks) = await ReadAsync(executionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(started, Is.False);
            Assert.That(stored.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled));
            Assert.That(stored.StartedAt, Is.Null);
            Assert.That(tasks.Select(t => t.Status), Is.All.EqualTo(WorkerTaskStatus.WaitingForPreviousStep), "nothing may be released");
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled), "the caller's instance is left as it was");
        }
    }

    [Test]
    public async Task TryStartScheduleExecutionAsync_InsideACallersTransaction_CommitsNothingOfItsOwnAsync()
    {
        // The two statements belong to whichever transaction is already open; the method commits only a transaction
        // it began. Rolling the caller's back must therefore undo both.
        var (executionId, _) = await SeedExecutionAsync(ScheduleExecutionStatus.Queued, waitingStepIndices: [0]);

        await using (var ctx = NewContext())
        {
            await using var outer = await ctx.Database.BeginTransactionAsync();
            var execution = await ctx.ScheduleExecutions.SingleAsync(e => e.Id == executionId);
            var started = await new PostgresDataRepository(ctx).Scheduling.TryStartScheduleExecutionAsync(execution, 0);
            Assert.That(started, Is.True);
            await outer.RollbackAsync();
        }

        var (stored, tasks) = await ReadAsync(executionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(ScheduleExecutionStatus.Queued));
            Assert.That(tasks.Single().Status, Is.EqualTo(WorkerTaskStatus.WaitingForPreviousStep));
        }
    }

    [Test]
    public async Task TryStartScheduleExecutionAsync_TrackedExecution_ALaterSaveDoesNotWriteItBackAsync()
    {
        // The Scheduler creates the execution on its cycle's context (so it is tracked), starts it, and then saves the
        // Schedule's next run time on the same context. By then the Worker may already have finished the only step
        // and completed the execution. That later save must not write the execution back as it stood at the start.
        var (scheduleId, _) = await SeedScheduleAsync();
        var execution = new ScheduleExecution
        {
            Id = Guid.NewGuid(),
            ScheduleId = scheduleId,
            ScheduleName = "Tracked",
            Status = ScheduleExecutionStatus.Queued,
            TotalSteps = 1,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "Test"
        };

        await using var schedulerContext = NewContext();
        var repository = new PostgresDataRepository(schedulerContext);
        await repository.Scheduling.CreateScheduleExecutionAsync(execution);
        await SeedWaitingTaskAsync(execution.Id, stepIndex: 0);

        Assert.That(await repository.Scheduling.TryStartScheduleExecutionAsync(execution, 0), Is.True);

        // The Worker finishes the step and completes the execution on its own context.
        await using (var workerContext = NewContext())
        {
            await workerContext.ScheduleExecutions.Where(e => e.Id == execution.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ScheduleExecutionStatus.Complete));
        }

        // The Scheduler then advances the Schedule's next run time on its original context.
        var schedule = await schedulerContext.Schedules.SingleAsync(s => s.Id == scheduleId);
        schedule.NextRunTime = DateTime.UtcNow.AddDays(1);
        await repository.Scheduling.UpdateScheduleAsync(schedule);

        var (stored, _) = await ReadAsync(execution.Id);
        Assert.That(stored.Status, Is.EqualTo(ScheduleExecutionStatus.Complete),
            "the Scheduler's save must not overwrite the Worker's completion with the execution as it was when started");
    }

    // -----------------------------------------------------------------------------------------------------------------
    // TryAdvanceScheduleExecutionAsync
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task TryAdvanceScheduleExecutionAsync_InProgressExecution_MovesOnAndReleasesThatGroupAsync()
    {
        var (executionId, _) = await SeedExecutionAsync(ScheduleExecutionStatus.InProgress, waitingStepIndices: [1, 2], currentStepIndex: 0);

        bool advanced;
        ScheduleExecution execution;
        await using (var ctx = NewContext())
        {
            execution = await ctx.ScheduleExecutions.SingleAsync(e => e.Id == executionId);
            advanced = await new PostgresDataRepository(ctx).Scheduling.TryAdvanceScheduleExecutionAsync(execution, 1);
        }

        var (stored, tasks) = await ReadAsync(executionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(advanced, Is.True);
            Assert.That(stored.Status, Is.EqualTo(ScheduleExecutionStatus.InProgress));
            Assert.That(stored.CurrentStepIndex, Is.EqualTo(1));
            Assert.That(tasks.Single(t => t.ScheduleStepIndex == 1).Status, Is.EqualTo(WorkerTaskStatus.Queued));
            Assert.That(tasks.Single(t => t.ScheduleStepIndex == 2).Status, Is.EqualTo(WorkerTaskStatus.WaitingForPreviousStep));
            Assert.That(execution.CurrentStepIndex, Is.EqualTo(1), "the caller's instance reflects the new position");
        }
    }

    [Test]
    public async Task TryAdvanceScheduleExecutionAsync_CancelledExecution_ChangesNothingAsync()
    {
        // The step that was running when the execution was cancelled has now finished. Nothing more may run.
        var (executionId, _) = await SeedExecutionAsync(ScheduleExecutionStatus.Cancelled, waitingStepIndices: [1], currentStepIndex: 0);

        bool advanced;
        await using (var ctx = NewContext())
        {
            var execution = await ctx.ScheduleExecutions.SingleAsync(e => e.Id == executionId);
            advanced = await new PostgresDataRepository(ctx).Scheduling.TryAdvanceScheduleExecutionAsync(execution, 1);
        }

        var (stored, tasks) = await ReadAsync(executionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(advanced, Is.False);
            Assert.That(stored.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled));
            Assert.That(stored.CurrentStepIndex, Is.EqualTo(0));
            Assert.That(tasks.Single().Status, Is.EqualTo(WorkerTaskStatus.WaitingForPreviousStep));
        }
    }

    [Test]
    public async Task TryAdvanceScheduleExecutionAsync_AlreadyAtThatStep_ChangesNothingAsync()
    {
        // The Worker and the Scheduler's safety net can both decide to advance the same execution. Only one may, and
        // neither may move it backwards.
        var (executionId, _) = await SeedExecutionAsync(ScheduleExecutionStatus.InProgress, waitingStepIndices: [3], currentStepIndex: 2);

        bool advanced;
        await using (var ctx = NewContext())
        {
            var execution = await ctx.ScheduleExecutions.SingleAsync(e => e.Id == executionId);
            advanced = await new PostgresDataRepository(ctx).Scheduling.TryAdvanceScheduleExecutionAsync(execution, 2);
        }

        var (stored, _) = await ReadAsync(executionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(advanced, Is.False);
            Assert.That(stored.CurrentStepIndex, Is.EqualTo(2));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // TryFinishScheduleExecutionAsync
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task TryFinishScheduleExecutionAsync_StatusStillAllowed_EndsItWithTheReasonAsync()
    {
        var (executionId, _) = await SeedExecutionAsync(ScheduleExecutionStatus.InProgress, waitingStepIndices: []);

        bool finished;
        ScheduleExecution execution;
        await using (var ctx = NewContext())
        {
            execution = await ctx.ScheduleExecutions.SingleAsync(e => e.Id == executionId);
            finished = await new PostgresDataRepository(ctx).Scheduling.TryFinishScheduleExecutionAsync(
                execution, [ScheduleExecutionStatus.InProgress], ScheduleExecutionStatus.Failed, "Step 2 failed.");
        }

        var (stored, _) = await ReadAsync(executionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finished, Is.True);
            Assert.That(stored.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
            Assert.That(stored.ErrorMessage, Is.EqualTo("Step 2 failed."));
            Assert.That(stored.CompletedAt, Is.Not.Null);
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
            Assert.That(execution.ErrorMessage, Is.EqualTo("Step 2 failed."));
            Assert.That(execution.CompletedAt, Is.Not.Null);
        }
    }

    [Test]
    public async Task TryFinishScheduleExecutionAsync_AlreadyCancelled_StaysCancelledAsync()
    {
        // The #1768 cancel-overwrite bug: a step finishing after its execution was cancelled marked the execution
        // Failed or Complete. A finished execution must stay finished.
        var (executionId, _) = await SeedExecutionAsync(ScheduleExecutionStatus.Cancelled, waitingStepIndices: [], errorMessage: "Cancelled by user");

        bool finished;
        await using (var ctx = NewContext())
        {
            var execution = await ctx.ScheduleExecutions.SingleAsync(e => e.Id == executionId);
            finished = await new PostgresDataRepository(ctx).Scheduling.TryFinishScheduleExecutionAsync(
                execution, [ScheduleExecutionStatus.InProgress], ScheduleExecutionStatus.Complete, null);
        }

        var (stored, _) = await ReadAsync(executionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finished, Is.False);
            Assert.That(stored.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled));
            Assert.That(stored.ErrorMessage, Is.EqualTo("Cancelled by user"));
        }
    }

    [Test]
    public async Task TryFinishScheduleExecutionAsync_NoReasonGiven_KeepsTheStoredMessageAsync()
    {
        var (executionId, _) = await SeedExecutionAsync(ScheduleExecutionStatus.Queued, waitingStepIndices: [], errorMessage: "Kept");

        await using (var ctx = NewContext())
        {
            var execution = await ctx.ScheduleExecutions.SingleAsync(e => e.Id == executionId);
            await new PostgresDataRepository(ctx).Scheduling.TryFinishScheduleExecutionAsync(
                execution, [ScheduleExecutionStatus.Queued, ScheduleExecutionStatus.InProgress], ScheduleExecutionStatus.Cancelled, null);
        }

        var (stored, _) = await ReadAsync(executionId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled), "any of the allowed statuses qualifies");
            Assert.That(stored.ErrorMessage, Is.EqualTo("Kept"));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // DeleteWaitingTasksForExecutionAsync
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task DeleteWaitingTasksForExecutionAsync_RecordsTheReasonOnEachCancelledStepAsync()
    {
        var (executionId, _) = await SeedExecutionAsync(ScheduleExecutionStatus.InProgress, waitingStepIndices: [1, 2], currentStepIndex: 0);
        var runningTaskActivityId = await SeedTaskAsync(executionId, stepIndex: 0, WorkerTaskStatus.Processing);

        int deleted;
        await using (var ctx = NewContext())
        {
            deleted = await new PostgresDataRepository(ctx).Tasking.DeleteWaitingTasksForExecutionAsync(
                executionId, ScheduleStepNotRunReasons.EarlierStepStoppedSchedule);
        }

        await using var read = NewContext();
        var remaining = await read.WorkerTasks.Where(t => t.ScheduleExecutionId == executionId).ToListAsync();
        var activities = await read.Activities.Where(a => a.ScheduleExecutionId == executionId).ToListAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deleted, Is.EqualTo(2));
            Assert.That(remaining.Select(t => t.ScheduleStepIndex), Is.EqualTo(new int?[] { 0 }), "only waiting tasks are removed");
            var cancelled = activities.Where(a => a.Id != runningTaskActivityId).ToList();
            Assert.That(cancelled.Select(a => a.Status), Is.All.EqualTo(ActivityStatus.Cancelled));
            Assert.That(cancelled.Select(a => a.Message), Is.All.EqualTo(ScheduleStepNotRunReasons.EarlierStepStoppedSchedule),
                "each cancelled step says why it did not run");
            Assert.That(activities.Single(a => a.Id == runningTaskActivityId).Status, Is.EqualTo(ActivityStatus.InProgress));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Fixture helpers
    // -----------------------------------------------------------------------------------------------------------------

    private async Task<(Guid ScheduleId, Guid StepId)> SeedScheduleAsync()
    {
        await using var ctx = NewContext();
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = $"Transition test {Guid.NewGuid():N}",
            IsEnabled = true,
            TriggerType = ScheduleTriggerType.Manual,
            CreatedByType = ActivityInitiatorType.System,
            CreatedByName = "Test"
        };
        var step = new ScheduleStep
        {
            Id = Guid.NewGuid(),
            ScheduleId = schedule.Id,
            StepIndex = 0,
            StepType = ScheduleStepType.TemporalScopeReconciliation,
            Name = "Reconcile"
        };
        ctx.Schedules.Add(schedule);
        ctx.ScheduleSteps.Add(step);
        await ctx.SaveChangesAsync();
        return (schedule.Id, step.Id);
    }

    private async Task<(Guid ExecutionId, Guid ScheduleId)> SeedExecutionAsync(
        ScheduleExecutionStatus status,
        int[] waitingStepIndices,
        int currentStepIndex = 0,
        string? errorMessage = null)
    {
        var (scheduleId, _) = await SeedScheduleAsync();
        var executionId = Guid.NewGuid();

        await using (var ctx = NewContext())
        {
            ctx.ScheduleExecutions.Add(new ScheduleExecution
            {
                Id = executionId,
                ScheduleId = scheduleId,
                ScheduleName = "Transition test",
                Status = status,
                CurrentStepIndex = currentStepIndex,
                TotalSteps = waitingStepIndices.Distinct().Count() + 1,
                InitiatedByType = ActivityInitiatorType.System,
                InitiatedByName = "Test",
                ErrorMessage = errorMessage
            });
            await ctx.SaveChangesAsync();
        }

        foreach (var stepIndex in waitingStepIndices)
            await SeedWaitingTaskAsync(executionId, stepIndex);

        return (executionId, scheduleId);
    }

    private Task<Guid> SeedWaitingTaskAsync(Guid executionId, int stepIndex) =>
        SeedTaskAsync(executionId, stepIndex, WorkerTaskStatus.WaitingForPreviousStep);

    private async Task<Guid> SeedTaskAsync(Guid executionId, int stepIndex, WorkerTaskStatus status)
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.TemporalScopeReconciliation,
            TargetOperationType = ActivityTargetOperationType.Execute,
            TargetName = "Temporal Scope Reconciliation",
            Status = ActivityStatus.InProgress,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "Test",
            ScheduleExecutionId = executionId,
            ScheduleStepIndex = stepIndex
        };
        ctx.Activities.Add(activity);
        ctx.WorkerTasks.Add(new TemporalScopeReconciliationWorkerTask
        {
            Id = Guid.NewGuid(),
            Status = status,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "Test",
            ScheduleExecutionId = executionId,
            ScheduleStepIndex = stepIndex,
            Activity = activity
        });
        await ctx.SaveChangesAsync();
        return activity.Id;
    }

    private async Task<(ScheduleExecution Execution, List<WorkerTask> Tasks)> ReadAsync(Guid executionId)
    {
        await using var ctx = NewContext();
        var execution = await ctx.ScheduleExecutions.SingleAsync(e => e.Id == executionId);
        var tasks = await ctx.WorkerTasks.Where(t => t.ScheduleExecutionId == executionId).ToListAsync();
        return (execution, tasks);
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Models.Activities;
using JIM.Models.Exceptions;
using JIM.Models.Tasking;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests;

/// <summary>
/// Which failures <c>Worker.SafeFailActivityAsync</c> records through a fresh DbContext first. A failure raised
/// while a page was being persisted leaves the run's own context holding that page's unsaved entities, so saving
/// the Activity on it re-attempts the same doomed write: the Activity is then only failed at the third attempt,
/// after two misleading Error lines (found by Scenario 023, where JIM's own integrity guard threw mid-flush and
/// the second attempt failed on a foreign key for execution items that were never written).
/// </summary>
[TestFixture]
public class WorkerFailActivityContextTests
{
    [Test]
    public void ShouldFailOnFreshContextFirst_DbUpdateException_ReturnsTrue()
    {
        Assert.That(Worker.ShouldFailOnFreshContextFirst(new DbUpdateException("save failed")), Is.True);
    }

    [Test]
    public void ShouldFailOnFreshContextFirst_SyncPersistenceExceptionWrappingANonDatabaseException_ReturnsTrue()
    {
        // The worker's own wrapper for "this page failed while persisting", whatever the inner cause: here the
        // unresolved-generated-value integrity guard, which is not a database exception at all.
        var exception = new SyncPersistenceException(
            "Failed to persist synchronisation changes on page 1 of 1 for Connected System 'Directory'.",
            new InvalidOperationException("Pending Export attribute change still has an unresolved generated value marker."),
            page: 1, totalPages: 1, connectedSystemName: "Directory");

        Assert.That(Worker.ShouldFailOnFreshContextFirst(exception), Is.True);
    }

    [Test]
    public void ShouldFailOnFreshContextFirst_ExceptionOutsidePersistence_ReturnsFalse()
    {
        // A failure outside persistence (a connector error, say) leaves the context usable, and failing the Activity
        // on it keeps whatever the run had already recorded alongside the failure.
        Assert.That(Worker.ShouldFailOnFreshContextFirst(new InvalidOperationException("connector failed")), Is.False);
    }

    /// <summary>
    /// #1874: a sync run that failed with a database exception was recorded as Complete. The failure is written through a
    /// fresh context's copy of the Activity, and the task's own copy, which the Worker completes the task with, still
    /// said InProgress, so completing the task completed the Activity over the top of the failure and cleared its error.
    /// Two contexts over one store stand in for the task's context and the fresh one, as the Worker's factory provides.
    /// </summary>
    [Test]
    public async Task SafeFailActivityAsync_FailedOnAFreshContext_StaysFailedWhenTheWorkerTaskIsCompletedAsync()
    {
        // Arrange
        using var harness = await FreshContextHarness.CreateAsync(persistedStatus: ActivityStatus.InProgress);

        // Act: fail the run the way the sync-run catch does, then complete the task the way the Worker always does next.
        await harness.Worker.SafeFailActivityAsync(harness.TaskJim, harness.Activity,
            new DbUpdateException("simulated staging failure"), "Unhandled exception whilst executing sync run");
        await harness.TaskJim.Tasking.CompleteWorkerTaskAsync(harness.WorkerTask);

        // Assert
        var persisted = await harness.ReadPersistedActivityAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Activity.Status, Is.EqualTo(ActivityStatus.FailedWithError),
                "the task's own copy of the Activity must reflect the failure that was recorded");
            Assert.That(persisted.Status, Is.EqualTo(ActivityStatus.FailedWithError),
                "completing the worker task must not complete a failed Activity");
            Assert.That(persisted.ErrorMessage, Does.Contain("simulated staging failure"),
                "the failure's error must survive the worker task's completion");
        }
    }

    /// <summary>
    /// The same divergence when the fresh context finds the Activity already terminal (here cancelled elsewhere while
    /// the run was failing): the task's copy must take that state too, or completing the task overwrites it.
    /// </summary>
    [Test]
    public async Task SafeFailActivityAsync_ActivityAlreadyTerminal_KeepsThatStateWhenTheWorkerTaskIsCompletedAsync()
    {
        // Arrange
        using var harness = await FreshContextHarness.CreateAsync(persistedStatus: ActivityStatus.Cancelled);

        // Act
        await harness.Worker.SafeFailActivityAsync(harness.TaskJim, harness.Activity,
            new DbUpdateException("simulated staging failure"), "Unhandled exception whilst executing sync run");
        await harness.TaskJim.Tasking.CompleteWorkerTaskAsync(harness.WorkerTask);

        // Assert
        var persisted = await harness.ReadPersistedActivityAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Activity.Status, Is.EqualTo(ActivityStatus.Cancelled));
            Assert.That(persisted.Status, Is.EqualTo(ActivityStatus.Cancelled),
                "completing the worker task must not complete an Activity that had already ended");
        }
    }

    /// <summary>
    /// A Worker whose factory hands out JimApplications over their own contexts on one in-memory store, and a task
    /// context holding the run's Activity as the sync run left it: in progress. The persisted row can be seeded in a
    /// different state, standing in for another context having ended the Activity already.
    /// </summary>
    private sealed class FreshContextHarness : IDisposable
    {
        private readonly DbContextOptions<JimDbContext> _options;

        private FreshContextHarness(DbContextOptions<JimDbContext> options, JimApplication taskJim, Worker worker, Activity activity, WorkerTask workerTask)
        {
            _options = options;
            TaskJim = taskJim;
            Worker = worker;
            Activity = activity;
            WorkerTask = workerTask;
        }

        public JimApplication TaskJim { get; }

        public Worker Worker { get; }

        public Activity Activity { get; }

        public WorkerTask WorkerTask { get; }

        public static async Task<FreshContextHarness> CreateAsync(ActivityStatus persistedStatus)
        {
            TestUtilities.SetEnvironmentVariables();
            var options = new DbContextOptionsBuilder<JimDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            JimApplication NewJim() => new(new PostgresDataRepository(new JimDbContext(options)), syncRepository: new JIM.InMemoryData.SyncRepository());

            var activityId = Guid.NewGuid();
            var workerTaskId = Guid.NewGuid();
            await using (var seedContext = new JimDbContext(options))
            {
                var seededActivity = NewInProgressActivity(activityId);
                seededActivity.Status = persistedStatus;
                seedContext.Activities.Add(seededActivity);
                seedContext.WorkerTasks.Add(new SynchronisationWorkerTask(1, 1) { Id = workerTaskId, Status = WorkerTaskStatus.Processing, Activity = seededActivity });
                await seedContext.SaveChangesAsync();
            }

            // The task's copy, as the sync run holds it: detached from the seed, still in progress.
            var activity = NewInProgressActivity(activityId);
            var workerTask = new SynchronisationWorkerTask(1, 1) { Id = workerTaskId, Status = WorkerTaskStatus.Processing, Activity = activity };

            var jimFactory = new Mock<IJimApplicationFactory>();
            jimFactory.Setup(f => f.Create()).Returns(NewJim);
            var worker = new Worker(jimFactory.Object, new Mock<IConnectorFactory>().Object, new Mock<IDbContextFactory<JimDbContext>>().Object);
            return new FreshContextHarness(options, NewJim(), worker, activity, workerTask);
        }

        public async Task<Activity> ReadPersistedActivityAsync()
        {
            await using var readContext = new JimDbContext(_options);
            return await readContext.Activities.AsNoTracking().SingleAsync(a => a.Id == Activity.Id);
        }

        public void Dispose()
        {
            TaskJim.Dispose();
            Worker.Dispose();
        }

        private static Activity NewInProgressActivity(Guid id) => new()
        {
            Id = id,
            Status = ActivityStatus.InProgress,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetName = "Delta Import",
            Executed = DateTime.UtcNow,
            Message = "Saving changes"
        };
    }
}

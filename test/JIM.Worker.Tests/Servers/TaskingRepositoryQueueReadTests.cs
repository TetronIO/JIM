// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.ExampleData;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.PostgresData;
using MockQueryable.Moq;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// How the Operations queue reads its rows. Two faults are pinned here, both reachable from an
/// ordinary queue: naming a Worker Task must not go to a second database, and a task whose
/// Connected System has since been deleted must not take the whole queue down with it.
/// </summary>
/// <remarks>
/// Driven through a mocked <see cref="JimDbContext"/> with no database behind it, which is the
/// point: a read that reaches for a connection of its own cannot pass this fixture, whereas one
/// that uses the context it was handed needs nothing else.
/// </remarks>
[TestFixture]
public class TaskingRepositoryQueueReadTests
{
    private static Activity NewActivity() => new()
    {
        Id = Guid.NewGuid(),
        InitiatedByType = ActivityInitiatorType.System,
        TargetType = ActivityTargetType.ConnectedSystem
    };

    private static JimApplication BuildApplication(
        List<WorkerTask> workerTasks,
        List<ConnectedSystem>? connectedSystems = null,
        List<ConnectedSystemRunProfile>? runProfiles = null)
    {
        // Moq's proxy runs JimDbContext's parameterless constructor, which builds a connection
        // string. The values point at a host called "dummy", so nothing that actually reaches for a
        // connection can pass this fixture; a read that uses the context it was handed needs none.
        TestUtilities.SetEnvironmentVariables();

        var mockDbContext = new Mock<JimDbContext>();
        mockDbContext.Setup(db => db.WorkerTasks).Returns(workerTasks.BuildMockDbSet().Object);
        mockDbContext.Setup(db => db.ActivityPhases).Returns(new List<ActivityPhase>().BuildMockDbSet().Object);
        mockDbContext.Setup(db => db.ConnectedSystems).Returns((connectedSystems ?? []).BuildMockDbSet().Object);
        mockDbContext.Setup(db => db.ConnectedSystemRunProfiles).Returns((runProfiles ?? []).BuildMockDbSet().Object);
        mockDbContext.Setup(db => db.ExampleDataTemplates).Returns(new List<ExampleDataTemplate>().BuildMockDbSet().Object);

        return new JimApplication(new PostgresDataRepository(mockDbContext.Object));
    }

    [Test]
    public async Task GetWorkerTaskHeadersAsync_NamingATask_ReadsFromTheContextItWasGivenAsync()
    {
        // The read used to open a JimDbContext of its own, per row: an extra pooled connection for
        // every task in the queue, configured from environment variables rather than from whatever
        // the caller was working against. Nothing in this fixture answers to that second context.
        var connectedSystem = new ConnectedSystem { Id = 7, Name = "HR Database" };
        var workerTasks = new List<WorkerTask>
        {
            new ClearConnectedSystemObjectsWorkerTask(connectedSystem.Id)
            {
                Id = Guid.NewGuid(),
                InitiatedByType = ActivityInitiatorType.System,
                Activity = NewActivity()
            }
        };

        using var jim = BuildApplication(workerTasks, [connectedSystem]);

        var headers = await jim.Tasking.GetWorkerTaskHeadersAsync();

        Assert.That(headers.Single().Name, Is.EqualTo("HR Database"));
    }

    [Test]
    public async Task GetWorkerTaskHeadersAsync_ClearTaskWhoseConnectedSystemHasGone_StillReturnsTheQueueAsync()
    {
        // Deleting a Connected System leaves any queued clear task for it behind, and naming that
        // task by Single() threw rather than returned. One orphaned row took out the whole queue,
        // including the rows an administrator would have needed to work out why.
        var workerTasks = new List<WorkerTask>
        {
            new ClearConnectedSystemObjectsWorkerTask(404)
            {
                Id = Guid.NewGuid(),
                InitiatedByType = ActivityInitiatorType.System,
                Activity = NewActivity()
            }
        };

        using var jim = BuildApplication(workerTasks);

        var headers = await jim.Tasking.GetWorkerTaskHeadersAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers, Has.Count.EqualTo(1));
            Assert.That(headers[0].Name, Is.EqualTo("Connected System 404"),
                "Named the same way a delete task for a missing system already is, so the two read alike");
        }
    }

    [Test]
    public async Task GetWorkerTaskHeadersAsync_SynchronisationTaskWhoseRunProfileHasGone_NamesItLikeTheOthersAsync()
    {
        // Deleting a Connected System cascades to its Run Profiles, so this is the same orphan as
        // the clear task above, reached by a different route. It did not throw, but it named itself
        // "Run Profile not found!", which reads as a fault in JIM rather than as a row describing
        // configuration that has been deleted.
        var workerTasks = new List<WorkerTask>
        {
            new SynchronisationWorkerTask(7, 404)
            {
                Id = Guid.NewGuid(),
                InitiatedByType = ActivityInitiatorType.System,
                Activity = NewActivity()
            }
        };

        using var jim = BuildApplication(workerTasks);

        var headers = await jim.Tasking.GetWorkerTaskHeadersAsync();

        Assert.That(headers.Single().Name, Is.EqualTo("Run Profile 404"));
    }

    [Test]
    public async Task GetWorkerTaskHeadersAsync_SynchronisationTask_CarriesItsRunProfileAndExecutionModeAsync()
    {
        // The Operations Activity tab (#2549) shows the Connected System and Run Profile in their own columns,
        // estimates start times from the Run Profile's recent durations, and groups tasks the worker will start
        // together; all three need more than the combined display name.
        var connectedSystem = new ConnectedSystem { Id = 7, Name = "HR Database" };
        var runProfile = new ConnectedSystemRunProfile { Id = 12, Name = "Full Import", ConnectedSystemId = connectedSystem.Id };
        var workerTasks = new List<WorkerTask>
        {
            new SynchronisationWorkerTask(connectedSystem.Id, runProfile.Id)
            {
                Id = Guid.NewGuid(),
                InitiatedByType = ActivityInitiatorType.System,
                ExecutionMode = WorkerTaskExecutionMode.Parallel,
                Activity = NewActivity()
            }
        };

        using var jim = BuildApplication(workerTasks, [connectedSystem], [runProfile]);

        var header = (await jim.Tasking.GetWorkerTaskHeadersAsync()).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.ConnectedSystemRunProfileId, Is.EqualTo(12));
            Assert.That(header.ConnectedSystemName, Is.EqualTo("HR Database"));
            Assert.That(header.RunProfileName, Is.EqualTo("Full Import"));
            Assert.That(header.ExecutionMode, Is.EqualTo(WorkerTaskExecutionMode.Parallel));
        }
    }

    [Test]
    public async Task GetWorkerTaskHeadersAsync_ProcessingTask_CarriesWhenItStartedButAQueuedOneDoesNotAsync()
    {
        // The worker stamps the Activity's Executed time when it starts the task; before that the value is only
        // when the Activity was created, which is not a start at all.
        var started = new DateTime(2026, 10, 27, 14, 25, 0, DateTimeKind.Utc);
        var processingActivity = NewActivity();
        processingActivity.Executed = started;
        var queuedActivity = NewActivity();
        queuedActivity.Executed = started.AddMinutes(-30);
        var processing = new ClearConnectedSystemObjectsWorkerTask(7)
        {
            Id = Guid.NewGuid(), InitiatedByType = ActivityInitiatorType.System, Status = WorkerTaskStatus.Processing, Activity = processingActivity
        };
        var queued = new ClearConnectedSystemObjectsWorkerTask(7)
        {
            Id = Guid.NewGuid(), InitiatedByType = ActivityInitiatorType.System, Status = WorkerTaskStatus.Queued, Activity = queuedActivity
        };

        using var jim = BuildApplication([processing, queued], [new ConnectedSystem { Id = 7, Name = "HR Database" }]);

        var headers = await jim.Tasking.GetWorkerTaskHeadersAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers.Single(h => h.Id == processing.Id).Started, Is.EqualTo(started));
            Assert.That(headers.Single(h => h.Id == queued.Id).Started, Is.Null);
        }
    }

    [Test]
    public async Task GetWorkerTaskHeadersAsync_SynchronisationTaskWhoseRunProfileHasGone_KeepsTheIdWithoutNamesAsync()
    {
        var workerTasks = new List<WorkerTask>
        {
            new SynchronisationWorkerTask(7, 404)
            {
                Id = Guid.NewGuid(),
                InitiatedByType = ActivityInitiatorType.System,
                Activity = NewActivity()
            }
        };

        using var jim = BuildApplication(workerTasks);

        var header = (await jim.Tasking.GetWorkerTaskHeadersAsync()).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(header.ConnectedSystemRunProfileId, Is.EqualTo(404));
            Assert.That(header.ConnectedSystemName, Is.Null);
            Assert.That(header.RunProfileName, Is.Null);
        }
    }

    [Test]
    public async Task GetWorkerTaskHeadersAsync_ExampleDataTaskWhoseTemplateHasGone_NamesItLikeTheOthersAsync()
    {
        var workerTasks = new List<WorkerTask>
        {
            new ExampleDataTemplateWorkerTask
            {
                Id = Guid.NewGuid(),
                TemplateId = 404,
                InitiatedByType = ActivityInitiatorType.System,
                Activity = NewActivity()
            }
        };

        using var jim = BuildApplication(workerTasks);

        var headers = await jim.Tasking.GetWorkerTaskHeadersAsync();

        Assert.That(headers.Single().Name, Is.EqualTo("Example Data Template 404"));
    }

    [Test]
    public async Task GetWorkerTaskHeadersAsync_MixedQueueWithOneOrphanedTask_NamesEveryOtherRowAsync()
    {
        // The damage from the throw was never confined to the offending row: the read builds the
        // whole list before returning, so one orphan lost every row.
        var connectedSystem = new ConnectedSystem { Id = 7, Name = "HR Database" };
        var workerTasks = new List<WorkerTask>
        {
            new ClearConnectedSystemObjectsWorkerTask(404)
            {
                Id = Guid.NewGuid(),
                InitiatedByType = ActivityInitiatorType.System,
                Activity = NewActivity()
            },
            new ClearConnectedSystemObjectsWorkerTask(connectedSystem.Id)
            {
                Id = Guid.NewGuid(),
                InitiatedByType = ActivityInitiatorType.System,
                Activity = NewActivity()
            }
        };

        using var jim = BuildApplication(workerTasks, [connectedSystem]);

        var headers = await jim.Tasking.GetWorkerTaskHeadersAsync();

        Assert.That(headers.Select(h => h.Name), Is.EquivalentTo(new[] { "Connected System 404", "HR Database" }));
    }
}

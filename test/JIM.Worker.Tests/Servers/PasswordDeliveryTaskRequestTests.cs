// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Tasking;
using JIM.PostgresData;
using MockQueryable.Moq;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Asking for a Password Delivery pass without asking twice (#1119).
/// <para>
/// A password change, a Connected System being enabled and the worker's idle housekeeping all want the same
/// thing: the queue drained soon. Each raising its own Worker Task would fill the Operations queue with
/// identical passes, most of which would find the work already done. What is pinned here is that a request
/// already waiting to run satisfies a new one, and that a pass already <em>running</em> does not, because it may
/// have read the queue before the new work reached it.
/// </para>
/// </summary>
[TestFixture]
public class PasswordDeliveryTaskRequestTests
{
    private const int ConnectedSystemId = 12;
    private const int OtherConnectedSystemId = 13;

    /// <summary>
    /// Builds the repository de-duplication reads against a mocked context holding the given queue.
    /// </summary>
    private static JimApplication BuildApplication(List<WorkerTask> workerTasks)
    {
        TestUtilities.SetEnvironmentVariables();

        var mockDbContext = new Mock<JimDbContext>();
        mockDbContext.Setup(db => db.WorkerTasks).Returns(workerTasks.BuildMockDbSet().Object);

        return new JimApplication(new PostgresDataRepository(mockDbContext.Object));
    }

    private static PasswordDeliveryWorkerTask QueuedTask(int? connectedSystemId, WorkerTaskStatus status = WorkerTaskStatus.Queued) =>
        new()
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            Status = status,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "Password Synchronisation"
        };

    [Test]
    public async Task HasQueuedPasswordDeliveryTaskAsync_EmptyQueue_IsFalseAsync()
    {
        var jim = BuildApplication([]);

        var queued = await jim.Repository.Tasking.HasQueuedPasswordDeliveryTaskAsync(ConnectedSystemId);

        Assert.That(queued, Is.False);
    }

    [Test]
    public async Task HasQueuedPasswordDeliveryTaskAsync_TaskForTheSameSystem_IsTrueAsync()
    {
        var jim = BuildApplication([QueuedTask(ConnectedSystemId)]);

        var queued = await jim.Repository.Tasking.HasQueuedPasswordDeliveryTaskAsync(ConnectedSystemId);

        Assert.That(queued, Is.True);
    }

    [Test]
    public async Task HasQueuedPasswordDeliveryTaskAsync_TaskForAnotherSystem_IsFalseAsync()
    {
        var jim = BuildApplication([QueuedTask(OtherConnectedSystemId)]);

        var queued = await jim.Repository.Tasking.HasQueuedPasswordDeliveryTaskAsync(ConnectedSystemId);

        Assert.That(queued, Is.False, "A pass aimed at one system does nothing for another.");
    }

    [Test]
    public async Task HasQueuedPasswordDeliveryTaskAsync_TaskForEverySystem_CoversANamedSystemAsync()
    {
        var jim = BuildApplication([QueuedTask(null)]);

        var queued = await jim.Repository.Tasking.HasQueuedPasswordDeliveryTaskAsync(ConnectedSystemId);

        Assert.That(queued, Is.True, "A pass over every system will reach this one too.");
    }

    [Test]
    public async Task HasQueuedPasswordDeliveryTaskAsync_TaskForOneSystemAndEverySystemWanted_IsFalseAsync()
    {
        var jim = BuildApplication([QueuedTask(ConnectedSystemId)]);

        var queued = await jim.Repository.Tasking.HasQueuedPasswordDeliveryTaskAsync(null);

        Assert.That(queued, Is.False, "A pass over one system leaves every other system undelivered.");
    }

    [Test]
    public async Task HasQueuedPasswordDeliveryTaskAsync_TaskAlreadyRunning_IsFalseAsync()
    {
        var jim = BuildApplication([QueuedTask(ConnectedSystemId, WorkerTaskStatus.Processing)]);

        var queued = await jim.Repository.Tasking.HasQueuedPasswordDeliveryTaskAsync(ConnectedSystemId);

        Assert.That(queued, Is.False,
            "A running pass may have read the queue before this work reached it, so it cannot be relied on to deliver it.");
    }

    [Test]
    public async Task HasQueuedPasswordDeliveryTaskAsync_UnrelatedTaskType_IsFalseAsync()
    {
        var jim = BuildApplication([
            new TemporalScopeReconciliationWorkerTask
            {
                Id = Guid.NewGuid(),
                Status = WorkerTaskStatus.Queued,
                InitiatedByType = ActivityInitiatorType.System
            }
        ]);

        var queued = await jim.Repository.Tasking.HasQueuedPasswordDeliveryTaskAsync(null);

        Assert.That(queued, Is.False);
    }

    [Test]
    public async Task RequestPasswordDeliveryAsync_NothingQueued_RaisesATaskAsync()
    {
        var harness = new RequestHarness(alreadyQueued: false);

        var raised = await harness.Application.Tasking.RequestPasswordDeliveryAsync(ConnectedSystemId, "Password Synchronisation");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(raised, Is.True);
            Assert.That(harness.Created, Has.Exactly(1).Items);
            Assert.That(harness.Created[0].ConnectedSystemId, Is.EqualTo(ConnectedSystemId));
            Assert.That(harness.Created[0].InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
            Assert.That(harness.Created[0].InitiatedByName, Is.EqualTo("Password Synchronisation"));
        }
    }

    [Test]
    public async Task RequestPasswordDeliveryAsync_EverySystem_RaisesAnUnscopedTaskAsync()
    {
        var harness = new RequestHarness(alreadyQueued: false);

        await harness.Application.Tasking.RequestPasswordDeliveryAsync(null, "Housekeeping");

        Assert.That(harness.Created[0].ConnectedSystemId, Is.Null);
    }

    [Test]
    public async Task RequestPasswordDeliveryAsync_OneAlreadyQueued_RaisesNothingAsync()
    {
        var harness = new RequestHarness(alreadyQueued: true);

        var raised = await harness.Application.Tasking.RequestPasswordDeliveryAsync(ConnectedSystemId, "Password Synchronisation");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(raised, Is.False);
            Assert.That(harness.Created, Is.Empty);
        }
    }

    /// <summary>
    /// A JimApplication whose tasking repository records what was created and answers the de-duplication read
    /// with whatever the test wants it to say.
    /// </summary>
    private sealed class RequestHarness
    {
        public JimApplication Application { get; }

        public List<PasswordDeliveryWorkerTask> Created { get; } = [];

        public RequestHarness(bool alreadyQueued)
        {
            TestUtilities.SetEnvironmentVariables();

            var repository = new Mock<IRepository>();
            var tasking = new Mock<ITaskingRepository>();
            var activity = new Mock<IActivityRepository>();

            tasking.Setup(t => t.HasQueuedPasswordDeliveryTaskAsync(It.IsAny<int?>())).ReturnsAsync(alreadyQueued);
            tasking.Setup(t => t.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
                .Callback<WorkerTask>(t =>
                {
                    if (t is PasswordDeliveryWorkerTask passwordDeliveryTask)
                        Created.Add(passwordDeliveryTask);
                })
                .Returns(Task.CompletedTask);

            repository.Setup(r => r.Tasking).Returns(tasking.Object);
            repository.Setup(r => r.Activity).Returns(activity.Object);

            Application = new JimApplication(repository.Object);
        }
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Tasking;
using JIM.PostgresData;
using MockQueryable.Moq;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Verifies that GetWorkerTaskHeadersAsync produces a display name and type for every Worker Task
/// subtype. The Operations Queue renders these directly, so a subtype missing from the mapping
/// surfaces to administrators as "Unknown WorkerTask type" (as Temporal Scope Reconciliation tasks
/// did; observed during issue #307 runtime validation).
/// </summary>
[TestFixture]
public class TaskingServerWorkerTaskHeaderTests
{
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var workerTasks = new List<WorkerTask>
        {
            new TemporalScopeReconciliationWorkerTask
            {
                Id = Guid.NewGuid(),
                InitiatedByType = ActivityInitiatorType.System,
                InitiatedByName = "Test Scheduler",
                Activity = new Activity
                {
                    Id = Guid.NewGuid(),
                    InitiatedByType = ActivityInitiatorType.System,
                    TargetType = ActivityTargetType.ConnectedSystemRunProfile
                }
            }
        };

        var mockDbContext = new Mock<JimDbContext>();
        mockDbContext.Setup(db => db.WorkerTasks).Returns(workerTasks.BuildMockDbSet().Object);

        _jim = new JimApplication(new PostgresDataRepository(mockDbContext.Object));
    }

    [TearDown]
    public void TearDown()
    {
        _jim.Dispose();
    }

    [Test]
    public async Task GetWorkerTaskHeadersAsync_TemporalScopeReconciliationTask_HasDisplayNameAndTypeAsync()
    {
        var headers = await _jim.Tasking.GetWorkerTaskHeadersAsync();

        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].Name, Is.EqualTo("Temporal Scope Reconciliation"));
        Assert.That(headers[0].Type, Is.EqualTo("Temporal Scope Reconciliation Worker Task"));
    }
}

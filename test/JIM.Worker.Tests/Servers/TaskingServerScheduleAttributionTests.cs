// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests that TaskingServer denormalises the producing Schedule's id and name onto every Activity a
/// Schedule Execution creates (#1196). Schedule -> ScheduleExecution cascades on delete and Activity holds
/// no foreign key to either, so deleting a Schedule would blank out any join-based attribution for every
/// historical Activity. Copying the values at creation time follows the same durability reasoning as the
/// ScheduleExecutionId/ScheduleStepIndex copy beside it, and as ScheduleExecution.ScheduleName itself.
/// </summary>
[TestFixture]
public class TaskingServerScheduleAttributionTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private JimApplication _application = null!;
    private readonly List<Activity> _createdActivities = [];

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _createdActivities.Clear();
        _mockRepository = new Mock<IRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();

        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);

        _mockActivityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivities.Add(a))
            .Returns(Task.CompletedTask);
        _mockTaskingRepository.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Returns(Task.CompletedTask);

        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task CreateWorkerTaskAsync_ScheduledTask_CopiesTheScheduleIdAndNameOntoTheActivityAsync()
    {
        var scheduleId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync(new ScheduleExecution
            {
                Id = executionId,
                ScheduleId = scheduleId,
                ScheduleName = "Nightly Sync"
            });

        var task = TemporalScopeReconciliationWorkerTask.ForSystem("Scheduler");
        task.ScheduleExecutionId = executionId;
        task.ScheduleStepIndex = 2;

        await _application.Tasking.CreateWorkerTaskAsync(task);

        Assert.That(_createdActivities, Has.Count.EqualTo(1), "the task type creates exactly one Activity");
        var activity = _createdActivities[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ScheduledByScheduleId, Is.EqualTo(scheduleId),
                "the Schedule id must be denormalised so the attribution survives the Schedule's deletion");
            Assert.That(activity.ScheduledByScheduleName, Is.EqualTo("Nightly Sync"),
                "the Schedule name must be denormalised so history reads correctly after a rename or deletion");
            Assert.That(activity.ScheduleExecutionId, Is.EqualTo(executionId));
            Assert.That(activity.ScheduleStepIndex, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task CreateWorkerTaskAsync_UnscheduledTask_LeavesTheScheduleAttributionNullAsync()
    {
        var task = TemporalScopeReconciliationWorkerTask.ForSystem("Scheduler");

        await _application.Tasking.CreateWorkerTaskAsync(task);

        Assert.That(_createdActivities, Has.Count.EqualTo(1), "the task type creates exactly one Activity");
        var activity = _createdActivities[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ScheduledByScheduleId, Is.Null, "no Schedule produced this Activity");
            Assert.That(activity.ScheduledByScheduleName, Is.Null, "no Schedule produced this Activity");
        }
        _mockSchedulingRepository.Verify(r => r.GetScheduleExecutionAsync(It.IsAny<Guid>()), Times.Never,
            "an unscheduled task must not pay for a Schedule Execution lookup");
    }

    [Test]
    public async Task CreateWorkerTaskAsync_ScheduleExecutionAlreadyPruned_LeavesTheScheduleAttributionNullAsync()
    {
        var executionId = Guid.NewGuid();
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(executionId))
            .ReturnsAsync((ScheduleExecution?)null);

        var task = TemporalScopeReconciliationWorkerTask.ForSystem("Scheduler");
        task.ScheduleExecutionId = executionId;
        task.ScheduleStepIndex = 0;

        await _application.Tasking.CreateWorkerTaskAsync(task);

        Assert.That(_createdActivities, Has.Count.EqualTo(1));
        var activity = _createdActivities[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.ScheduledByScheduleId, Is.Null, "there is nothing to attribute the Activity to");
            Assert.That(activity.ScheduledByScheduleName, Is.Null, "there is nothing to attribute the Activity to");
            Assert.That(activity.ScheduleExecutionId, Is.EqualTo(executionId),
                "the execution context itself is still copied, exactly as before");
        }
    }
}

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
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the History Retention Cleanup schedule step (issue #1118): that a step of that type queues a
/// HistoryRetentionCleanupWorkerTask carrying the execution and step it came from, and that the task is
/// tracked by a System-targeted Activity under the same target type manual and API-initiated cleanups use.
/// <para>
/// The step's attribution is what makes retention visible at all. It used to run on a six-hourly timer inside
/// the worker's idle loop, where nothing said when it last ran, when it would run next, or what it removed.
/// </para>
/// </summary>
[TestFixture]
public class SchedulerServerHistoryRetentionCleanupTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;
    private List<WorkerTask> _capturedTasks = null!;
    private List<Activity> _capturedActivities = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);

        _application = new JimApplication(_mockRepository.Object);

        _capturedTasks = new List<WorkerTask>();
        _mockTaskingRepository.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(task => _capturedTasks.Add(task))
            .Returns(Task.CompletedTask);

        _capturedActivities = new List<Activity>();
        _mockActivityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(activity => _capturedActivities.Add(activity))
            .Returns(Task.CompletedTask);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task StartScheduleExecution_HistoryRetentionCleanupStep_QueuesCleanupTaskAsync()
    {
        var execution = await StartCleanupScheduleAsync();

        Assert.That(execution, Is.Not.Null);
        Assert.That(_capturedTasks, Has.Count.EqualTo(1));

        var task = _capturedTasks.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(task, Is.InstanceOf<HistoryRetentionCleanupWorkerTask>());
            Assert.That(task.Status, Is.EqualTo(WorkerTaskStatus.Queued));
            Assert.That(task.ScheduleStepIndex, Is.EqualTo(0));
            Assert.That(task.ScheduleExecutionId, Is.EqualTo(execution!.Id),
                "the task must name the execution it belongs to, or the step's outcome cannot be read back " +
                "against the Schedule that ran it");
        }
    }

    [Test]
    public async Task StartScheduleExecution_HistoryRetentionCleanupStep_TracksTheTaskWithARetentionCleanupActivityAsync()
    {
        await StartCleanupScheduleAsync();

        var activity = _capturedActivities.Single(a => a.TargetType == ActivityTargetType.HistoryRetentionCleanup);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.TargetName, Is.EqualTo("History Retention Cleanup"));
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Delete));
            Assert.That(_capturedTasks.Single().Activity, Is.SameAs(activity),
                "the worker completes the Activity the task carries; an unattached one would be left in flight");
        }
    }

    private async Task<ScheduleExecution?> StartCleanupScheduleAsync()
    {
        var scheduleId = Guid.NewGuid();
        var schedule = new Schedule
        {
            Id = scheduleId,
            Name = "History Retention Cleanup",
            BuiltIn = true,
            IsEnabled = true,
            Steps = new List<ScheduleStep>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ScheduleId = scheduleId,
                    StepIndex = 0,
                    Name = "Clean Up Expired History",
                    StepType = ScheduleStepType.HistoryRetentionCleanup,
                    ExecutionMode = StepExecutionMode.Sequential
                }
            }
        };

        return await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Scheduler Service");
    }
}

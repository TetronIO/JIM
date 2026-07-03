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
/// Tests for the Temporal Scope Reconciliation schedule step (issue #892): that a step of type
/// TemporalScopeReconciliation queues a TemporalScopeReconciliationWorkerTask, and that the failure-safe
/// watermark helper resolves the previous successfully completed execution's start time.
/// </summary>
[TestFixture]
public class SchedulerServerTemporalScopeReconciliationTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;
    private List<WorkerTask> _capturedTasks = null!;

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
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task StartScheduleExecution_TemporalScopeReconciliationStep_QueuesReconciliationTaskAsync()
    {
        // Arrange: a schedule with a single Temporal Scope Reconciliation step.
        var scheduleId = Guid.NewGuid();
        var schedule = new Schedule
        {
            Id = scheduleId,
            Name = "Temporal Scope Reconciliation",
            BuiltIn = true,
            IsEnabled = true,
            Steps = new List<ScheduleStep>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ScheduleId = scheduleId,
                    StepIndex = 0,
                    Name = "Reconcile Temporal Scope",
                    StepType = ScheduleStepType.TemporalScopeReconciliation,
                    ExecutionMode = StepExecutionMode.Sequential
                }
            }
        };

        // Act
        var execution = await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Scheduler Service");

        // Assert
        Assert.That(execution, Is.Not.Null);
        Assert.That(_capturedTasks, Has.Count.EqualTo(1));
        var task = _capturedTasks.Single();
        Assert.That(task, Is.InstanceOf<TemporalScopeReconciliationWorkerTask>());
        Assert.That(task.Status, Is.EqualTo(WorkerTaskStatus.Queued));
        Assert.That(task.ScheduleStepIndex, Is.EqualTo(0));
        Assert.That(task.ScheduleExecutionId, Is.EqualTo(execution!.Id));
    }

    [Test]
    public async Task GetTemporalScopeReconciliationWatermark_PreviousCompletedExists_ReturnsItsStartedAtAsync()
    {
        var currentExecutionId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var currentStartedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var previousStartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(currentExecutionId))
            .ReturnsAsync(new ScheduleExecution { Id = currentExecutionId, ScheduleId = scheduleId, StartedAt = currentStartedAt });
        _mockSchedulingRepository.Setup(r => r.GetLastCompletedScheduleExecutionAsync(scheduleId, currentStartedAt))
            .ReturnsAsync(new ScheduleExecution { Id = Guid.NewGuid(), ScheduleId = scheduleId, Status = ScheduleExecutionStatus.Completed, StartedAt = previousStartedAt });

        var result = await _application.Scheduler.GetTemporalScopeReconciliationWatermarkAsync(currentExecutionId);

        Assert.That(result, Is.EqualTo(previousStartedAt));
    }

    [Test]
    public async Task GetTemporalScopeReconciliationWatermark_NoPreviousCompleted_ReturnsNullAsync()
    {
        var currentExecutionId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(currentExecutionId))
            .ReturnsAsync(new ScheduleExecution { Id = currentExecutionId, ScheduleId = scheduleId, StartedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) });
        _mockSchedulingRepository.Setup(r => r.GetLastCompletedScheduleExecutionAsync(scheduleId, It.IsAny<DateTime>()))
            .ReturnsAsync((ScheduleExecution?)null);

        var result = await _application.Scheduler.GetTemporalScopeReconciliationWatermarkAsync(currentExecutionId);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetTemporalScopeReconciliationWatermark_ExecutionNotFound_ReturnsNullAsync()
    {
        var currentExecutionId = Guid.NewGuid();
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(currentExecutionId))
            .ReturnsAsync((ScheduleExecution?)null);

        var result = await _application.Scheduler.GetTemporalScopeReconciliationWatermarkAsync(currentExecutionId);

        Assert.That(result, Is.Null);
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests the built-in Schedule protection (issue #892, Phase 5): a built-in Schedule (such as the
/// seeded Temporal Scope Reconciliation schedule) may be enabled, disabled and re-timed, but must not
/// be deleted, nor may its steps be added to, changed or removed. Delete and the step guards are
/// enforced authoritatively in the application layer; the rename guard lives at the API controller (the
/// sole rename write-path), covered by SchedulesControllerTests.
/// </summary>
[TestFixture]
public class SchedulerServerBuiltInGuardTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);
        // CRUD now writes an audit Activity via the application layer, so the Activity repository must be present.
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public void DeleteScheduleAsync_BuiltInSchedule_ThrowsInvalidOperation()
    {
        var schedule = new Schedule { Id = Guid.NewGuid(), Name = "Temporal Scope Reconciliation", BuiltIn = true };

        Assert.ThatAsync(async () => await _application.Scheduler.DeleteScheduleAsync(schedule, ActivityInitiatorType.User, Guid.NewGuid(), "Test User"),
            Throws.InstanceOf<InvalidOperationException>());

        _mockSchedulingRepository.Verify(r => r.DeleteScheduleAsync(It.IsAny<Schedule>()), Times.Never);
    }

    [Test]
    public async Task DeleteScheduleAsync_NonBuiltInSchedule_DeletesViaRepositoryAsync()
    {
        var schedule = new Schedule { Id = Guid.NewGuid(), Name = "Nightly Full Sync", BuiltIn = false };
        _mockSchedulingRepository.Setup(r => r.DeleteScheduleAsync(It.IsAny<Schedule>()))
            .Returns(Task.CompletedTask);

        await _application.Scheduler.DeleteScheduleAsync(schedule, ActivityInitiatorType.User, Guid.NewGuid(), "Test User");

        _mockSchedulingRepository.Verify(r => r.DeleteScheduleAsync(schedule), Times.Once);
    }

    [Test]
    public void UpdateScheduleStepAsync_ParentBuiltIn_ThrowsInvalidOperation()
    {
        var scheduleId = Guid.NewGuid();
        var builtInSchedule = new Schedule { Id = scheduleId, Name = "Temporal Scope Reconciliation", BuiltIn = true };
        var step = new ScheduleStep { Id = Guid.NewGuid(), ScheduleId = scheduleId, StepType = ScheduleStepType.TemporalScopeReconciliation };
        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(scheduleId)).ReturnsAsync(builtInSchedule);

        Assert.ThatAsync(async () => await _application.Scheduler.UpdateScheduleStepAsync(step),
            Throws.InstanceOf<InvalidOperationException>());

        _mockSchedulingRepository.Verify(r => r.UpdateScheduleStepAsync(It.IsAny<ScheduleStep>()), Times.Never);
    }

    [Test]
    public void DeleteScheduleStepAsync_ParentBuiltIn_ThrowsInvalidOperation()
    {
        var scheduleId = Guid.NewGuid();
        var builtInSchedule = new Schedule { Id = scheduleId, Name = "Temporal Scope Reconciliation", BuiltIn = true };
        var step = new ScheduleStep { Id = Guid.NewGuid(), ScheduleId = scheduleId, StepType = ScheduleStepType.TemporalScopeReconciliation };
        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(scheduleId)).ReturnsAsync(builtInSchedule);

        Assert.ThatAsync(async () => await _application.Scheduler.DeleteScheduleStepAsync(step),
            Throws.InstanceOf<InvalidOperationException>());

        _mockSchedulingRepository.Verify(r => r.DeleteScheduleStepAsync(It.IsAny<ScheduleStep>()), Times.Never);
    }

    [Test]
    public void CreateScheduleStepAsync_ParentBuiltIn_ThrowsInvalidOperation()
    {
        var scheduleId = Guid.NewGuid();
        var builtInSchedule = new Schedule { Id = scheduleId, Name = "Temporal Scope Reconciliation", BuiltIn = true };
        var step = new ScheduleStep { Id = Guid.NewGuid(), ScheduleId = scheduleId, StepType = ScheduleStepType.RunProfile };
        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(scheduleId)).ReturnsAsync(builtInSchedule);

        Assert.ThatAsync(async () => await _application.Scheduler.CreateScheduleStepAsync(step),
            Throws.InstanceOf<InvalidOperationException>());

        _mockSchedulingRepository.Verify(r => r.CreateScheduleStepAsync(It.IsAny<ScheduleStep>()), Times.Never);
    }

    [Test]
    public async Task UpdateScheduleStepAsync_ParentNotBuiltIn_UpdatesViaRepositoryAsync()
    {
        var scheduleId = Guid.NewGuid();
        var userSchedule = new Schedule { Id = scheduleId, Name = "Nightly Full Sync", BuiltIn = false };
        var step = new ScheduleStep { Id = Guid.NewGuid(), ScheduleId = scheduleId, StepType = ScheduleStepType.RunProfile };
        _mockSchedulingRepository.Setup(r => r.GetScheduleAsync(scheduleId)).ReturnsAsync(userSchedule);
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleStepAsync(It.IsAny<ScheduleStep>())).Returns(Task.CompletedTask);

        await _application.Scheduler.UpdateScheduleStepAsync(step);

        _mockSchedulingRepository.Verify(r => r.UpdateScheduleStepAsync(step), Times.Once);
    }
}

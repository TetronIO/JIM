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
/// Tests that Schedule configuration changes are tracked by an immutable Activity (issue #892), the same as
/// Connected Systems and Synchronisation Rules. Every create/update/delete through the application layer must
/// record an attributed, completed Activity with the correct target type and operation. Internal run-time
/// bookkeeping (NextRunTime / LastRunTime) bypasses these methods and is deliberately not audited.
/// </summary>
[TestFixture]
public class SchedulerServerActivityAuditTests
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
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task CreateScheduleAsync_WritesAttributedCreateActivityAndPersistsAsync()
    {
        Activity? captured = null;
        _mockActivityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => captured = a).Returns(Task.CompletedTask);
        _mockActivityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockSchedulingRepository.Setup(r => r.CreateScheduleAsync(It.IsAny<Schedule>())).Returns(Task.CompletedTask);

        var schedule = new Schedule { Id = Guid.NewGuid(), Name = "Nightly Full Sync" };
        var userId = Guid.NewGuid();

        await _application.Scheduler.CreateScheduleAsync(schedule, ActivityInitiatorType.User, userId, "Alice Admin");

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.TargetType, Is.EqualTo(ActivityTargetType.Schedule));
        Assert.That(captured.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
        Assert.That(captured.TargetName, Is.EqualTo("Nightly Full Sync"));
        Assert.That(captured.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(captured.InitiatedById, Is.EqualTo(userId));
        Assert.That(captured.InitiatedByName, Is.EqualTo("Alice Admin"));
        _mockSchedulingRepository.Verify(r => r.CreateScheduleAsync(schedule), Times.Once);
        _mockActivityRepository.Verify(r => r.UpdateActivityAsync(It.IsAny<Activity>()), Times.Once); // completed
    }

    [Test]
    public async Task UpdateScheduleAsync_WritesUpdateActivityAndPersistsAsync()
    {
        Activity? captured = null;
        _mockActivityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => captured = a).Returns(Task.CompletedTask);
        _mockActivityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleAsync(It.IsAny<Schedule>())).Returns(Task.CompletedTask);

        var schedule = new Schedule { Id = Guid.NewGuid(), Name = "Delta Sync Schedule" };

        await _application.Scheduler.UpdateScheduleAsync(schedule, ActivityInitiatorType.User, Guid.NewGuid(), "Bob Admin");

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.TargetType, Is.EqualTo(ActivityTargetType.Schedule));
        Assert.That(captured.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(captured.TargetName, Is.EqualTo("Delta Sync Schedule"));
        _mockSchedulingRepository.Verify(r => r.UpdateScheduleAsync(schedule), Times.Once);
        _mockActivityRepository.Verify(r => r.UpdateActivityAsync(It.IsAny<Activity>()), Times.Once);
    }

    [Test]
    public async Task DeleteScheduleAsync_NonBuiltIn_WritesDeleteActivityAndPersistsAsync()
    {
        Activity? captured = null;
        _mockActivityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => captured = a).Returns(Task.CompletedTask);
        _mockActivityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockSchedulingRepository.Setup(r => r.DeleteScheduleAsync(It.IsAny<Schedule>())).Returns(Task.CompletedTask);

        var schedule = new Schedule { Id = Guid.NewGuid(), Name = "Old Schedule", BuiltIn = false };

        await _application.Scheduler.DeleteScheduleAsync(schedule, ActivityInitiatorType.User, Guid.NewGuid(), "Carol Admin");

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.TargetType, Is.EqualTo(ActivityTargetType.Schedule));
        Assert.That(captured.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Delete));
        Assert.That(captured.TargetName, Is.EqualTo("Old Schedule"));
        _mockSchedulingRepository.Verify(r => r.DeleteScheduleAsync(schedule), Times.Once);
    }

    [Test]
    public void DeleteScheduleAsync_BuiltIn_WritesNoActivity()
    {
        var schedule = new Schedule { Id = Guid.NewGuid(), Name = "Temporal Scope Reconciliation", BuiltIn = true };

        Assert.ThatAsync(async () => await _application.Scheduler.DeleteScheduleAsync(schedule, ActivityInitiatorType.User, Guid.NewGuid(), "Dan Admin"),
            Throws.InstanceOf<InvalidOperationException>());

        // The built-in guard throws before any audit record is written.
        _mockActivityRepository.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    [Test]
    public async Task CreateScheduleAsync_SystemInitiator_AttributesToSystemAsync()
    {
        Activity? captured = null;
        _mockActivityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => captured = a).Returns(Task.CompletedTask);
        _mockActivityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _mockSchedulingRepository.Setup(r => r.CreateScheduleAsync(It.IsAny<Schedule>())).Returns(Task.CompletedTask);

        var schedule = new Schedule { Id = Guid.NewGuid(), Name = "System Schedule" };

        await _application.Scheduler.CreateScheduleAsync(schedule, ActivityInitiatorType.System, null, null);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(captured.InitiatedById, Is.Null);
        Assert.That(captured.InitiatedByName, Is.EqualTo("System"));
    }
}

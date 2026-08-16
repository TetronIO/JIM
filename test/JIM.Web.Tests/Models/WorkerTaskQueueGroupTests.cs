// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using JIM.Web.Models;
using NUnit.Framework;

namespace JIM.Web.Tests.Models;

/// <summary>
/// The Operations queue is drawn as one grid per Schedule Execution plus one for everything running outside a
/// Schedule, because a virtualised grid cannot also group. That makes this the only place the queue's shape is
/// decided, and both halves of it matter operationally: which block a task lands in, and the order the blocks and
/// their rows appear in, which is what the "worker processing, from top to bottom" bar above the queue asserts.
/// </summary>
[TestFixture]
public class WorkerTaskQueueGroupTests
{
    private static WorkerTaskHeader Task(
        string name,
        WorkerTaskStatus status = WorkerTaskStatus.Queued,
        Guid? scheduleExecutionId = null,
        string? scheduleName = null,
        int? stepIndex = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Type = "Synchronisation",
        Status = status,
        Timestamp = DateTime.UtcNow,
        ScheduleExecutionId = scheduleExecutionId,
        ScheduleExecutionName = scheduleName,
        ScheduleStepIndex = stepIndex
    };

    [Test]
    public void Build_TasksFromTwoSchedulesAndOutsideThem_MakesAGroupPerScheduleAndOneForTheRest()
    {
        var nightly = Guid.NewGuid();
        var cloud = Guid.NewGuid();

        var groups = WorkerTaskQueueGroup.Build(
        [
            Task("HR System - Full Import", scheduleExecutionId: nightly, scheduleName: "Nightly Full Sync", stepIndex: 0),
            Task("AD - Delta Sync", scheduleExecutionId: nightly, scheduleName: "Nightly Full Sync", stepIndex: 1),
            Task("Azure AD - Export", scheduleExecutionId: cloud, scheduleName: "Cloud Provisioning", stepIndex: 0),
            Task("LDAP Directory - Full Import"),
            Task("Clear Connected System Objects")
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(groups, Has.Count.EqualTo(3));
            Assert.That(groups.Count(g => g.ScheduleExecutionId == nightly), Is.EqualTo(1));
            Assert.That(groups.Single(g => g.ScheduleExecutionId == nightly).ScheduleName, Is.EqualTo("Nightly Full Sync"));
            Assert.That(groups.Single(g => g.ScheduleExecutionId == cloud).Tasks, Has.Count.EqualTo(1));

            // Everything outside a Schedule is one block, not one block per task: they were a group each while the
            // queue was a single grouped table, which only worked because each drew nothing but a separator.
            var standalone = groups.Single(g => g.Key == WorkerTaskQueueGroup.StandaloneKey);
            Assert.That(standalone.ScheduleExecutionId, Is.Null);
            Assert.That(standalone.Tasks, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void Build_ScheduleWithAStepRunning_LeadsTheQueueAheadOfAScheduleMerelyQueued()
    {
        var queuedSchedule = Guid.NewGuid();
        var runningSchedule = Guid.NewGuid();

        var groups = WorkerTaskQueueGroup.Build(
        [
            Task("Azure AD - Export", scheduleExecutionId: queuedSchedule, scheduleName: "Cloud Provisioning", stepIndex: 0),
            Task("HR System - Full Import", WorkerTaskStatus.Processing, runningSchedule, "Nightly Full Sync", 0)
        ]);

        Assert.That(groups[0].ScheduleExecutionId, Is.EqualTo(runningSchedule),
            "the group the worker is actually running has to lead; the bar above the queue says it processes from top to bottom");
    }

    [Test]
    public void Build_ScheduleSteps_OrdersARunningStepAboveTheStepsWaitingOnIt()
    {
        var execution = Guid.NewGuid();

        var groups = WorkerTaskQueueGroup.Build(
        [
            Task("AD - Export", WorkerTaskStatus.WaitingForPreviousStep, execution, "Nightly Full Sync", 2),
            Task("AD - Delta Sync", WorkerTaskStatus.Queued, execution, "Nightly Full Sync", 1),
            Task("HR System - Full Import", WorkerTaskStatus.Processing, execution, "Nightly Full Sync", 0)
        ]);

        Assert.That(groups.Single().Tasks.Select(t => t.Name),
            Is.EqualTo(new[] { "HR System - Full Import", "AD - Delta Sync", "AD - Export" }));
    }
}

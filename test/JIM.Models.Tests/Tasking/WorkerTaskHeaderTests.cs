// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;
using NUnit.Framework;

namespace JIM.Models.Tests.Tasking;

[TestFixture]
public class WorkerTaskHeaderTests
{
    private static RunPhaseSummary Steps(int currentStepNumber, ActivityPhaseStatus firstStepStatus) => new()
    {
        CurrentStepName = $"Step {currentStepNumber}",
        CurrentStepNumber = currentStepNumber,
        TotalSteps = 2,
        Steps =
        [
            new RunPhaseStep { Order = 0, Name = "Step 1", Status = firstStepStatus },
            new RunPhaseStep { Order = 1, Name = "Step 2", Status = ActivityPhaseStatus.Pending }
        ]
    };

    // Fixed rather than generated, so two headers built here are equal value for value, as two reads of the same
    // task are.
    private static readonly Guid FixedInitiatorId = new("5d0c8a52-3c1e-4f43-9a55-0f3a2b7d6e11");
    private static readonly Guid FixedActivityId = new("a7e2f1c4-8b3d-4e6a-b1f0-2c9d4e5f6a72");
    private static readonly Guid FixedScheduleExecutionId = new("c3b9d8e7-1f2a-4b5c-8d6e-7f8091a2b3c4");

    private static WorkerTaskHeader FullyPopulated(Guid id) => new()
    {
        Id = id,
        Name = "HR System - Full Import",
        Type = "Synchronisation",
        Timestamp = new DateTime(2026, 10, 24, 21, 0, 0, DateTimeKind.Utc),
        Status = WorkerTaskStatus.Processing,
        InitiatedByType = ActivityInitiatorType.ApiKey,
        InitiatedById = FixedInitiatorId,
        InitiatedByName = "Infrastructure Key",
        ActivityId = FixedActivityId,
        ObjectsToProcess = 790007,
        ObjectsProcessed = 174000,
        ProgressMessage = "Saving changes",
        Steps = Steps(2, ActivityPhaseStatus.Completed),
        ExecutionMode = WorkerTaskExecutionMode.Parallel,
        ConnectedSystemRunProfileId = 12,
        ConnectedSystemName = "HR System",
        RunProfileName = "Full Import",
        ScheduleExecutionId = FixedScheduleExecutionId,
        ScheduleExecutionName = "Nightly Full Sync",
        ScheduleStepIndex = 1,
        ScheduleTotalSteps = 3,
        ScheduleCurrentStepIndex = 1
    };

    [Test]
    public void UpdateFrom_LatestHeader_CopiesEveryPropertyButTheId()
    {
        // Reflected rather than listed, so a property added to the header later cannot be left out of the copy and
        // sit frozen on screen at the value it had when the task was first shown.
        var existing = new WorkerTaskHeader { Id = Guid.NewGuid(), Name = "old", Type = "old" };
        var latest = FullyPopulated(Guid.NewGuid());

        existing.UpdateFrom(latest);

        var properties = typeof(WorkerTaskHeader).GetProperties().Where(p => p.Name != nameof(WorkerTaskHeader.Id));
        using (Assert.EnterMultipleScope())
        {
            foreach (var property in properties)
                Assert.That(property.GetValue(existing), Is.EqualTo(property.GetValue(latest)), property.Name);
            Assert.That(existing.Id, Is.Not.EqualTo(latest.Id), "the header keeps its own identity");
        }
    }

    [Test]
    public void UpdateFrom_FigureChanged_ReportsAChange()
    {
        var id = Guid.NewGuid();
        var existing = FullyPopulated(id);
        var latest = FullyPopulated(id);
        latest.ObjectsProcessed = existing.ObjectsProcessed + 1000;

        Assert.That(existing.UpdateFrom(latest), Is.True);
    }

    [Test]
    public void UpdateFrom_StepMovedOnWithNothingElseChanged_ReportsAChange()
    {
        // A step can finish without the count or the message moving; the rail still has to move with it.
        var id = Guid.NewGuid();
        var existing = FullyPopulated(id);
        var latest = FullyPopulated(id);
        latest.Steps = Steps(2, ActivityPhaseStatus.Active);

        Assert.That(existing.UpdateFrom(latest), Is.True);
    }

    [Test]
    public void UpdateFrom_SameValuesInFreshInstances_ReportsNoChange()
    {
        // The repository hands back fresh objects on every read, so a change must be judged on values; judged on
        // references, every quiet poll would repaint the queue.
        var id = Guid.NewGuid();
        var existing = FullyPopulated(id);
        var latest = FullyPopulated(id);

        Assert.That(existing.UpdateFrom(latest), Is.False);
    }
}

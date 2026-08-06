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
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// A Schedule Execution's progress as automation reads it (#1162). The portal's queue draws a
/// Schedule as a rail of step groups; a caller of the REST API had no equivalent, only a list of
/// step rows each carrying its own status string, which is a different question from "how far
/// through is this Schedule".
/// </summary>
/// <remarks>
/// The row list is deliberately left alone: it names every Schedule Step, with timings, errors and
/// Activity ids, and its status strings distinguish states the rail collapses ("Completed with
/// Warning"). The progress block answers the different question, from the same reader the portal
/// uses, so the two surfaces cannot drift on what "step 2 of 5" means.
/// </remarks>
[TestFixture]
public class ScheduleExecutionProgressReadTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockScheduling = null!;
    private Mock<IActivityRepository> _mockActivity = null!;
    private Mock<ITaskingRepository> _mockTasking = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystems = null!;
    private JimApplication _application = null!;
    private ScheduleExecutionsController _controller = null!;

    private static readonly Guid ExecutionId = Guid.NewGuid();
    private static readonly Guid ScheduleId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockScheduling = new Mock<ISchedulingRepository>();
        _mockActivity = new Mock<IActivityRepository>();
        _mockTasking = new Mock<ITaskingRepository>();
        _mockConnectedSystems = new Mock<IConnectedSystemRepository>();

        _mockRepository.Setup(r => r.Scheduling).Returns(_mockScheduling.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivity.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTasking.Object);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystems.Object);

        // The detail read resolves each Run Profile step's Connected System and Run Profile names.
        _mockConnectedSystems.Setup(r => r.GetConnectedSystemHeadersAsync()).ReturnsAsync(
        [
            new ConnectedSystemHeader { Id = 1, Name = "Yellowstone APAC" },
            new ConnectedSystemHeader { Id = 2, Name = "Glitterband EMEA" }
        ]);
        _mockConnectedSystems.Setup(r => r.GetConnectedSystemRunProfilesAsync(It.IsAny<int>())).ReturnsAsync(
        [
            new ConnectedSystemRunProfile { Id = 1, Name = "Full Import" },
            new ConnectedSystemRunProfile { Id = 3, Name = "Full Sync" },
            new ConnectedSystemRunProfile { Id = 7, Name = "Full Import" },
            new ConnectedSystemRunProfile { Id = 9, Name = "Full Sync" }
        ]);

        _application = new JimApplication(_mockRepository.Object);
        _controller = new ScheduleExecutionsController(
            new Mock<ILogger<ScheduleExecutionsController>>().Object, _application);
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    /// <summary>
    /// A Schedule two groups in: the first ran two Run Profiles concurrently and one of them failed,
    /// the second is running, and the third has yet to start. Its finished tasks have been deleted,
    /// so only their Activities describe them.
    /// </summary>
    private void ArrangeExecution(ScheduleExecutionStatus status = ScheduleExecutionStatus.InProgress)
    {
        var execution = new ScheduleExecution
        {
            Id = ExecutionId,
            ScheduleId = ScheduleId,
            ScheduleName = "Nightly Full Sync",
            Status = status,
            CurrentStepIndex = 1,
            TotalSteps = 3,
            Schedule = new Schedule { Id = ScheduleId, Name = "Nightly Full Sync" }
        };

        _mockScheduling.Setup(r => r.GetScheduleExecutionWithScheduleAsync(ExecutionId)).ReturnsAsync(execution);
        _mockScheduling.Setup(r => r.GetScheduleStepsAsync(ScheduleId)).ReturnsAsync(new List<ScheduleStep>
        {
            new() { Id = Guid.NewGuid(), ScheduleId = ScheduleId, StepIndex = 0, ConnectedSystemId = 1, RunProfileId = 1, StepType = ScheduleStepType.RunProfile },
            new() { Id = Guid.NewGuid(), ScheduleId = ScheduleId, StepIndex = 0, ConnectedSystemId = 2, RunProfileId = 7, StepType = ScheduleStepType.RunProfile },
            new() { Id = Guid.NewGuid(), ScheduleId = ScheduleId, StepIndex = 1, ConnectedSystemId = 1, RunProfileId = 3, StepType = ScheduleStepType.RunProfile },
            new() { Id = Guid.NewGuid(), ScheduleId = ScheduleId, StepIndex = 2, ConnectedSystemId = 2, RunProfileId = 9, StepType = ScheduleStepType.RunProfile }
        });

        var runningActivity = new Activity
        {
            Id = Guid.NewGuid(), ScheduleExecutionId = ExecutionId, ScheduleStepIndex = 1,
            ConnectedSystemId = 1, TargetContext = "Yellowstone APAC", TargetName = "Full Sync",
            Status = ActivityStatus.InProgress
        };

        _mockActivity.Setup(r => r.GetActivitiesByScheduleExecutionAsync(ExecutionId)).ReturnsAsync(
        [
            new Activity
            {
                Id = Guid.NewGuid(), ScheduleExecutionId = ExecutionId, ScheduleStepIndex = 0,
                ConnectedSystemId = 1, TargetContext = "Yellowstone APAC", TargetName = "Full Import",
                Status = ActivityStatus.Complete
            },
            new Activity
            {
                Id = Guid.NewGuid(), ScheduleExecutionId = ExecutionId, ScheduleStepIndex = 0,
                ConnectedSystemId = 2, TargetContext = "Glitterband EMEA", TargetName = "Full Import",
                Status = ActivityStatus.FailedWithError
            },
            runningActivity
        ]);

        // Only the running step and the one behind it still have tasks; step 0's were deleted when
        // its work finished, which is the whole reason Activities are read at all.
        _mockTasking.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(ExecutionId)).ReturnsAsync(
        [
            new SynchronisationWorkerTask
            {
                Id = Guid.NewGuid(), ScheduleExecutionId = ExecutionId, ScheduleStepIndex = 1,
                ConnectedSystemId = 1, ConnectedSystemRunProfileId = 3,
                Status = WorkerTaskStatus.Processing, Activity = runningActivity
            },
            new SynchronisationWorkerTask
            {
                Id = Guid.NewGuid(), ScheduleExecutionId = ExecutionId, ScheduleStepIndex = 2,
                ConnectedSystemId = 2, ConnectedSystemRunProfileId = 9,
                Status = WorkerTaskStatus.WaitingForPreviousStep
            }
        ]);
    }

    private async Task<ScheduleExecutionDetailDto> GetDetailAsync()
    {
        var result = await _controller.GetByIdAsync(ExecutionId);
        return (ScheduleExecutionDetailDto)((Microsoft.AspNetCore.Mvc.OkObjectResult)result).Value!;
    }

    [Test]
    public async Task GetScheduleExecution_InProgress_ReportsWhichStepGroupItHasReachedAsync()
    {
        ArrangeExecution();

        var dto = await GetDetailAsync();

        Assert.That(dto.Progress, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Progress!.CurrentStepNumber, Is.EqualTo(2));
            Assert.That(dto.Progress.TotalSteps, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task GetScheduleExecution_StepGroupWhoseTasksHaveGone_IsStillReportedFromItsActivitiesAsync()
    {
        // Reading progress from the Worker Tasks alone would drop the finished group entirely, and
        // with it the failure inside it.
        ArrangeExecution();

        var dto = await GetDetailAsync();

        Assert.That(dto.Progress, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Progress!.Steps, Has.Count.EqualTo(3));
            Assert.That(dto.Progress.Steps[0].Status, Is.EqualTo(ScheduleExecutionStepStatus.Failed));
            Assert.That(dto.Progress.Steps[1].Status, Is.EqualTo(ScheduleExecutionStepStatus.Processing));
            Assert.That(dto.Progress.Steps[2].Status, Is.EqualTo(ScheduleExecutionStepStatus.Waiting));
        }
    }

    [Test]
    public async Task GetScheduleExecution_ParallelStepGroup_ReportsEachTasksOwnOutcomeAsync()
    {
        // The group aggregates to failed, but a caller deciding whether to re-run needs to know that
        // one of the two concurrent imports did succeed.
        ArrangeExecution();

        var dto = await GetDetailAsync();

        Assert.That(dto.Progress, Is.Not.Null);
        var parallelStep = dto.Progress!.Steps[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parallelStep.IsParallel, Is.True);
            Assert.That(parallelStep.TaskStatuses, Is.EqualTo(new[]
            {
                ScheduleExecutionStepStatus.Failed, ScheduleExecutionStepStatus.Completed
            }), "Ordered by outcome, the same order the portal draws the wedges in");
        }
    }

    [Test]
    public async Task GetScheduleExecution_StillReturnsThePerStepRowsItAlwaysHasAsync()
    {
        // The progress block is additional, not a replacement: the row list names every Schedule Step
        // and carries detail the block deliberately does not, and automation already reads it.
        ArrangeExecution();

        var dto = await GetDetailAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Steps, Has.Count.EqualTo(4), "One per Schedule Step row, parallel rows included");
            Assert.That(dto.Steps.Select(s => s.Status), Does.Contain("Completed with Error").Or.Contain("Failed"));
        }
    }
}

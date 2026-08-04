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
using JIM.Models.Tasking.DTOs;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The Worker Tasks REST read carries everything the portal's queue shows (#1162): the run's own
/// steps, and the Schedule Execution's shape where the task belongs to one.
/// </summary>
/// <remarks>
/// The endpoint returns <see cref="WorkerTaskHeader"/> directly, so today this parity is free. That is
/// exactly why it is pinned: the day someone wraps the endpoint in a hand-written response DTO, these
/// fields are the ones most likely to be left behind, and their absence would be silent.
/// </remarks>
[TestFixture]
public class WorkerTaskStepParityTests
{
    private Mock<ITaskingRepository> _mockTasking = null!;
    private JimApplication _application = null!;
    private WorkerTasksController _controller = null!;

    private static readonly Guid TaskId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        var mockRepository = new Mock<IRepository>();
        _mockTasking = new Mock<ITaskingRepository>();
        mockRepository.Setup(r => r.Tasking).Returns(_mockTasking.Object);
        _application = new JimApplication(mockRepository.Object);
        _controller = new WorkerTasksController(new Mock<ILogger<WorkerTasksController>>().Object, _application);

        _mockTasking.Setup(r => r.GetWorkerTaskHeadersAsync()).ReturnsAsync([BuildHeader()]);
    }

    [TearDown]
    public void TearDown() => _application.Dispose();

    private static WorkerTaskHeader BuildHeader() => new()
    {
        Id = TaskId,
        Name = "Yellowstone APAC - Full Import",
        Type = "Synchronisation",
        Timestamp = DateTime.UtcNow,
        Status = WorkerTaskStatus.Processing,
        ObjectsToProcess = 40000,
        ObjectsProcessed = 12480,
        ScheduleExecutionId = Guid.NewGuid(),
        ScheduleExecutionName = "Nightly Full Sync",
        ScheduleStepIndex = 1,
        ScheduleTotalSteps = 5,
        ScheduleCurrentStepIndex = 1,
        Steps = RunPhaseSummary.From(
        [
            new ActivityPhase { Id = Guid.NewGuid(), Key = RunPhaseKeys.ImportFetch, Name = "Importing objects", Order = 0, Status = ActivityPhaseStatus.Completed },
            new ActivityPhase { Id = Guid.NewGuid(), Key = RunPhaseKeys.ImportSave, Name = "Saving changes", Order = 1, Status = ActivityPhaseStatus.Active },
            new ActivityPhase { Id = Guid.NewGuid(), Key = RunPhaseKeys.ImportRecordResults, Name = "Recording results", Order = 2, Status = ActivityPhaseStatus.Pending }
        ])
    };

    private async Task<WorkerTaskHeader> GetOneAsync()
    {
        var result = await _controller.GetWorkerTaskAsync(TaskId);
        return (WorkerTaskHeader)((OkObjectResult)result).Value!;
    }

    [Test]
    public async Task GetWorkerTask_RunProfileExecution_CarriesTheRunsStepsAsync()
    {
        var header = await GetOneAsync();

        Assert.That(header.Steps, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(header.Steps!.CurrentStepName, Is.EqualTo("Saving changes"));
            Assert.That(header.Steps.CurrentStepNumber, Is.EqualTo(2));
            Assert.That(header.Steps.TotalSteps, Is.EqualTo(3));
            Assert.That(header.Steps.Steps.Select(s => s.Name),
                Is.EqualTo(new[] { "Importing objects", "Saving changes", "Recording results" }));
        });
    }

    [Test]
    public async Task GetWorkerTask_TaskInASchedule_CarriesTheSchedulesShapeTooAsync()
    {
        // Without these a caller can see which step of the run is going, but not which step of the
        // Schedule the run itself is, which is the question a scheduled overnight batch raises.
        var header = await GetOneAsync();

        Assert.Multiple(() =>
        {
            Assert.That(header.ScheduleTotalSteps, Is.EqualTo(5));
            Assert.That(header.ScheduleCurrentStepIndex, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ListWorkerTasks_CarriesTheSameStepsAsTheSingleReadAsync()
    {
        var result = await _controller.GetWorkerTasksAsync();
        var page = (PaginatedResponse<WorkerTaskHeader>)((OkObjectResult)result).Value!;
        var items = page.Items.ToList();

        Assert.That(items, Has.Count.EqualTo(1));
        Assert.That(items[0].Steps?.CurrentStepName, Is.EqualTo("Saving changes"));
    }
}

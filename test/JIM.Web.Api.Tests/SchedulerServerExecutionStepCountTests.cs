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
using JIM.Models.Core;
using JIM.Models.Scheduling;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// <see cref="ScheduleExecution.TotalSteps"/> and <see cref="ScheduleExecution.CurrentStepIndex"/> are
/// read together as "step X of Y", so they have to count the same thing. They did not: the scheduler
/// works in step groups (steps sharing a <see cref="ScheduleStep.StepIndex"/> run concurrently, and
/// <c>CurrentStepIndex</c> is a group index), while <c>TotalSteps</c> was set from the number of step
/// rows. A Schedule with any parallel step therefore reported a larger total than it had positions to
/// advance through, so "step 2 of 6" could never reach 6.
/// </summary>
[TestFixture]
public class SchedulerServerExecutionStepCountTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepository = null!;
    private JimApplication _application = null!;

    /// <summary>
    /// The execution record the scheduler created, which is what these tests are about.
    /// </summary>
    private ScheduleExecution? _createdExecution;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockConnectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _mockServiceSettingsRepository = new Mock<IServiceSettingsRepository>();

        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepository.Object);
        _mockRepository.Setup(r => r.ServiceSettings).Returns(_mockServiceSettingsRepository.Object);

        _application = new JimApplication(_mockRepository.Object);

        _mockSchedulingRepository.Setup(r => r.CreateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()))
            .Callback<ScheduleExecution>(execution => _createdExecution = execution)
            .Returns(Task.CompletedTask);

        var connectorDefinition = new ConnectorDefinition
        {
            Id = 1,
            Name = "Test Connector",
            SupportsPartitions = false
        };

        ConnectedSystem BuildSystem(int id) => new()
        {
            Id = id,
            Name = $"System {id}",
            ConnectorDefinition = connectorDefinition,
            RunProfiles =
            [
                new ConnectedSystemRunProfile { Id = id * 100, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport }
            ]
        };

        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int id, bool _) => BuildSystem(id));

        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemCoreAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int id, bool _) => BuildSystem(id));

        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemRunProfilesAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => new List<ConnectedSystemRunProfile>
            {
                new() { Id = id * 100, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport }
            });
    }

    [Test]
    public async Task StartScheduleExecution_ScheduleWithAParallelStep_CountsStepGroupsNotStepRowsAsync()
    {
        // Three step rows, two of which share a StepIndex: two positions to advance through, not three.
        var schedule = CreateScheduleWithSteps(
            new StepConfig(StepIndex: 0, ConnectedSystemId: 1),
            new StepConfig(StepIndex: 1, ConnectedSystemId: 2),
            new StepConfig(StepIndex: 1, ConnectedSystemId: 3));

        await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        Assert.That(_createdExecution, Is.Not.Null);
        Assert.That(_createdExecution!.TotalSteps, Is.EqualTo(2),
            "CurrentStepIndex advances by step group, so the total it is read against must count groups too");
    }

    [Test]
    public async Task StartScheduleExecution_ScheduleWithAParallelStep_LeavesTheLastStepReachableAsync()
    {
        // The consequence stated as the reader sees it: the final group's 1-based position has to be
        // the total, or the execution finishes while still reporting steps left to run.
        var schedule = CreateScheduleWithSteps(
            new StepConfig(StepIndex: 0, ConnectedSystemId: 1),
            new StepConfig(StepIndex: 0, ConnectedSystemId: 2),
            new StepConfig(StepIndex: 1, ConnectedSystemId: 3),
            new StepConfig(StepIndex: 2, ConnectedSystemId: 4));

        await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        var lastStepIndex = schedule.Steps.Max(s => s.StepIndex);
        Assert.That(_createdExecution, Is.Not.Null);
        Assert.That(lastStepIndex + 1, Is.EqualTo(_createdExecution!.TotalSteps));
    }

    [Test]
    public async Task StartScheduleExecution_ScheduleWithNoParallelSteps_IsUnchangedAsync()
    {
        // The two counts agree when every step is alone at its index, which is why this went unnoticed.
        var schedule = CreateScheduleWithSteps(
            new StepConfig(StepIndex: 0, ConnectedSystemId: 1),
            new StepConfig(StepIndex: 1, ConnectedSystemId: 2),
            new StepConfig(StepIndex: 2, ConnectedSystemId: 3));

        await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        Assert.That(_createdExecution, Is.Not.Null);
        Assert.That(_createdExecution!.TotalSteps, Is.EqualTo(3));
    }

    #region Helper methods

    private record StepConfig(int StepIndex, int ConnectedSystemId);

    private static Schedule CreateScheduleWithSteps(params StepConfig[] stepConfigs)
    {
        var scheduleId = Guid.NewGuid();
        var steps = stepConfigs.Select(config => new ScheduleStep
        {
            Id = Guid.NewGuid(),
            ScheduleId = scheduleId,
            StepIndex = config.StepIndex,
            StepType = ScheduleStepType.RunProfile,
            ConnectedSystemId = config.ConnectedSystemId,
            RunProfileId = config.ConnectedSystemId * 100,
            Name = $"System {config.ConnectedSystemId}",
            ExecutionMode = stepConfigs.Count(s => s.StepIndex == config.StepIndex) > 1
                ? StepExecutionMode.ParallelWithPrevious
                : StepExecutionMode.Sequential
        }).ToList();

        return new Schedule
        {
            Id = scheduleId,
            Name = "Test Schedule",
            IsEnabled = true,
            Steps = steps
        };
    }

    #endregion
}

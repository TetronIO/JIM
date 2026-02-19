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
/// Tests that the SchedulerServer queues ALL schedule steps upfront when starting an execution.
/// Step 0 tasks should be Queued, all subsequent step tasks should be WaitingForPreviousStep.
/// ContinueOnFailure should be copied from ScheduleStep to WorkerTask at queue time.
/// </summary>
[TestFixture]
public class SchedulerServerQueueAllStepsTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepository = null!;
    private JimApplication _application = null!;
    private List<WorkerTask> _capturedTasks = null!;

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

        _capturedTasks = new List<WorkerTask>();

        _mockTaskingRepository.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(task => _capturedTasks.Add(task))
            .Returns(Task.CompletedTask);

        // Set up connected systems that pass partition validation
        var connectorDefinition = new ConnectorDefinition
        {
            Id = 1,
            Name = "Test Connector",
            SupportsPartitions = false
        };

        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => new ConnectedSystem
            {
                Id = id,
                Name = $"System {id}",
                ConnectorDefinition = connectorDefinition,
                RunProfiles = new List<ConnectedSystemRunProfile>
                {
                    new() { Id = id * 100, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport }
                }
            });

        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemRunProfilesAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => new List<ConnectedSystemRunProfile>
            {
                new() { Id = id * 100, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport }
            });
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task StartScheduleExecution_ThreeSequentialSteps_AllQueuedUpfrontAsync()
    {
        // Arrange: Three sequential steps at indices 0, 1, 2
        var schedule = CreateScheduleWithSteps(
            new StepConfig(0, 1, 100),
            new StepConfig(1, 2, 200),
            new StepConfig(2, 3, 300));

        // Act
        await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        // Assert: All 3 tasks created
        Assert.That(_capturedTasks, Has.Count.EqualTo(3));

        // Step 0: Queued
        var step0 = _capturedTasks.Single(t => t.ScheduleStepIndex == 0);
        Assert.That(step0.Status, Is.EqualTo(WorkerTaskStatus.Queued));

        // Steps 1 and 2: WaitingForPreviousStep
        var step1 = _capturedTasks.Single(t => t.ScheduleStepIndex == 1);
        Assert.That(step1.Status, Is.EqualTo(WorkerTaskStatus.WaitingForPreviousStep));

        var step2 = _capturedTasks.Single(t => t.ScheduleStepIndex == 2);
        Assert.That(step2.Status, Is.EqualTo(WorkerTaskStatus.WaitingForPreviousStep));
    }

    [Test]
    public async Task StartScheduleExecution_ParallelFirstGroup_AllGroupMembersQueuedAsync()
    {
        // Arrange: Two parallel steps at index 0, one sequential at index 1
        var schedule = CreateScheduleWithSteps(
            new StepConfig(0, 1, 100),
            new StepConfig(0, 2, 200),
            new StepConfig(1, 3, 300));

        // Act
        await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        // Assert
        Assert.That(_capturedTasks, Has.Count.EqualTo(3));

        // Step 0 group: Both Queued
        var step0Tasks = _capturedTasks.Where(t => t.ScheduleStepIndex == 0).ToList();
        Assert.That(step0Tasks, Has.Count.EqualTo(2));
        Assert.That(step0Tasks.All(t => t.Status == WorkerTaskStatus.Queued), Is.True);

        // Step 1: WaitingForPreviousStep
        var step1 = _capturedTasks.Single(t => t.ScheduleStepIndex == 1);
        Assert.That(step1.Status, Is.EqualTo(WorkerTaskStatus.WaitingForPreviousStep));
    }

    [Test]
    public async Task StartScheduleExecution_ContinueOnFailure_CopiedToWorkerTaskAsync()
    {
        // Arrange: Step 0 has ContinueOnFailure=false, Step 1 has ContinueOnFailure=true
        var schedule = CreateScheduleWithSteps(
            new StepConfig(0, 1, 100, ContinueOnFailure: false),
            new StepConfig(1, 2, 200, ContinueOnFailure: true));

        // Act
        await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        // Assert
        Assert.That(_capturedTasks, Has.Count.EqualTo(2));

        var step0 = _capturedTasks.Single(t => t.ScheduleStepIndex == 0);
        Assert.That(step0.ContinueOnFailure, Is.False);

        var step1 = _capturedTasks.Single(t => t.ScheduleStepIndex == 1);
        Assert.That(step1.ContinueOnFailure, Is.True);
    }

    [Test]
    public async Task StartScheduleExecution_NoSteps_ReturnsNullAsync()
    {
        // Arrange: Schedule with no steps
        var schedule = new Schedule
        {
            Id = Guid.NewGuid(),
            Name = "Empty Schedule",
            IsEnabled = true,
            Steps = new List<ScheduleStep>()
        };

        // Act
        var result = await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        // Assert
        Assert.That(result, Is.Null);
        Assert.That(_capturedTasks, Has.Count.EqualTo(0));
    }

    [Test]
    public async Task StartScheduleExecution_ScheduleStepIndex_SetOnAllTasksAsync()
    {
        // Arrange: 5 steps across 3 groups
        var schedule = CreateScheduleWithSteps(
            new StepConfig(0, 1, 100),
            new StepConfig(1, 2, 200),
            new StepConfig(1, 3, 300),
            new StepConfig(2, 4, 400),
            new StepConfig(2, 5, 500));

        // Act
        await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        // Assert
        Assert.That(_capturedTasks, Has.Count.EqualTo(5));
        Assert.That(_capturedTasks.All(t => t.ScheduleStepIndex.HasValue), Is.True);
        Assert.That(_capturedTasks.Count(t => t.ScheduleStepIndex == 0), Is.EqualTo(1));
        Assert.That(_capturedTasks.Count(t => t.ScheduleStepIndex == 1), Is.EqualTo(2));
        Assert.That(_capturedTasks.Count(t => t.ScheduleStepIndex == 2), Is.EqualTo(2));
    }

    [Test]
    public async Task StartScheduleExecution_ScheduleExecutionId_SetOnAllTasksAsync()
    {
        // Arrange
        ScheduleExecution? capturedExecution = null;
        _mockSchedulingRepository.Setup(r => r.CreateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()))
            .Callback<ScheduleExecution>(e => capturedExecution = e)
            .Returns(Task.CompletedTask);

        var schedule = CreateScheduleWithSteps(
            new StepConfig(0, 1, 100),
            new StepConfig(1, 2, 200));

        // Act
        var result = await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(_capturedTasks, Has.Count.EqualTo(2));
        Assert.That(_capturedTasks.All(t => t.ScheduleExecutionId == result!.Id), Is.True);
    }

    #region Helper methods

    private record StepConfig(int StepIndex, int ConnectedSystemId, int RunProfileId, bool ContinueOnFailure = false);

    private static Schedule CreateScheduleWithSteps(params StepConfig[] stepConfigs)
    {
        var scheduleId = Guid.NewGuid();
        var steps = stepConfigs.Select((config, index) => new ScheduleStep
        {
            Id = Guid.NewGuid(),
            ScheduleId = scheduleId,
            StepIndex = config.StepIndex,
            StepType = ScheduleStepType.RunProfile,
            ConnectedSystemId = config.ConnectedSystemId,
            RunProfileId = config.RunProfileId,
            Name = $"Step {index}",
            ContinueOnFailure = config.ContinueOnFailure,
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

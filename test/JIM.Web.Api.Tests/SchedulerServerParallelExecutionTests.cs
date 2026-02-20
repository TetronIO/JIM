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
/// Tests that the SchedulerServer correctly sets WorkerTaskExecutionMode.Parallel
/// when schedule steps share a StepIndex (parallel step group), and Sequential
/// when a step is alone at its StepIndex.
/// </summary>
[TestFixture]
public class SchedulerServerParallelExecutionTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepository = null!;
    private JimApplication _application = null!;

    /// <summary>
    /// Captures all WorkerTasks passed to CreateWorkerTaskAsync.
    /// </summary>
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

        // Capture all worker tasks created
        _mockTaskingRepository.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(task => _capturedTasks.Add(task))
            .Returns(Task.CompletedTask);

        // Set up connected systems that pass partition validation (SupportsPartitions=false)
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
                    new() { Id = id * 100, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport },
                    new() { Id = id * 100 + 1, Name = "Export", RunType = ConnectedSystemRunType.Export }
                }
            });

        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemRunProfilesAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => new List<ConnectedSystemRunProfile>
            {
                new() { Id = id * 100, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport },
                new() { Id = id * 100 + 1, Name = "Export", RunType = ConnectedSystemRunType.Export }
            });
    }

    [Test]
    public async Task StartScheduleExecution_SingleStep_CreatesSequentialTaskAsync()
    {
        // Arrange: Schedule with a single step (StepIndex 0)
        var schedule = CreateScheduleWithSteps(
            new StepConfig(StepIndex: 0, ConnectedSystemId: 1, RunProfileId: 100));

        // Act
        await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        // Assert
        Assert.That(_capturedTasks, Has.Count.EqualTo(1));
        var task = (SynchronisationWorkerTask)_capturedTasks[0];
        Assert.That(task.ExecutionMode, Is.EqualTo(WorkerTaskExecutionMode.Sequential));
    }

    [Test]
    public async Task StartScheduleExecution_ParallelSteps_CreatesParallelTasksAsync()
    {
        // Arrange: Schedule with two steps at StepIndex 0 (parallel group)
        var schedule = CreateScheduleWithSteps(
            new StepConfig(StepIndex: 0, ConnectedSystemId: 1, RunProfileId: 100),
            new StepConfig(StepIndex: 0, ConnectedSystemId: 2, RunProfileId: 200));

        // Act
        await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        // Assert
        Assert.That(_capturedTasks, Has.Count.EqualTo(2));
        Assert.That(_capturedTasks.All(t => t.ExecutionMode == WorkerTaskExecutionMode.Parallel), Is.True,
            "All tasks in a parallel step group should have Parallel execution mode");
    }

    [Test]
    public async Task StartScheduleExecution_ThreeParallelSteps_AllGetParallelModeAsync()
    {
        // Arrange: Three steps all at StepIndex 0
        var schedule = CreateScheduleWithSteps(
            new StepConfig(StepIndex: 0, ConnectedSystemId: 1, RunProfileId: 100),
            new StepConfig(StepIndex: 0, ConnectedSystemId: 2, RunProfileId: 200),
            new StepConfig(StepIndex: 0, ConnectedSystemId: 3, RunProfileId: 300));

        // Act
        await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        // Assert
        Assert.That(_capturedTasks, Has.Count.EqualTo(3));
        Assert.That(_capturedTasks.All(t => t.ExecutionMode == WorkerTaskExecutionMode.Parallel), Is.True);
    }

    [Test]
    public async Task StartScheduleExecution_MixedSequentialAndParallel_AllStepsQueuedUpfrontAsync()
    {
        // Arrange: StepIndex 0 = single (sequential), StepIndex 1 = two parallel steps
        // All steps are queued upfront: step 0 as Queued, step 1 as WaitingForPreviousStep.
        var schedule = CreateScheduleWithSteps(
            new StepConfig(StepIndex: 0, ConnectedSystemId: 1, RunProfileId: 100),
            new StepConfig(StepIndex: 1, ConnectedSystemId: 2, RunProfileId: 200),
            new StepConfig(StepIndex: 1, ConnectedSystemId: 3, RunProfileId: 300));

        // Act
        await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        // Assert: All 3 tasks are created
        Assert.That(_capturedTasks, Has.Count.EqualTo(3));

        // Step 0: Sequential, Queued
        var step0Task = (SynchronisationWorkerTask)_capturedTasks.Single(t => ((SynchronisationWorkerTask)t).ConnectedSystemId == 1);
        Assert.That(step0Task.ExecutionMode, Is.EqualTo(WorkerTaskExecutionMode.Sequential));
        Assert.That(step0Task.Status, Is.EqualTo(WorkerTaskStatus.Queued));

        // Step 1: Parallel, WaitingForPreviousStep
        var step1Tasks = _capturedTasks.Where(t => t.ScheduleStepIndex == 1).Cast<SynchronisationWorkerTask>().ToList();
        Assert.That(step1Tasks, Has.Count.EqualTo(2));
        Assert.That(step1Tasks.All(t => t.ExecutionMode == WorkerTaskExecutionMode.Parallel), Is.True);
        Assert.That(step1Tasks.All(t => t.Status == WorkerTaskStatus.WaitingForPreviousStep), Is.True);
    }

    [Test]
    public async Task StartScheduleExecution_ParallelTasks_HaveCorrectConnectedSystemIdsAsync()
    {
        // Arrange
        var schedule = CreateScheduleWithSteps(
            new StepConfig(StepIndex: 0, ConnectedSystemId: 1, RunProfileId: 100),
            new StepConfig(StepIndex: 0, ConnectedSystemId: 2, RunProfileId: 200));

        // Act
        await _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test");

        // Assert: Both tasks created with correct connected system IDs
        var syncTasks = _capturedTasks.Cast<SynchronisationWorkerTask>().ToList();
        Assert.That(syncTasks.Select(t => t.ConnectedSystemId), Is.EquivalentTo(new[] { 1, 2 }));
    }

    #region Helper methods

    private record StepConfig(int StepIndex, int ConnectedSystemId, int RunProfileId);

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
            ExecutionMode = config.StepIndex == stepConfigs[0].StepIndex && stepConfigs.Count(s => s.StepIndex == config.StepIndex) > 1
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

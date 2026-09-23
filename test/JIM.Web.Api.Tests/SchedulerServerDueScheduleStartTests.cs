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
/// Tests the Scheduler's handling of due Schedules (issue #1765): a Schedule's next run time is advanced whether or
/// not its execution starts, so a Schedule that cannot start fails once per scheduled occurrence rather than on every
/// polling cycle, and a Schedule Execution that fails to start is recorded as Failed with its error, never left
/// In Progress.
/// </summary>
[TestFixture]
public class SchedulerServerDueScheduleStartTests
{
    private const int HealthyConnectedSystemId = 1;
    private const int DeletingConnectedSystemId = 2;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepository = null!;
    private Mock<IServiceSettingsRepository> _mockServiceSettingsRepository = null!;
    private JimApplication _application = null!;

    /// <summary>
    /// The next run time each Schedule held at every point it was saved, keyed by Schedule id.
    /// </summary>
    private Dictionary<Guid, List<DateTime?>> _savedNextRunTimes = null!;

    /// <summary>
    /// Every Schedule Execution created. The start's outcome is written through the conditional transitions (#1768),
    /// which the mock applies to these instances, so each one's final state is read straight off it.
    /// </summary>
    private List<ScheduleExecution> _createdExecutions = null!;

    private List<WorkerTask> _createdTasks = null!;

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

        _savedNextRunTimes = new Dictionary<Guid, List<DateTime?>>();
        _createdExecutions = new List<ScheduleExecution>();
        _createdTasks = new List<WorkerTask>();

        _mockSchedulingRepository.Setup(r => r.GetActiveScheduleExecutionsAsync())
            .ReturnsAsync(new List<ScheduleExecution>());

        _mockSchedulingRepository.Setup(r => r.UpdateScheduleAsync(It.IsAny<Schedule>()))
            .Callback<Schedule>(s =>
            {
                if (!_savedNextRunTimes.TryGetValue(s.Id, out var times))
                    _savedNextRunTimes[s.Id] = times = new List<DateTime?>();
                times.Add(s.NextRunTime);
            })
            .Returns(Task.CompletedTask);

        _mockSchedulingRepository.EmulateConditionalTransitions();
        _mockSchedulingRepository.Setup(r => r.CreateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()))
            .Callback<ScheduleExecution>(e => _createdExecutions.Add(e))
            .Returns(Task.CompletedTask);

        _mockTaskingRepository.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => _createdTasks.Add(t))
            .Returns(Task.CompletedTask);

        var connectorDefinition = new ConnectorDefinition { Id = 1, Name = "Test Connector", SupportsPartitions = false };

        ConnectedSystem BuildSystem(int id) => new()
        {
            Id = id,
            Name = $"System {id}",
            ConnectorDefinition = connectorDefinition,
            // A Connected System being deleted refuses to have Run Profiles queued against it (#809), which is the
            // realistic way a Schedule comes to fail to start.
            Status = id == DeletingConnectedSystemId ? ConnectedSystemStatus.Deleting : ConnectedSystemStatus.Active,
            RunProfiles = new List<ConnectedSystemRunProfile>
            {
                new() { Id = id * 100, Name = "Full Import", RunType = ConnectedSystemRunType.FullImport }
            }
        };

        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int id, bool _) => BuildSystem(id));
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemCoreAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int id, bool _) => BuildSystem(id));
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemRunProfilesAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => BuildSystem(id).RunProfiles!.ToList());
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task StartDueSchedulesAsync_ScheduleStarts_AdvancesNextRunTimeAsync()
    {
        var schedule = CreateDueSchedule("Healthy Schedule", HealthyConnectedSystemId);
        SetDueSchedules(schedule);

        await _application.Scheduler.StartDueSchedulesAsync();

        Assert.That(_createdTasks, Has.Count.EqualTo(1));
        AssertNextRunTimeAdvanced(schedule);
    }

    [Test]
    public async Task StartDueSchedulesAsync_ScheduleFailsToStart_StillAdvancesNextRunTimeAsync()
    {
        // The Schedule's only step targets a Connected System that is being deleted, so starting it throws.
        var schedule = CreateDueSchedule("Broken Schedule", DeletingConnectedSystemId);
        SetDueSchedules(schedule);

        await _application.Scheduler.StartDueSchedulesAsync();

        // Without the advance the Schedule stays due and the next polling cycle, seconds later, fails it again.
        AssertNextRunTimeAdvanced(schedule);
    }

    [Test]
    public async Task StartDueSchedulesAsync_ScheduleFailsToStart_OtherDueSchedulesStillStartAsync()
    {
        var broken = CreateDueSchedule("Broken Schedule", DeletingConnectedSystemId);
        var healthy = CreateDueSchedule("Healthy Schedule", HealthyConnectedSystemId);
        SetDueSchedules(broken, healthy);

        await _application.Scheduler.StartDueSchedulesAsync();

        Assert.That(_createdTasks, Has.Count.EqualTo(1));
        Assert.That(((SynchronisationWorkerTask)_createdTasks[0]).ConnectedSystemId, Is.EqualTo(HealthyConnectedSystemId));
        AssertNextRunTimeAdvanced(broken);
        AssertNextRunTimeAdvanced(healthy);
    }

    [Test]
    public async Task StartDueSchedulesAsync_AdvancingOneScheduleFails_OtherDueSchedulesStillStartAndAdvanceAsync()
    {
        var first = CreateDueSchedule("First Schedule", HealthyConnectedSystemId);
        var second = CreateDueSchedule("Second Schedule", HealthyConnectedSystemId);
        SetDueSchedules(first, second);

        // Saving the first Schedule fails on its second save (the next run time; the first save is its last run
        // time, made while starting). The failure must stay with that Schedule.
        var firstSaves = 0;
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleAsync(It.Is<Schedule>(s => s.Id == first.Id)))
            .Returns(() => ++firstSaves == 2
                ? Task.FromException(new InvalidOperationException("Simulated database failure"))
                : Task.CompletedTask);

        await _application.Scheduler.StartDueSchedulesAsync();

        Assert.That(_createdTasks, Has.Count.EqualTo(2));
        AssertNextRunTimeAdvanced(second);
    }

    [Test]
    public async Task StartDueSchedulesAsync_ScheduleAlreadyRunning_DoesNotStartOrAdvanceAsync()
    {
        var schedule = CreateDueSchedule("Running Schedule", HealthyConnectedSystemId);
        SetDueSchedules(schedule);
        _mockSchedulingRepository.Setup(r => r.GetActiveScheduleExecutionsAsync())
            .ReturnsAsync(new List<ScheduleExecution>
            {
                new() { Id = Guid.NewGuid(), ScheduleId = schedule.Id, Status = ScheduleExecutionStatus.InProgress }
            });

        await _application.Scheduler.StartDueSchedulesAsync();

        Assert.That(_createdTasks, Is.Empty);
        Assert.That(_savedNextRunTimes.ContainsKey(schedule.Id), Is.False,
            "A Schedule skipped for overlap keeps its due time so it starts as soon as the running execution ends.");
    }

    [Test]
    public void StartScheduleExecutionAsync_StepCannotBeQueued_ExecutionEndsFailedWithErrorAsync()
    {
        var schedule = CreateDueSchedule("Broken Schedule", DeletingConnectedSystemId);

        Assert.ThrowsAsync<InvalidOperationException>(() => _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test"));

        var execution = _createdExecutions.Single();
        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
        Assert.That(execution.ErrorMessage, Does.Contain("is being deleted"));
    }

    [Test]
    public void StartScheduleExecutionAsync_SavingLastRunTimeFails_ExecutionEndsFailedWithErrorAsync()
    {
        // The execution record is created before the Schedule's last run time is saved. If that save throws, the
        // execution must be recorded as Failed with the reason, never left half-started: an In Progress execution
        // with no steps queued used to be found by the stuck-execution safety net and marked Complete, reporting a
        // run that never happened as a success.
        var schedule = CreateDueSchedule("Schedule", HealthyConnectedSystemId);
        _mockSchedulingRepository.Setup(r => r.UpdateScheduleAsync(It.IsAny<Schedule>()))
            .ThrowsAsync(new InvalidOperationException("Simulated database failure"));

        Assert.ThrowsAsync<InvalidOperationException>(() => _application.Scheduler.StartScheduleExecutionAsync(
            schedule, ActivityInitiatorType.System, null, "Test"));

        var execution = _createdExecutions.Single();
        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
        Assert.That(execution.ErrorMessage, Does.Contain("Simulated database failure"));
        Assert.That(_createdTasks, Is.Empty);
    }

    #region Helpers

    private void SetDueSchedules(params Schedule[] schedules)
    {
        _mockSchedulingRepository.Setup(r => r.GetDueSchedulesAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(schedules.ToList());
    }

    private void AssertNextRunTimeAdvanced(Schedule schedule)
    {
        Assert.That(_savedNextRunTimes.TryGetValue(schedule.Id, out var times), Is.True,
            $"Schedule '{schedule.Name}' was never saved.");
        Assert.That(times!.Last(), Is.GreaterThan(DateTime.UtcNow),
            $"Schedule '{schedule.Name}' was not saved with a next run time in the future, so it is still due.");
    }

    private static Schedule CreateDueSchedule(string name, int connectedSystemId)
    {
        var scheduleId = Guid.NewGuid();
        return new Schedule
        {
            Id = scheduleId,
            Name = name,
            IsEnabled = true,
            TriggerType = ScheduleTriggerType.Cron,
            CronExpression = "0 2 * * *",
            NextRunTime = DateTime.UtcNow.AddMinutes(-1),
            Steps = new List<ScheduleStep>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ScheduleId = scheduleId,
                    StepIndex = 0,
                    StepType = ScheduleStepType.RunProfile,
                    ConnectedSystemId = connectedSystemId,
                    RunProfileId = connectedSystemId * 100,
                    Name = "Full Import",
                    ExecutionMode = StepExecutionMode.Sequential
                }
            }
        };
    }

    #endregion
}

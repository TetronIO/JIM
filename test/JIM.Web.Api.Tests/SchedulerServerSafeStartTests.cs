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
/// Tests that starting a Schedule is safe when a step cannot be queued (#1768). The execution is held at Queued while
/// every step is queued as waiting, and only then is the first step group released, atomically and only if the
/// execution is still Queued. A step that cannot be queued either stops the start (nothing runs, the execution is
/// Failed with the reason, and the waiting steps are cancelled) or, when it is set to continue, is recorded as failed
/// and skipped while the rest of the Schedule runs.
/// </summary>
[TestFixture]
public class SchedulerServerSafeStartTests
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

    private List<WorkerTask> _createdTasks = null!;
    private List<Activity> _createdActivities = null!;
    private ScheduleExecution? _createdExecution;
    private ScheduleExecutionStatus? _statusWhenCreated;

    /// <summary>
    /// The order the start's writes happened in, so a test can prove nothing was released until every step had been
    /// queued.
    /// </summary>
    private List<string> _events = null!;

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

        _createdTasks = new List<WorkerTask>();
        _createdActivities = new List<Activity>();
        _createdExecution = null;
        _statusWhenCreated = null;
        _events = new List<string>();

        _mockSchedulingRepository.EmulateConditionalTransitions();

        // Record the Start transition's place in the sequence, keeping the emulated behaviour.
        _mockSchedulingRepository.Setup(r => r.TryStartScheduleExecutionAsync(It.IsAny<ScheduleExecution>(), It.IsAny<int>()))
            .Returns((ScheduleExecution execution, int firstStepIndex) =>
            {
                _events.Add($"start:{firstStepIndex}");
                if (execution.Status != ScheduleExecutionStatus.Queued)
                    return Task.FromResult(false);
                execution.Status = ScheduleExecutionStatus.InProgress;
                execution.CurrentStepIndex = firstStepIndex;
                execution.StartedAt = DateTime.UtcNow;
                return Task.FromResult(true);
            });

        _mockSchedulingRepository.Setup(r => r.CreateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()))
            .Callback<ScheduleExecution>(e =>
            {
                _createdExecution = e;
                _statusWhenCreated = e.Status;
            })
            .Returns(Task.CompletedTask);

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => _createdExecution?.Id == id ? _createdExecution : null);

        _mockTaskingRepository.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t =>
            {
                _createdTasks.Add(t);
                _events.Add($"task:{t.ScheduleStepIndex}:{t.Status}");
            })
            .Returns(Task.CompletedTask);

        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .Callback<Guid, string>((_, reason) => _events.Add($"cancel-waiting:{reason}"))
            .ReturnsAsync(0);

        _mockActivityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivities.Add(a))
            .Returns(Task.CompletedTask);

        var connectorDefinition = new ConnectorDefinition { Id = 1, Name = "Test Connector", SupportsPartitions = false };

        ConnectedSystem BuildSystem(int id) => new()
        {
            Id = id,
            Name = $"System {id}",
            ConnectorDefinition = connectorDefinition,
            // A Connected System being deleted refuses to have Run Profiles queued against it (#809), which is the
            // realistic way a step comes to be impossible to queue.
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
    public async Task StartScheduleExecutionAsync_EveryStepQueues_HoldsTheExecutionAtQueuedUntilAllAreQueuedAsync()
    {
        var schedule = CreateSchedule(
            Step(0, HealthyConnectedSystemId),
            Step(1, HealthyConnectedSystemId),
            Step(2, HealthyConnectedSystemId));

        var execution = await _application.Scheduler.StartScheduleExecutionAsync(schedule, ActivityInitiatorType.System, null, "Test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_statusWhenCreated, Is.EqualTo(ScheduleExecutionStatus.Queued),
                "the execution is held at Queued while its steps are queued, so nothing can act on it half-started");
            Assert.That(_createdExecution!.StartedAt, Is.Not.Null, "it has started once its first step is released");
            Assert.That(_createdTasks.Select(t => t.Status), Is.All.EqualTo(WorkerTaskStatus.WaitingForPreviousStep),
                "every step is queued as waiting; none is runnable until the start is complete");
            Assert.That(_events.Last(), Is.EqualTo("start:0"),
                "the first step group is released only after every step has been queued");
            Assert.That(_events.Count(e => e.StartsWith("start:")), Is.EqualTo(1));
            Assert.That(execution!.Status, Is.EqualTo(ScheduleExecutionStatus.InProgress));
        }
    }

    [Test]
    public async Task StartScheduleExecutionAsync_EveryStepQueues_CarriesEachStepsIdOnItsTaskAsync()
    {
        var schedule = CreateSchedule(
            Step(0, HealthyConnectedSystemId),
            Step(1, HealthyConnectedSystemId),
            Step(1, HealthyConnectedSystemId));

        await _application.Scheduler.StartScheduleExecutionAsync(schedule, ActivityInitiatorType.System, null, "Test");

        Assert.That(_createdTasks.Select(t => t.ScheduleStepId), Is.EquivalentTo(schedule.Steps.Select(s => (Guid?)s.Id)),
            "each task names the step it runs, so its Activity can say which parallel step it belongs to");
    }

    [Test]
    public void StartScheduleExecutionAsync_StepThreeCannotBeQueuedAndStops_NothingRunsAndTheExecutionFailsAsync()
    {
        var schedule = CreateSchedule(
            Step(0, HealthyConnectedSystemId),
            Step(1, HealthyConnectedSystemId),
            Step(2, DeletingConnectedSystemId),
            Step(3, HealthyConnectedSystemId));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _application.Scheduler.StartScheduleExecutionAsync(schedule, ActivityInitiatorType.System, null, "Test"));

        const string expectedMessage = "The Schedule could not start. Step 3, System 2 - Full Import, could not be queued: " +
                                       "Connected System 'System 2' is being deleted; run profiles cannot be executed against it. No steps ran.";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Is.EqualTo(expectedMessage), "the caller is told the same reason the execution records");
            Assert.That(_events.Any(e => e.StartsWith("start:")), Is.False, "no step may be released");
            Assert.That(_createdTasks.Select(t => t.Status), Is.All.EqualTo(WorkerTaskStatus.WaitingForPreviousStep),
                "no task was ever runnable");
            Assert.That(_createdTasks.Select(t => t.ScheduleStepIndex), Is.EqualTo(new int?[] { 0, 1 }),
                "queuing stops at the step that could not be queued");
            Assert.That(_events, Does.Contain($"cancel-waiting:{ScheduleStepNotRunReasons.ScheduleCouldNotStart}"),
                "the steps already queued are cancelled, saying why");
            Assert.That(_createdExecution!.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
            Assert.That(_createdExecution.ErrorMessage, Is.EqualTo(expectedMessage));
            Assert.That(_createdExecution.StartedAt, Is.Null, "an execution none of whose steps ran never started");
        }

        _mockTaskingRepository.Verify(r => r.TransitionStepToQueuedAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public void StartScheduleExecutionAsync_StepThreeCannotBeQueuedAndStops_RecordsAFailedActivityForThatStepAsync()
    {
        var schedule = CreateSchedule(
            Step(0, HealthyConnectedSystemId),
            Step(1, HealthyConnectedSystemId),
            Step(2, DeletingConnectedSystemId),
            Step(3, HealthyConnectedSystemId));
        var stepThree = schedule.Steps.Single(s => s.StepIndex == 2);

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            _application.Scheduler.StartScheduleExecutionAsync(schedule, ActivityInitiatorType.System, null, "Test"));

        var failed = _createdActivities.Single(a => a.ScheduleStepIndex == 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(failed.ErrorMessage, Is.EqualTo(
                "Could not be queued: Connected System 'System 2' is being deleted; run profiles cannot be executed against it."));
            Assert.That(failed.ScheduleStepId, Is.EqualTo(stepThree.Id));
            Assert.That(failed.ScheduleExecutionId, Is.EqualTo(_createdExecution!.Id));
            Assert.That(failed.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystemRunProfile),
                "shaped like the Activity the step would have produced, so it reads alongside the others");
            Assert.That(failed.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Execute));
            Assert.That(failed.TargetName, Is.EqualTo("Full Import"));
            Assert.That(failed.TargetContext, Is.EqualTo("System 2"));
            Assert.That(failed.ConnectedSystemId, Is.EqualTo(DeletingConnectedSystemId));
            Assert.That(failed.ConnectedSystemRunProfileId, Is.EqualTo(DeletingConnectedSystemId * 100));
            Assert.That(failed.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        }
    }

    [Test]
    public async Task StartScheduleExecutionAsync_StepThreeCannotBeQueuedAndContinues_TheRestOfTheScheduleRunsAsync()
    {
        var schedule = CreateSchedule(
            Step(0, HealthyConnectedSystemId),
            Step(1, HealthyConnectedSystemId),
            Step(2, DeletingConnectedSystemId, continueOnFailure: true),
            Step(3, HealthyConnectedSystemId));
        var stepThree = schedule.Steps.Single(s => s.StepIndex == 2);

        var execution = await _application.Scheduler.StartScheduleExecutionAsync(schedule, ActivityInitiatorType.System, null, "Test");

        var failed = _createdActivities.Single(a => a.ScheduleStepIndex == 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_createdTasks.Select(t => t.ScheduleStepIndex), Is.EqualTo(new int?[] { 0, 1, 3 }),
                "every step that can be queued is; the one that cannot gets no task");
            Assert.That(_events.Last(), Is.EqualTo("start:0"), "and the Schedule starts as normal");
            Assert.That(_events.Any(e => e.StartsWith("cancel-waiting:")), Is.False);
            Assert.That(execution!.Status, Is.EqualTo(ScheduleExecutionStatus.InProgress));
            Assert.That(failed.Status, Is.EqualTo(ActivityStatus.FailedWithError), "the skipped step shows why it did not run");
            Assert.That(failed.ScheduleStepId, Is.EqualTo(stepThree.Id));
        }
    }

    [Test]
    public async Task StartScheduleExecutionAsync_FirstStepCannotBeQueuedAndContinues_ReleasesTheFirstGroupWithTasksAsync()
    {
        var schedule = CreateSchedule(
            Step(0, DeletingConnectedSystemId, continueOnFailure: true),
            Step(1, HealthyConnectedSystemId),
            Step(2, HealthyConnectedSystemId));

        var execution = await _application.Scheduler.StartScheduleExecutionAsync(schedule, ActivityInitiatorType.System, null, "Test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_events.Last(), Is.EqualTo("start:1"), "step 1 has nothing to run, so step 2 is released first");
            Assert.That(execution!.CurrentStepIndex, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task StartScheduleExecutionAsync_NoStepCanBeQueuedAndAllContinue_CompletesTheExecutionAsync()
    {
        var schedule = CreateSchedule(
            Step(0, DeletingConnectedSystemId, continueOnFailure: true),
            Step(1, DeletingConnectedSystemId, continueOnFailure: true));

        var execution = await _application.Scheduler.StartScheduleExecutionAsync(schedule, ActivityInitiatorType.System, null, "Test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_createdTasks, Is.Empty);
            Assert.That(_events.Any(e => e.StartsWith("start:")), Is.False, "there is nothing to release");
            Assert.That(execution!.Status, Is.EqualTo(ScheduleExecutionStatus.Complete),
                "with nothing left to run the execution ends straight away rather than waiting for the safety net");
            Assert.That(execution.CompletedAt, Is.Not.Null);
            Assert.That(_createdActivities.Count(a => a.Status == ActivityStatus.FailedWithError), Is.EqualTo(2));
        }
    }

    [Test]
    public async Task StartScheduleExecutionAsync_CancelledWhileStarting_ReleasesNothingAndRemovesTheWaitingStepsAsync()
    {
        var schedule = CreateSchedule(
            Step(0, HealthyConnectedSystemId),
            Step(1, HealthyConnectedSystemId));

        // An administrator cancels the execution while its last step is being queued.
        var tasksQueued = 0;
        _mockTaskingRepository.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t =>
            {
                _createdTasks.Add(t);
                if (++tasksQueued == 2)
                    _createdExecution!.Status = ScheduleExecutionStatus.Cancelled;
            })
            .Returns(Task.CompletedTask);

        var execution = await _application.Scheduler.StartScheduleExecutionAsync(schedule, ActivityInitiatorType.System, null, "Test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution!.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled), "the cancellation stands");
            Assert.That(execution.StartedAt, Is.Null);
            Assert.That(_events, Does.Contain($"cancel-waiting:{ScheduleStepNotRunReasons.ExecutionCancelled}"),
                "steps queued after the cancellation read the tasks are removed, saying why");
        }
    }

    [Test]
    public void StartScheduleExecutionAsync_UnexpectedErrorQueuingAStep_FailsTheStartEvenWhenTheStepWouldContinueAsync()
    {
        // Continue On Failure covers a step that JIM refuses to queue, not an unexpected error such as the database
        // becoming unavailable part-way through.
        var schedule = CreateSchedule(
            Step(0, HealthyConnectedSystemId),
            Step(1, HealthyConnectedSystemId, continueOnFailure: true));

        _mockTaskingRepository.Setup(r => r.CreateWorkerTaskAsync(It.Is<WorkerTask>(t => t.ScheduleStepIndex == 1)))
            .ThrowsAsync(new TimeoutException("The database did not respond."));

        var ex = Assert.ThrowsAsync<TimeoutException>(() =>
            _application.Scheduler.StartScheduleExecutionAsync(schedule, ActivityInitiatorType.System, null, "Test"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Is.EqualTo("The database did not respond."), "the original error is rethrown");
            Assert.That(_events.Any(e => e.StartsWith("start:")), Is.False);
            Assert.That(_events, Does.Contain($"cancel-waiting:{ScheduleStepNotRunReasons.ScheduleCouldNotStart}"));
            Assert.That(_createdExecution!.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
            Assert.That(_createdExecution.ErrorMessage, Is.EqualTo(
                "The Schedule could not start. Step 2, System 1 - Full Import, could not be queued: The database did not respond. No steps ran."));
        }
    }

    [Test]
    public void StartScheduleExecutionAsync_CleaningUpAfterAFailedStartFails_LeavesItQueuedForTheSafetyNetAsync()
    {
        var schedule = CreateSchedule(
            Step(0, HealthyConnectedSystemId),
            Step(1, DeletingConnectedSystemId));

        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(It.IsAny<Guid>(), It.IsAny<string>()))
            .ThrowsAsync(new TimeoutException("Simulated database failure"));

        // The failure to clean up is logged, never thrown: the error that explains what happened is the one rethrown.
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _application.Scheduler.StartScheduleExecutionAsync(schedule, ActivityInitiatorType.System, null, "Test"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.StartWith("The Schedule could not start. Step 2"));
            // Marking it Failed now would leave its waiting task behind with nothing ever to remove it. Left Queued,
            // the Scheduler's stale-start safety net removes the task and fails the execution.
            Assert.That(_createdExecution!.Status, Is.EqualTo(ScheduleExecutionStatus.Queued));
            Assert.That(_events.Any(e => e.StartsWith("start:")), Is.False, "and still nothing is released");
        }
    }

    #region Helpers

    private static ScheduleStep Step(int stepIndex, int connectedSystemId, bool continueOnFailure = false) => new()
    {
        Id = Guid.NewGuid(),
        StepIndex = stepIndex,
        StepType = ScheduleStepType.RunProfile,
        ConnectedSystemId = connectedSystemId,
        RunProfileId = connectedSystemId * 100,
        ContinueOnFailure = continueOnFailure
    };

    private static Schedule CreateSchedule(params ScheduleStep[] steps)
    {
        var scheduleId = Guid.NewGuid();
        foreach (var step in steps)
        {
            step.ScheduleId = scheduleId;
            step.ExecutionMode = steps.Count(s => s.StepIndex == step.StepIndex) > 1 && steps.First(s => s.StepIndex == step.StepIndex) != step
                ? StepExecutionMode.ParallelWithPrevious
                : StepExecutionMode.Sequential;
        }

        return new Schedule
        {
            Id = scheduleId,
            Name = "Nightly Sync",
            IsEnabled = true,
            Steps = steps.ToList()
        };
    }

    #endregion
}

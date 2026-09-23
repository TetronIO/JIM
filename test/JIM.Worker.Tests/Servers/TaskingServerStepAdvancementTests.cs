// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests for TryAdvanceScheduleExecutionAsync, the worker-driven step advancement that runs after
/// CompleteWorkerTaskAsync deletes a schedule-linked task. A finished execution stays finished (#1768): nothing is
/// released and no status is written unless the execution is still InProgress, so a step that finishes after its
/// execution was cancelled leaves it Cancelled. In a parallel step group, the failing step's own Continue On Failure
/// setting decides whether the Schedule stops.
/// </summary>
[TestFixture]
public class TaskingServerStepAdvancementTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private JimApplication _application = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();

        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);

        _application = new JimApplication(_mockRepository.Object);
        _mockSchedulingRepository.EmulateConditionalTransitions();

        // Default: DeleteWorkerTaskAsync succeeds
        _mockTaskingRepository.Setup(r => r.DeleteWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Returns(Task.CompletedTask);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task CompleteWorkerTask_NonScheduleTask_DoesNotCallAdvancementAsync()
    {
        // Arrange: A standalone task (no schedule execution)
        var task = CreateWorkerTask(scheduleExecutionId: null, stepIndex: null);

        // Act
        await _application.Tasking.CompleteWorkerTaskAsync(task);

        // Assert: No schedule advancement queries should be made
        _mockTaskingRepository.Verify(
            r => r.GetWorkerTaskCountByExecutionStepAsync(It.IsAny<Guid>(), It.IsAny<int>()),
            Times.Never);
    }

    [Test]
    public async Task CompleteWorkerTask_NotLastTaskInStep_DoesNotAdvanceAsync()
    {
        // Arrange: Task is part of a schedule, but there are still remaining tasks at this step
        var executionId = Guid.NewGuid();
        var task = CreateWorkerTask(scheduleExecutionId: executionId, stepIndex: 0);

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskCountByExecutionStepAsync(executionId, 0))
            .ReturnsAsync(1); // One task still remaining

        // Act
        await _application.Tasking.CompleteWorkerTaskAsync(task);

        // Assert: No advancement, no status change: still tasks remaining
        _mockSchedulingRepository.Verify(
            r => r.TryAdvanceScheduleExecutionAsync(It.IsAny<ScheduleExecution>(), It.IsAny<int>()), Times.Never);
        _mockSchedulingRepository.Verify(
            r => r.TryFinishScheduleExecutionAsync(It.IsAny<ScheduleExecution>(), It.IsAny<IReadOnlyCollection<ScheduleExecutionStatus>>(),
                It.IsAny<ScheduleExecutionStatus>(), It.IsAny<string?>()), Times.Never);
    }

    [Test]
    public async Task CompleteWorkerTask_LastTaskInStep_AdvancesToNextStepAsync()
    {
        // Arrange: Last task at step 0, next waiting step is 1
        var step = RunProfileStep(0);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, step, RunProfileStep(1));
        var task = CreateWorkerTask(execution.Id, stepIndex: 0);
        SetUpStepOutcome(execution.Id, 0, remainingTasks: 0, Outcome(step, ActivityStatus.Complete));
        SetUpNextWaitingStep(execution.Id, 1);

        // Act
        await _application.Tasking.CompleteWorkerTaskAsync(task);

        // Assert: moved on to step 1, releasing it in the same atomic operation
        Assert.That(execution.CurrentStepIndex, Is.EqualTo(1));
        _mockSchedulingRepository.Verify(r => r.TryAdvanceScheduleExecutionAsync(execution, 1), Times.Once,
            "the release happens with the advance, as one atomic operation");
    }

    [Test]
    public async Task CompleteWorkerTask_LastTaskInLastStep_CompletesExecutionAsync()
    {
        // Arrange: Last task at step 2, no more waiting steps
        var step = RunProfileStep(2);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 2, step);
        var task = CreateWorkerTask(execution.Id, stepIndex: 2);
        SetUpStepOutcome(execution.Id, 2, remainingTasks: 0, Outcome(step, ActivityStatus.Complete));
        SetUpNextWaitingStep(execution.Id, null);

        // Act
        await _application.Tasking.CompleteWorkerTaskAsync(task);

        // Assert: Execution marked as completed
        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Complete));
            Assert.That(execution.CompletedAt, Is.Not.Null);
        }
    }

    [Test]
    public async Task CompleteWorkerTask_StepFailedAndSetToStop_FailsExecutionWithAPlainReasonAsync()
    {
        var step = RunProfileStep(0, continueOnFailure: false);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, step, RunProfileStep(1));
        var task = CreateWorkerTask(execution.Id, stepIndex: 0);
        SetUpStepOutcome(execution.Id, 0, remainingTasks: 0, Outcome(step, ActivityStatus.FailedWithError, "HR", "Full Import"));
        SetUpNextWaitingStep(execution.Id, 1);

        await _application.Tasking.CompleteWorkerTaskAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
            Assert.That(execution.CompletedAt, Is.Not.Null);
            Assert.That(execution.ErrorMessage, Is.EqualTo(
                "Step 1, HR - Full Import, failed. It is set to stop the Schedule when it fails, so the remaining steps did not run."));
        }
        _mockTaskingRepository.Verify(
            r => r.DeleteWaitingTasksForExecutionAsync(execution.Id, ScheduleStepNotRunReasons.EarlierStepStoppedSchedule), Times.Once,
            "each remaining step is cancelled, saying why it did not run");
        _mockSchedulingRepository.Verify(r => r.TryAdvanceScheduleExecutionAsync(It.IsAny<ScheduleExecution>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task CompleteWorkerTask_StepFailedAndSetToContinue_ContinuesToNextStepAsync()
    {
        var step = RunProfileStep(0, continueOnFailure: true);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, step, RunProfileStep(1));
        var task = CreateWorkerTask(execution.Id, stepIndex: 0);
        SetUpStepOutcome(execution.Id, 0, remainingTasks: 0, Outcome(step, ActivityStatus.FailedWithError));
        SetUpNextWaitingStep(execution.Id, 1);

        await _application.Tasking.CompleteWorkerTaskAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.InProgress));
            Assert.That(execution.CurrentStepIndex, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task CompleteWorkerTask_ExecutionCancelledWhileTheStepRan_StaysCancelledAndReleasesNothingAsync()
    {
        // The #1768 cancel-overwrite bug: an administrator cancels while a step is processing, the step then finishes,
        // and advancing used to overwrite Cancelled with Complete (or Failed) and release the next step.
        var step = RunProfileStep(0);
        var execution = SetUpExecution(ScheduleExecutionStatus.Cancelled, currentStepIndex: 0, step, RunProfileStep(1));
        var task = CreateWorkerTask(execution.Id, stepIndex: 0);
        SetUpStepOutcome(execution.Id, 0, remainingTasks: 0, Outcome(step, ActivityStatus.Complete));
        SetUpNextWaitingStep(execution.Id, null);

        await _application.Tasking.CompleteWorkerTaskAsync(task);

        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled));
        _mockSchedulingRepository.Verify(r => r.TryAdvanceScheduleExecutionAsync(It.IsAny<ScheduleExecution>(), It.IsAny<int>()), Times.Never);
        _mockSchedulingRepository.Verify(r => r.UpdateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()), Times.Never);
    }

    [Test]
    public async Task CompleteWorkerTask_ExecutionCancelledWhileAStopStepRanAndFailed_StaysCancelledAsync()
    {
        var step = RunProfileStep(0, continueOnFailure: false);
        var execution = SetUpExecution(ScheduleExecutionStatus.Cancelled, currentStepIndex: 0, step, RunProfileStep(1));
        var task = CreateWorkerTask(execution.Id, stepIndex: 0);
        SetUpStepOutcome(execution.Id, 0, remainingTasks: 0, Outcome(step, ActivityStatus.FailedWithError));

        await _application.Tasking.CompleteWorkerTaskAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled),
                "a failure after the cancellation must not relabel the execution Failed");
            Assert.That(execution.ErrorMessage, Is.EqualTo("Cancelled by user"));
        }
    }

    [Test]
    public async Task CompleteWorkerTask_CancelledJustBeforeTheExecutionWouldComplete_StaysCancelledAsync()
    {
        // The execution reads InProgress, but is cancelled between that read and the completion. The completion is
        // conditional, so the cancellation stands.
        var step = RunProfileStep(0);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, step);
        var task = CreateWorkerTask(execution.Id, stepIndex: 0);
        SetUpStepOutcome(execution.Id, 0, remainingTasks: 0, Outcome(step, ActivityStatus.Complete));
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id))
            .Callback(() => execution.Status = ScheduleExecutionStatus.Cancelled)
            .ReturnsAsync((int?)null);

        await _application.Tasking.CompleteWorkerTaskAsync(task);

        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled));
        _mockSchedulingRepository.Verify(r => r.UpdateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()), Times.Never,
            "no unconditional full-row write of the execution may happen during advancement");
    }

    [Test]
    public async Task CompleteWorkerTask_ParallelGroupFailingStepContinuesAndSucceedingSiblingStops_ContinuesAsync()
    {
        // Only the step that failed decides. Its sibling succeeded, so the sibling's setting is irrelevant.
        var failing = RunProfileStep(0, continueOnFailure: true);
        var succeeding = RunProfileStep(0, continueOnFailure: false);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, failing, succeeding, RunProfileStep(1));
        var task = CreateWorkerTask(execution.Id, stepIndex: 0);
        SetUpStepOutcome(execution.Id, 0, remainingTasks: 0,
            Outcome(failing, ActivityStatus.FailedWithError), Outcome(succeeding, ActivityStatus.Complete));
        SetUpNextWaitingStep(execution.Id, 1);

        await _application.Tasking.CompleteWorkerTaskAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.InProgress));
            Assert.That(execution.CurrentStepIndex, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task CompleteWorkerTask_ParallelGroupFailingStepStops_StopsAndNamesThatStepAsync()
    {
        var failing = RunProfileStep(0, continueOnFailure: false);
        var succeeding = RunProfileStep(0, continueOnFailure: true);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, failing, succeeding, RunProfileStep(1));
        var task = CreateWorkerTask(execution.Id, stepIndex: 0);
        SetUpStepOutcome(execution.Id, 0, remainingTasks: 0,
            Outcome(failing, ActivityStatus.FailedWithError, "Active Directory", "Export"),
            Outcome(succeeding, ActivityStatus.Complete, "HR", "Full Import"));
        SetUpNextWaitingStep(execution.Id, 1);

        await _application.Tasking.CompleteWorkerTaskAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
            Assert.That(execution.ErrorMessage, Is.EqualTo(
                "Step 1, Active Directory - Export, failed. It is set to stop the Schedule when it fails, so the remaining steps did not run."),
                "the message names the step that failed, not its sibling");
        }
    }

    [Test]
    public async Task CompleteWorkerTask_ParallelGroupLegacyActivityWithoutStepId_FallsBackToTheStrictRuleAsync()
    {
        // An Activity recorded before steps were identified cannot say which sibling it belongs to, so the group is
        // judged as before: any step at the position set to stop the Schedule stops it.
        var failing = RunProfileStep(0, continueOnFailure: true);
        var sibling = RunProfileStep(0, continueOnFailure: false);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, failing, sibling, RunProfileStep(1));
        var task = CreateWorkerTask(execution.Id, stepIndex: 0);
        var legacyFailure = Outcome(failing, ActivityStatus.FailedWithError, "HR", "Full Import");
        legacyFailure.ScheduleStepId = null;
        SetUpStepOutcome(execution.Id, 0, remainingTasks: 0, legacyFailure, Outcome(sibling, ActivityStatus.Complete));
        SetUpNextWaitingStep(execution.Id, 1);

        await _application.Tasking.CompleteWorkerTaskAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
            Assert.That(execution.ErrorMessage, Is.EqualTo("Step 1, HR - Full Import, failed, so the remaining steps did not run."));
        }
    }

    [Test]
    public async Task CompleteWorkerTask_FailedActivityForAStepSinceDeleted_FallsBackToTheStrictRuleAsync()
    {
        var sibling = RunProfileStep(0, continueOnFailure: false);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, sibling, RunProfileStep(1));
        var task = CreateWorkerTask(execution.Id, stepIndex: 0);
        var orphanedFailure = Outcome(RunProfileStep(0, continueOnFailure: true), ActivityStatus.FailedWithError);
        SetUpStepOutcome(execution.Id, 0, remainingTasks: 0, orphanedFailure, Outcome(sibling, ActivityStatus.Complete));
        SetUpNextWaitingStep(execution.Id, 1);

        await _application.Tasking.CompleteWorkerTaskAsync(task);

        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed), "an unidentifiable failure fails safe");
    }

    [Test]
    public async Task CompleteWorkerTask_StepThatCouldNotBeQueuedAndContinues_IsTreatedAsFailedButContinueAsync()
    {
        // Step 3 of the group could not be queued at start and was set to continue: its Failed Activity carries its
        // step, so when its queued sibling finishes, the group reads as failed-but-continue and the Schedule moves on,
        // even though the sibling itself is set to stop.
        var notQueued = RunProfileStep(2, continueOnFailure: true);
        var sibling = RunProfileStep(2, continueOnFailure: false);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 2, notQueued, sibling, RunProfileStep(3));
        var task = CreateWorkerTask(execution.Id, stepIndex: 2);
        var notQueuedActivity = Outcome(notQueued, ActivityStatus.FailedWithError);
        notQueuedActivity.ErrorMessage = "Could not be queued: Connected System 'HR' is being deleted; run profiles cannot be executed against it.";
        SetUpStepOutcome(execution.Id, 2, remainingTasks: 0, notQueuedActivity, Outcome(sibling, ActivityStatus.Complete));
        SetUpNextWaitingStep(execution.Id, 3);

        await _application.Tasking.CompleteWorkerTaskAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.InProgress));
            Assert.That(execution.CurrentStepIndex, Is.EqualTo(3));
        }
    }

    [Test]
    public void CompleteWorkerTask_AdvancementFails_DoesNotThrowAsync()
    {
        // Arrange: The advancement logic throws, but the task completion should not fail
        var executionId = Guid.NewGuid();
        var task = CreateWorkerTask(scheduleExecutionId: executionId, stepIndex: 0);

        _mockTaskingRepository.Setup(r => r.GetWorkerTaskCountByExecutionStepAsync(executionId, 0))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        // Act & Assert: Should not throw
        Assert.That(() =>
            _application.Tasking.CompleteWorkerTaskAsync(task), Throws.Nothing);
    }

    #region Helper methods

    private static ScheduleStep RunProfileStep(int stepIndex, bool continueOnFailure = false) => new()
    {
        Id = Guid.NewGuid(),
        StepIndex = stepIndex,
        StepType = ScheduleStepType.RunProfile,
        ConnectedSystemId = 1,
        RunProfileId = 100,
        ContinueOnFailure = continueOnFailure
    };

    private ScheduleExecution SetUpExecution(ScheduleExecutionStatus status, int currentStepIndex, params ScheduleStep[] steps)
    {
        var schedule = new Schedule { Id = Guid.NewGuid(), Name = "Nightly Sync", Steps = steps.ToList() };
        var execution = new ScheduleExecution
        {
            Id = Guid.NewGuid(),
            ScheduleId = schedule.Id,
            Schedule = schedule,
            Status = status,
            CurrentStepIndex = currentStepIndex,
            ErrorMessage = status == ScheduleExecutionStatus.Cancelled ? "Cancelled by user" : null
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(execution.Id)).ReturnsAsync(execution);
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(execution.Id)).ReturnsAsync(execution);
        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(execution.Id, It.IsAny<string>())).ReturnsAsync(0);
        return execution;
    }

    private void SetUpStepOutcome(Guid executionId, int stepIndex, int remainingTasks, params Activity[] activities)
    {
        _mockTaskingRepository.Setup(r => r.GetWorkerTaskCountByExecutionStepAsync(executionId, stepIndex))
            .ReturnsAsync(remainingTasks);
        _mockActivityRepository.Setup(r => r.GetActivitiesByScheduleExecutionStepAsync(executionId, stepIndex))
            .ReturnsAsync(activities.ToList());
    }

    private void SetUpNextWaitingStep(Guid executionId, int? nextStepIndex)
    {
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(executionId)).ReturnsAsync(nextStepIndex);
    }

    private static Activity Outcome(ScheduleStep step, ActivityStatus status, string? connectedSystemName = null, string? runProfileName = null) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        TargetContext = connectedSystemName,
        TargetName = runProfileName,
        ScheduleStepIndex = step.StepIndex,
        ScheduleStepId = step.Id
    };

    private static SynchronisationWorkerTask CreateWorkerTask(Guid? scheduleExecutionId, int? stepIndex)
    {
        return new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 1,
            ConnectedSystemRunProfileId = 100,
            Status = WorkerTaskStatus.Processing,
            ScheduleExecutionId = scheduleExecutionId,
            ScheduleStepIndex = stepIndex,
            Activity = new Activity
            {
                Id = Guid.NewGuid(),
                Status = ActivityStatus.InProgress
            }
        };
    }

    #endregion
}

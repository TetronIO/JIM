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
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for CheckAndAdvanceExecutionAsync, the Scheduler's safety-net advancement (#1768). It shares its decision with
/// the Worker's advancement: a finished execution stays finished, the failing step's own Continue On Failure setting
/// decides whether a parallel step group stops the Schedule, and the reason reads plainly.
/// </summary>
[TestFixture]
public class SchedulerServerStepAdvancementTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;

    /// <summary>
    /// Every Activity recorded against the executions so far, as the repository would hold them.
    /// </summary>
    private List<Activity> _recordedActivities = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);

        _application = new JimApplication(_mockRepository.Object);
        _mockSchedulingRepository.EmulateConditionalTransitions();

        _recordedActivities = new List<Activity>();
        _mockActivityRepository.Setup(r => r.GetFailedScheduleExecutionActivitiesAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid executionId) => _recordedActivities
                .Where(a => a.ScheduleExecutionId == executionId && ScheduleFailureHandling.IsFailedStepOutcome(a.Status))
                .OrderBy(a => a.ScheduleStepIndex)
                .ToList());
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task CheckAndAdvanceExecutionAsync_CancelledExecution_ChangesNothingAsync()
    {
        var step = RunProfileStep(0);
        var execution = SetUpExecution(ScheduleExecutionStatus.Cancelled, currentStepIndex: 0, step, RunProfileStep(1));
        SetUpStep(execution.Id, 0, Outcome(step, ActivityStatus.Complete));
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(1);

        var stillRunning = await _application.Scheduler.CheckAndAdvanceExecutionAsync(execution);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stillRunning, Is.False);
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled));
        }
        _mockSchedulingRepository.Verify(r => r.TryAdvanceScheduleExecutionAsync(It.IsAny<ScheduleExecution>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task CheckAndAdvanceExecutionAsync_CancelledJustBeforeItWouldComplete_StaysCancelledAsync()
    {
        // The safety net reads the execution InProgress, then an administrator cancels it before the safety net writes.
        var step = RunProfileStep(0);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, step);
        SetUpStep(execution.Id, 0, Outcome(step, ActivityStatus.Complete));
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id))
            .Callback(() => execution.Status = ScheduleExecutionStatus.Cancelled)
            .ReturnsAsync((int?)null);

        await _application.Scheduler.CheckAndAdvanceExecutionAsync(execution);

        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Cancelled));
        _mockSchedulingRepository.Verify(r => r.UpdateScheduleExecutionAsync(It.IsAny<ScheduleExecution>()), Times.Never,
            "no unconditional full-row write of the execution may happen during advancement");
    }

    [Test]
    public async Task CheckAndAdvanceExecutionAsync_StepCompleted_AdvancesAndReleasesTheNextGroupAtomicallyAsync()
    {
        var step = RunProfileStep(0);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, step, RunProfileStep(1));
        SetUpStep(execution.Id, 0, Outcome(step, ActivityStatus.Complete));
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(1);

        var stillRunning = await _application.Scheduler.CheckAndAdvanceExecutionAsync(execution);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stillRunning, Is.True);
            Assert.That(execution.CurrentStepIndex, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task CheckAndAdvanceExecutionAsync_StepSetToStopFailed_FailsWithAPlainReasonAsync()
    {
        var step = new ScheduleStep
        {
            Id = Guid.NewGuid(),
            StepIndex = 1,
            StepType = ScheduleStepType.TemporalScopeReconciliation,
            Name = "Reconcile Temporal Scope",
            OnFailure = ScheduleStepFailureBehaviour.FollowSchedule
        };
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 1, RunProfileStep(0), step, RunProfileStep(2));
        var failure = Outcome(step, ActivityStatus.FailedWithError);
        failure.TargetName = "Temporal Scope Reconciliation";
        SetUpStep(execution.Id, 1, failure);
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(2);

        var stillRunning = await _application.Scheduler.CheckAndAdvanceExecutionAsync(execution);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stillRunning, Is.False);
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
            Assert.That(execution.ErrorMessage, Is.EqualTo(
                "Step 2, Reconcile Temporal Scope, failed. It is set to stop the Schedule when it fails, so the remaining steps did not run."),
                "a step with a name of its own is called by it");
        }
        _mockTaskingRepository.Verify(
            r => r.DeleteWaitingTasksForExecutionAsync(execution.Id, ScheduleStepNotRunReasons.EarlierStepStoppedSchedule), Times.Once);
    }

    [Test]
    public async Task CheckAndAdvanceExecutionAsync_ParallelGroupFailingStepContinuesAndSucceedingSiblingStops_ContinuesAsync()
    {
        var failing = RunProfileStep(0, continueOnFailure: true);
        var succeeding = RunProfileStep(0, continueOnFailure: false);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, failing, succeeding, RunProfileStep(1));
        SetUpStep(execution.Id, 0, Outcome(failing, ActivityStatus.FailedWithError), Outcome(succeeding, ActivityStatus.Complete));
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(1);

        var stillRunning = await _application.Scheduler.CheckAndAdvanceExecutionAsync(execution);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stillRunning, Is.True);
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.InProgress));
            Assert.That(execution.CurrentStepIndex, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task CheckAndAdvanceExecutionAsync_ParallelGroupTwoStopStepsFail_NamesBothAsync()
    {
        var first = RunProfileStep(0, continueOnFailure: false);
        var second = RunProfileStep(0, continueOnFailure: false);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, first, second, RunProfileStep(1));
        SetUpStep(execution.Id, 0,
            Outcome(first, ActivityStatus.FailedWithError, "HR", "Full Import"),
            Outcome(second, ActivityStatus.CompleteWithError, "Active Directory", "Export"));

        await _application.Scheduler.CheckAndAdvanceExecutionAsync(execution);

        Assert.That(execution.ErrorMessage, Is.EqualTo(
            "Step 1, HR - Full Import and Active Directory - Export, failed. They are set to stop the Schedule when they fail, so the remaining steps did not run."));
    }

    [Test]
    public async Task CheckAndAdvanceExecutionAsync_ParallelGroupLegacyActivityWithoutStepId_FallsBackToTheStrictRuleAsync()
    {
        var failing = RunProfileStep(0, continueOnFailure: true);
        var sibling = RunProfileStep(0, continueOnFailure: false);
        var execution = SetUpExecution(ScheduleExecutionStatus.InProgress, currentStepIndex: 0, failing, sibling, RunProfileStep(1));
        var legacyFailure = Outcome(failing, ActivityStatus.FailedWithError);
        legacyFailure.ScheduleStepId = null;
        SetUpStep(execution.Id, 0, legacyFailure, Outcome(sibling, ActivityStatus.Complete));
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(1);

        await _application.Scheduler.CheckAndAdvanceExecutionAsync(execution);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
            Assert.That(execution.ErrorMessage, Is.EqualTo("Step 1 failed, so the remaining steps did not run."));
        }
    }

    #region Helpers

    private static ScheduleStep RunProfileStep(int stepIndex, bool continueOnFailure = false) => new()
    {
        Id = Guid.NewGuid(),
        StepIndex = stepIndex,
        StepType = ScheduleStepType.RunProfile,
        ConnectedSystemId = 1,
        RunProfileId = 100,
        // A step set to continue overrides the Schedule; otherwise it follows the Schedule, which stops (the default).
        OnFailure = continueOnFailure ? ScheduleStepFailureBehaviour.Continue : ScheduleStepFailureBehaviour.FollowSchedule
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
            CurrentStepIndex = currentStepIndex
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(execution.Id)).ReturnsAsync(execution);
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(execution.Id)).ReturnsAsync(execution);
        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(execution.Id, It.IsAny<string>())).ReturnsAsync(0);
        return execution;
    }

    /// <summary>
    /// A step group whose Worker Tasks have all gone and whose Activities say how it ended.
    /// </summary>
    private void SetUpStep(Guid executionId, int stepIndex, params Activity[] activities)
    {
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionStepAsync(executionId, stepIndex))
            .ReturnsAsync(new List<WorkerTask>());
        _mockActivityRepository.Setup(r => r.GetActivitiesByScheduleExecutionStepAsync(executionId, stepIndex))
            .ReturnsAsync(activities.ToList());
        foreach (var activity in activities)
            activity.ScheduleExecutionId = executionId;
        _recordedActivities.AddRange(activities);
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

    #endregion
}

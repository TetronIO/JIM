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
/// Tests Schedule failure handling (#1787) through the Scheduler's step-group conclusion, which the Worker's advancement
/// shares: a step's effective behaviour is its own setting, or its Schedule's "When a step fails" when it follows the
/// Schedule; and a run that carried on past a failed step ends Complete With Error, naming each failed step, rather than
/// Complete.
/// </summary>
/// <remarks>
/// The example Schedule is the PRD's "Nightly HR sync": 1 HR / Full Import, 2 HR / Full Synchronisation,
/// 3 Active Directory / Export, 4 Service Desk / Export.
/// </remarks>
[TestFixture]
public class SchedulerServerFailureHandlingTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private JimApplication _application = null!;

    /// <summary>
    /// Every Activity the execution has recorded so far, as the repository would hold them.
    /// </summary>
    private List<Activity> _executionActivities = null!;

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

        _executionActivities = new List<Activity>();
        _mockActivityRepository.Setup(r => r.GetFailedScheduleExecutionActivitiesAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid executionId) => _executionActivities
                .Where(a => a.ScheduleExecutionId == executionId &&
                            a.Status is ActivityStatus.FailedWithError or ActivityStatus.CompleteWithError or ActivityStatus.Cancelled)
                .OrderBy(a => a.ScheduleStepIndex)
                .ToList());
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    [Test]
    public async Task Scenario1_ScheduleContinuesAndAFollowingStepFails_TheNextStepRunsAndTheRunEndsCompleteWithErrorAsync()
    {
        var steps = NightlyHrSync();
        var execution = SetUpExecution(ScheduleFailureBehaviour.Continue, currentStepIndex: 2, steps);

        // Step 3 fails; step 4 is still waiting.
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(3);
        var stillRunning = await ConcludeAsync(execution, 2, Outcome(execution, steps[2], ActivityStatus.FailedWithError));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stillRunning, Is.True, "a step that follows a continuing Schedule does not stop it");
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.InProgress));
            Assert.That(execution.CurrentStepIndex, Is.EqualTo(3), "step 4 is released");
        }
        _mockTaskingRepository.Verify(r => r.DeleteWaitingTasksForExecutionAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);

        // Step 4 then succeeds, and nothing is left waiting.
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync((int?)null);
        await ConcludeAsync(execution, 3, Outcome(execution, steps[3], ActivityStatus.Complete));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.CompleteWithError),
                "a run that carried on past a failed step is not a clean run");
            Assert.That(execution.ErrorMessage, Is.EqualTo(
                "The Schedule finished, but step 3, Active Directory - Export, failed. It is set to let the Schedule continue."));
            Assert.That(execution.CompletedAt, Is.Not.Null);
        }
    }

    [Test]
    public async Task Scenario2_ScheduleContinuesButTheFailingStepIsSetToStop_TheRunFailsAndTheRestIsCancelledAsync()
    {
        var steps = NightlyHrSync();
        steps[2].OnFailure = ScheduleStepFailureBehaviour.Stop;
        var execution = SetUpExecution(ScheduleFailureBehaviour.Continue, currentStepIndex: 2, steps);
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(3);

        var stillRunning = await ConcludeAsync(execution, 2, Outcome(execution, steps[2], ActivityStatus.FailedWithError));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stillRunning, Is.False);
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed), "the step's own setting overrides the Schedule's");
            Assert.That(execution.ErrorMessage, Is.EqualTo(
                "Step 3, Active Directory - Export, failed. It is set to stop the Schedule when it fails, so the remaining steps did not run."));
        }
        _mockTaskingRepository.Verify(
            r => r.DeleteWaitingTasksForExecutionAsync(execution.Id, ScheduleStepNotRunReasons.EarlierStepStoppedSchedule), Times.Once,
            "step 4 is not run");
    }

    [Test]
    public async Task Scenario3_ScheduleStopsButTheFailingStepIsSetToContinue_TheRunEndsCompleteWithErrorAsync()
    {
        var steps = NightlyHrSync();
        steps[2].OnFailure = ScheduleStepFailureBehaviour.Continue;
        var execution = SetUpExecution(ScheduleFailureBehaviour.Stop, currentStepIndex: 2, steps);

        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(3);
        await ConcludeAsync(execution, 2, Outcome(execution, steps[2], ActivityStatus.CompleteWithError));
        Assert.That(execution.CurrentStepIndex, Is.EqualTo(3), "step 4 runs");

        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync((int?)null);
        await ConcludeAsync(execution, 3, Outcome(execution, steps[3], ActivityStatus.Complete));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.CompleteWithError), "not Complete");
            Assert.That(execution.ErrorMessage, Is.EqualTo(
                "The Schedule finished, but step 3, Active Directory - Export, failed. It is set to let the Schedule continue."));
        }
    }

    [Test]
    public async Task ConcludeStepGroup_CleanRunUnderAContinuingSchedule_EndsCompleteAsync()
    {
        var steps = NightlyHrSync();
        var execution = SetUpExecution(ScheduleFailureBehaviour.Continue, currentStepIndex: 3, steps);
        _executionActivities.Add(Outcome(execution, steps[0], ActivityStatus.Complete));
        _executionActivities.Add(Outcome(execution, steps[1], ActivityStatus.CompleteWithWarning));
        _executionActivities.Add(Outcome(execution, steps[2], ActivityStatus.Complete));
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync((int?)null);

        await ConcludeAsync(execution, 3, Outcome(execution, steps[3], ActivityStatus.Complete));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Complete),
                "only a failed step makes a run Complete With Error; warnings do not");
            Assert.That(execution.ErrorMessage, Is.Null);
        }
    }

    [Test]
    public async Task ConcludeStepGroup_TwoStepsFailedAndContinued_NamesBothInStepOrderAsync()
    {
        var steps = NightlyHrSync();
        var execution = SetUpExecution(ScheduleFailureBehaviour.Continue, currentStepIndex: 3, steps);
        // Recorded out of step order, to prove the message is in step order.
        _executionActivities.Add(Outcome(execution, steps[2], ActivityStatus.FailedWithError));
        _executionActivities.Add(Outcome(execution, steps[0], ActivityStatus.Complete));
        _executionActivities.Add(Outcome(execution, steps[1], ActivityStatus.Cancelled));
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync((int?)null);

        await ConcludeAsync(execution, 3, Outcome(execution, steps[3], ActivityStatus.Complete));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.CompleteWithError));
            Assert.That(execution.ErrorMessage, Is.EqualTo(
                "The Schedule finished, but steps 2 and 3 (HR - Full Synchronisation; Active Directory - Export) failed. " +
                "They are set to let the Schedule continue."));
        }
    }

    [Test]
    public async Task ConcludeStepGroup_ParallelGroupFailingMemberFollowsAContinuingSchedule_DoesNotStopAsync()
    {
        var failing = RunProfileStep(0, ScheduleStepFailureBehaviour.FollowSchedule);
        var succeeding = RunProfileStep(0, ScheduleStepFailureBehaviour.Stop);
        var execution = SetUpExecution(ScheduleFailureBehaviour.Continue, currentStepIndex: 0, [failing, succeeding, RunProfileStep(1)]);
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(1);

        var stillRunning = await ConcludeAsync(execution, 0,
            Outcome(execution, failing, ActivityStatus.FailedWithError), Outcome(execution, succeeding, ActivityStatus.Complete));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stillRunning, Is.True, "the failing step's effective behaviour decides; the sibling that succeeded has no say");
            Assert.That(execution.CurrentStepIndex, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ConcludeStepGroup_ParallelGroupFailingMemberFollowsAStoppingSchedule_StopsAsync()
    {
        var failing = RunProfileStep(0, ScheduleStepFailureBehaviour.FollowSchedule);
        var succeeding = RunProfileStep(0, ScheduleStepFailureBehaviour.Continue);
        var execution = SetUpExecution(ScheduleFailureBehaviour.Stop, currentStepIndex: 0, [failing, succeeding, RunProfileStep(1)]);
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(1);

        await ConcludeAsync(execution, 0,
            Outcome(execution, failing, ActivityStatus.FailedWithError), Outcome(execution, succeeding, ActivityStatus.Complete));

        Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed));
    }

    [Test]
    public async Task ConcludeStepGroup_LegacyActivityAndEveryStepFollowsAContinuingSchedule_ContinuesAsync()
    {
        // The older, stricter rule (used when a failed Activity names no step) judges every step at the position by its
        // effective behaviour, so steps following a continuing Schedule let it carry on.
        var failing = RunProfileStep(0, ScheduleStepFailureBehaviour.FollowSchedule);
        var sibling = RunProfileStep(0, ScheduleStepFailureBehaviour.FollowSchedule);
        var execution = SetUpExecution(ScheduleFailureBehaviour.Continue, currentStepIndex: 0, [failing, sibling, RunProfileStep(1)]);
        var legacyFailure = Outcome(execution, failing, ActivityStatus.FailedWithError);
        legacyFailure.ScheduleStepId = null;
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(1);

        var stillRunning = await ConcludeAsync(execution, 0, legacyFailure, Outcome(execution, sibling, ActivityStatus.Complete));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stillRunning, Is.True);
            Assert.That(execution.CurrentStepIndex, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ConcludeStepGroup_LegacyActivityAndOneStepOverridesToStop_StopsAsync()
    {
        var failing = RunProfileStep(0, ScheduleStepFailureBehaviour.FollowSchedule);
        var sibling = RunProfileStep(0, ScheduleStepFailureBehaviour.Stop);
        var execution = SetUpExecution(ScheduleFailureBehaviour.Continue, currentStepIndex: 0, [failing, sibling, RunProfileStep(1)]);
        var legacyFailure = Outcome(execution, failing, ActivityStatus.FailedWithError);
        legacyFailure.ScheduleStepId = null;
        _mockTaskingRepository.Setup(r => r.GetNextWaitingStepIndexAsync(execution.Id)).ReturnsAsync(1);

        await ConcludeAsync(execution, 0, legacyFailure, Outcome(execution, sibling, ActivityStatus.Complete));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(execution.Status, Is.EqualTo(ScheduleExecutionStatus.Failed), "the strict rule still fails safe");
            Assert.That(execution.ErrorMessage, Is.EqualTo("Step 1 failed, so the remaining steps did not run."));
        }
    }

    [Test]
    public async Task ExecutionDetail_StepState_CarriesTheEffectiveBehaviourAndItsSourceAsync()
    {
        // Steps with no Connected System, so the detail has no Run Profile names to resolve.
        var following = new ScheduleStep { Id = Guid.NewGuid(), StepIndex = 0, StepType = ScheduleStepType.HistoryRetentionCleanup, Name = "Clean up" };
        var stopping = new ScheduleStep
        {
            Id = Guid.NewGuid(),
            StepIndex = 1,
            StepType = ScheduleStepType.TemporalScopeReconciliation,
            Name = "Reconcile",
            OnFailure = ScheduleStepFailureBehaviour.Stop
        };
        var execution = SetUpExecution(ScheduleFailureBehaviour.Continue, currentStepIndex: 0, [following, stopping]);
        _mockSchedulingRepository.Setup(r => r.GetScheduleStepsAsync(execution.ScheduleId)).ReturnsAsync(execution.Schedule!.Steps);
        _mockActivityRepository.Setup(r => r.GetActivitiesByScheduleExecutionAsync(execution.Id)).ReturnsAsync(new List<Activity>());
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(execution.Id)).ReturnsAsync(new List<WorkerTask>());

        var detail = await _application.Scheduler.GetScheduleExecutionDetailAsync(execution.Id);

        var followingState = detail!.Steps.Single(s => s.ScheduleStepId == following.Id);
        var stoppingState = detail.Steps.Single(s => s.ScheduleStepId == stopping.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(followingState.ContinueOnFailure, Is.True);
            Assert.That(followingState.FailureBehaviourSource, Is.EqualTo(ScheduleFailureBehaviourSource.Schedule));
            Assert.That(stoppingState.ContinueOnFailure, Is.False);
            Assert.That(stoppingState.FailureBehaviourSource, Is.EqualTo(ScheduleFailureBehaviourSource.Step));
        }
    }

    #region Helpers

    /// <summary>
    /// The Connected System and Run Profile each Run Profile step runs, as its Activity names them.
    /// </summary>
    private readonly Dictionary<Guid, (string ConnectedSystem, string RunProfile)> _stepNames = new();

    /// <summary>
    /// The PRD's example Schedule, with every step following the Schedule.
    /// </summary>
    private List<ScheduleStep> NightlyHrSync() =>
    [
        Named(RunProfileStep(0, ScheduleStepFailureBehaviour.FollowSchedule), "HR", "Full Import"),
        Named(RunProfileStep(1, ScheduleStepFailureBehaviour.FollowSchedule), "HR", "Full Synchronisation"),
        Named(RunProfileStep(2, ScheduleStepFailureBehaviour.FollowSchedule), "Active Directory", "Export"),
        Named(RunProfileStep(3, ScheduleStepFailureBehaviour.FollowSchedule), "Service Desk", "Export")
    ];

    private ScheduleStep Named(ScheduleStep step, string connectedSystemName, string runProfileName)
    {
        _stepNames[step.Id] = (connectedSystemName, runProfileName);
        return step;
    }

    private static ScheduleStep RunProfileStep(int stepIndex, ScheduleStepFailureBehaviour onFailure = ScheduleStepFailureBehaviour.FollowSchedule) => new()
    {
        Id = Guid.NewGuid(),
        StepIndex = stepIndex,
        StepType = ScheduleStepType.RunProfile,
        ConnectedSystemId = 1,
        RunProfileId = 100,
        OnFailure = onFailure
    };

    private ScheduleExecution SetUpExecution(ScheduleFailureBehaviour onStepFailure, int currentStepIndex, List<ScheduleStep> steps)
    {
        var schedule = new Schedule { Id = Guid.NewGuid(), Name = "Nightly HR sync", OnStepFailure = onStepFailure, Steps = steps };
        foreach (var step in steps)
        {
            step.ScheduleId = schedule.Id;
            step.Schedule = schedule;
        }

        var execution = new ScheduleExecution
        {
            Id = Guid.NewGuid(),
            ScheduleId = schedule.Id,
            Schedule = schedule,
            Status = ScheduleExecutionStatus.InProgress,
            CurrentStepIndex = currentStepIndex,
            TotalSteps = steps.Select(s => s.StepIndex).Distinct().Count()
        };

        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(execution.Id)).ReturnsAsync(execution);
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionAsync(execution.Id)).ReturnsAsync(execution);
        _mockTaskingRepository.Setup(r => r.DeleteWaitingTasksForExecutionAsync(execution.Id, It.IsAny<string>())).ReturnsAsync(0);
        return execution;
    }

    /// <summary>
    /// A step group has finished with these Activities: record them against the execution, then let the Scheduler's
    /// safety net conclude it, which is the same decision the Worker's advancement makes.
    /// </summary>
    private async Task<bool> ConcludeAsync(ScheduleExecution execution, int stepIndex, params Activity[] activities)
    {
        _executionActivities.AddRange(activities);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionStepAsync(execution.Id, stepIndex))
            .ReturnsAsync(new List<WorkerTask>());
        _mockActivityRepository.Setup(r => r.GetActivitiesByScheduleExecutionStepAsync(execution.Id, stepIndex))
            .ReturnsAsync(activities.ToList());

        return await _application.Scheduler.CheckAndAdvanceExecutionAsync(execution);
    }

    private Activity Outcome(ScheduleExecution execution, ScheduleStep step, ActivityStatus status)
    {
        var named = _stepNames.TryGetValue(step.Id, out var names);
        return new Activity
        {
            Id = Guid.NewGuid(),
            Status = status,
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetContext = named ? names.ConnectedSystem : null,
            TargetName = named ? names.RunProfile : null,
            ScheduleExecutionId = execution.Id,
            ScheduleStepIndex = step.StepIndex,
            ScheduleStepId = step.Id,
            Created = DateTime.UtcNow
        };
    }

    #endregion
}

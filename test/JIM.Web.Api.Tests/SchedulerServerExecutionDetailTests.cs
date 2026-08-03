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
/// Tests GetScheduleExecutionDetailAsync, which assembles a Schedule Execution's per-step state from the step
/// definitions plus their Activities (permanent) and Worker Tasks (ephemeral). This logic previously lived in
/// ScheduleExecutionsController and is shared by the REST endpoint and the portal, so its behaviour is pinned here.
/// </summary>
[TestFixture]
public class SchedulerServerExecutionDetailTests
{
    private Mock<IRepository> _mockRepository = null!;
    private Mock<ISchedulingRepository> _mockSchedulingRepository = null!;
    private Mock<ITaskingRepository> _mockTaskingRepository = null!;
    private Mock<IActivityRepository> _mockActivityRepository = null!;
    private Mock<IConnectedSystemRepository> _mockConnectedSystemRepository = null!;
    private JimApplication _application = null!;

    private static readonly Guid ExecutionId = Guid.NewGuid();
    private static readonly Guid ScheduleId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _mockRepository = new Mock<IRepository>();
        _mockSchedulingRepository = new Mock<ISchedulingRepository>();
        _mockTaskingRepository = new Mock<ITaskingRepository>();
        _mockActivityRepository = new Mock<IActivityRepository>();
        _mockConnectedSystemRepository = new Mock<IConnectedSystemRepository>();

        _mockRepository.Setup(r => r.Scheduling).Returns(_mockSchedulingRepository.Object);
        _mockRepository.Setup(r => r.Tasking).Returns(_mockTaskingRepository.Object);
        _mockRepository.Setup(r => r.Activity).Returns(_mockActivityRepository.Object);
        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockConnectedSystemRepository.Object);

        _application = new JimApplication(_mockRepository.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    /// <summary>
    /// The miss path must return before touching Activities, Worker Tasks or steps. Callers that only stub the
    /// execution lookup would otherwise await a null Task from a loose mock and throw.
    /// </summary>
    [Test]
    public async Task GetScheduleExecutionDetailAsync_ExecutionNotFound_ReturnsNullWithoutLoadingAnythingElseAsync()
    {
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(ExecutionId))
            .ReturnsAsync((ScheduleExecution?)null);

        var detail = await _application.Scheduler.GetScheduleExecutionDetailAsync(ExecutionId);

        Assert.That(detail, Is.Null);
        _mockActivityRepository.Verify(r => r.GetActivitiesByScheduleExecutionAsync(It.IsAny<Guid>()), Times.Never);
        _mockTaskingRepository.Verify(r => r.GetWorkerTasksByScheduleExecutionAsync(It.IsAny<Guid>()), Times.Never);
        _mockSchedulingRepository.Verify(r => r.GetScheduleStepsAsync(It.IsAny<Guid>()), Times.Never);
    }

    /// <summary>
    /// A Schedule can be deleted while its executions remain. The execution must still be returned, with no steps,
    /// rather than throwing.
    /// </summary>
    [Test]
    public async Task GetScheduleExecutionDetailAsync_ScheduleDeleted_ReturnsExecutionWithNoStepsAsync()
    {
        var execution = NewExecution();
        execution.Schedule = null!;
        SetUpExecution(execution, steps: [], activities: [], tasks: []);

        var detail = await _application.Scheduler.GetScheduleExecutionDetailAsync(ExecutionId);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Execution.Id, Is.EqualTo(ExecutionId));
        Assert.That(detail.Steps, Is.Empty);
    }

    /// <summary>
    /// Parallel steps share a step index, so their Activities must be matched on Connected System. Getting this
    /// wrong attributes one system's failure to another, which is the failure the whole feature exists to surface.
    /// </summary>
    [Test]
    public async Task GetScheduleExecutionDetailAsync_ParallelSteps_MatchesActivityByConnectedSystemAsync()
    {
        var badgeStep = NewStep(stepIndex: 1, connectedSystemId: 10, StepExecutionMode.Sequential);
        var contractorStep = NewStep(stepIndex: 1, connectedSystemId: 20, StepExecutionMode.ParallelWithPrevious);

        var badgeActivity = NewActivity(stepIndex: 1, connectedSystemId: 10, ActivityStatus.Complete);
        var contractorActivity = NewActivity(stepIndex: 1, connectedSystemId: 20, ActivityStatus.FailedWithError);

        SetUpExecution(NewExecution(), [badgeStep, contractorStep], [badgeActivity, contractorActivity], []);

        var detail = await _application.Scheduler.GetScheduleExecutionDetailAsync(ExecutionId);

        Assert.That(detail, Is.Not.Null);
        var badge = detail!.Steps.Single(s => s.ConnectedSystemId == 10);
        var contractor = detail.Steps.Single(s => s.ConnectedSystemId == 20);

        Assert.Multiple(() =>
        {
            Assert.That(badge.Status, Is.EqualTo(ScheduleExecutionStepStatus.Completed));
            Assert.That(badge.ActivityId, Is.EqualTo(badgeActivity.Id));
            Assert.That(contractor.Status, Is.EqualTo(ScheduleExecutionStepStatus.Failed));
            Assert.That(contractor.ActivityId, Is.EqualTo(contractorActivity.Id));
        });
    }

    /// <summary>
    /// Where a step index holds only one step, its Activity is matched even when the Connected System does not line
    /// up: non-Run-Profile steps carry no Connected System at all.
    /// </summary>
    [Test]
    public async Task GetScheduleExecutionDetailAsync_SingleStepAtIndex_FallsBackToTheOnlyActivityAsync()
    {
        var step = NewStep(stepIndex: 0, connectedSystemId: null, StepExecutionMode.Sequential);
        step.StepType = ScheduleStepType.PowerShell;
        var activity = NewActivity(stepIndex: 0, connectedSystemId: null, ActivityStatus.CompleteWithWarning);

        SetUpExecution(NewExecution(), [step], [activity], []);

        var detail = await _application.Scheduler.GetScheduleExecutionDetailAsync(ExecutionId);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Steps.Single().Status, Is.EqualTo(ScheduleExecutionStepStatus.CompletedWithWarning));
        Assert.That(detail.Steps.Single().ActivityId, Is.EqualTo(activity.Id));
    }

    /// <summary>
    /// A live Worker Task outranks the Activity, because the Activity is still in progress while the task runs.
    /// </summary>
    [Test]
    public async Task GetScheduleExecutionDetailAsync_ActiveWorkerTask_PrefersTaskStatusAsync()
    {
        var step = NewStep(stepIndex: 0, connectedSystemId: 10, StepExecutionMode.Sequential);
        var task = new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = 10,
            ScheduleExecutionId = ExecutionId,
            ScheduleStepIndex = 0,
            Status = WorkerTaskStatus.Processing
        };

        SetUpExecution(NewExecution(), [step], [], [task]);

        var detail = await _application.Scheduler.GetScheduleExecutionDetailAsync(ExecutionId);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Steps.Single().Status, Is.EqualTo(ScheduleExecutionStepStatus.Processing));
        Assert.That(detail.Steps.Single().TaskId, Is.EqualTo(task.Id));
    }

    /// <summary>
    /// With neither a task nor an Activity, the status is inferred from where the execution has got to. A step past
    /// the current index on a stopped execution is Pending, which is what tells an administrator it never ran.
    /// </summary>
    [Test]
    public async Task GetScheduleExecutionDetailAsync_NoTaskOrActivity_InfersStatusFromExecutionPositionAsync()
    {
        var doneStep = NewStep(stepIndex: 0, connectedSystemId: 10, StepExecutionMode.Sequential);
        var currentStep = NewStep(stepIndex: 1, connectedSystemId: 20, StepExecutionMode.Sequential);
        var laterStep = NewStep(stepIndex: 2, connectedSystemId: 30, StepExecutionMode.Sequential);

        var execution = NewExecution();
        execution.CurrentStepIndex = 1;
        execution.Status = ScheduleExecutionStatus.InProgress;

        SetUpExecution(execution, [doneStep, currentStep, laterStep], [], []);

        var detail = await _application.Scheduler.GetScheduleExecutionDetailAsync(ExecutionId);

        Assert.That(detail, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(detail!.Steps[0].Status, Is.EqualTo(ScheduleExecutionStepStatus.Completed));
            Assert.That(detail.Steps[1].Status, Is.EqualTo(ScheduleExecutionStepStatus.Waiting));
            Assert.That(detail.Steps[2].Status, Is.EqualTo(ScheduleExecutionStepStatus.Pending));
        });
    }

    /// <summary>
    /// Duration is derived from the Activity's start plus its total run time; the portal shows it per step.
    /// </summary>
    [Test]
    public async Task GetScheduleExecutionDetailAsync_CompletedStep_ComputesDurationAsync()
    {
        var step = NewStep(stepIndex: 0, connectedSystemId: 10, StepExecutionMode.Sequential);
        var activity = NewActivity(stepIndex: 0, connectedSystemId: 10, ActivityStatus.Complete);
        activity.Executed = new DateTime(2026, 8, 3, 2, 0, 0, DateTimeKind.Utc);
        activity.TotalActivityTime = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(15);

        SetUpExecution(NewExecution(), [step], [activity], []);

        var detail = await _application.Scheduler.GetScheduleExecutionDetailAsync(ExecutionId);

        Assert.That(detail, Is.Not.Null);
        var state = detail!.Steps.Single();
        Assert.Multiple(() =>
        {
            Assert.That(state.StartedAt, Is.EqualTo(activity.Executed));
            Assert.That(state.CompletedAt, Is.EqualTo(activity.Executed + activity.TotalActivityTime));
            Assert.That(state.Duration, Is.EqualTo(TimeSpan.FromSeconds(135)));
        });
    }

    /// <summary>
    /// Run Profile steps store no name, so the portal would otherwise show "Step 1", "Step 2" for every one of them.
    /// The Connected System and Run Profile names are resolved so the step list names the work that actually ran.
    /// </summary>
    [Test]
    public async Task GetScheduleExecutionDetailAsync_RunProfileStep_ResolvesConnectedSystemAndRunProfileNamesAsync()
    {
        var step = NewStep(stepIndex: 0, connectedSystemId: 10, StepExecutionMode.Sequential);
        step.RunProfileId = 77;

        SetUpExecution(NewExecution(), [step], [], []);

        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemHeadersAsync())
            .ReturnsAsync([new JIM.Models.Staging.DTOs.ConnectedSystemHeader { Id = 10, Name = "Corporate LDAP" }]);
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemRunProfilesAsync(10))
            .ReturnsAsync([new JIM.Models.Staging.ConnectedSystemRunProfile { Id = 77, Name = "LDAP Delta Import" }]);

        var detail = await _application.Scheduler.GetScheduleExecutionDetailAsync(ExecutionId);

        Assert.That(detail, Is.Not.Null);
        var state = detail!.Steps.Single();
        Assert.Multiple(() =>
        {
            Assert.That(state.ConnectedSystemName, Is.EqualTo("Corporate LDAP"));
            Assert.That(state.RunProfileName, Is.EqualTo("LDAP Delta Import"));
        });
    }

    /// <summary>
    /// Steps arrive ordered by index and then by Connected System, so parallel siblings render in a stable order
    /// rather than shuffling between polls.
    /// </summary>
    [Test]
    public async Task GetScheduleExecutionDetailAsync_Steps_AreOrderedByIndexThenConnectedSystemAsync()
    {
        var later = NewStep(stepIndex: 1, connectedSystemId: 20, StepExecutionMode.Sequential);
        var laterSibling = NewStep(stepIndex: 1, connectedSystemId: 5, StepExecutionMode.ParallelWithPrevious);
        var first = NewStep(stepIndex: 0, connectedSystemId: 99, StepExecutionMode.Sequential);

        SetUpExecution(NewExecution(), [later, laterSibling, first], [], []);

        var detail = await _application.Scheduler.GetScheduleExecutionDetailAsync(ExecutionId);

        Assert.That(detail, Is.Not.Null);
        Assert.That(detail!.Steps.Select(s => s.ConnectedSystemId), Is.EqualTo(new int?[] { 99, 5, 20 }));
    }

    // ─── helpers ───

    private static ScheduleExecution NewExecution()
    {
        return new ScheduleExecution
        {
            Id = ExecutionId,
            ScheduleId = ScheduleId,
            ScheduleName = "Nightly Full Sync",
            Status = ScheduleExecutionStatus.Failed,
            CurrentStepIndex = 0,
            TotalSteps = 6,
            QueuedAt = new DateTime(2026, 8, 3, 2, 0, 0, DateTimeKind.Utc),
            Schedule = new Schedule { Id = ScheduleId, Name = "Nightly Full Sync" }
        };
    }

    private static ScheduleStep NewStep(int stepIndex, int? connectedSystemId, StepExecutionMode executionMode)
    {
        return new ScheduleStep
        {
            Id = Guid.NewGuid(),
            ScheduleId = ScheduleId,
            StepIndex = stepIndex,
            ConnectedSystemId = connectedSystemId,
            ExecutionMode = executionMode,
            StepType = ScheduleStepType.RunProfile
        };
    }

    private static Activity NewActivity(int stepIndex, int? connectedSystemId, ActivityStatus status)
    {
        return new Activity
        {
            Id = Guid.NewGuid(),
            ScheduleExecutionId = ExecutionId,
            ScheduleStepIndex = stepIndex,
            ConnectedSystemId = connectedSystemId,
            Status = status,
            Executed = new DateTime(2026, 8, 3, 2, 0, 0, DateTimeKind.Utc)
        };
    }

    private void SetUpExecution(
        ScheduleExecution execution,
        List<ScheduleStep> steps,
        List<Activity> activities,
        List<WorkerTask> tasks)
    {
        _mockSchedulingRepository.Setup(r => r.GetScheduleExecutionWithScheduleAsync(ExecutionId))
            .ReturnsAsync(execution);
        _mockSchedulingRepository.Setup(r => r.GetScheduleStepsAsync(ScheduleId))
            .ReturnsAsync(steps);
        _mockActivityRepository.Setup(r => r.GetActivitiesByScheduleExecutionAsync(ExecutionId))
            .ReturnsAsync(activities);
        _mockTaskingRepository.Setup(r => r.GetWorkerTasksByScheduleExecutionAsync(ExecutionId))
            .ReturnsAsync(tasks);
        _mockConnectedSystemRepository.Setup(r => r.GetConnectedSystemHeadersAsync())
            .ReturnsAsync([]);
    }
}

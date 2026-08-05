// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;

namespace JIM.Models.Scheduling;

/// <summary>
/// The rules for reading a Schedule Execution's progress out of the records it leaves behind (#1162).
/// The Operations queue's group header, the Schedule Executions REST read and PowerShell all have to
/// agree on what "step 2 of 5" means, so the derivation lives here rather than at each call site.
/// </summary>
/// <remarks>
/// The sibling of <see cref="RunPhaseReading"/> one level up: that reads the steps within a single Run
/// Profile execution, this reads the steps of the Schedule running it.
/// </remarks>
public static class ScheduleStepReading
{
    /// <summary>
    /// Where one task has got to, from whichever of its two records exist.
    /// </summary>
    /// <remarks>
    /// The precedence is not "newest record wins". An Activity that has reached a terminal status has
    /// concluded, and outranks a Worker Task that has not yet been tidied away; an Activity that is
    /// still in progress says less than the task beside it, which distinguishes waiting from running.
    /// </remarks>
    public static ScheduleStepStatus StatusOf(ScheduleStepObservation observation)
    {
        if (observation.ActivityStatus is { } activityStatus && activityStatus is not (ActivityStatus.InProgress or ActivityStatus.NotSet))
        {
            return activityStatus switch
            {
                ActivityStatus.Complete or ActivityStatus.CompleteWithWarning => ScheduleStepStatus.Completed,
                ActivityStatus.CompleteWithError or ActivityStatus.FailedWithError => ScheduleStepStatus.Failed,
                ActivityStatus.Cancelled => ScheduleStepStatus.Cancelled,
                _ => ScheduleStepStatus.Pending
            };
        }

        if (observation.TaskStatus is { } taskStatus)
        {
            return taskStatus switch
            {
                WorkerTaskStatus.Processing => ScheduleStepStatus.Running,
                WorkerTaskStatus.CancellationRequested => ScheduleStepStatus.Cancelled,
                _ => ScheduleStepStatus.Pending
            };
        }

        return observation.ActivityStatus == ActivityStatus.InProgress
            ? ScheduleStepStatus.Running
            : ScheduleStepStatus.Pending;
    }

    /// <summary>
    /// Where a step group as a whole has got to, given each of its tasks.
    /// </summary>
    /// <remarks>
    /// A step is not fine because most of it was fine, so a failure carries the group. A group that
    /// has some tasks done and others yet to start counts as under way rather than pending: reporting
    /// it as pending would walk the Schedule backwards on screen as each concurrent task landed.
    /// </remarks>
    public static ScheduleStepStatus Aggregate(IReadOnlyCollection<ScheduleStepStatus> taskStatuses)
    {
        if (taskStatuses.Count == 0)
            return ScheduleStepStatus.Pending;

        if (taskStatuses.Contains(ScheduleStepStatus.Failed))
            return ScheduleStepStatus.Failed;

        if (taskStatuses.Contains(ScheduleStepStatus.Running))
            return ScheduleStepStatus.Running;

        if (taskStatuses.Contains(ScheduleStepStatus.Cancelled))
            return ScheduleStepStatus.Cancelled;

        if (taskStatuses.All(s => s == ScheduleStepStatus.Completed))
            return ScheduleStepStatus.Completed;

        return taskStatuses.Contains(ScheduleStepStatus.Completed)
            ? ScheduleStepStatus.Running
            : ScheduleStepStatus.Pending;
    }

    /// <summary>
    /// A parallel step's task statuses in drawing order: failed, cancelled, completed, running, then
    /// not started, clockwise from twelve o'clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Load-bearing, and the least obvious thing here. A parallel step is drawn as one marker divided
    /// into a wedge per task. Ordering the wedges by task would scatter a single failure anywhere
    /// around a 16px disc, and at one task in twelve it would be invisible; ordering by status means a
    /// failure always starts at twelve o'clock and always reads.
    /// </para>
    /// <para>
    /// It is also what lets the marker degrade gracefully. Past about six tasks the wedges stop being
    /// countable, and what the reader is left with is "mostly done, one failure" rather than a
    /// meaningless pinwheel. That is the right thing to degrade into, and it is why this treatment
    /// survives a Schedule fanning out across a dozen Connected Systems where the alternatives, which
    /// grew a row per task, did not.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ScheduleStepStatus> OrderWedges(IEnumerable<ScheduleStepStatus> taskStatuses) =>
        taskStatuses.OrderBy(WedgeRank).ToList();

    private static int WedgeRank(ScheduleStepStatus status) => status switch
    {
        ScheduleStepStatus.Failed => 0,
        ScheduleStepStatus.Cancelled => 1,
        ScheduleStepStatus.Completed => 2,
        ScheduleStepStatus.Running => 3,
        _ => 4
    };

    /// <summary>
    /// A Schedule Execution's shape, from its recorded step count and whatever its tasks and
    /// Activities currently say. Returns null where there is nothing to draw.
    /// </summary>
    /// <param name="totalSteps">
    /// The step groups the execution recorded when it started. Needed because a step group can leave
    /// no record at all (a step type that queues no task and writes no Activity is passed straight
    /// through), and a rail assembled only from what it can see would shorten as the run proceeded.
    /// </param>
    /// <param name="currentStepIndex">The step group the execution has reached (0-based).</param>
    /// <param name="observations">Every task of the execution that either record still describes.</param>
    public static ScheduleExecutionProgress? Read(
        int totalSteps,
        int currentStepIndex,
        IEnumerable<ScheduleStepObservation> observations)
    {
        var byStep = observations
            .GroupBy(o => o.StepIndex)
            .ToDictionary(g => g.Key, g => g.ToList());

        // The recorded total is a snapshot; the observations are what is actually there. Where they
        // disagree, draw everything, so that a view whose job is to account for every task cannot hide
        // one behind a stale number.
        var stepCount = Math.Max(totalSteps, byStep.Count == 0 ? 0 : byStep.Keys.Max() + 1);
        if (stepCount == 0)
            return null;

        var steps = new List<ScheduleStepProgress>(stepCount);
        for (var stepIndex = 0; stepIndex < stepCount; stepIndex++)
        {
            if (!byStep.TryGetValue(stepIndex, out var stepObservations))
            {
                steps.Add(new ScheduleStepProgress
                {
                    StepIndex = stepIndex,
                    Name = $"Step {stepIndex + 1}",
                    Status = stepIndex < currentStepIndex ? ScheduleStepStatus.Completed : ScheduleStepStatus.Pending,
                    TaskStatuses = []
                });
                continue;
            }

            var taskStatuses = stepObservations.Select(StatusOf).ToList();
            steps.Add(new ScheduleStepProgress
            {
                StepIndex = stepIndex,
                Name = stepObservations.Count == 1
                    ? stepObservations[0].Name
                    : $"{stepObservations.Count} in parallel",
                Status = Aggregate(taskStatuses),
                TaskStatuses = OrderWedges(taskStatuses)
            });
        }

        // Where the Schedule has got to is recorded on the execution, not inferred from what its steps
        // happen to be doing. Reading it off the step statuses instead loses the position exactly when
        // it matters most: a step group holding a failure alongside a task still running aggregates to
        // failed, and the header would fall back to counting tasks at the moment an administrator most
        // needs to know which step they are looking at.
        return new ScheduleExecutionProgress
        {
            CurrentStepNumber = currentStepIndex >= 0 && currentStepIndex < steps.Count
                ? currentStepIndex + 1
                : null,
            Steps = steps
        };
    }

    /// <summary>
    /// What to call the task an Activity describes: the same "Connected System - Run Profile" the
    /// Operations queue names a Worker Task with, so a step reads identically either side of its task
    /// being deleted, falling back to its position where the Activity names nothing.
    /// </summary>
    public static string NameOf(string? targetContext, string? targetName, int stepIndex)
    {
        if (string.IsNullOrEmpty(targetName))
            return $"Step {stepIndex + 1}";

        return string.IsNullOrEmpty(targetContext) ? targetName : $"{targetContext} - {targetName}";
    }

    /// <summary>
    /// A Schedule Execution's shape from the records themselves, for callers holding the entities
    /// rather than a queue's headers (the Schedule Executions REST read).
    /// </summary>
    /// <remarks>
    /// One observation per task: a Worker Task carries its own Activity where it has started, and any
    /// Activity not reached that way belongs to a task that has since been deleted. Matching them up
    /// here rather than at the call site is what stops a task being counted twice.
    /// </remarks>
    public static ScheduleExecutionProgress? FromRecords(
        IReadOnlyCollection<WorkerTask> tasks,
        IReadOnlyCollection<Activity> activities,
        int totalSteps,
        int currentStepIndex)
    {
        var taskActivityIds = tasks
            .Where(t => t.Activity != null)
            .Select(t => t.Activity!.Id)
            .ToHashSet();

        var fromTasks = tasks
            .Where(t => t.ScheduleStepIndex.HasValue)
            .Select(t => new ScheduleStepObservation
            {
                StepIndex = t.ScheduleStepIndex!.Value,
                Name = t.Activity != null
                    ? NameOf(t.Activity.TargetContext, t.Activity.TargetName, t.ScheduleStepIndex!.Value)
                    : $"Step {t.ScheduleStepIndex!.Value + 1}",
                ActivityId = t.Activity?.Id,
                TaskStatus = t.Status,
                ActivityStatus = t.Activity?.Status
            });

        var fromActivities = activities
            .Where(a => a.ScheduleStepIndex.HasValue && !taskActivityIds.Contains(a.Id))
            .Select(a => new ScheduleStepObservation
            {
                StepIndex = a.ScheduleStepIndex!.Value,
                Name = NameOf(a.TargetContext, a.TargetName, a.ScheduleStepIndex!.Value),
                ActivityId = a.Id,
                ActivityStatus = a.Status
            });

        return Read(totalSteps, currentStepIndex, fromTasks.Concat(fromActivities));
    }

    /// <summary>
    /// A Schedule Execution's shape as the Operations queue sees it: the tasks of one Schedule
    /// Execution that are still queued, plus what its finished tasks left behind.
    /// </summary>
    /// <remarks>
    /// The two sources overlap. A task that has started is described by its Worker Task and its
    /// Activity at once, and the two are read a moment apart, so an outcome whose Activity the queue
    /// already holds is discarded rather than counted as a second task.
    /// </remarks>
    /// <param name="queuedTasks">Every task of one Schedule Execution still in the queue.</param>
    /// <param name="stepOutcomes">
    /// What that execution's Activities record, from
    /// <c>ActivityServer.GetScheduleStepOutcomesAsync</c>.
    /// </param>
    public static ScheduleExecutionProgress? FromQueue(
        IReadOnlyCollection<WorkerTaskHeader> queuedTasks,
        IReadOnlyCollection<ScheduleStepObservation> stepOutcomes)
    {
        var first = queuedTasks.FirstOrDefault(t => t.ScheduleExecutionId.HasValue);
        if (first == null)
            return null;

        var queuedActivityIds = queuedTasks
            .Where(t => t.ActivityId.HasValue)
            .Select(t => t.ActivityId!.Value)
            .ToHashSet();

        var observations = queuedTasks
            .Where(t => t.ScheduleStepIndex.HasValue)
            .Select(t => new ScheduleStepObservation
            {
                StepIndex = t.ScheduleStepIndex!.Value,
                Name = t.Name,
                ActivityId = t.ActivityId,
                TaskStatus = t.Status
            })
            .Concat(stepOutcomes.Where(o => o.ActivityId == null || !queuedActivityIds.Contains(o.ActivityId.Value)));

        return Read(first.ScheduleTotalSteps ?? 0, first.ScheduleCurrentStepIndex ?? 0, observations);
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;

namespace JIM.Models.Scheduling;

/// <summary>
/// The rules for reading a Schedule Execution's progress out of the records it leaves behind (#1162).
/// The Operations queue's group header, the Schedule Execution detail page, the Schedule Executions
/// REST read and PowerShell all have to agree on what "step 2 of 5" means and on how far a given step
/// got, so the derivation lives here rather than at each call site.
/// </summary>
/// <remarks>
/// The sibling of <see cref="RunPhaseReading"/> one level up: that reads the steps within a single Run
/// Profile execution, this reads the steps of the Schedule running it. The two are deliberately not
/// parallel in construction. A run's phases are recorded rows; a Schedule step's evidence lives half
/// in <see cref="WorkerTask"/> rows and half in <see cref="Activity"/> rows, because a task is deleted
/// the moment its work finishes.
/// </remarks>
public static class ScheduleStepReading
{
    /// <summary>
    /// How far one step of a Schedule Execution got, from whichever of its two records exist.
    /// </summary>
    /// <remarks>
    /// The single definition, shared by the detail read (which reports it per Schedule Step row, and
    /// whose display strings are a published REST contract) and by the queue's group header (which
    /// aggregates it per step group). Deriving it twice is how the two surfaces would come to disagree
    /// about a step that is finishing at the moment they are each asked.
    /// </remarks>
    /// <param name="taskStatus">The Worker Task's status, or null once it has been deleted.</param>
    /// <param name="activityStatus">The Activity's status, or null before the step started.</param>
    /// <param name="stepIndex">The step group this step belongs to.</param>
    /// <param name="currentStepIndex">The step group the execution has reached.</param>
    /// <param name="executionStatus">The execution's own status.</param>
    public static ScheduleExecutionStepStatus StatusOf(
        WorkerTaskStatus? taskStatus,
        ActivityStatus? activityStatus,
        int stepIndex,
        int currentStepIndex,
        ScheduleExecutionStatus executionStatus)
    {
        // A Worker Task only still exists while the step is live, so it describes the step better than the
        // in-progress Activity beside it.
        if (taskStatus is { } task)
        {
            return task switch
            {
                WorkerTaskStatus.Queued => ScheduleExecutionStepStatus.Queued,
                WorkerTaskStatus.Processing => ScheduleExecutionStepStatus.Processing,
                WorkerTaskStatus.CancellationRequested => ScheduleExecutionStepStatus.Cancelling,
                WorkerTaskStatus.WaitingForPreviousStep => ScheduleExecutionStepStatus.Waiting,
                _ => ScheduleExecutionStepStatus.Unknown
            };
        }

        if (activityStatus is { } activity)
        {
            return activity switch
            {
                ActivityStatus.InProgress => ScheduleExecutionStepStatus.Processing,
                ActivityStatus.Complete => ScheduleExecutionStepStatus.Completed,
                ActivityStatus.CompleteWithWarning => ScheduleExecutionStepStatus.CompletedWithWarning,
                ActivityStatus.CompleteWithError => ScheduleExecutionStepStatus.CompletedWithError,
                ActivityStatus.FailedWithError => ScheduleExecutionStepStatus.Failed,
                ActivityStatus.Cancelled => ScheduleExecutionStepStatus.Cancelled,
                _ => ScheduleExecutionStepStatus.Unknown
            };
        }

        // Neither exists: infer from how far the execution got. A step beyond the current index on an execution
        // that has stopped never ran, and reads as Pending.
        if (stepIndex < currentStepIndex)
            return ScheduleExecutionStepStatus.Completed;

        if (stepIndex == currentStepIndex && executionStatus == ScheduleExecutionStatus.InProgress)
            return ScheduleExecutionStepStatus.Waiting;

        return ScheduleExecutionStepStatus.Pending;
    }

    /// <summary>
    /// Where a step group as a whole has got to, given each of its tasks.
    /// </summary>
    /// <remarks>
    /// A step is not fine because most of it was fine, so a failure carries the group. A group that
    /// has some tasks done and others yet to start counts as under way rather than pending: reporting
    /// it as pending would walk the Schedule backwards on screen as each concurrent task landed.
    /// </remarks>
    public static ScheduleExecutionStepStatus Aggregate(IReadOnlyCollection<ScheduleExecutionStepStatus> taskStatuses)
    {
        if (taskStatuses.Count == 0)
            return ScheduleExecutionStepStatus.Pending;

        if (taskStatuses.Contains(ScheduleExecutionStepStatus.Failed))
            return ScheduleExecutionStepStatus.Failed;

        if (taskStatuses.Contains(ScheduleExecutionStepStatus.CompletedWithError))
            return ScheduleExecutionStepStatus.CompletedWithError;

        if (taskStatuses.Contains(ScheduleExecutionStepStatus.Cancelling))
            return ScheduleExecutionStepStatus.Cancelling;

        if (taskStatuses.Contains(ScheduleExecutionStepStatus.Processing))
            return ScheduleExecutionStepStatus.Processing;

        if (taskStatuses.Contains(ScheduleExecutionStepStatus.Cancelled))
            return ScheduleExecutionStepStatus.Cancelled;

        if (taskStatuses.All(IsComplete))
        {
            return taskStatuses.Contains(ScheduleExecutionStepStatus.CompletedWithWarning)
                ? ScheduleExecutionStepStatus.CompletedWithWarning
                : ScheduleExecutionStepStatus.Completed;
        }

        // Part done and part still to start: the group has begun even though nothing is running this
        // instant, and saying otherwise would walk the Schedule backwards as each task lands.
        if (taskStatuses.Any(IsComplete))
            return ScheduleExecutionStepStatus.Processing;

        if (taskStatuses.Contains(ScheduleExecutionStepStatus.Queued))
            return ScheduleExecutionStepStatus.Queued;

        return taskStatuses.Contains(ScheduleExecutionStepStatus.Waiting)
            ? ScheduleExecutionStepStatus.Waiting
            : ScheduleExecutionStepStatus.Pending;
    }

    private static bool IsComplete(ScheduleExecutionStepStatus status) => status is
        ScheduleExecutionStepStatus.Completed or ScheduleExecutionStepStatus.CompletedWithWarning;

    /// <summary>
    /// A parallel step's task statuses in drawing order: what went wrong, then what was cancelled, then
    /// what finished, then what is running, then what has yet to start, clockwise from twelve o'clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Load-bearing, and the least obvious thing here. A parallel step is drawn as one marker divided
    /// into a wedge per task. Ordering the wedges by task would scatter a single failure anywhere
    /// around a 16px disc, and at one task in twelve it would be invisible; ordering by outcome means a
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
    public static IReadOnlyList<ScheduleExecutionStepStatus> OrderWedges(IEnumerable<ScheduleExecutionStepStatus> taskStatuses) =>
        taskStatuses.OrderBy(WedgeRank).ToList();

    private static int WedgeRank(ScheduleExecutionStepStatus status) => status switch
    {
        ScheduleExecutionStepStatus.Failed => 0,
        ScheduleExecutionStepStatus.CompletedWithError => 1,
        ScheduleExecutionStepStatus.Cancelled => 2,
        ScheduleExecutionStepStatus.Cancelling => 3,
        ScheduleExecutionStepStatus.CompletedWithWarning => 4,
        ScheduleExecutionStepStatus.Completed => 5,
        ScheduleExecutionStepStatus.Processing => 6,
        _ => 7
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
    /// <param name="executionStatus">The execution's own status, for steps with no record of their own.</param>
    /// <param name="observations">Every task of the execution that either record still describes.</param>
    public static ScheduleExecutionProgress? Read(
        int totalSteps,
        int currentStepIndex,
        ScheduleExecutionStatus executionStatus,
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
                    Status = StatusOf(null, null, stepIndex, currentStepIndex, executionStatus),
                    TaskStatuses = []
                });
                continue;
            }

            var taskStatuses = stepObservations
                .Select(o => o.Status ?? StatusOf(o.TaskStatus, o.ActivityStatus, stepIndex, currentStepIndex, executionStatus))
                .ToList();

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
    /// A Schedule Execution's shape from the per-step state a detail read has already assembled.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="DTOs.ScheduleExecutionStepState"/> rather than from the Worker Tasks and
    /// Activities behind it, for two reasons: the detail read has already paid for those queries and
    /// already resolved each step's names, and deriving the group view from the row view makes it
    /// impossible for a caller to be shown two accounts of the same execution that disagree.
    /// </remarks>
    public static ScheduleExecutionProgress? FromStepStates(
        IReadOnlyCollection<DTOs.ScheduleExecutionStepState> steps,
        ScheduleExecution execution)
    {
        var observations = steps.Select(s => new ScheduleStepObservation
        {
            StepIndex = s.StepIndex,
            Name = LabelOf(s),
            Status = s.Status
        });

        return Read(execution.TotalSteps, execution.CurrentStepIndex, execution.Status, observations);
    }

    /// <summary>
    /// What to call one Schedule Step: the Connected System and Run Profile where it runs one, matching
    /// how the Operations queue names the same task, and the step's stored name otherwise.
    /// </summary>
    private static string LabelOf(DTOs.ScheduleExecutionStepState step) =>
        !string.IsNullOrEmpty(step.ConnectedSystemName) && !string.IsNullOrEmpty(step.RunProfileName)
            ? $"{step.ConnectedSystemName} - {step.RunProfileName}"
            : step.Name;

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

        // The queue only ever shows an execution that is still running, so its own status is InProgress
        // by construction; a step group with no record of its own is therefore one still to come.
        return Read(first.ScheduleTotalSteps ?? 0, first.ScheduleCurrentStepIndex ?? 0,
            ScheduleExecutionStatus.InProgress, observations);
    }
}

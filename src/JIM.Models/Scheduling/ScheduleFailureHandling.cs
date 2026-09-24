// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Scheduling;

/// <summary>
/// Where a step's effective failure behaviour comes from (#1787).
/// </summary>
public enum ScheduleFailureBehaviourSource
{
    /// <summary>
    /// The step has a setting of its own (Stop or Continue), which overrides the Schedule's.
    /// </summary>
    Step = 0,

    /// <summary>
    /// The step follows the Schedule, so the Schedule's "When a step fails" setting decides.
    /// </summary>
    Schedule = 1
}

/// <summary>
/// Resolves what a failed step does to its Schedule (#1787). The one place effective failure behaviour is worked out:
/// the Scheduler's decisions (start refusals, the parallel-group rule, advancement and the recovery sweep), the API,
/// the portal and the execution step state all ask here, so none of them can come to resolve it differently.
/// </summary>
public static class ScheduleFailureHandling
{
    /// <summary>
    /// The Activity outcomes that count as a step failing: it failed outright, completed with errors, or was cancelled.
    /// Warnings, including object-level data errors reported as warnings, do not. The one definition, shared by the
    /// Scheduler's decisions and the repository query that finds an execution's failed steps.
    /// </summary>
    public static IReadOnlyList<ActivityStatus> FailedStepOutcomes { get; } =
        [ActivityStatus.FailedWithError, ActivityStatus.CompleteWithError, ActivityStatus.Cancelled];

    /// <summary>
    /// Whether an Activity outcome counts as its step failing; see <see cref="FailedStepOutcomes"/>.
    /// </summary>
    public static bool IsFailedStepOutcome(ActivityStatus status) => FailedStepOutcomes.Contains(status);

    /// <summary>
    /// Whether the Schedule carries on when this step fails: the step's own setting, or the Schedule's when the step
    /// follows it. Read at the moment of each decision, so a change made mid-run applies to the next decision.
    /// </summary>
    /// <param name="step">The step that failed, or that might.</param>
    /// <param name="schedule">The step's Schedule, passed explicitly rather than read from the step's navigation, which
    /// may not be loaded. With no Schedule to follow, a step that follows the Schedule stops it, failing safe.</param>
    public static bool ContinuesOnFailure(ScheduleStep step, Schedule? schedule)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.OnFailure switch
        {
            ScheduleStepFailureBehaviour.Continue => true,
            ScheduleStepFailureBehaviour.Stop => false,
            _ => schedule?.OnStepFailure == ScheduleFailureBehaviour.Continue
        };
    }

    /// <summary>
    /// Where the step's effective behaviour comes from: the step itself when it has a setting of its own, or the
    /// Schedule when it follows it.
    /// </summary>
    public static ScheduleFailureBehaviourSource Source(ScheduleStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.OnFailure == ScheduleStepFailureBehaviour.FollowSchedule
            ? ScheduleFailureBehaviourSource.Schedule
            : ScheduleFailureBehaviourSource.Step;
    }
}

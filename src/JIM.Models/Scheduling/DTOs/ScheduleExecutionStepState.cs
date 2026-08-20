// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Scheduling.DTOs;

/// <summary>
/// The derived state of one Schedule Step within a Schedule Execution: how far it got, when, and which Activity
/// produced it. Assembled by SchedulerServer.GetScheduleExecutionDetailAsync from the step definition plus its
/// Activity (permanent) and Worker Task (ephemeral).
/// </summary>
public class ScheduleExecutionStepState
{
    /// <summary>
    /// The Schedule Step this state describes. A stable identity, so the portal can key a list on it.
    /// </summary>
    public Guid ScheduleStepId { get; set; }

    /// <summary>
    /// The step index (0-based). Steps sharing an index run in parallel with each other.
    /// </summary>
    public int StepIndex { get; set; }

    /// <summary>
    /// The step's stored name, or "Step {StepIndex + 1}" when it has none. Run Profile steps deliberately store no
    /// name, so they always take the fallback; use <see cref="ConnectedSystemName"/> and
    /// <see cref="RunProfileName"/> to label those.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// What kind of work the step performs.
    /// </summary>
    public ScheduleStepType StepType { get; set; }

    /// <summary>
    /// How this step runs relative to its siblings at the same <see cref="StepIndex"/>. Note the first step of a
    /// parallel group is Sequential and only its siblings are ParallelWithPrevious, so a step cannot be identified
    /// as parallel in isolation; count the steps sharing its index instead.
    /// </summary>
    public StepExecutionMode ExecutionMode { get; set; }

    /// <summary>
    /// The Connected System for Run Profile steps. Also disambiguates parallel siblings at the same step index.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The name of the Connected System for Run Profile steps, resolved for display.
    /// </summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>
    /// The Run Profile for Run Profile steps.
    /// </summary>
    public int? RunProfileId { get; set; }

    /// <summary>
    /// The name of the Run Profile for Run Profile steps, resolved for display.
    /// </summary>
    public string? RunProfileName { get; set; }

    /// <summary>
    /// How far the step got.
    /// </summary>
    public ScheduleExecutionStepStatus Status { get; set; }

    /// <summary>
    /// The still-live Worker Task for this step, if it has one. Worker Tasks are deleted on completion.
    /// </summary>
    public Guid? TaskId { get; set; }

    /// <summary>
    /// When the step started (UTC). Null until its Activity exists.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the step finished (UTC). Null while it is still running, or if it never started.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// The error reported by the step's Activity, if it failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The Activity this step produced, if one exists.
    /// </summary>
    public Guid? ActivityId { get; set; }

    /// <summary>
    /// The Activity's own status, when there is one.
    /// </summary>
    public ActivityStatus? ActivityStatus { get; set; }

    /// <summary>
    /// Whether the execution was configured to carry on past this step if it failed. Explains why an execution
    /// continued after a failure.
    /// </summary>
    public bool ContinueOnFailure { get; set; }

    /// <summary>
    /// How long the step took, once it has both started and finished.
    /// </summary>
    public TimeSpan? Duration => StartedAt.HasValue && CompletedAt.HasValue
        ? CompletedAt.Value - StartedAt.Value
        : null;
}

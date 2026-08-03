// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scheduling;

/// <summary>
/// Defines how a schedule is triggered.
/// </summary>
public enum ScheduleTriggerType
{
    /// <summary>
    /// Schedule is triggered by a cron expression.
    /// The cron expression is built via a user-friendly UI - users do not enter cron syntax directly.
    /// </summary>
    Cron = 0,

    /// <summary>
    /// Schedule is only triggered manually (on-demand).
    /// </summary>
    Manual = 1
}

/// <summary>
/// Defines how a step executes relative to the previous step.
/// </summary>
public enum StepExecutionMode
{
    /// <summary>
    /// Step runs after the previous step completes. Starts a new parallel group.
    /// </summary>
    Sequential = 0,

    /// <summary>
    /// Step runs in parallel with the previous step(s) in the same group.
    /// </summary>
    ParallelWithPrevious = 1
}

/// <summary>
/// Defines the type of action a schedule step performs.
/// </summary>
public enum ScheduleStepType
{
    /// <summary>
    /// Execute a Connected System Run Profile.
    /// </summary>
    RunProfile = 0,

    /// <summary>
    /// Execute a PowerShell script.
    /// </summary>
    PowerShell = 1,

    /// <summary>
    /// Execute an external program.
    /// </summary>
    Executable = 2,

    /// <summary>
    /// Execute a SQL script.
    /// </summary>
    SqlScript = 3,

    /// <summary>
    /// Run the Temporal Scope Reconciler sweep (issue #892): re-evaluate relative-date scoping for objects
    /// whose scope membership drifts with the clock but whose source data has not changed. Used only by the
    /// built-in Temporal Scope Reconciliation schedule.
    /// </summary>
    TemporalScopeReconciliation = 4
}

/// <summary>
/// Defines how a schedule's timing is configured.
/// </summary>
public enum SchedulePatternType
{
    /// <summary>
    /// Run at specific times on selected days (e.g., 9am, 12pm, 3pm, 6pm on weekdays).
    /// </summary>
    SpecificTimes = 0,

    /// <summary>
    /// Run at regular intervals on selected days (e.g., every 2 hours between 6am-6pm).
    /// </summary>
    Interval = 1,

    /// <summary>
    /// Use a raw cron expression for full control over scheduling.
    /// </summary>
    Custom = 2
}

/// <summary>
/// Defines the unit for interval-based schedules.
/// </summary>
public enum ScheduleIntervalUnit
{
    /// <summary>
    /// Interval measured in minutes.
    /// </summary>
    Minutes = 0,

    /// <summary>
    /// Interval measured in hours.
    /// </summary>
    Hours = 1
}

/// <summary>
/// The status of a schedule execution.
/// </summary>
public enum ScheduleExecutionStatus
{
    /// <summary>
    /// Execution is queued but not yet started.
    /// </summary>
    Queued = 0,

    /// <summary>
    /// Execution is currently in progress.
    /// </summary>
    InProgress = 1,

    /// <summary>
    /// Execution completed successfully.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// Execution failed with an error.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// Execution was cancelled by a user or system.
    /// </summary>
    Cancelled = 4,

    /// <summary>
    /// Execution is paused and can be resumed.
    /// </summary>
    Paused = 5
}

/// <summary>
/// The display status of an individual step within a Schedule Execution. Derived from the step's Worker Task while
/// it is still live, from its Activity once the Worker Task has been deleted, and otherwise from the execution's own
/// position. Worker Tasks are ephemeral and Activities are permanent, so the Activity is the durable source.
/// </summary>
public enum ScheduleExecutionStepStatus
{
    /// <summary>
    /// The step has not been reached, and will not run if the execution has already stopped.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The step is waiting for a previous step to finish.
    /// </summary>
    Waiting = 1,

    /// <summary>
    /// The step has been queued for a worker to pick up.
    /// </summary>
    Queued = 2,

    /// <summary>
    /// The step is currently being processed by a worker.
    /// </summary>
    Processing = 3,

    /// <summary>
    /// Cancellation has been requested for the step but has not yet taken effect.
    /// </summary>
    Cancelling = 4,

    /// <summary>
    /// The step completed without errors or warnings.
    /// </summary>
    Completed = 5,

    /// <summary>
    /// The step completed, but at least one object raised a warning.
    /// </summary>
    CompletedWithWarning = 6,

    /// <summary>
    /// The step ran to completion, but at least one object raised an error.
    /// </summary>
    CompletedWithError = 7,

    /// <summary>
    /// The step failed outright and did not complete.
    /// </summary>
    Failed = 8,

    /// <summary>
    /// The step was cancelled by a user or by the system.
    /// </summary>
    Cancelled = 9,

    /// <summary>
    /// The step's status could not be determined.
    /// </summary>
    Unknown = 10
}

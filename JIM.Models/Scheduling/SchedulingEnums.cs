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
    /// Execute a connected system run profile.
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
    SqlScript = 3
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

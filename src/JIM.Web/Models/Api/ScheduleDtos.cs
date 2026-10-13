// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;
using JIM.Models.Scheduling.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Schedule's configuration, as the list returns it, with the outcome of its most recent run.
/// </summary>
public class ScheduleDto
{
    /// <summary>
    /// The unique identifier of the Schedule.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The Schedule's name.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// What the Schedule does, in its author's words. Null when it has no description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// How the Schedule starts: <c>Cron</c> runs it automatically on its pattern; <c>Manual</c> runs it only when it is
    /// started.
    /// </summary>
    public ScheduleTriggerType TriggerType { get; set; }

    /// <summary>
    /// The cron expression the Schedule runs on; null for a <c>Manual</c> Schedule. JIM builds it from the pattern
    /// fields, except for a <c>Custom</c> pattern, which gives it directly.
    /// </summary>
    public string? CronExpression { get; set; }

    /// <summary>
    /// How a <c>Cron</c> Schedule is timed: at set times of day (<c>SpecificTimes</c>), every few hours or minutes
    /// (<c>Interval</c>), or by a cron expression of its own (<c>Custom</c>).
    /// </summary>
    public SchedulePatternType PatternType { get; set; }

    /// <summary>
    /// The days the Schedule runs, as a comma-separated list of numbers from 0 (Sunday) to 6 (Saturday), for example
    /// <c>1,2,3,4,5</c> for weekdays. A <c>Custom</c> pattern does not use it.
    /// </summary>
    public string? DaysOfWeek { get; set; }

    /// <summary>
    /// For a <c>SpecificTimes</c> pattern, the times of day the Schedule runs, as a comma-separated list of 24-hour
    /// times, for example <c>06:00,18:00</c>.
    /// </summary>
    public string? RunTimes { get; set; }

    /// <summary>
    /// For an <c>Interval</c> pattern, how often the Schedule runs, counted in IntervalUnit: 2 with <c>Hours</c> runs
    /// it every two hours.
    /// </summary>
    public int? IntervalValue { get; set; }

    /// <summary>
    /// For an <c>Interval</c> pattern, the unit of IntervalValue.
    /// </summary>
    public ScheduleIntervalUnit? IntervalUnit { get; set; }

    /// <summary>
    /// For an hourly <c>Interval</c> pattern, the start of the window the Schedule runs in, as a 24-hour time such
    /// as <c>06:00</c>. Null when the interval runs all day.
    /// </summary>
    public string? IntervalWindowStart { get; set; }

    /// <summary>
    /// For an hourly <c>Interval</c> pattern, the end of the window the Schedule runs in, as a 24-hour time such as
    /// <c>18:00</c>. Null when the interval runs all day.
    /// </summary>
    public string? IntervalWindowEnd { get; set; }

    /// <summary>
    /// Whether the Schedule is enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// What the Schedule does when a step fails, for every step that follows the Schedule: <c>Stop</c> ends the run
    /// (Failed); <c>Continue</c> runs the remaining steps, and a run that carried on past a failure ends
    /// <c>CompleteWithError</c>. A step with a failure setting of its own overrides it.
    /// </summary>
    public ScheduleFailureBehaviour OnStepFailure { get; set; }

    /// <summary>
    /// When the Schedule last ran (UTC).
    /// </summary>
    public DateTime? LastRunTime { get; set; }

    /// <summary>
    /// When the Schedule is next due to run (UTC).
    /// </summary>
    public DateTime? NextRunTime { get; set; }

    /// <summary>
    /// The number of steps in the Schedule.
    /// </summary>
    public int StepCount { get; set; }

    /// <summary>
    /// When the Schedule was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// When the Schedule was last changed (UTC).
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The unique identifier of the most recent Schedule Execution, if the Schedule has ever run. The LastExecution
    /// fields are filled in only when Schedules are listed: reading a single Schedule, or creating, changing,
    /// enabling or disabling one, leaves them all null, because that describes the Schedule's configuration rather
    /// than its run history. The Schedule Executions hold the full history.
    /// </summary>
    public Guid? LastExecutionId { get; set; }

    /// <summary>
    /// The outcome of the most recent Schedule Execution. Null when the Schedule has never run, or where the
    /// LastExecution fields are not filled in (see LastExecutionId).
    /// </summary>
    public ScheduleExecutionStatus? LastExecutionStatus { get; set; }

    /// <summary>
    /// The step the most recent Schedule Execution reached (0-based). Read with LastExecutionTotalSteps to see how
    /// far a failed run got before it stopped. Null when the Schedule has never run, or where the LastExecution
    /// fields are not filled in (see LastExecutionId).
    /// </summary>
    public int? LastExecutionCurrentStepIndex { get; set; }

    /// <summary>
    /// How many steps the most recent Schedule Execution set out to run. Null when the Schedule has never run, or
    /// where the LastExecution fields are not filled in (see LastExecutionId).
    /// </summary>
    public int? LastExecutionTotalSteps { get; set; }

    /// <summary>
    /// The steps (0-based, ascending, each listed once) that failed in the most recent Schedule Execution and let it
    /// carry on, when that execution is <c>CompleteWithError</c>; empty for any other outcome. Null where the
    /// LastExecution fields are not filled in (see LastExecutionId).
    /// </summary>
    public int[]? LastExecutionFailedStepIndices { get; set; }

    /// <summary>
    /// When the most recent Schedule Execution finished (UTC). Null while it is still running, when the Schedule has
    /// never run, or where the LastExecution fields are not filled in (see LastExecutionId).
    /// </summary>
    public DateTime? LastExecutionCompletedAt { get; set; }

    /// <summary>
    /// The error reported by the most recent Schedule Execution, if it failed, or the message naming each failed step
    /// when it is <c>CompleteWithError</c>. Null when the run succeeded, when the Schedule has never run, or where
    /// the LastExecution fields are not filled in (see LastExecutionId).
    /// </summary>
    public string? LastExecutionErrorMessage { get; set; }

    /// <summary>
    /// Creates a DTO from a Schedule entity. A Schedule entity carries no last-execution projection, so every
    /// LastExecution field is left null here; see <see cref="LastExecutionId"/>.
    /// </summary>
    public static ScheduleDto FromEntity(Schedule schedule)
    {
        return new ScheduleDto
        {
            Id = schedule.Id,
            Name = schedule.Name,
            Description = schedule.Description,
            TriggerType = schedule.TriggerType,
            CronExpression = schedule.CronExpression,
            PatternType = schedule.PatternType,
            DaysOfWeek = schedule.DaysOfWeek,
            RunTimes = schedule.RunTimes,
            IntervalValue = schedule.IntervalValue,
            IntervalUnit = schedule.IntervalUnit,
            IntervalWindowStart = schedule.IntervalWindowStart,
            IntervalWindowEnd = schedule.IntervalWindowEnd,
            IsEnabled = schedule.IsEnabled,
            OnStepFailure = schedule.OnStepFailure,
            LastRunTime = schedule.LastRunTime,
            NextRunTime = schedule.NextRunTime,
            StepCount = schedule.Steps?.Count ?? 0,
            Created = schedule.Created,
            LastUpdated = schedule.LastUpdated
        };
    }

    /// <summary>
    /// Creates a DTO from a ScheduleHeader projection, as returned by the list query. The header carries the step
    /// count as a projected value rather than as a materialised collection, and is the only source that can populate
    /// the last-execution fields; see <see cref="LastExecutionId"/>.
    /// </summary>
    public static ScheduleDto FromHeader(ScheduleHeader header)
    {
        return new ScheduleDto
        {
            Id = header.Id,
            Name = header.Name,
            Description = header.Description,
            TriggerType = header.TriggerType,
            CronExpression = header.CronExpression,
            PatternType = header.PatternType,
            DaysOfWeek = header.DaysOfWeek,
            RunTimes = header.RunTimes,
            IntervalValue = header.IntervalValue,
            IntervalUnit = header.IntervalUnit,
            IntervalWindowStart = header.IntervalWindowStart,
            IntervalWindowEnd = header.IntervalWindowEnd,
            IsEnabled = header.IsEnabled,
            OnStepFailure = header.OnStepFailure,
            LastRunTime = header.LastRunTime,
            NextRunTime = header.NextRunTime,
            StepCount = header.StepCount,
            Created = header.Created,
            LastUpdated = header.LastUpdated,
            LastExecutionId = header.LastExecutionId,
            LastExecutionStatus = header.LastExecutionStatus,
            LastExecutionCurrentStepIndex = header.LastExecutionCurrentStepIndex,
            LastExecutionTotalSteps = header.LastExecutionTotalSteps,
            LastExecutionFailedStepIndices = header.LastExecutionFailedStepIndices,
            LastExecutionCompletedAt = header.LastExecutionCompletedAt,
            LastExecutionErrorMessage = header.LastExecutionErrorMessage
        };
    }
}

/// <summary>
/// A Schedule's configuration with its steps.
/// </summary>
public class ScheduleDetailDto : ScheduleDto
{
    /// <summary>
    /// The Schedule's steps, in the order they run (by StepIndex).
    /// </summary>
    public List<ScheduleStepDto> Steps { get; set; } = new();

    /// <summary>
    /// Creates a detail DTO from a Schedule entity with steps. As with <see cref="ScheduleDto.FromEntity"/>, the
    /// inherited last-execution fields are left null; see <see cref="ScheduleDto.LastExecutionId"/>.
    /// </summary>
    public static new ScheduleDetailDto FromEntity(Schedule schedule)
    {
        var dto = new ScheduleDetailDto
        {
            Id = schedule.Id,
            Name = schedule.Name,
            Description = schedule.Description,
            TriggerType = schedule.TriggerType,
            CronExpression = schedule.CronExpression,
            PatternType = schedule.PatternType,
            DaysOfWeek = schedule.DaysOfWeek,
            RunTimes = schedule.RunTimes,
            IntervalValue = schedule.IntervalValue,
            IntervalUnit = schedule.IntervalUnit,
            IntervalWindowStart = schedule.IntervalWindowStart,
            IntervalWindowEnd = schedule.IntervalWindowEnd,
            IsEnabled = schedule.IsEnabled,
            OnStepFailure = schedule.OnStepFailure,
            LastRunTime = schedule.LastRunTime,
            NextRunTime = schedule.NextRunTime,
            StepCount = schedule.Steps?.Count ?? 0,
            Created = schedule.Created,
            LastUpdated = schedule.LastUpdated,
            Steps = schedule.Steps?
                .OrderBy(s => s.StepIndex)
                .Select(step => ScheduleStepDto.FromEntity(step, schedule))
                .ToList() ?? new()
        };
        return dto;
    }
}

/// <summary>
/// DTO for a schedule step with polymorphic configuration.
/// The step type determines which configuration properties are relevant.
/// </summary>
public class ScheduleStepDto
{
    /// <summary>
    /// The unique identifier of the step.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The order in which this step executes (0-based).
    /// Steps with the same index run in parallel.
    /// </summary>
    public int StepIndex { get; set; }

    /// <summary>
    /// Display name for the step. For RunProfile steps, this is null and the name should
    /// be derived from the Connected System and Run Profile. For other step types, this
    /// contains the user-specified name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// How this step executes relative to the previous step.
    /// </summary>
    public StepExecutionMode ExecutionMode { get; set; }

    /// <summary>
    /// The type of action this step performs (discriminator for configuration properties).
    /// </summary>
    public ScheduleStepType StepType { get; set; }

    /// <summary>
    /// This step's own failure setting: <c>FollowSchedule</c> (the Schedule's <c>onStepFailure</c> decides),
    /// <c>Stop</c> or <c>Continue</c>. Send this back to preserve the setting exactly.
    /// </summary>
    public ScheduleStepFailureBehaviour OnFailure { get; set; }

    /// <summary>
    /// Whether the Schedule carries on when this step fails: its EFFECTIVE behaviour, being <c>onFailure</c>, or
    /// the Schedule's <c>onStepFailure</c> when the step follows the Schedule. Kept for existing clients; see
    /// <c>failureBehaviourSource</c> for where the value comes from.
    /// </summary>
    public bool ContinueOnFailure { get; set; }

    /// <summary>
    /// Where <c>continueOnFailure</c> comes from: <c>Step</c> when the step has a setting of its own, or
    /// <c>Schedule</c> when it follows the Schedule.
    /// </summary>
    public ScheduleFailureBehaviourSource FailureBehaviourSource { get; set; }

    /// <summary>
    /// Optional timeout for this step in seconds.
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // RunProfile configuration (used when StepType == RunProfile)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The Connected System ID (for RunProfile steps).
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The Run Profile ID (for RunProfile steps).
    /// </summary>
    public int? RunProfileId { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // PowerShell configuration (used when StepType == PowerShell)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The path to the PowerShell script (for PowerShell steps).
    /// </summary>
    public string? ScriptPath { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Executable configuration (used when StepType == Executable)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The path to the executable (for Executable steps).
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// The working directory (for Executable steps).
    /// </summary>
    public string? WorkingDirectory { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Shared configuration (used by PowerShell and Executable steps)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Arguments to pass to the script or executable (for PowerShell and Executable steps).
    /// </summary>
    public string? Arguments { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // SqlScript configuration (used when StepType == SqlScript)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The connection string (for SqlScript steps).
    /// </summary>
    public string? SqlConnectionString { get; set; }

    /// <summary>
    /// The path to the SQL script (for SqlScript steps).
    /// </summary>
    public string? SqlScriptPath { get; set; }

    /// <summary>
    /// Creates a DTO from a ScheduleStep entity.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="schedule">The step's Schedule, passed explicitly rather than read from the step's navigation, which
    /// may not be loaded; its setting decides the effective behaviour of a step that follows it.</param>
    public static ScheduleStepDto FromEntity(ScheduleStep step, Schedule schedule)
    {
        return new ScheduleStepDto
        {
            Id = step.Id,
            StepIndex = step.StepIndex,
            Name = step.Name,
            ExecutionMode = step.ExecutionMode,
            StepType = step.StepType,
            OnFailure = step.OnFailure,
            ContinueOnFailure = ScheduleFailureHandling.ContinuesOnFailure(step, schedule),
            FailureBehaviourSource = ScheduleFailureHandling.Source(step),
            TimeoutSeconds = step.Timeout.HasValue ? (int)step.Timeout.Value.TotalSeconds : null,
            // RunProfile
            ConnectedSystemId = step.ConnectedSystemId,
            RunProfileId = step.RunProfileId,
            // PowerShell
            ScriptPath = step.ScriptPath,
            // Executable
            ExecutablePath = step.ExecutablePath,
            WorkingDirectory = step.WorkingDirectory,
            // Shared
            Arguments = step.Arguments,
            // SqlScript
            SqlConnectionString = step.SqlConnectionString,
            SqlScriptPath = step.SqlScriptPath
        };
    }
}

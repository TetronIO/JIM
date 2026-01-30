using JIM.Models.Scheduling;

namespace JIM.Web.Models.Api;

/// <summary>
/// DTO for a schedule in list views.
/// </summary>
public class ScheduleDto
{
    /// <summary>
    /// The unique identifier of the schedule.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The user-defined name for this schedule.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Optional description of what this schedule does.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether this schedule runs on a cron trigger or is manual-only.
    /// </summary>
    public ScheduleTriggerType TriggerType { get; set; }

    /// <summary>
    /// The cron expression for scheduled triggers (null for manual schedules).
    /// </summary>
    public string? CronExpression { get; set; }

    /// <summary>
    /// Whether the schedule is currently enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// When the schedule last ran (UTC).
    /// </summary>
    public DateTime? LastRunTime { get; set; }

    /// <summary>
    /// When the schedule is next due to run (UTC).
    /// </summary>
    public DateTime? NextRunTime { get; set; }

    /// <summary>
    /// The number of steps in this schedule.
    /// </summary>
    public int StepCount { get; set; }

    /// <summary>
    /// When the schedule was created (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// When the schedule was last modified (UTC).
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// Creates a DTO from a Schedule entity.
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
            IsEnabled = schedule.IsEnabled,
            LastRunTime = schedule.LastRunTime,
            NextRunTime = schedule.NextRunTime,
            StepCount = schedule.Steps?.Count ?? 0,
            Created = schedule.Created,
            LastUpdated = schedule.LastUpdated
        };
    }
}

/// <summary>
/// DTO for a schedule with its steps.
/// </summary>
public class ScheduleDetailDto : ScheduleDto
{
    /// <summary>
    /// The steps in this schedule, ordered by StepIndex.
    /// </summary>
    public List<ScheduleStepDto> Steps { get; set; } = new();

    /// <summary>
    /// Creates a detail DTO from a Schedule entity with steps.
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
            IsEnabled = schedule.IsEnabled,
            LastRunTime = schedule.LastRunTime,
            NextRunTime = schedule.NextRunTime,
            StepCount = schedule.Steps?.Count ?? 0,
            Created = schedule.Created,
            LastUpdated = schedule.LastUpdated,
            Steps = schedule.Steps?
                .OrderBy(s => s.StepIndex)
                .Select(ScheduleStepDto.FromEntity)
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
    /// be derived from the connected system and run profile. For other step types, this
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
    /// Whether to continue the schedule if this step fails.
    /// </summary>
    public bool ContinueOnFailure { get; set; }

    /// <summary>
    /// Optional timeout for this step in seconds.
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // RunProfile configuration (used when StepType == RunProfile)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The connected system ID (for RunProfile steps).
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The run profile ID (for RunProfile steps).
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
    public static ScheduleStepDto FromEntity(ScheduleStep step)
    {
        return new ScheduleStepDto
        {
            Id = step.Id,
            StepIndex = step.StepIndex,
            Name = step.Name,
            ExecutionMode = step.ExecutionMode,
            StepType = step.StepType,
            ContinueOnFailure = step.ContinueOnFailure,
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

    /// <summary>
    /// Converts this DTO to a ScheduleStep entity.
    /// </summary>
    public ScheduleStep ToEntity(Guid scheduleId)
    {
        return new ScheduleStep
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            ScheduleId = scheduleId,
            StepIndex = StepIndex,
            Name = Name,
            ExecutionMode = ExecutionMode,
            StepType = StepType,
            ContinueOnFailure = ContinueOnFailure,
            Timeout = TimeoutSeconds.HasValue ? TimeSpan.FromSeconds(TimeoutSeconds.Value) : null,
            // RunProfile
            ConnectedSystemId = ConnectedSystemId,
            RunProfileId = RunProfileId,
            // PowerShell
            ScriptPath = ScriptPath,
            // Executable
            ExecutablePath = ExecutablePath,
            WorkingDirectory = WorkingDirectory,
            // Shared
            Arguments = Arguments,
            // SqlScript
            SqlConnectionString = SqlConnectionString,
            SqlScriptPath = SqlScriptPath
        };
    }
}

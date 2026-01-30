using System.ComponentModel.DataAnnotations;
using JIM.Models.Scheduling;

namespace JIM.Web.Models.Api;

/// <summary>
/// Request DTO for creating a new schedule.
/// </summary>
public class CreateScheduleRequest
{
    /// <summary>
    /// The user-defined name for this schedule.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Optional description of what this schedule does.
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Whether this schedule runs on a cron trigger or is manual-only.
    /// </summary>
    public ScheduleTriggerType TriggerType { get; set; } = ScheduleTriggerType.Manual;

    /// <summary>
    /// The cron expression for scheduled triggers. Required when TriggerType is Cron.
    /// </summary>
    [StringLength(100)]
    public string? CronExpression { get; set; }

    /// <summary>
    /// Whether the schedule should be enabled upon creation.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// The steps to include in this schedule.
    /// </summary>
    public List<ScheduleStepRequest> Steps { get; set; } = new();
}

/// <summary>
/// Request DTO for updating an existing schedule.
/// </summary>
public class UpdateScheduleRequest
{
    /// <summary>
    /// The user-defined name for this schedule.
    /// </summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Optional description of what this schedule does.
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Whether this schedule runs on a cron trigger or is manual-only.
    /// </summary>
    public ScheduleTriggerType TriggerType { get; set; }

    /// <summary>
    /// The cron expression for scheduled triggers. Required when TriggerType is Cron.
    /// </summary>
    [StringLength(100)]
    public string? CronExpression { get; set; }

    /// <summary>
    /// Whether the schedule is enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// The complete list of steps for this schedule.
    /// Existing steps not in this list will be deleted.
    /// </summary>
    public List<ScheduleStepRequest> Steps { get; set; } = new();
}

/// <summary>
/// Request DTO for a schedule step with polymorphic configuration.
/// The step type determines which configuration properties are required.
/// </summary>
public class ScheduleStepRequest
{
    /// <summary>
    /// The unique identifier of the step. Use an existing ID to update,
    /// or omit/use empty GUID to create a new step.
    /// </summary>
    public Guid? Id { get; set; }

    /// <summary>
    /// The order in which this step executes (0-based).
    /// Steps with the same index run in parallel.
    /// </summary>
    [Range(0, 1000)]
    public int StepIndex { get; set; }

    /// <summary>
    /// Display name for the step. Required for non-RunProfile steps (PowerShell, Executable, SqlScript).
    /// For RunProfile steps, this should be null as the name is derived from the FKs.
    /// </summary>
    [StringLength(200)]
    public string? Name { get; set; }

    /// <summary>
    /// How this step executes relative to the previous step.
    /// </summary>
    public StepExecutionMode ExecutionMode { get; set; } = StepExecutionMode.Sequential;

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
    [Range(1, 86400)] // Max 24 hours
    public int? TimeoutSeconds { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // RunProfile configuration (required when StepType == RunProfile)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The connected system ID (required for RunProfile steps).
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The run profile ID (required for RunProfile steps).
    /// </summary>
    public int? RunProfileId { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // PowerShell configuration (required when StepType == PowerShell)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The path to the PowerShell script (required for PowerShell steps).
    /// </summary>
    [StringLength(500)]
    public string? ScriptPath { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Executable configuration (required when StepType == Executable)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The path to the executable (required for Executable steps).
    /// </summary>
    [StringLength(500)]
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// The working directory (optional for Executable steps).
    /// </summary>
    [StringLength(500)]
    public string? WorkingDirectory { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Shared configuration (used by PowerShell and Executable steps)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Arguments to pass to the script or executable (optional for PowerShell and Executable steps).
    /// </summary>
    [StringLength(2000)]
    public string? Arguments { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // SqlScript configuration (required when StepType == SqlScript)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The connection string (required for SqlScript steps).
    /// </summary>
    [StringLength(500)]
    public string? SqlConnectionString { get; set; }

    /// <summary>
    /// The path to the SQL script (required for SqlScript steps).
    /// </summary>
    [StringLength(500)]
    public string? SqlScriptPath { get; set; }

    /// <summary>
    /// Converts this request to a ScheduleStep entity.
    /// </summary>
    public ScheduleStep ToEntity(Guid scheduleId)
    {
        return new ScheduleStep
        {
            Id = Id ?? Guid.NewGuid(),
            ScheduleId = scheduleId,
            StepIndex = StepIndex,
            Name = Name ?? string.Empty,
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

/// <summary>
/// Response returned when a schedule is triggered manually.
/// </summary>
public class ScheduleRunResponse
{
    /// <summary>
    /// The execution ID for tracking the schedule run.
    /// </summary>
    public Guid ExecutionId { get; set; }

    /// <summary>
    /// Message describing the result.
    /// </summary>
    public string Message { get; set; } = null!;
}

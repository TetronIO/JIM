using JIM.Models.Activities;
using JIM.Models.Interfaces;

namespace JIM.Models.Scheduling;

/// <summary>
/// Represents a single step within a schedule. Steps execute in order based on StepIndex,
/// with support for parallel execution via ExecutionMode.
/// </summary>
public class ScheduleStep : IAuditable
{
    public Guid Id { get; set; }

    /// <summary>
    /// The schedule this step belongs to.
    /// </summary>
    public Guid ScheduleId { get; set; }
    public Schedule Schedule { get; set; } = null!;

    /// <summary>
    /// The order in which this step executes (0-based). Steps with the same index
    /// and ParallelWithPrevious mode execute concurrently.
    /// </summary>
    public int StepIndex { get; set; }

    /// <summary>
    /// Optional display name for the step. Used for non-RunProfile steps (PowerShell, Executable, SqlScript)
    /// where users provide a descriptive name. For RunProfile steps, the name is derived from the
    /// connected system and run profile FKs at display time.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// How this step executes relative to the previous step.
    /// Sequential: waits for previous step(s) to complete, starts a new parallel group.
    /// ParallelWithPrevious: runs concurrently with other steps in the same group.
    /// </summary>
    public StepExecutionMode ExecutionMode { get; set; } = StepExecutionMode.Sequential;

    /// <summary>
    /// The type of action this step performs.
    /// </summary>
    public ScheduleStepType StepType { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Step Type Configuration (polymorphic - only properties relevant to StepType are used)
    // -----------------------------------------------------------------------------------------------------------------

    // RunProfile step configuration
    /// <summary>
    /// The connected system ID for RunProfile steps.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The run profile ID for RunProfile steps.
    /// </summary>
    public int? RunProfileId { get; set; }

    // PowerShell step configuration
    /// <summary>
    /// The path to the PowerShell script for PowerShell steps.
    /// </summary>
    public string? ScriptPath { get; set; }

    /// <summary>
    /// Arguments to pass to the script or executable.
    /// Used by PowerShell and Executable step types.
    /// </summary>
    public string? Arguments { get; set; }

    // Executable step configuration
    /// <summary>
    /// The path to the executable for Executable steps.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// The working directory for Executable steps.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    // SqlScript step configuration
    /// <summary>
    /// The connection string for SqlScript steps.
    /// </summary>
    public string? SqlConnectionString { get; set; }

    /// <summary>
    /// The path to the SQL script for SqlScript steps.
    /// </summary>
    public string? SqlScriptPath { get; set; }

    /// <summary>
    /// Whether to continue the schedule if this step fails.
    /// If false (default), schedule execution stops on failure.
    /// </summary>
    public bool ContinueOnFailure { get; set; }

    /// <summary>
    /// Optional timeout for this step. If null, uses the default timeout.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Audit (IAuditable)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// When the step was created (UTC).
    /// </summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The type of security principal that created this step.
    /// </summary>
    public ActivityInitiatorType CreatedByType { get; set; }

    /// <summary>
    /// The ID of the principal that created this step.
    /// </summary>
    public Guid? CreatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of creation.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the step was last modified (UTC).
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The type of security principal that last modified this step.
    /// </summary>
    public ActivityInitiatorType LastUpdatedByType { get; set; }

    /// <summary>
    /// The ID of the principal that last modified this step.
    /// </summary>
    public Guid? LastUpdatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of the last modification.
    /// </summary>
    public string? LastUpdatedByName { get; set; }
}

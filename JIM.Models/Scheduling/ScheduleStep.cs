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
    /// Display name for the step (e.g., "HR Import", "Delta Sync", "AD Export").
    /// </summary>
    public string Name { get; set; } = string.Empty;

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

    /// <summary>
    /// JSON configuration specific to the StepType.
    /// For RunProfile: { "ConnectedSystemId": guid, "RunProfileId": int }
    /// For PowerShell: { "ScriptPath": string, "Arguments": string }
    /// For Executable: { "Path": string, "Arguments": string, "WorkingDirectory": string }
    /// For SqlScript: { "ConnectionString": string, "ScriptPath": string }
    /// </summary>
    public string Configuration { get; set; } = "{}";

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

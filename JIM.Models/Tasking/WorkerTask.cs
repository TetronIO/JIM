using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
namespace JIM.Models.Tasking;

public abstract class WorkerTask
{
	public Guid Id { get; set; }

	/// <summary>
	/// Typically the value for the timestamp will be when the task was created, though the value can
	/// be changed to change the order in which the tasks will be processed in relation to others, i.e. it controls ordering.
	/// </summary>
	public DateTime Timestamp { get; set; } = DateTime.UtcNow;

	public WorkerTaskStatus Status { get; set; } = WorkerTaskStatus.Queued;

	public WorkerTaskExecutionMode ExecutionMode { get; set; } = WorkerTaskExecutionMode.Sequential;

	// -----------------------------------------------------------------------------------------------------------------
	// Initiator tracking - all tasks MUST be attributed to a security principal for audit compliance
	// -----------------------------------------------------------------------------------------------------------------

	/// <summary>
	/// The type of security principal that initiated this task.
	/// </summary>
	public ActivityInitiatorType InitiatedByType { get; set; } = ActivityInitiatorType.NotSet;

	/// <summary>
	/// The unique identifier of the security principal (MetaverseObject or ApiKey) that initiated this task.
	/// </summary>
	public Guid? InitiatedById { get; set; }

	/// <summary>
	/// The name of the security principal at the time of task creation, retained for audit trail.
	/// </summary>
	public string? InitiatedByName { get; set; }

	/// <summary>
	/// If this task was initiated by a user, reference them here.
	/// </summary>
	public MetaverseObject? InitiatedByMetaverseObject { get; set; }

	/// <summary>
	/// If this task was initiated via API key, reference it here.
	/// </summary>
	public ApiKey? InitiatedByApiKey { get; set; }

	/// <summary>
	/// If this worker task has already resulted in an activity being created, then it can be found here, and when the worker task
	/// is initiated then the execution time must be set, and when complete, the activity must also be completed.
	/// </summary>
	public Activity Activity { get; set; } = null!;
}
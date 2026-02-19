namespace JIM.Models.Tasking;

public enum WorkerTaskStatus
{
	Queued = 0,
	Processing = 1,
	CancellationRequested = 2,

	/// <summary>
	/// Task is part of a schedule execution but its preceding step has not yet completed.
	/// The worker ignores tasks in this status. When the prior step completes, the worker
	/// transitions tasks at the next step index from WaitingForPreviousStep to Queued.
	/// </summary>
	WaitingForPreviousStep = 3
}

/// <summary>
/// Determines whether a task must be executed on its own, i.e. sequentially, 
/// or if it can be run in parallel with other tasks.
/// </summary>
public enum WorkerTaskExecutionMode
{
	Sequential = 0,
	Parallel = 1
}
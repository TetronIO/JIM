namespace JIM.Models.Tasking;

public enum WorkerTaskStatus
{
	Queued = 0,
	Processing = 1,
	CancellationRequested = 2
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
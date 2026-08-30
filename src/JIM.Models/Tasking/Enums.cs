// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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

/// <summary>
/// The passes of a Connected System Synchronised Deprovisioning run (#809), recorded on the task row as
/// its resumability checkpoint. A value means the run last completed a batch within (or reached) that
/// pass; a worker restart resumes there rather than reprocessing committed work.
/// </summary>
public enum SynchronisedDeprovisioningPhase
{
	/// <summary>
	/// The per-object pass: each Connected System Object processed through the obsoletion core, in
	/// ascending id order, batched.
	/// </summary>
	ObjectPass = 0,

	/// <summary>
	/// The by-provenance residue pass: per import Synchronisation Rule, remaining contributed values
	/// recalled, before any rule is deleted.
	/// </summary>
	ResiduePass = 1,

	/// <summary>
	/// The final step: the existing Connected System deletion (tombstone, bulk delete).
	/// </summary>
	FinalDeletion = 2
}

/// <summary>
/// The database operation that raised a real-time Worker Task change notification (issue #307).
/// Values map to the PostgreSQL trigger operation (TG_OP) that fired.
/// </summary>
public enum WorkerTaskChangeOperation
{
	Insert = 0,
	Update = 1,
	Delete = 2
}
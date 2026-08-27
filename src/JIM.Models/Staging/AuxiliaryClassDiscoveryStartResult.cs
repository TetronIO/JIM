// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Whether an auxiliary class discovery run was queued, and what to tell an administrator if it was not.
/// </summary>
/// <remarks>
/// A refusal here is an ordinary outcome rather than an error: the most common one is that a run is already in
/// flight, which an administrator needs explaining rather than throwing at them.
/// </remarks>
public class AuxiliaryClassDiscoveryStartResult
{
    public bool Success { get; private init; }

    /// <summary>
    /// Why the run was not queued. Null when it was.
    /// </summary>
    public string? ErrorMessage { get; private init; }

    /// <summary>
    /// The queued task, for a caller that wants to follow it.
    /// </summary>
    public Guid? WorkerTaskId { get; private init; }

    /// <summary>
    /// The Activity the run reports its progress and outcome against.
    /// </summary>
    public Guid? ActivityId { get; private init; }

    public static AuxiliaryClassDiscoveryStartResult Queued(Guid workerTaskId, Guid activityId)
    {
        return new AuxiliaryClassDiscoveryStartResult { Success = true, WorkerTaskId = workerTaskId, ActivityId = activityId };
    }

    public static AuxiliaryClassDiscoveryStartResult Failed(string errorMessage)
    {
        return new AuxiliaryClassDiscoveryStartResult { Success = false, ErrorMessage = errorMessage };
    }
}

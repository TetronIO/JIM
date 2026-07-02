// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
namespace JIM.Models.Tasking;

/// <summary>
/// Worker task that runs one Temporal Scope Reconciler sweep (issue #892). Carries no per-instance
/// configuration: the sweep operates across all enabled Synchronisation Rules, and its lower-bound watermark
/// is derived at execution time from the last successfully completed run of the built-in schedule (so a failed
/// sweep never advances the watermark and no window is skipped).
/// </summary>
public class TemporalScopeReconciliationWorkerTask : WorkerTask
{
    public TemporalScopeReconciliationWorkerTask()
    {
        // for use by EntityFramework to construct db-sourced objects.
    }

    /// <summary>
    /// Factory method for creating a task triggered by the system (the scheduler).
    /// </summary>
    public static TemporalScopeReconciliationWorkerTask ForSystem(string initiatedByName)
    {
        return new TemporalScopeReconciliationWorkerTask
        {
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = initiatedByName
        };
    }
}

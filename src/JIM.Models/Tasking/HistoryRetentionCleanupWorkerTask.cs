// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
namespace JIM.Models.Tasking;

/// <summary>
/// Worker task that runs one history retention cleanup pass (issue #1118). Carries no per-instance
/// configuration: every cutoff is derived at execution time from the Service Settings that hold each retention
/// period, so an administrator's change to one takes effect on the next pass without the Schedule being touched.
/// <para>
/// Retention used to run on a timer inside the worker's idle loop, where it was invisible: nothing said when it
/// last ran, when it would run next, or what it removed, and a busy worker could put it off indefinitely. As a
/// Schedule step it has an execution history, a next run time, and the same cancel and observability affordances
/// as every other scheduled step.
/// </para>
/// </summary>
public class HistoryRetentionCleanupWorkerTask : WorkerTask
{
    public HistoryRetentionCleanupWorkerTask()
    {
        // for use by EntityFramework to construct db-sourced objects.
    }

    /// <summary>
    /// Factory method for creating a task triggered by the system (the scheduler).
    /// </summary>
    public static HistoryRetentionCleanupWorkerTask ForSystem(string initiatedByName)
    {
        return new HistoryRetentionCleanupWorkerTask
        {
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = initiatedByName
        };
    }
}

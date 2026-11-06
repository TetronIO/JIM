// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Web.Models;

/// <summary>
/// What the Operations page's Completed list shows for an Activity's Connected System, Run Profile and, for a clear, its
/// outcomes. A Run Profile execution names its Run Profile as the target and its Connected System as the context; a
/// Connected System operation (a clear, a deletion) names the Connected System itself as the target and has no Run
/// Profile at all, so reading the two fields straight into the columns put the system under Run Profile and left
/// Connected System empty, where the Queued and In Progress lists show the same work as system and operation.
/// </summary>
public static class CompletedActivityCells
{
    /// <summary>
    /// The Connected System the Activity acted on.
    /// </summary>
    public static string? ConnectedSystem(Activity activity) =>
        activity.TargetType == ActivityTargetType.ConnectedSystem ? activity.TargetName : activity.TargetContext;

    /// <summary>
    /// The Run Profile that ran, or the name of the Connected System operation where none did.
    /// </summary>
    public static string? Operation(Activity activity)
    {
        if (activity.TargetType != ActivityTargetType.ConnectedSystem)
            return activity.TargetName;

        return activity.TargetOperationType switch
        {
            ActivityTargetOperationType.Clear => "Clear Connected System Objects",
            ActivityTargetOperationType.Delete => "Delete Connected System",
            ActivityTargetOperationType.Deprovision => "Deprovision Connected System",
            var other => $"{other} Connected System"
        };
    }

    /// <summary>
    /// What a clear removed, and the joins it kept for the next Full Synchronisation to reconcile; empty for anything
    /// that is not a clear. Counts of zero are left out, as every other outcome on the list is.
    /// </summary>
    public static IReadOnlyList<CompletedActivityOutcome> ClearOutcomes(Activity activity)
    {
        if (activity is not { TargetType: ActivityTargetType.ConnectedSystem, TargetOperationType: ActivityTargetOperationType.Clear })
            return [];

        var outcomes = new List<CompletedActivityOutcome>
        {
            new("Connected System Objects removed", activity.ClearedConnectedSystemObjectCount ?? 0, IsRemoval: true),
            new("Pending Exports removed", activity.ClearedPendingExportCount ?? 0, IsRemoval: true),
            new("Joins tracked for reconciliation", activity.ClearedJoinRecordCount ?? 0, IsRemoval: false)
        };
        return outcomes.Where(o => o.Count > 0).ToList();
    }
}

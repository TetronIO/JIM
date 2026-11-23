// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Web.Resources;

namespace JIM.Web.Models;

/// <summary>
/// How the shared Configuration Change Preview panel words and orders what a preview found, where that depends on
/// what was found or on the surface previewed (#134). Kept out of the panel so each rule is testable on its own.
/// </summary>
public static class ConfigurationChangePreviewWording
{
    /// <summary>
    /// The transitions in which a surviving contributor takes an attribute over: the Connected System on such a row
    /// is where the value now comes from, not where the objects are.
    /// </summary>
    private static readonly HashSet<ActivityRunProfileExecutionItemSyncOutcomeType> Takeovers =
    [
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverSameValue
    ];

    /// <summary>
    /// The Metaverse-side transitions whose current value is the one the deleted system contributed.
    /// </summary>
    private static readonly HashSet<ActivityRunProfileExecutionItemSyncOutcomeType> RecalledValues =
    [
        ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverContributedValue,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldTakeOverSameValue,
        ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues
    ];

    /// <summary>
    /// What follows "<c>person</c> objects" on a summary row naming a Connected System: ", now from X" for a takeover,
    /// whose system is the new source of the value, and " in X" for everything else, whose system holds the objects.
    /// Empty where the row names no system.
    /// </summary>
    public static string SystemClause(ActivityRunProfileExecutionItemSyncOutcomeType transition, string? connectedSystemName)
    {
        if (string.IsNullOrEmpty(connectedSystemName))
            return string.Empty;

        return string.Format(CultureInfo.CurrentCulture,
            Takeovers.Contains(transition) ? ActivityText.ActivityWordingNowFrom : ActivityText.ActivityWordingIn,
            connectedSystemName);
    }

    /// <summary>
    /// The Connected System a drill-down value came from, where the reader needs it to tell two values apart, or null.
    /// On a deletion preview the current value of a recalled attribute is the deleted system's, and a takeover's
    /// proposed value is its new contributor's (the row's Connected System); a target system's own values, and every
    /// value on other surfaces, name none.
    /// </summary>
    /// <param name="surface">The surface the preview was of.</param>
    /// <param name="transition">The transition the row describes.</param>
    /// <param name="proposedValue">True for the proposed value, false for the current one.</param>
    /// <param name="previewTargetName">The name of the object the preview was about: the deleted system.</param>
    /// <param name="groupSystemName">The Connected System the row's summary group names.</param>
    public static string? ValueContributor(ConfigurationChangePreviewSurface surface, ActivityRunProfileExecutionItemSyncOutcomeType transition,
        bool proposedValue, string? previewTargetName, string? groupSystemName)
    {
        if (surface != ConfigurationChangePreviewSurface.ConnectedSystemDeletion)
            return null;

        if (proposedValue)
            return Takeovers.Contains(transition) ? groupSystemName : null;

        return RecalledValues.Contains(transition) ? previewTargetName : null;
    }

    /// <summary>
    /// What a completed preview that found nothing says. The generic line is false for a deletion, whose own
    /// Connected System Objects go with it whatever else happens, so that surface says what it means: nothing outside
    /// the system changes. A Full Synchronisation is not a change at all, so it says what the run would do.
    /// </summary>
    /// <param name="surface">The surface the preview was of.</param>
    /// <param name="previewTargetName">The name of the object the preview was about, where the sentence names it.</param>
    public static string NothingWouldChange(ConfigurationChangePreviewSurface surface, string? previewTargetName = null) => surface switch
    {
        ConfigurationChangePreviewSurface.ConnectedSystemDeletion => ActivityText.ActivityWordingDeletionNoChange,
        ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation =>
            string.Format(CultureInfo.CurrentCulture, ActivityText.ActivityWordingFullSyncNoChange,
                string.IsNullOrEmpty(previewTargetName) ? ActivityText.ActivityWordingThisConnectedSystem : previewTargetName),
        _ => ActivityText.ActivityWordingNoChange
    };

    /// <summary>
    /// Whether a transition says an object would not change (#1530). Its groups are not changes, so they are left out of
    /// the What would change grid and stated as one line under it, as the verdict leaves them out of its sentence.
    /// </summary>
    public static bool IsUnchanged(ActivityRunProfileExecutionItemSyncOutcomeType transition) =>
        transition == ActivityRunProfileExecutionItemSyncOutcomeType.WouldNotChange;

    /// <summary>
    /// The drill-down's two value column headings for one group. A proposed configuration has a current and a proposed
    /// value; a Full Synchronisation proposes no configuration, so its values are the object's now and after the run. A
    /// group of withdrawn Pending Export changes is neither (#2001): its values are the change that was queued and the
    /// value the target already holds, which it keeps.
    /// </summary>
    public static (string Current, string Proposed) ValueColumnTitles(ConfigurationChangePreviewSurface surface,
        ActivityRunProfileExecutionItemSyncOutcomeType transition)
    {
        if (transition == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportChangesWithdrawn)
            return (ActivityText.ActivityWordingQueued, ActivityText.ActivityWordingTargetKeeps);

        return surface == ConfigurationChangePreviewSurface.ConnectedSystemFullSynchronisation
            ? (ActivityText.ActivityWordingNow, ActivityText.ActivityWordingAfterSynchronisation)
            : (ActivityText.ActivityWordingCurrentValue, ActivityText.ActivityWordingProposedValue);
    }

    /// <summary>
    /// The Connected System a drill-down row's Connected System Object is in. A provisioning names an object that does
    /// not exist yet in the system it is provisioned to, so where it carries an object at all, that object is the one
    /// being projected, in the system the preview was of (#1530). Every other row's object is in the row's own system.
    /// </summary>
    public static int? ObjectConnectedSystemId(ConfigurationChangePreviewDelta row, int? previewedConnectedSystemId) =>
        row.TransitionType == ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned && previewedConnectedSystemId.HasValue
            ? previewedConnectedSystemId
            : row.ConnectedSystemId;

    /// <summary>
    /// Summary groups in the order of consequence the verdict states them in, then largest first, so the grid and the
    /// sentence above it agree about what matters most.
    /// </summary>
    public static IOrderedEnumerable<ConfigurationChangePreviewGroup> ByConsequence(IEnumerable<ConfigurationChangePreviewGroup> groups) =>
        groups
            .OrderBy(g => ConfigurationChangePreviewVerdict.ConsequenceOrder(g.TransitionType))
            .ThenByDescending(g => g.ObjectCount)
            .ThenBy(g => g.TransitionType);
}

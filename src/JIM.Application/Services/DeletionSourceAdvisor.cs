// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;

namespace JIM.Application.Services;

/// <summary>
/// Finds Connected Systems that can create Metaverse Objects no selected authoritative source governs (#1256).
///
/// Under When Authoritative Source Disconnected the authoritative sources are an include list, so a system onboarded
/// later is silently left out of it. That matters only when the system <em>projects</em>: a join-only contributor
/// (projection off) cannot create objects, and leaving it out is the normal way to add a system that contributes
/// attributes without governing lifecycle. A projecting one can create objects that no selected source holds, which
/// are then never deleted, and the objects it shares with a selected source are deleted when that source departs.
///
/// Advisory only: nothing here refuses a save. All three surfaces (the portal, the REST responses and the PowerShell
/// cmdlets that read them) derive the finding from this one helper so they can never disagree.
/// </summary>
public static class DeletionSourceAdvisor
{
    /// <summary>
    /// The systems with an enabled, projecting inbound Synchronisation Rule for the type that are not selected
    /// authoritative sources, one entry per system ordered by name. Empty for any rule other than When Authoritative
    /// Source Disconnected.
    /// </summary>
    /// <param name="deletionRule">The deletion rule being configured or displayed.</param>
    /// <param name="authoritativeSourceIds">The selected authoritative sources (stored, or as edited).</param>
    /// <param name="metaverseObjectTypeId">The Metaverse Object Type the deletion settings belong to.</param>
    /// <param name="syncRules">Synchronisation Rule headers; any not projecting into the type are ignored.</param>
    public static IReadOnlyList<DeletionSourceGap> GetUnlistedProjectingSources(
        MetaverseObjectDeletionRule deletionRule,
        IEnumerable<int> authoritativeSourceIds,
        int metaverseObjectTypeId,
        IEnumerable<SyncRuleHeader> syncRules)
    {
        if (deletionRule != MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected)
            return [];

        var sources = authoritativeSourceIds.ToHashSet();
        return syncRules
            .Where(rule => rule.Direction == SyncRuleDirection.Import
                && rule.Enabled
                && rule.ProjectToMetaverse == true
                && rule.MetaverseObjectTypeId == metaverseObjectTypeId
                && !sources.Contains(rule.ConnectedSystemId))
            .GroupBy(rule => rule.ConnectedSystemId)
            .Select(group => new DeletionSourceGap(group.Key, group.First().ConnectedSystemName))
            .OrderBy(gap => gap.ConnectedSystemName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The Metaverse Object Type <paramref name="rule"/> projects into, or null when it projects nothing (not an
    /// import rule, disabled, or projection off). Callers capture this before an edit so
    /// <see cref="GetSyncRuleSaveWarning"/> can tell a rule that newly projects from one that already did.
    /// </summary>
    public static int? GetProjectedMetaverseObjectTypeId(SyncRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.Direction == SyncRuleDirection.Import && rule.Enabled && rule.ProjectToMetaverse == true
            ? rule.ResolveMetaverseObjectTypeId()
            : null;
    }

    /// <summary>
    /// The warning for saving <paramref name="proposed"/>, or null when there is nothing to say. Fires only when the
    /// save takes the rule into projecting into <paramref name="metaverseObjectType"/> (a new projecting rule, a rule
    /// enabled, projection switched on, or a rule retargeted from another type), so re-saving a rule that already
    /// projected does not repeat it on every save.
    /// </summary>
    /// <param name="proposed">The rule as about to be saved.</param>
    /// <param name="projectedTypeIdBefore">What <see cref="GetProjectedMetaverseObjectTypeId"/> returned for the rule
    /// as stored, or null for a new rule.</param>
    /// <param name="metaverseObjectType">The type the rule projects into, carrying its deletion settings.</param>
    /// <param name="connectedSystemNames">Connected System names by id, for the rule's own system and the type's
    /// authoritative sources.</param>
    public static SyncRuleDeletionSourceWarning? GetSyncRuleSaveWarning(
        SyncRule proposed,
        int? projectedTypeIdBefore,
        MetaverseObjectType metaverseObjectType,
        IReadOnlyDictionary<int, string> connectedSystemNames)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(metaverseObjectType);
        ArgumentNullException.ThrowIfNull(connectedSystemNames);

        var projectedTypeId = GetProjectedMetaverseObjectTypeId(proposed);
        if (projectedTypeId == null || projectedTypeId == projectedTypeIdBefore || projectedTypeId != metaverseObjectType.Id)
            return null;

        // With no sources selected the deletion rule falls back to When Last Connector Disconnected semantics (the
        // panel will not save that state, but the API could have), so a missing source is not this warning's concern.
        var sourceIds = metaverseObjectType.DeletionTriggerConnectedSystemIds ?? [];
        if (metaverseObjectType.DeletionRule != MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected
            || sourceIds.Count == 0
            || sourceIds.Contains(proposed.ConnectedSystemId))
            return null;

        return new SyncRuleDeletionSourceWarning(
            NameOf(proposed.ConnectedSystemId, connectedSystemNames),
            metaverseObjectType.Name,
            sourceIds.Select(id => NameOf(id, connectedSystemNames))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    private static string NameOf(int connectedSystemId, IReadOnlyDictionary<int, string> names) =>
        names.TryGetValue(connectedSystemId, out var name) ? name : $"Connected System {connectedSystemId}";
}

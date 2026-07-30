// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Staging.DTOs;

namespace JIM.Application.Services;

/// <summary>
/// Answers "has this Connected System's configuration changed in a way that needs a Full Synchronisation to take
/// effect?" by comparing the classified configuration changes recorded against the system with the start of its last
/// completed Full Synchronisation.
///
/// Attribution is precise rather than blanket: a Metaverse Attribute edit raises the indicator only on the systems
/// whose Synchronisation Rules actually reference that attribute. Flagging every system on every Metaverse-side edit
/// would make the indicator noise, and an indicator administrators learn to ignore is worse than none.
/// </summary>
public class ConfigurationDriftService
{
    private JimApplication Application { get; }

    internal ConfigurationDriftService(JimApplication application)
    {
        Application = application;
    }

    /// <summary>
    /// Determines whether one Connected System has Sync-affecting or Destructive configuration changes recorded since
    /// its last completed Full Synchronisation.
    /// </summary>
    public async Task<ConfigurationDriftStatus> GetConnectedSystemDriftAsync(int connectedSystemId)
    {
        var results = await GetConnectedSystemDriftAsync([connectedSystemId]);
        return results[connectedSystemId];
    }

    /// <summary>
    /// The batch counterpart of <see cref="GetConnectedSystemDriftAsync(int)"/>, for list surfaces that need a status
    /// per system. Issues a fixed number of queries regardless of how many systems are asked about, so a list page
    /// does not degrade into an N+1.
    /// </summary>
    /// <returns>A status for every requested system id.</returns>
    public async Task<Dictionary<int, ConfigurationDriftStatus>> GetConnectedSystemDriftAsync(IList<int> connectedSystemIds)
    {
        ArgumentNullException.ThrowIfNull(connectedSystemIds);

        var ids = connectedSystemIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<int, ConfigurationDriftStatus>();

        // With change tracking off nothing is recorded, so drift is unknowable. Reporting that honestly matters more
        // than reporting a comfortable answer: "no changes pending" here would be a claim JIM cannot support.
        if (!await Application.ServiceSettings.GetConfigurationChangeTrackingEnabledAsync())
            return ids.ToDictionary(id => id, id => new ConfigurationDriftStatus
            {
                ConnectedSystemId = id,
                TrackingDisabled = true
            });

        var lastFullSyncs = await Application.Repository.Activity.GetLastFullSynchronisationStartsAsync(ids);

        var synchronisedIds = ids.Where(lastFullSyncs.ContainsKey).ToList();
        var results = ids.Where(id => !lastFullSyncs.ContainsKey(id))
            .ToDictionary(id => id, id => new ConfigurationDriftStatus
            {
                ConnectedSystemId = id,
                NeverFullySynchronised = true
            });

        if (synchronisedIds.Count == 0)
            return results;

        // One changes query covering the earliest reference point across the batch, then per-system filtering. Each
        // system still compares against its own reference point below.
        var earliestReferencePoint = synchronisedIds.Min(id => lastFullSyncs[id]);
        var impacts = await Application.Repository.Activity.GetConfigurationChangeImpactsSinceAsync(
            earliestReferencePoint, ConfigurationChangeClass.SyncAffecting);

        var scopes = (await Application.Repository.ConnectedSystems.GetConfigurationScopesAsync(synchronisedIds))
            .ToDictionary(s => s.ConnectedSystemId);

        // The reference point is projected alongside the id rather than looked up as the loop body's first statement,
        // which reads as a map-only foreach to the code-quality analyser (see the Select rule in src/CLAUDE.md).
        foreach (var (id, referencePoint) in synchronisedIds.Select(id => (id, referencePoint: lastFullSyncs[id])))
        {
            // A system with no scope entry is treated as having an empty scope rather than skipped: changes to the
            // system itself still count, and silently dropping it would under-report.
            var scope = scopes.TryGetValue(id, out var found)
                ? found
                : new ConnectedSystemConfigurationScope { ConnectedSystemId = id };

            var qualifying = impacts
                .Where(i => i.When >= referencePoint && Affects(i, scope))
                .ToList();

            results[id] = new ConfigurationDriftStatus
            {
                ConnectedSystemId = id,
                HasPendingChanges = qualifying.Count > 0,
                LastFullSynchronisation = referencePoint,
                MostRecentChange = qualifying.Count > 0 ? qualifying.Max(i => i.When) : null,
                ChangeCount = qualifying.Count,
                HighestChangeClass = qualifying.Count > 0
                    ? qualifying.Max(i => i.Class)
                    : ConfigurationChangeClass.NotClassified
            };
        }

        return results;
    }

    /// <summary>
    /// Whether a recorded configuration change affects the given system's synchronisation outcomes.
    /// </summary>
    private static bool Affects(ConfigurationChangeImpactData impact, ConnectedSystemConfigurationScope scope)
    {
        // The system itself, its sub-entities, and Synchronisation Rule deletions (which carry the owning system's id
        // because the rule they describe no longer exists to be referenced).
        if (impact.ConnectedSystemId == scope.ConnectedSystemId)
            return true;

        if (impact.SyncRuleId is { } syncRuleId && scope.SyncRuleIds.Contains(syncRuleId))
            return true;

        if (impact.MetaverseObjectTypeId is { } objectTypeId && scope.MetaverseObjectTypeIds.Contains(objectTypeId))
            return true;

        if (impact.MetaverseAttributeId is { } attributeId && scope.MetaverseAttributeIds.Contains(attributeId))
            return true;

        // Service Settings are global, so a Sync-affecting one affects every system. The change reaching here has
        // already been classified at or above Sync-affecting, so no further filtering by key is needed.
        return impact.ServiceSettingKey != null;
    }
}

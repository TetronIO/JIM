// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Logic;

namespace JIM.Application.Services;

/// <summary>
/// Answers, for a disconnection's recall decision, whether any of a Metaverse Object's remaining joined
/// Connected Systems is still a contributing source: a system carrying an enabled import-direction
/// Synchronisation Rule for the object's type (#1570). The answer selects between recalling a departed
/// system's sole-contributed values (a source remains, so leftovers are staleness on an actively managed
/// object) and preserving them as last known state (only provisioned targets remain, so a recall would
/// blank live target accounts and feed expression-based mappings such as a Distinguished Name with nulls).
/// The Synchronisation Rule map is loaded lazily on first use and cached for the instance's lifetime, so a
/// run pays one small query however many objects it disconnects; create one instance per run.
/// </summary>
public sealed class RemainingImportSourceEvaluator(ISyncRepository syncRepository)
{
    private Dictionary<int, HashSet<int>>? _importTypeIdsBySystemId;

    /// <summary>
    /// Whether any of the given remaining joined Connected Systems carries an enabled import
    /// Synchronisation Rule for the given Metaverse Object Type. Duplicate system ids (a system with two
    /// joined objects) are naturally tolerated; an empty remaining list answers false.
    /// </summary>
    public async Task<bool> AnyImportSourceRemainsAsync(IReadOnlyCollection<int> remainingConnectedSystemIds, int metaverseObjectTypeId)
    {
        if (remainingConnectedSystemIds.Count == 0)
            return false;

        if (_importTypeIdsBySystemId == null)
        {
            var allSyncRules = await syncRepository.GetAllSyncRulesAsync();
            _importTypeIdsBySystemId = allSyncRules
                .Where(rule => rule.Enabled && rule.Direction == SyncRuleDirection.Import)
                .GroupBy(rule => rule.ConnectedSystemId)
                .ToDictionary(group => group.Key, group => group.Select(rule => rule.MetaverseObjectTypeId).ToHashSet());
        }

        return remainingConnectedSystemIds.Any(systemId =>
            _importTypeIdsBySystemId.TryGetValue(systemId, out var importTypeIds) && importTypeIds.Contains(metaverseObjectTypeId));
    }
}

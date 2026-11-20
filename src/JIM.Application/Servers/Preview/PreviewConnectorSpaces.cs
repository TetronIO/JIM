// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// The answers the preview adapters give to <see cref="IConfigurationChangePreviewAdapter.GetEvaluatedConnectorSpacesAsync"/>
/// (#3014), kept in one place so the Synchronisation Rule surfaces cannot drift apart.
/// </summary>
public static class PreviewConnectorSpaces
{
    /// <summary>None: the surface evaluates no Connector Space as its synchronisation would.</summary>
    public static Task<IReadOnlyCollection<int>> None { get; } = Task.FromResult<IReadOnlyCollection<int>>([]);

    /// <summary>One Connected System's Connector Space.</summary>
    public static IReadOnlyCollection<int> Of(int connectedSystemId) => [connectedSystemId];

    /// <summary>
    /// A Synchronisation Rule change's: an import rule's preview evaluates its Connected System's objects as that
    /// system's synchronisation would, and that synchronisation applies the change. An export rule's evaluates
    /// Metaverse Objects, whose synchronisations are not refused by a target system's stopped import.
    /// </summary>
    public static IReadOnlyCollection<int> ForSyncRule(SyncRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Direction == SyncRuleDirection.Import ? Of(rule.ConnectedSystemId) : [];
    }
}

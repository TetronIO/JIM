// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// What a synchronisation does with a joined object's join when it leaves the last import rule covering it, or is
/// deleted from its Connected System, worked out as the run works it out (#2823): the rules that held the object
/// decide, and Remain Joined wins. Shared so every preview adapter answers it identically.
/// </summary>
internal static class PreviewLastScopeExit
{
    /// <summary>
    /// The action under each of <paramref name="ruleSets"/> (the current rules and the proposed ones, say), in order.
    /// Which rules held the object is read from its Metaverse Object, loaded only when the answer can depend on it,
    /// that is when the rules in a set do not all have one action; a streamed object carries no Metaverse values.
    /// The object is left as it was found.
    /// </summary>
    /// <param name="application">The application, for the Metaverse Object read.</param>
    /// <param name="syncEngine">The engine whose decision the run applies.</param>
    /// <param name="cso">The joined Connected System Object.</param>
    /// <param name="ruleSets">Each set of import rules the object would have left.</param>
    internal static async Task<List<InboundOutOfScopeAction>> DecideAsync(
        JimApplication application,
        ISyncEngine syncEngine,
        ConnectedSystemObject cso,
        params IReadOnlyList<SyncRule>[] ruleSets)
    {
        var originalMetaverseObject = cso.MetaverseObject;
        try
        {
            var dependsOnWhoHeldIt = ruleSets.Any(rules => rules.Select(rule => rule.InboundOutOfScopeAction).Distinct().Count() > 1);
            if (dependsOnWhoHeldIt && cso.MetaverseObject == null && cso.MetaverseObjectId is { } metaverseObjectId)
                cso.MetaverseObject = (await application.Metaverse.GetMetaverseObjectsByIdsNoTrackingAsync([metaverseObjectId]))
                    .SingleOrDefault();

            return [.. ruleSets.Select(rules => syncEngine.DecideLastScopeExit(cso, rules).Action)];
        }
        finally
        {
            cso.MetaverseObject = originalMetaverseObject;
        }
    }
}

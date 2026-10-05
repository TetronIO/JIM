// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Logic;

namespace JIM.Application.Servers;

/// <summary>
/// The deletion source advisory (#1256) for a stored Metaverse Object Type: projecting Connected Systems that are not
/// selected authoritative sources. The rules live in <see cref="DeletionSourceAdvisor"/>; this only loads their inputs.
/// </summary>
public partial class MetaverseServer
{
    /// <summary>
    /// The Connected Systems that project into <paramref name="objectType"/> without being one of its selected
    /// authoritative sources, as the type is stored. Empty for any deletion rule other than When Authoritative Source
    /// Disconnected, in which case no Synchronisation Rules are read.
    /// </summary>
    public async Task<IReadOnlyList<DeletionSourceGap>> GetDeletionSourceGapsAsync(MetaverseObjectType objectType)
    {
        ArgumentNullException.ThrowIfNull(objectType);

        if (objectType.DeletionRule != MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected)
            return [];

        var importRules = await Application.ConnectedSystems.GetSyncRuleHeadersAsync(objectType.Id, SyncRuleDirection.Import) ?? [];
        return DeletionSourceAdvisor.GetUnlistedProjectingSources(
            objectType.DeletionRule, objectType.DeletionTriggerConnectedSystemIds ?? [], objectType.Id, importRules);
    }
}

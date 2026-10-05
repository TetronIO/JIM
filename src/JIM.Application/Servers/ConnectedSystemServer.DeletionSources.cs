// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Logic;

namespace JIM.Application.Servers;

/// <summary>
/// The deletion source advisory (#1256) for a Synchronisation Rule save: whether the save takes the rule into
/// projecting into a Metaverse Object Type deleted When Authoritative Source Disconnected, from a Connected System that
/// is not one of the type's authoritative sources. The rules live in <see cref="DeletionSourceAdvisor"/>; this only
/// loads their inputs, and reads nothing at all for a rule that does not newly project.
/// </summary>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// The warning for saving <paramref name="proposed"/>, or null when there is nothing to say. Advisory only; the save
    /// is never refused for it.
    /// </summary>
    /// <param name="proposed">The rule as about to be (or just) saved.</param>
    /// <param name="projectedTypeIdBefore">
    /// <see cref="DeletionSourceAdvisor.GetProjectedMetaverseObjectTypeId"/> of the rule as it was stored before the
    /// edit, captured by the caller before applying any change; null for a new rule.
    /// </param>
    public async Task<SyncRuleDeletionSourceWarning?> GetSyncRuleDeletionSourceWarningAsync(SyncRule proposed, int? projectedTypeIdBefore)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        var projectedTypeId = DeletionSourceAdvisor.GetProjectedMetaverseObjectTypeId(proposed);
        if (projectedTypeId == null || projectedTypeId == projectedTypeIdBefore)
            return null;

        var objectType = await Application.Metaverse.GetMetaverseObjectTypeAsync(projectedTypeId.Value, false);
        if (objectType == null || objectType.DeletionRule != MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected)
            return null;

        var connectedSystemNames = await Application.Repository.ConnectedSystems.GetConnectedSystemNamesAsync() ?? [];
        return DeletionSourceAdvisor.GetSyncRuleSaveWarning(proposed, projectedTypeIdBefore, objectType, connectedSystemNames);
    }
}

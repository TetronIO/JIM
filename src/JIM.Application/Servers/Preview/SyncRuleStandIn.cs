// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// Builds the unsaved Synchronisation Rule a configuration change preview evaluates in place of the stored one.
/// </summary>
/// <remarks>
/// Shared by every adapter that previews part of a rule, because a stand-in has to be faithful in every respect
/// EXCEPT the part being proposed. Each adapter replacing only its own part on a copy that carried nothing else
/// would answer for a rule that does not exist: a scope preview whose stand-in has no Attribute Flow reports a
/// projection that flows nothing, and an Attribute Flow preview whose stand-in has no Scoping Criteria reports
/// values flowing to objects the rule does not manage.
///
/// The copy is always a new rule and never the loaded one with a collection swapped: an adapter evaluates each
/// object against both the stored rule and the proposal, so mutating the loaded rule would have it comparing the
/// proposal against itself and reporting that nothing would change.
/// </remarks>
internal static class SyncRuleStandIn
{
    /// <summary>
    /// A copy of <paramref name="storedRule"/> carrying its identity, its settings, its Scoping Criteria and its
    /// Attribute Flow mappings. Callers replace the one part they are proposing.
    /// </summary>
    public static SyncRule CloneOf(SyncRule storedRule)
    {
        ArgumentNullException.ThrowIfNull(storedRule);

        var standIn = new SyncRule
        {
            Id = storedRule.Id,
            Name = storedRule.Name,
            Direction = storedRule.Direction,
            Enabled = storedRule.Enabled,
            ConnectedSystemId = storedRule.ConnectedSystemId,
            ConnectedSystemObjectTypeId = storedRule.ConnectedSystemObjectTypeId,
            ConnectedSystemObjectType = storedRule.ConnectedSystemObjectType,
            MetaverseObjectTypeId = storedRule.MetaverseObjectTypeId,
            MetaverseObjectType = storedRule.MetaverseObjectType,
            ProjectToMetaverse = storedRule.ProjectToMetaverse,
            ProvisionToConnectedSystem = storedRule.ProvisionToConnectedSystem,
            InboundOutOfScopeAction = storedRule.InboundOutOfScopeAction,
            OutboundDeprovisionAction = storedRule.OutboundDeprovisionAction
        };

        // Carried by reference: the stand-in is read-only to every adapter, and copying the criteria and mapping
        // graphs would be work done only to be thrown away by whichever adapter then replaces one of them.
        foreach (var group in storedRule.ObjectScopingCriteriaGroups)
            standIn.ObjectScopingCriteriaGroups.Add(group);

        foreach (var mapping in storedRule.AttributeFlowRules)
            standIn.AttributeFlowRules.Add(mapping);

        return standIn;
    }
}

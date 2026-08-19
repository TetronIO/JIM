// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// Turns a proposed criteria tree back into a Synchronisation Rule the scope evaluator can be asked about (#1436),
/// so a scope preview puts its questions to the engine's own evaluation rather than reimplementing any part of it.
/// </summary>
/// <remarks>
/// Two things here are load-bearing and neither is obvious from the evaluator's signature.
///
/// <see cref="ScopingEvaluationServer"/> reads each criterion's attribute ENTITY rather than its id, and a
/// criterion whose attribute navigation is null evaluates to false. A stand-in carrying only the ids would
/// therefore match nothing while looking perfectly well formed, and the preview would report the entire population
/// leaving scope.
///
/// And the stand-in is always a new rule, never the loaded one with its criteria replaced. The adapter evaluates
/// each object against both the stored rule and the proposal, so mutating the loaded rule would have it comparing
/// the proposal against itself and reporting that nothing would change.
/// </remarks>
internal static class SyncRuleScopingProposalMaterialiser
{
    /// <summary>
    /// A copy of <paramref name="storedRule"/> carrying <paramref name="proposal"/>'s criteria instead of its own,
    /// with every criterion's attribute entity attached.
    /// </summary>
    /// <param name="storedRule">The rule being edited; its identity and settings are carried across unchanged.</param>
    /// <param name="proposal">The proposed Scoping Criteria.</param>
    /// <param name="connectedSystemAttributes">The Connected System attributes an import rule's criteria may name.</param>
    /// <param name="metaverseAttributes">The Metaverse Attributes an export rule's criteria may name.</param>
    /// <exception cref="InvalidOperationException">
    /// A criterion names an attribute that is not in the supplied lookups, or names no attribute at all. Thrown
    /// rather than dropped: a dropped criterion evaluates as though it were never written, which widens the
    /// proposed scope instead of narrowing it, and pulls objects in that the administrator never asked for.
    /// </exception>
    public static SyncRule Materialise(
        SyncRule storedRule,
        SyncRuleScopingProposal proposal,
        IReadOnlyCollection<ConnectedSystemObjectTypeAttribute> connectedSystemAttributes,
        IReadOnlyCollection<MetaverseAttribute> metaverseAttributes)
    {
        ArgumentNullException.ThrowIfNull(storedRule);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(connectedSystemAttributes);
        ArgumentNullException.ThrowIfNull(metaverseAttributes);

        var connectedSystemAttributesById = connectedSystemAttributes.ToDictionary(attribute => attribute.Id);
        var metaverseAttributesById = metaverseAttributes.ToDictionary(attribute => attribute.Id);

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

        foreach (var group in proposal.CriteriaGroups)
            standIn.ObjectScopingCriteriaGroups.Add(MaterialiseGroup(group, connectedSystemAttributesById, metaverseAttributesById));

        return standIn;
    }

    private static SyncRuleScopingCriteriaGroup MaterialiseGroup(
        SyncRuleScopingCriteriaGroupProposal proposal,
        IReadOnlyDictionary<int, ConnectedSystemObjectTypeAttribute> connectedSystemAttributesById,
        IReadOnlyDictionary<int, MetaverseAttribute> metaverseAttributesById)
    {
        var group = new SyncRuleScopingCriteriaGroup { Type = proposal.Type };

        foreach (var criterion in proposal.Criteria)
            group.Criteria.Add(MaterialiseCriterion(criterion, connectedSystemAttributesById, metaverseAttributesById));

        foreach (var childGroup in proposal.ChildGroups)
            group.ChildGroups.Add(MaterialiseGroup(childGroup, connectedSystemAttributesById, metaverseAttributesById));

        return group;
    }

    private static SyncRuleScopingCriteria MaterialiseCriterion(
        SyncRuleScopingCriterionProposal proposal,
        IReadOnlyDictionary<int, ConnectedSystemObjectTypeAttribute> connectedSystemAttributesById,
        IReadOnlyDictionary<int, MetaverseAttribute> metaverseAttributesById)
    {
        var criterion = new SyncRuleScopingCriteria
        {
            ComparisonType = proposal.ComparisonType,
            StringValue = proposal.StringValue,
            IntValue = proposal.IntValue,
            LongValue = proposal.LongValue,
            DecimalValue = proposal.DecimalValue,
            DateTimeValue = proposal.DateTimeValue,
            BoolValue = proposal.BoolValue,
            GuidValue = proposal.GuidValue,
            CaseSensitive = proposal.CaseSensitive,
            ValueMode = proposal.ValueMode,
            RelativeCount = proposal.RelativeCount,
            RelativeUnit = proposal.RelativeUnit,
            RelativeDirection = proposal.RelativeDirection
        };

        if (proposal.ConnectedSystemAttributeId is { } connectedSystemAttributeId)
        {
            if (!connectedSystemAttributesById.TryGetValue(connectedSystemAttributeId, out var connectedSystemAttribute))
            {
                throw new InvalidOperationException(
                    $"A proposed scoping criterion names Connected System attribute {connectedSystemAttributeId}, which is " +
                    "not an attribute of this Synchronisation Rule's Connected System Object Type.");
            }

            criterion.ConnectedSystemAttribute = connectedSystemAttribute;
            criterion.ConnectedSystemAttributeId = connectedSystemAttributeId;
            return criterion;
        }

        if (proposal.MetaverseAttributeId is { } metaverseAttributeId)
        {
            if (!metaverseAttributesById.TryGetValue(metaverseAttributeId, out var metaverseAttribute))
            {
                throw new InvalidOperationException(
                    $"A proposed scoping criterion names Metaverse Attribute {metaverseAttributeId}, which is not an " +
                    "attribute of this Synchronisation Rule's Metaverse Object Type.");
            }

            criterion.MetaverseAttribute = metaverseAttribute;
            criterion.MetaverseAttributeId = metaverseAttributeId;
            return criterion;
        }

        throw new InvalidOperationException(
            "A proposed scoping criterion names no attribute. A criterion with nothing to read cannot be evaluated, " +
            "and evaluating around it would report a wider scope than the proposal describes.");
    }
}

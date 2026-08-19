// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// Turns a proposed mapping set back into a Synchronisation Rule the flow evaluation can be asked about (#1437),
/// so an Attribute Flow preview puts its questions to the engine rather than reimplementing any part of it.
/// </summary>
/// <remarks>
/// Attribute ENTITIES are attached, not just their ids, for the same reason the scope materialiser attaches them:
/// the evaluation reads the entity (its data type decides how a value is written, its name is what a delta row
/// says), and a mapping whose target navigation is null contributes nothing while looking well formed. Here the
/// failure is quieter still than in scope evaluation, because a mapping that writes nothing produces no error and
/// no delta: the preview would simply report that the change does nothing.
/// </remarks>
internal static class SyncRuleAttributeFlowProposalMaterialiser
{
    /// <summary>
    /// A copy of <paramref name="storedRule"/> carrying <paramref name="proposal"/>'s mappings instead of its own,
    /// with every target and source attribute entity attached. Everything else about the rule, its Scoping
    /// Criteria included, comes across unchanged: the proposal concerns what flows, not to which objects.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A mapping or source names an attribute that is not in the supplied lookups, or a mapping names no target.
    /// Thrown rather than dropped: a dropped mapping evaluates as though the administrator had removed it, which
    /// reports a withdrawal they never proposed.
    /// </exception>
    public static SyncRule Materialise(
        SyncRule storedRule,
        SyncRuleAttributeFlowProposal proposal,
        IReadOnlyCollection<ConnectedSystemObjectTypeAttribute> connectedSystemAttributes,
        IReadOnlyCollection<MetaverseAttribute> metaverseAttributes)
    {
        ArgumentNullException.ThrowIfNull(storedRule);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(connectedSystemAttributes);
        ArgumentNullException.ThrowIfNull(metaverseAttributes);

        var connectedSystemAttributesById = connectedSystemAttributes.ToDictionary(attribute => attribute.Id);
        var metaverseAttributesById = metaverseAttributes.ToDictionary(attribute => attribute.Id);

        var standIn = SyncRuleStandIn.CloneOf(storedRule);
        standIn.AttributeFlowRules.Clear();

        foreach (var mapping in proposal.Mappings)
        {
            standIn.AttributeFlowRules.Add(
                MaterialiseMapping(standIn, mapping, connectedSystemAttributesById, metaverseAttributesById));
        }

        return standIn;
    }

    private static SyncRuleMapping MaterialiseMapping(
        SyncRule standIn,
        SyncRuleMappingProposal proposal,
        IReadOnlyDictionary<int, ConnectedSystemObjectTypeAttribute> connectedSystemAttributesById,
        IReadOnlyDictionary<int, MetaverseAttribute> metaverseAttributesById)
    {
        var mapping = new SyncRuleMapping
        {
            SyncRule = standIn,
            SyncRuleId = standIn.Id,
            InboundValueProcessing = proposal.InboundValueProcessing,
            CaseNormalisation = proposal.CaseNormalisation,
            Priority = proposal.Priority,
            NullIsValue = proposal.NullIsValue,
            InitialExportOnly = proposal.InitialExportOnly
        };

        if (proposal.TargetMetaverseAttributeId is { } targetMetaverseAttributeId)
        {
            mapping.TargetMetaverseAttribute = Resolve(metaverseAttributesById, targetMetaverseAttributeId,
                "target Metaverse Attribute", "this Synchronisation Rule's Metaverse Object Type");
            mapping.TargetMetaverseAttributeId = targetMetaverseAttributeId;
        }
        else if (proposal.TargetConnectedSystemAttributeId is { } targetConnectedSystemAttributeId)
        {
            mapping.TargetConnectedSystemAttribute = Resolve(connectedSystemAttributesById, targetConnectedSystemAttributeId,
                "target Connected System attribute", "this Synchronisation Rule's Connected System Object Type");
            mapping.TargetConnectedSystemAttributeId = targetConnectedSystemAttributeId;
        }
        else
        {
            throw new InvalidOperationException(
                "A proposed Attribute Flow mapping names no target attribute. A mapping with nowhere to write " +
                "cannot be evaluated, and skipping it would report the flow it replaces as withdrawn.");
        }

        foreach (var source in proposal.Sources.OrderBy(source => source.Order))
            mapping.Sources.Add(MaterialiseSource(source, connectedSystemAttributesById, metaverseAttributesById));

        return mapping;
    }

    private static SyncRuleMappingSource MaterialiseSource(
        SyncRuleMappingSourceProposal proposal,
        IReadOnlyDictionary<int, ConnectedSystemObjectTypeAttribute> connectedSystemAttributesById,
        IReadOnlyDictionary<int, MetaverseAttribute> metaverseAttributesById)
    {
        var source = new SyncRuleMappingSource
        {
            Order = proposal.Order,
            Expression = proposal.Expression,
            MissingInputBehaviour = proposal.MissingInputBehaviour
        };

        if (proposal.ConnectedSystemAttributeId is { } connectedSystemAttributeId)
        {
            source.ConnectedSystemAttribute = Resolve(connectedSystemAttributesById, connectedSystemAttributeId,
                "source Connected System attribute", "this Synchronisation Rule's Connected System Object Type");
            source.ConnectedSystemAttributeId = connectedSystemAttributeId;
        }

        if (proposal.MetaverseAttributeId is { } metaverseAttributeId)
        {
            source.MetaverseAttribute = Resolve(metaverseAttributesById, metaverseAttributeId,
                "source Metaverse Attribute", "this Synchronisation Rule's Metaverse Object Type");
            source.MetaverseAttributeId = metaverseAttributeId;
        }

        return source;
    }

    private static T Resolve<T>(IReadOnlyDictionary<int, T> attributesById, int attributeId, string role, string owner)
    {
        if (!attributesById.TryGetValue(attributeId, out var attribute))
        {
            throw new InvalidOperationException(
                $"A proposed Attribute Flow mapping names {role} {attributeId}, which is not an attribute of {owner}.");
        }

        return attribute;
    }
}

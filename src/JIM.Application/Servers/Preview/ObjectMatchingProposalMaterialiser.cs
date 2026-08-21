// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Staging;

namespace JIM.Application.Servers.Preview;

/// <summary>
/// Turns a proposed Object Matching Rule back into the entity the matching engine can be asked about (#1457), so a
/// matching preview puts its questions to the engine's own implementation rather than reimplementing any part of
/// it.
/// </summary>
/// <remarks>
/// The matching query reads the attribute ENTITIES, not their ids: it needs the source attribute's data type to
/// pick a comparison, and it filters Metaverse Objects on the target attribute's id read off the navigation. A
/// stand-in carrying only ids would therefore match nothing while looking perfectly well formed, and the preview
/// would report every object losing its match.
/// </remarks>
internal static class ObjectMatchingProposalMaterialiser
{
    /// <summary>
    /// <paramref name="proposal"/> as a rule the matching engine can evaluate.
    /// </summary>
    /// <param name="proposal">The proposed rule.</param>
    /// <param name="connectedSystemAttributes">The Connected System attributes a source may name, by id.</param>
    /// <param name="metaverseAttributes">The Metaverse Attributes a target may name, by id.</param>
    /// <param name="metaverseObjectTypes">The Metaverse Object Types a rule may search, by id.</param>
    /// <param name="fallbackMetaverseObjectType">
    /// The type to search when the rule names none: the owning Synchronisation Rule's type in Advanced mode. Null
    /// in Simple mode, where a rule naming no type searches nothing and the engine skips it.
    /// </param>
    public static ObjectMatchingRule Materialise(
        ObjectMatchingRuleProposal proposal,
        IReadOnlyDictionary<int, ConnectedSystemObjectTypeAttribute> connectedSystemAttributes,
        IReadOnlyDictionary<int, MetaverseAttribute> metaverseAttributes,
        IReadOnlyDictionary<int, MetaverseObjectType> metaverseObjectTypes,
        MetaverseObjectType? fallbackMetaverseObjectType = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(connectedSystemAttributes);
        ArgumentNullException.ThrowIfNull(metaverseAttributes);
        ArgumentNullException.ThrowIfNull(metaverseObjectTypes);

        var metaverseObjectType = proposal.MetaverseObjectTypeId is { } typeId
            ? metaverseObjectTypes.GetValueOrDefault(typeId)
            : null;
        metaverseObjectType ??= fallbackMetaverseObjectType;

        var rule = new ObjectMatchingRule
        {
            Order = proposal.Order,
            ConnectedSystemObjectTypeId = proposal.ConnectedSystemObjectTypeId,
            SyncRuleId = proposal.SyncRuleId,
            CaseSensitive = proposal.CaseSensitive,
            MetaverseObjectTypeId = metaverseObjectType?.Id,
            MetaverseObjectType = metaverseObjectType,
            TargetMetaverseAttributeId = proposal.TargetMetaverseAttributeId,
            TargetMetaverseAttribute = proposal.TargetMetaverseAttributeId is { } targetId
                ? metaverseAttributes.GetValueOrDefault(targetId)
                : null,
            Sources = [.. proposal.Sources.OrderBy(source => source.Order).Select(source => new ObjectMatchingRuleSource
            {
                Order = source.Order,
                Expression = source.Expression,
                ConnectedSystemAttributeId = source.ConnectedSystemAttributeId,
                ConnectedSystemAttribute = source.ConnectedSystemAttributeId is { } sourceId
                    ? connectedSystemAttributes.GetValueOrDefault(sourceId)
                    : null
            })]
        };

        return rule;
    }
}

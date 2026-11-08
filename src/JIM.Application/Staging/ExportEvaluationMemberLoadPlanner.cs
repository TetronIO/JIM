// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Staging;

/// <summary>
/// Decides which of a page's target Connected System Objects' multi-valued reference attributes export evaluation can
/// load in part (#2832): only the values naming the members the page's changes add or remove, rather than every member a
/// large group holds.
/// </summary>
/// <remarks>
/// <para>
/// Export staging stages a membership change as one Add or Remove per member that changed, each naming the member's
/// Metaverse Object, and no-net-change detection asks of each only whether the target holds that member. Those values
/// answer it exactly. An attribute qualifies only when everything that could ask about it asks that: every enabled
/// mapping into it, across every export rule of the object's type for the target's Connected System, is a direct flow
/// from a multi-valued reference Metaverse Attribute. A mapping with an expression or a generated value, or from a
/// single-valued source (an Update meaning the whole set), reads the whole held set, so its attribute is loaded whole.
/// </para>
/// <para>
/// A Create never reads the held values, and an evaluation that changes none of an attribute's members asks nothing
/// about it, so such an attribute is planned with no Metaverse Objects and none of its values is loaded.
/// </para>
/// </remarks>
public static class ExportEvaluationMemberLoadPlanner
{
    /// <summary>
    /// The member questions for a page: one per target object and qualifying attribute, naming the Metaverse Objects the
    /// page's changes to the attribute's sources add or remove.
    /// </summary>
    /// <param name="cache">The export evaluation cache, with the page's <see cref="ExportEvaluationCache.CsoLookup"/>
    /// already loaded.</param>
    /// <param name="evaluations">The page's export evaluations: each Metaverse Object with the values it changed.</param>
    public static List<ExportEvaluationMemberQuestion> Plan(
        ExportEvaluationCache cache,
        IEnumerable<(MetaverseObject Mvo, IReadOnlyCollection<MetaverseObjectAttributeValue> ChangedAttributes)> evaluations)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(evaluations);

        var membersByPair = new Dictionary<(Guid CsoId, int AttributeId), HashSet<Guid>>();
        var qualifyingByTypeAndSystem = new Dictionary<(int MvoTypeId, int ConnectedSystemId), Dictionary<int, HashSet<int>>>();

        foreach (var (mvo, changedAttributes) in evaluations)
        {
            if (mvo.Id == Guid.Empty || mvo.Type == null || !cache.ExportRulesByMvoTypeId.TryGetValue(mvo.Type.Id, out var rules))
                continue;

            foreach (var systemRules in rules.GroupBy(rule => rule.ConnectedSystemId))
            {
                if (!cache.CsoLookup.TryGetValue((mvo.Id, systemRules.Key), out var target) || target.Id == Guid.Empty)
                    continue;

                if (!qualifyingByTypeAndSystem.TryGetValue((mvo.Type.Id, systemRules.Key), out var qualifying))
                {
                    qualifying = QualifyingAttributes(systemRules);
                    qualifyingByTypeAndSystem[(mvo.Type.Id, systemRules.Key)] = qualifying;
                }

                foreach (var (attributeId, sourceAttributeIds) in qualifying)
                {
                    if (!membersByPair.TryGetValue((target.Id, attributeId), out var members))
                    {
                        members = [];
                        membersByPair[(target.Id, attributeId)] = members;
                    }

                    members.UnionWith(changedAttributes
                        .Where(av => sourceAttributeIds.Contains(av.AttributeId) && !av.NullValue)
                        .Select(av => av.ReferenceValue?.Id ?? av.ReferenceValueId)
                        .OfType<Guid>());
                }
            }
        }

        return membersByPair
            .Select(pair => new ExportEvaluationMemberQuestion
            {
                ConnectedSystemObjectId = pair.Key.CsoId,
                AttributeId = pair.Key.AttributeId,
                MetaverseObjectIds = pair.Value
            })
            .ToList();
    }

    /// <summary>
    /// The target attributes of one Connected System's export rules for one Metaverse Object Type that qualify, each with
    /// the Metaverse Attributes that flow into it.
    /// </summary>
    internal static Dictionary<int, HashSet<int>> QualifyingAttributes(IEnumerable<SyncRule> rulesForSystem)
    {
        var qualifying = new Dictionary<int, HashSet<int>>();
        var disqualified = new HashSet<int>();

        foreach (var mapping in rulesForSystem.SelectMany(rule => rule.AttributeFlowRules).Where(m => m.Enabled))
        {
            var target = mapping.TargetConnectedSystemAttribute;
            if (target is not { Type: AttributeDataType.Reference, AttributePlurality: AttributePlurality.MultiValued })
                continue;

            if (!IsDirectMultiValuedReferenceFlow(mapping))
            {
                disqualified.Add(target.Id);
                continue;
            }

            if (!qualifying.TryGetValue(target.Id, out var sources))
            {
                sources = [];
                qualifying[target.Id] = sources;
            }

            sources.UnionWith(mapping.Sources.Select(source => source.MetaverseAttribute!.Id));
        }

        foreach (var attributeId in disqualified)
            qualifying.Remove(attributeId);

        return qualifying;
    }

    /// <summary>
    /// Whether a mapping stages a multi-valued reference attribute only as an Add or Remove per member that changed: a
    /// direct flow, no expression and no generated value, from multi-valued reference Metaverse Attributes.
    /// </summary>
    private static bool IsDirectMultiValuedReferenceFlow(SyncRuleMapping mapping) =>
        mapping.Generation == null &&
        mapping.Sources.Count > 0 &&
        mapping.Sources.All(source =>
            string.IsNullOrWhiteSpace(source.Expression) &&
            source.MetaverseAttribute is { Type: AttributeDataType.Reference, AttributePlurality: AttributePlurality.MultiValued });
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Services;

/// <summary>
/// Computes the order a synchronisation pages through a Connected System's Object Types (#2492). Pure: it performs no
/// I/O and works from the Object Types and Synchronisation Rules the run already holds.
/// </summary>
/// <remarks>
/// <para>
/// A type depends on every type one of its <em>mapped</em> reference attributes can point at: an attribute an import
/// Synchronisation Rule flows, from a Connected System attribute source, to a Metaverse reference attribute. That is
/// exactly the set the page loop checks before deferring an object to the cross-page reference step, so ordering by
/// it removes the deferrals that ordering can remove, and no others.
/// </para>
/// <para>
/// Where the schema declares the attribute's target (<see cref="ConnectedSystemObjectTypeAttribute.ReferencedObjectTypeId"/>)
/// that is the only target; where it does not (LDAP <c>member</c>), the attribute may point at any of the types being
/// ordered, its own included. Types that reach one another form one group (a strongly connected component); groups
/// are ordered dependencies first, ties broken by lowest type id, and types within a group by id, so the order is
/// deterministic for a given configuration.
/// </para>
/// <para>
/// Ordering by schema alone is too pessimistic for LDAP, whose <c>member</c> and <c>manager</c> declare nothing, so
/// a caller can pass the types those attributes' stored values are observed to point at; a Full Synchronisation does,
/// at the cost of one aggregate query.
/// </para>
/// <para>
/// A reference path the rules do not show (an expression producing a reference, for instance) is not ordered for.
/// That costs time, never correctness: the cross-page step still resolves whatever the page loop could not.
/// </para>
/// </remarks>
public static class SyncObjectTypeOrdering
{
    /// <summary>
    /// Computes the paging order.
    /// </summary>
    /// <param name="objectTypes">Every Object Type to page through, with its attributes loaded.</param>
    /// <param name="syncRules">The run's Synchronisation Rules; only import rules for these types are considered.</param>
    /// <param name="observedTargetTypeIds">For attributes whose schema declares no target type: the types their stored
    /// values actually point at, by attribute id (see <see cref="GetUndeclaredMappedReferenceAttributeIds"/>). An
    /// attribute present here takes exactly these targets, none if its set is empty; one absent is assumed to point
    /// at every type. Without it, LDAP's undeclared <c>member</c> and <c>manager</c> make people and groups reach
    /// each other, and the two collapse into one group that pages in plain id order.</param>
    public static SyncObjectTypeOrder Compute(
        IEnumerable<ConnectedSystemObjectType> objectTypes,
        IEnumerable<SyncRule> syncRules,
        IReadOnlyDictionary<int, IReadOnlySet<int>>? observedTargetTypeIds = null)
    {
        var types = objectTypes.DistinctBy(t => t.Id).OrderBy(t => t.Id).ToList();
        var typeIds = types.Select(t => t.Id).ToList();
        var typeIdSet = typeIds.ToHashSet();

        var edges = typeIds.ToDictionary(id => id, _ => new SortedSet<int>());
        foreach (var (referencingTypeId, attributeId, attribute) in MappedReferenceSources(types, syncRules))
        {
            var declaredTargetTypeId = attribute?.ReferencedObjectTypeId;
            if (declaredTargetTypeId.HasValue)
            {
                if (typeIdSet.Contains(declaredTargetTypeId.Value))
                    edges[referencingTypeId].Add(declaredTargetTypeId.Value);
            }
            else if (observedTargetTypeIds != null && observedTargetTypeIds.TryGetValue(attributeId, out var observed))
            {
                edges[referencingTypeId].UnionWith(observed.Where(typeIdSet.Contains));
            }
            else
            {
                edges[referencingTypeId].UnionWith(typeIds);
            }
        }

        var selfReferencingTypeIds = typeIds.Where(id => edges[id].Contains(id)).ToHashSet();
        var components = FindStronglyConnectedComponents(typeIds, edges);
        var groups = OrderComponents(components, edges);

        return new SyncObjectTypeOrder(groups, selfReferencingTypeIds);
    }

    /// <summary>
    /// The mapped reference attributes whose schema declares no target type: the ones worth asking the data about
    /// before <see cref="Compute"/>.
    /// </summary>
    public static IReadOnlySet<int> GetUndeclaredMappedReferenceAttributeIds(
        IEnumerable<ConnectedSystemObjectType> objectTypes, IEnumerable<SyncRule> syncRules)
    {
        var types = objectTypes.DistinctBy(t => t.Id).ToList();
        return MappedReferenceSources(types, syncRules)
            .Where(source => source.Attribute?.ReferencedObjectTypeId == null)
            .Select(source => source.AttributeId)
            .ToHashSet();
    }

    /// <summary>
    /// Every (referencing type, attribute) an import rule flows from a Connected System attribute to a Metaverse
    /// reference attribute, the attribute resolved from the source or the type's schema (null if neither has it).
    /// </summary>
    private static IEnumerable<(int ReferencingTypeId, int AttributeId, ConnectedSystemObjectTypeAttribute? Attribute)> MappedReferenceSources(
        List<ConnectedSystemObjectType> types, IEnumerable<SyncRule> syncRules)
    {
        var typeIdSet = types.Select(t => t.Id).ToHashSet();
        var attributesById = types
            .SelectMany(t => t.Attributes)
            .GroupBy(a => a.Id)
            .ToDictionary(g => g.Key, g => g.First());

        return syncRules
            .Where(r => r.Direction == SyncRuleDirection.Import && typeIdSet.Contains(r.ConnectedSystemObjectTypeId))
            .SelectMany(rule => rule.AttributeFlowRules
                .Where(m => m.TargetMetaverseAttribute?.Type == AttributeDataType.Reference)
                .SelectMany(m => m.Sources)
                .Where(s => s.ConnectedSystemAttributeId.HasValue)
                .Select(s => (rule.ConnectedSystemObjectTypeId, s.ConnectedSystemAttributeId!.Value,
                    s.ConnectedSystemAttribute ?? attributesById.GetValueOrDefault(s.ConnectedSystemAttributeId!.Value))));
    }

    /// <summary>
    /// Tarjan's algorithm. Iterates the (small) type set recursively; a Connected System has tens of types at most.
    /// </summary>
    private static List<List<int>> FindStronglyConnectedComponents(List<int> typeIds, Dictionary<int, SortedSet<int>> edges)
    {
        var index = 0;
        var indices = new Dictionary<int, int>();
        var lowLinks = new Dictionary<int, int>();
        var stack = new Stack<int>();
        var onStack = new HashSet<int>();
        var components = new List<List<int>>();

        void StrongConnect(int v)
        {
            indices[v] = index;
            lowLinks[v] = index;
            index++;
            stack.Push(v);
            onStack.Add(v);

            foreach (var w in edges[v])
            {
                if (!indices.ContainsKey(w))
                {
                    StrongConnect(w);
                    lowLinks[v] = Math.Min(lowLinks[v], lowLinks[w]);
                }
                else if (onStack.Contains(w))
                {
                    lowLinks[v] = Math.Min(lowLinks[v], indices[w]);
                }
            }

            if (lowLinks[v] != indices[v])
                return;

            var component = new List<int>();
            int member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            } while (member != v);

            component.Sort();
            components.Add(component);
        }

        foreach (var id in typeIds.Where(id => !indices.ContainsKey(id)))
            StrongConnect(id);

        return components;
    }

    /// <summary>
    /// Kahn's algorithm over the component graph: a component is ready once every component it references has been
    /// placed; among ready components the one with the lowest type id goes first.
    /// </summary>
    private static List<IReadOnlyList<int>> OrderComponents(List<List<int>> components, Dictionary<int, SortedSet<int>> edges)
    {
        var componentOf = new Dictionary<int, int>();
        for (var c = 0; c < components.Count; c++)
        {
            foreach (var typeId in components[c])
                componentOf[typeId] = c;
        }

        // dependsOn[c]: the other components c references; dependents[d]: the components referencing d.
        var dependsOn = Enumerable.Range(0, components.Count).Select(_ => new HashSet<int>()).ToList();
        var dependents = Enumerable.Range(0, components.Count).Select(_ => new HashSet<int>()).ToList();
        foreach (var (typeId, targets) in edges)
        {
            var from = componentOf[typeId];
            foreach (var to in targets.Select(t => componentOf[t]).Where(to => to != from))
            {
                dependsOn[from].Add(to);
                dependents[to].Add(from);
            }
        }

        var remaining = dependsOn.Select(d => d.Count).ToList();
        var ready = new PriorityQueue<int, int>();
        for (var c = 0; c < components.Count; c++)
        {
            if (remaining[c] == 0)
                ready.Enqueue(c, components[c][0]);
        }

        var ordered = new List<IReadOnlyList<int>>(components.Count);
        while (ready.TryDequeue(out var c, out _))
        {
            ordered.Add(components[c]);
            foreach (var dependent in dependents[c])
            {
                remaining[dependent]--;
                if (remaining[dependent] == 0)
                    ready.Enqueue(dependent, components[dependent][0]);
            }
        }

        return ordered;
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Application.Services;

/// <summary>
/// Finds the Metaverse-Derived Attribute Flows a configuration change leaves with a missing input (#1750, FR 3), after
/// the <see cref="SchemaRefreshDependentDetector"/> pattern: pure, static, and handed the configuration before and after
/// the change rather than reading anything itself, so the Configuration Change Preview, the deletion and settings-update
/// responses and schema refresh can all ask the same question of their own change.
/// </summary>
public static class DerivedFlowDependentDetector
{
    /// <summary>
    /// Lists every enabled derived flow in <paramref name="syncRulesAfter"/> that reads, directly or transitively, a
    /// Metaverse attribute the change leaves with no enabled contributor able to supply a value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A contributor is an enabled import mapping, on an enabled import Synchronisation Rule, targeting the attribute
    /// (for the rule's Metaverse Object Type). An attribute is <em>starved</em> when it had at least one contributor
    /// before the change and, after it, every contributor it has left is a derived flow that is itself missing an
    /// input (none at all being the direct case). An attribute with no contributor before the change is not reported:
    /// its dependants were already missing that input, so the change is not what caused it.
    /// </para>
    /// <para>
    /// Worked to a fixed point, so a chain (User Principal Name reads Email, Email reads Account Name) is followed as
    /// far as it goes, and stops wherever an attribute keeps a contributor that still has its inputs. A derived flow the
    /// change removes or disables is not a dependant: it will not run.
    /// </para>
    /// </remarks>
    /// <param name="syncRulesBefore">The Synchronisation Rules as they stand today (any direction; disabled included).</param>
    /// <param name="syncRulesAfter">The same rules with the change applied.</param>
    /// <param name="metaverseObjectTypes">The Metaverse Object Types the rules flow to, with their attributes, for resolving
    /// <c>mv["..."]</c> names; a rule whose type is absent falls back to its own navigation.</param>
    /// <returns>The dependants ordered by Metaverse Object Type, Synchronisation Rule, target attribute and mapping.</returns>
    public static IReadOnlyList<DerivedFlowDependent> Detect(
        IEnumerable<SyncRule> syncRulesBefore,
        IEnumerable<SyncRule> syncRulesAfter,
        IEnumerable<MetaverseObjectType> metaverseObjectTypes)
    {
        ArgumentNullException.ThrowIfNull(syncRulesBefore);
        ArgumentNullException.ThrowIfNull(syncRulesAfter);
        ArgumentNullException.ThrowIfNull(metaverseObjectTypes);

        var after = syncRulesAfter.ToList();
        var contributedBefore = GetContributors(syncRulesBefore).Keys.ToHashSet();
        var contributorsAfter = GetContributors(after);

        // The derived flows that will run after the change: enabled mappings on enabled rules.
        var graph = new DerivedFlowGraph(after, metaverseObjectTypes, DerivedFlowGraphScope.EnabledMappingsOnly);
        var derivedFlows = after
            .Where(rule => rule.Direction == SyncRuleDirection.Import && rule.Enabled)
            .SelectMany(rule => rule.AttributeFlowRules.Where(mapping => mapping.Enabled))
            .Select(graph.GetDerivedFlow)
            .Where(flow => flow != null)
            .Select(flow => flow!)
            .ToList();

        // Fixed point: starving an attribute can make a derived flow a dependant, which can starve the attribute that
        // flow contributes, and so on down the chain.
        var starved = new Dictionary<(int TypeId, int AttributeId), bool>();
        var dependants = new HashSet<SyncRuleMapping>(ReferenceEqualityComparer.Instance);
        bool changed;
        do
        {
            changed = false;
            // Only attributes that had a contributor before the change: one that had none was already missing, and
            // the change is not what caused it.
            foreach (var key in contributedBefore)
            {
                if (starved.ContainsKey(key))
                    continue;

                var remaining = contributorsAfter.GetValueOrDefault(key) ?? [];
                if (remaining.Any(mapping => !dependants.Contains(mapping)))
                    continue;

                // No contributor at all is the direct case; otherwise every one left is a derived flow already
                // known to be missing an input.
                starved[key] = remaining.Count > 0;
                changed = true;
            }

            foreach (var flow in derivedFlows.Where(flow => !dependants.Contains(flow.Mapping)))
            {
                if (flow.Inputs.Any(input => starved.ContainsKey((flow.MetaverseObjectTypeId, input.Id))))
                {
                    dependants.Add(flow.Mapping);
                    changed = true;
                }
            }
        }
        while (changed);

        return derivedFlows
            .Where(flow => dependants.Contains(flow.Mapping))
            .OrderBy(flow => flow.MetaverseObjectTypeId)
            .ThenBy(flow => flow.SyncRule.Id)
            .ThenBy(flow => flow.TargetAttributeId)
            .ThenBy(flow => flow.Mapping.Id)
            .Select(flow => new DerivedFlowDependent(flow, flow.Inputs
                .Where(input => starved.ContainsKey((flow.MetaverseObjectTypeId, input.Id)))
                .Select(input => new DerivedFlowMissingInput(input.Id, input.Name, starved[(flow.MetaverseObjectTypeId, input.Id)]))
                .ToList())
            {
                LostInputs = FindLostInputs(flow, starved, contributorsAfter, graph)
            })
            .ToList();
    }

    /// <summary>
    /// The attributes at the root of a dependant's missing inputs: those left with no enabled contributor at all,
    /// found breadth-first from the flow's own inputs through the derived contributors of every attribute starved
    /// only transitively, so each root is named once at its shortest chain, nearest first.
    /// </summary>
    private static List<DerivedFlowLostInput> FindLostInputs(
        DerivedFlow flow,
        Dictionary<(int TypeId, int AttributeId), bool> starved,
        Dictionary<(int TypeId, int AttributeId), List<SyncRuleMapping>> contributorsAfter,
        DerivedFlowGraph graph)
    {
        var typeId = flow.MetaverseObjectTypeId;
        var lost = new List<DerivedFlowLostInput>();
        var visited = new HashSet<int>();
        var frontier = new Queue<(MetaverseAttribute Attribute, List<string> Via)>();
        foreach (var input in flow.Inputs.Where(input => starved.ContainsKey((typeId, input.Id)) && visited.Add(input.Id)))
            frontier.Enqueue((input, []));

        while (frontier.TryDequeue(out var current))
        {
            if (!starved[(typeId, current.Attribute.Id)])
            {
                lost.Add(new DerivedFlowLostInput(current.Attribute.Id, current.Attribute.Name, current.Via));
                continue;
            }

            // Starved only transitively: every contributor it has left is a derived flow itself missing an input, so
            // follow their starved inputs one step further down the chain.
            var via = current.Via.Append(current.Attribute.Name).ToList();
            var next = (contributorsAfter.GetValueOrDefault((typeId, current.Attribute.Id)) ?? [])
                .Select(graph.GetDerivedFlow)
                .Where(contributor => contributor != null)
                .SelectMany(contributor => contributor!.Inputs)
                .Where(input => starved.ContainsKey((typeId, input.Id)) && visited.Add(input.Id));
            foreach (var input in next)
                frontier.Enqueue((input, via));
        }

        return lost;
    }

    /// <summary>
    /// The enabled contributors of each (Metaverse Object Type, Metaverse attribute): enabled import mappings on
    /// enabled import rules that target it.
    /// </summary>
    private static Dictionary<(int TypeId, int AttributeId), List<SyncRuleMapping>> GetContributors(IEnumerable<SyncRule> syncRules)
    {
        var contributors = new Dictionary<(int TypeId, int AttributeId), List<SyncRuleMapping>>();
        foreach (var rule in syncRules.Where(rule => rule.Direction == SyncRuleDirection.Import && rule.Enabled))
        {
            foreach (var mapping in rule.AttributeFlowRules.Where(mapping => mapping.Enabled))
            {
                if ((mapping.TargetMetaverseAttributeId ?? mapping.TargetMetaverseAttribute?.Id) is not { } targetId)
                    continue;

                var key = (rule.MetaverseObjectTypeId, targetId);
                if (!contributors.TryGetValue(key, out var list))
                {
                    list = [];
                    contributors[key] = list;
                }
                list.Add(mapping);
            }
        }

        return contributors;
    }
}

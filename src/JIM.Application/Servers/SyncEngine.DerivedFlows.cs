// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// The derived pass of Metaverse-Derived Attribute Flows (#1750, plan Phase 2): import mappings whose expression reads
/// <c>mv["..."]</c> are excluded from the ordinary inbound pass (<see cref="FlowInboundAttributes"/>) and evaluated
/// here instead, level by level over the run's <see cref="DerivedFlowGraph"/>, against the Metaverse Object's
/// effective values as of this pass. Each derived mapping goes through <see cref="ProcessMapping"/> unchanged (plan
/// decision 6), so the Attribute Priority gate, the #1199 clean-up, "Null is a value", provenance and generation
/// requests behave exactly as for any other contribution: no new writer, outcome or causality member. Pure: no I/O.
/// </summary>
public partial class SyncEngine
{
    /// <inheritdoc />
    public List<AttributeFlowError> EvaluateDerivedLevel(
        ConnectedSystemObject cso,
        int level,
        IReadOnlyList<SyncRule> syncRules,
        IReadOnlyList<ConnectedSystemObjectType> objectTypes,
        IExpressionEvaluator? expressionEvaluator,
        AttributePriorityContext priorityContext)
    {
        ArgumentNullException.ThrowIfNull(cso);
        ArgumentNullException.ThrowIfNull(syncRules);
        ArgumentNullException.ThrowIfNull(objectTypes);
        ArgumentNullException.ThrowIfNull(priorityContext);
        ArgumentOutOfRangeException.ThrowIfNegative(level);

        var graph = priorityContext.DerivedFlowGraph
            ?? throw new ArgumentException(
                "The attribute priority context carries no derived flow graph, so there is no derived pass to evaluate. " +
                "Build the context with the graph from DerivedFlowGraphFactory, or do not call the derived pass when it returns null.",
                nameof(priorityContext));

        var errors = new List<AttributeFlowError>();
        var mvo = cso.MetaverseObject;
        if (mvo == null)
        {
            Log.Error("EvaluateDerivedLevel: CSO ({CsoId}) has no MVO!", cso.Id);
            return errors;
        }

        var toEvaluate = FindDerivedMappingsInScope(graph, level, syncRules);
        if (toEvaluate.Count == 0)
            return errors;

        // Built once for the level: a mapping at this level reads only attributes at lower levels, which are settled
        // by now, and never another mapping at the same level, so every mapping here sees the same view. Rebuilt per
        // level, so the next level reads what this one contributed.
        var metaverseAttributes = BuildEffectiveAttributeDictionary(mvo);

        foreach (var (syncRule, mapping) in toEvaluate)
        {
            ProcessMapping(cso, mapping, objectTypes, expressionEvaluator,
                contributingSystemId: cso.ConnectedSystemId,
                errors: errors,
                mvoObjectTypeId: syncRule.MetaverseObjectTypeId,
                priorityContext: priorityContext,
                metaverseAttributes: metaverseAttributes);
        }

        Log.Verbose("EvaluateDerivedLevel: evaluated {Count} derived Attribute Flow(s) at level {Level} for MVO {MvoId} (CSO {CsoId}).",
            toEvaluate.Count, level, mvo.Id, cso.Id);

        return errors;
    }

    /// <inheritdoc />
    public List<AttributeFlowError> EvaluateDerivedLevels(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> syncRules,
        IReadOnlyList<ConnectedSystemObjectType> objectTypes,
        IExpressionEvaluator? expressionEvaluator,
        AttributePriorityContext priorityContext)
    {
        ArgumentNullException.ThrowIfNull(syncRules);
        ArgumentNullException.ThrowIfNull(priorityContext);

        var errors = new List<AttributeFlowError>();
        var graph = priorityContext.DerivedFlowGraph;
        if (graph == null)
            return errors;

        var maxLevel = syncRules
            .Select(rule => rule.MetaverseObjectTypeId)
            .Distinct()
            .Select(graph.MaxLevel)
            .DefaultIfEmpty(0)
            .Max();

        for (var level = 1; level <= maxLevel; level++)
            errors.AddRange(EvaluateDerivedLevel(cso, level, syncRules, objectTypes, expressionEvaluator, priorityContext));

        return errors;
    }

    /// <summary>
    /// The Metaverse Object's effective values as of this pass, for a Metaverse-Derived Attribute Flow's
    /// <c>mv["..."]</c> inputs (#1750, FR 6): persisted values, minus those pending removal, plus those pending
    /// addition, so a value contributed earlier in this same pass is visible before it is persisted. Keyed and typed by
    /// the same code as the export expression dictionary (<see cref="BuildAttributeDictionary(MetaverseObject)"/>),
    /// so a derived flow reading an attribute here sees exactly what an export expression reads once the pending
    /// changes are applied: keys case-insensitive, asserted-null markers absent, the last value winning for a
    /// multi-valued attribute. Pending generation requests (#242) are not values yet and are not included; the
    /// caller resolves them between levels. Does not modify the object.
    /// </summary>
    internal static Dictionary<string, object?> BuildEffectiveAttributeDictionary(MetaverseObject mvo)
    {
        ArgumentNullException.ThrowIfNull(mvo);

        var pendingRemovals = new HashSet<MetaverseObjectAttributeValue>(mvo.PendingAttributeValueRemovals);
        var effectiveValues = mvo.AttributeValues
            .Where(av => !pendingRemovals.Contains(av))
            .Concat(mvo.PendingAttributeValueAdditions);

        return BuildAttributeDictionary(mvo, effectiveValues);
    }

    /// <summary>
    /// The derived mappings the graph places at <paramref name="level"/> that are hosted on <paramref name="syncRules"/>
    /// (the rules in scope for the Connected System Object being processed), in the graph's canonical order, each
    /// paired with the caller's own rule and mapping instances. A derived flow on a rule not in scope is not this
    /// synchronisation's to evaluate: it runs in its hosting system's own synchronisation.
    /// </summary>
    /// <exception cref="InvalidOperationException">An in-scope rule hosts a derived mapping in the graph that the
    /// rule itself does not hold as an enabled mapping: the graph and the rules were built from different
    /// configuration, and evaluating either view would be a guess.</exception>
    private static List<(SyncRule SyncRule, SyncRuleMapping Mapping)> FindDerivedMappingsInScope(
        DerivedFlowGraph graph,
        int level,
        IReadOnlyList<SyncRule> syncRules)
    {
        var inScopeRules = syncRules
            .Where(rule => rule.Direction == SyncRuleDirection.Import && rule.Enabled)
            .ToList();

        return inScopeRules
            .Select(rule => rule.MetaverseObjectTypeId)
            .Distinct()
            .Order()
            .SelectMany(typeId => graph.GetDerivedMappings(typeId, level))
            .Select(graphMapping => (GraphMapping: graphMapping, HostRule: graph.GetDerivedFlow(graphMapping)!.SyncRule))
            .Select(candidate => (candidate.GraphMapping, SyncRule: inScopeRules.FirstOrDefault(rule => IsSameRule(rule, candidate.HostRule))))
            .Where(candidate => candidate.SyncRule != null)
            .Select(candidate => (candidate.SyncRule!, ResolveCallerMapping(candidate.SyncRule!, candidate.GraphMapping)))
            .ToList();
    }

    private static bool IsSameRule(SyncRule rule, SyncRule hostRule) =>
        ReferenceEquals(rule, hostRule) || (rule.Id > 0 && rule.Id == hostRule.Id);

    private static SyncRuleMapping ResolveCallerMapping(SyncRule syncRule, SyncRuleMapping graphMapping)
    {
        var mapping = syncRule.AttributeFlowRules.FirstOrDefault(m =>
            ReferenceEquals(m, graphMapping) || (graphMapping.Id > 0 && m.Id == graphMapping.Id));

        if (mapping is not { Enabled: true } || mapping.TargetMetaverseAttribute == null)
        {
            throw new InvalidOperationException(
                $"The derived flow graph holds Attribute Flow mapping {graphMapping.Id} on Synchronisation Rule {syncRule.Id}, " +
                "but that rule, as supplied for synchronisation, has no such enabled mapping with a target Metaverse attribute. " +
                "The graph and the Synchronisation Rules must be built from the same configuration.");
        }

        return mapping;
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Exceptions;
using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Builds the run-time Metaverse-Derived Attribute Flow graph (#1750) for a synchronisation run, a preview or a
/// server-side recall: the one place the <c>Features.MetaverseDerivedAttributeFlows</c> flag is read at run time
/// (plan decision 12). Every site that builds an <see cref="AttributePriorityContext"/> calls this and hands the
/// result to the context, so the engine itself never consults the flag: a null graph is the engine exactly as it was
/// before the feature.
/// </summary>
public static class DerivedFlowGraphFactory
{
    /// <summary>
    /// Returns the graph of enabled derived mappings (<see cref="DerivedFlowGraphScope.EnabledMappingsOnly"/>) when
    /// the feature is on, or null when it is off.
    /// </summary>
    /// <param name="featureFlags">The unit of work's feature flag server.</param>
    /// <param name="syncRules">Every Synchronisation Rule the run's priority context is built from (all Connected
    /// Systems); only enabled import rules and mappings are considered.</param>
    /// <param name="metaverseObjectTypes">The Metaverse Object Types those rules flow to, with their attributes, for
    /// resolving <c>mv["..."]</c> names. A rule whose type is absent falls back to its own navigation, if loaded.</param>
    /// <exception cref="DerivedFlowCycleException">The feature is on and the enabled derived flows contain a dependency
    /// cycle (plan decision 11). Save-time validation refuses one, so this means a cycle slipped past it; running on
    /// a guessed order would write values nobody asked for, so the run must not start.</exception>
    public static async Task<DerivedFlowGraph?> CreateAsync(
        FeatureFlagServer featureFlags,
        IEnumerable<SyncRule> syncRules,
        IEnumerable<MetaverseObjectType> metaverseObjectTypes)
    {
        ArgumentNullException.ThrowIfNull(featureFlags);
        ArgumentNullException.ThrowIfNull(syncRules);
        ArgumentNullException.ThrowIfNull(metaverseObjectTypes);

        if (!await featureFlags.IsEnabledAsync(FeatureFlagCatalogue.MetaverseDerivedAttributeFlows.Key))
            return null;

        var graph = new DerivedFlowGraph(syncRules, metaverseObjectTypes, DerivedFlowGraphScope.EnabledMappingsOnly);
        if (graph.HasCycle)
        {
            var exception = new DerivedFlowCycleException(DescribeCycles(graph), graph.Cycles);
            Log.Error("DerivedFlowGraphFactory: refusing to evaluate Metaverse-Derived Attribute Flows; {Message}",
                LogSanitiser.Sanitise(exception.Message));
            throw exception;
        }

        Log.Debug("DerivedFlowGraphFactory: built the derived flow graph; {UnknownInputCount} unresolved mv input name(s).",
            graph.UnknownInputs.Count);
        return graph;
    }

    private static string DescribeCycles(DerivedFlowGraph graph)
    {
        var cycles = graph.Cycles.Select(cycle =>
            $"Metaverse Object Type '{graph.GetMetaverseObjectTypeName(cycle.MetaverseObjectTypeId)}': {DerivedFlowValidator.DescribeCyclePath(cycle)}");

        return "The enabled Attribute Flows that derive Metaverse attributes from other Metaverse attributes contain a " +
               "dependency cycle, so no evaluation order exists and synchronisation cannot proceed. " +
               string.Join(" ", cycles) +
               " Disable or change one of the Attribute Flows on the cycle to break it.";
    }
}

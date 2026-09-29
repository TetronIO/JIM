// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Exceptions;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Metaverse-Derived Attribute Flows' save-time surface (#1750, plan Phase 1): the feature-flag gate on an import
/// expression that newly reads <c>mv["..."]</c>, and the dependency-graph validation every mapping save path runs
/// before anything is written.
/// </summary>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// Builds the Attribute Priority context for a server-side recall (Synchronisation Rule deletion recall,
    /// Synchronised Deprovisioning, the stranded value sweep), carrying the run's Metaverse-Derived Attribute Flow
    /// graph (#1750, plan decision 12) exactly as a synchronisation run's does. With the graph attached, contributor
    /// re-election skips derived mappings (decision 5): re-flowed there, a derived flow would read no
    /// <c>mv["..."]</c> inputs at all and write a value nobody configured. Null graph (the feature off) is the
    /// context exactly as before.
    /// </summary>
    /// <exception cref="DerivedFlowCycleException">The enabled derived flows contain a dependency cycle; the recall
    /// fails hard before touching any object (decision 11).</exception>
    private async Task<AttributePriorityContext> BuildRecallPriorityContextAsync(List<SyncRule> allSyncRules)
    {
        var derivedFlowGraph = await DerivedFlowGraphFactory.CreateAsync(Application.FeatureFlags, allSyncRules, []);
        return new AttributePriorityContext(allSyncRules, honourNullAssertions: true, derivedFlowGraph);
    }

    /// <summary>
    /// Builds the derived-input mark collector (#1750, plan Phase 4, FR 9) for a server-side writer outside
    /// synchronisation, from the Synchronisation Rules that will exist once the write completes. A rule being deleted,
    /// or a system being deprovisioned, is left out of <paramref name="survivingSyncRules"/> by the caller: its derived
    /// mappings are going away with it, so they must neither mark its own system nor carry transitivity onwards. Null
    /// graph (the feature off) makes the collector inert.
    /// </summary>
    /// <exception cref="DerivedFlowCycleException">The surviving enabled derived flows contain a cycle (decision 11).</exception>
    private async Task<DerivedInputMarkBatch> CreateDerivedInputMarkBatchAsync(IEnumerable<SyncRule> survivingSyncRules, string writerName)
    {
        var derivedFlowGraph = await DerivedFlowGraphFactory.CreateAsync(Application.FeatureFlags, survivingSyncRules, []);
        return new DerivedInputMarkBatch(derivedFlowGraph, writerName);
    }

    /// <summary>
    /// The single-mapping save paths' entry point (create, update, settings update): gates and validates
    /// <paramref name="mapping"/> as a proposal replacing the persisted mapping with the same id on its rule, and
    /// stamps any warnings onto <see cref="SyncRuleMapping.SaveWarnings"/>.
    /// </summary>
    /// <exception cref="FeatureDisabledException">The flag is off and the mapping's expression newly reads <c>mv</c>.</exception>
    /// <exception cref="DerivedFlowValidationException">The flag is on and the proposal is invalid.</exception>
    private async Task EnsureDerivedFlowAllowedAsync(SyncRuleMapping mapping)
    {
        mapping.SaveWarnings.Clear();

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId;
        if (!ReadsMetaverseAsImportMapping(mapping))
            return;

        var hostRule = mapping.SyncRule
            ?? (syncRuleId > 0 ? await Application.Repository.ConnectedSystems.GetSyncRuleAsync(syncRuleId) : null);
        if (hostRule == null || hostRule.Direction != SyncRuleDirection.Import)
            return;

        await EnsureDerivedFlowProposalAllowedAsync(hostRule, [mapping], persistedRules =>
        {
            // The rule as it will stand after this save: its persisted mappings, with the proposal replacing the one
            // it updates (matched by id) or joining them as a new mapping.
            var persistedHost = syncRuleId > 0 ? persistedRules.FirstOrDefault(rule => rule.Id == syncRuleId) : null;
            var proposedHost = new SyncRule
            {
                Id = syncRuleId,
                Name = string.IsNullOrEmpty(hostRule.Name) ? persistedHost?.Name ?? string.Empty : hostRule.Name,
                Direction = SyncRuleDirection.Import,
                ConnectedSystemId = hostRule.ConnectedSystemId > 0 ? hostRule.ConnectedSystemId : persistedHost?.ConnectedSystemId ?? 0,
                MetaverseObjectTypeId = hostRule.MetaverseObjectTypeId,
                Enabled = hostRule.Enabled
            };
            proposedHost.AttributeFlowRules.AddRange((persistedHost?.AttributeFlowRules ?? [])
                .Where(persisted => mapping.Id == 0 || persisted.Id != mapping.Id));
            proposedHost.AttributeFlowRules.Add(mapping);

            return persistedRules
                .Where(rule => syncRuleId == 0 || rule.Id != syncRuleId)
                .Append(proposedHost)
                .ToList();
        });
    }

    /// <summary>
    /// The whole-rule save's entry point (<c>CreateOrUpdateSyncRuleAsync</c>, the portal's save path): gates and
    /// validates every mapping of <paramref name="syncRule"/> as a proposal replacing the persisted rule wholesale,
    /// and stamps any warnings onto each mapping's <see cref="SyncRuleMapping.SaveWarnings"/>.
    /// </summary>
    /// <exception cref="FeatureDisabledException">The flag is off and a mapping's expression newly reads <c>mv</c>.</exception>
    /// <exception cref="DerivedFlowValidationException">The flag is on and the proposal is invalid.</exception>
    private async Task EnsureDerivedFlowsAllowedAsync(SyncRule syncRule)
    {
        foreach (var mapping in syncRule.AttributeFlowRules)
            mapping.SaveWarnings.Clear();

        if (syncRule.Direction != SyncRuleDirection.Import)
            return;

        await EnsureDerivedFlowProposalAllowedAsync(syncRule, syncRule.AttributeFlowRules, persistedRules => persistedRules
            .Where(rule => syncRule.Id == 0 || rule.Id != syncRule.Id)
            .Append(syncRule)
            .ToList());
    }

    /// <summary>
    /// The shared core. A no-op, with no I/O at all, unless a proposed import mapping reads <c>mv["..."]</c>, so every
    /// other save behaves exactly as before. Otherwise loads every import rule of the Metaverse Object Type (all
    /// Connected Systems, disabled included) and:
    /// <list type="bullet">
    /// <item>flag off: refuses a proposed mapping that did not already read <c>mv</c> before this save (a new
    /// mapping, or one whose persisted expression read none), exactly as the Unique Value Generation gate refuses new
    /// generated configuration; runs no validation at all, so existing configuration is left as it was;</item>
    /// <item>flag on: builds the graph with the proposal substituted (<see cref="DerivedFlowGraphScope.AllMappings"/>),
    /// refuses the save with every error in one message, and stamps each warning onto the mapping it concerns.</item>
    /// </list>
    /// </summary>
    private async Task EnsureDerivedFlowProposalAllowedAsync(
        SyncRule hostRule,
        IReadOnlyCollection<SyncRuleMapping> proposedMappings,
        Func<List<SyncRule>, List<SyncRule>> substituteProposal)
    {
        var readingMetaverse = proposedMappings.Where(ReadsMetaverseAsImportMapping).ToList();
        if (readingMetaverse.Count == 0)
            return;

        var flagKey = FeatureFlagCatalogue.MetaverseDerivedAttributeFlows.Key;
        var enabled = await Application.FeatureFlags.IsEnabledAsync(flagKey);
        var metaverseObjectTypeId = hostRule.MetaverseObjectTypeId;
        var persistedRules = await Application.Repository.ConnectedSystems.GetImportSyncRulesForMetaverseObjectTypeAsync(metaverseObjectTypeId);

        if (!enabled)
        {
            var persistedMappingsById = persistedRules
                .SelectMany(rule => rule.AttributeFlowRules)
                .Where(persisted => persisted.Id > 0)
                .GroupBy(persisted => persisted.Id)
                .ToDictionary(group => group.Key, group => group.First());

            var newlyReadsMetaverse = readingMetaverse.Any(mapping =>
                mapping.Id == 0 ||
                !persistedMappingsById.TryGetValue(mapping.Id, out var persisted) ||
                !DerivedFlowGraph.ReadsMetaverse(persisted));

            if (newlyReadsMetaverse)
                await Application.FeatureFlags.EnsureEnabledAsync(flagKey);

            return;
        }

        var metaverseObjectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(metaverseObjectTypeId, true)
            ?? hostRule.MetaverseObjectType;
        List<MetaverseObjectType> types = metaverseObjectType == null ? [] : [metaverseObjectType];

        var graph = new DerivedFlowGraph(substituteProposal(persistedRules), types, DerivedFlowGraphScope.AllMappings);
        var result = DerivedFlowValidator.Validate(graph, readingMetaverse);

        if (result.HasErrors)
        {
            var exception = new DerivedFlowValidationException(result.Errors);
            Log.Warning("EnsureDerivedFlowProposalAllowedAsync: rejecting Attribute Flow save on Synchronisation Rule {SyncRuleId}; {Message}",
                hostRule.Id, LogSanitiser.Sanitise(exception.Message));
            throw exception;
        }

        foreach (var warning in result.Warnings)
            warning.Mapping.SaveWarnings.Add(warning.Message);
    }

    /// <summary>
    /// Whether a mapping is an import mapping (it targets a Metaverse attribute) whose expression reads <c>mv["..."]</c>.
    /// </summary>
    private static bool ReadsMetaverseAsImportMapping(SyncRuleMapping mapping) =>
        (mapping.TargetMetaverseAttributeId ?? mapping.TargetMetaverseAttribute?.Id) != null &&
        DerivedFlowGraph.ReadsMetaverse(mapping);
}

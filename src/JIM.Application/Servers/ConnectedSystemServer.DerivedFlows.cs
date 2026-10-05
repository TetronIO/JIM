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
/// Metaverse-Derived Attribute Flows' save-time surface (#1750, plan Phase 1): the dependency-graph validation every
/// mapping save path runs, before anything is written, on an import expression that reads <c>mv["..."]</c>.
/// </summary>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// Builds the Attribute Priority context for a server-side recall (Synchronisation Rule deletion recall,
    /// Synchronised Deprovisioning, the stranded value sweep), carrying the run's Metaverse-Derived Attribute Flow
    /// graph (#1750, plan decision 12) exactly as a synchronisation run's does. With the graph attached, contributor
    /// re-election skips derived mappings (decision 5): re-flowed there, a derived flow would read no
    /// <c>mv["..."]</c> inputs at all and write a value nobody configured.
    /// </summary>
    /// <exception cref="DerivedFlowCycleException">The enabled derived flows contain a dependency cycle; the recall
    /// fails hard before touching any object (decision 11).</exception>
    private static AttributePriorityContext BuildRecallPriorityContext(List<SyncRule> allSyncRules)
    {
        var derivedFlowGraph = DerivedFlowGraphFactory.Create(allSyncRules, []);
        return new AttributePriorityContext(allSyncRules, honourNullAssertions: true, derivedFlowGraph);
    }

    /// <summary>
    /// Builds the derived-input mark collector (#1750, plan Phase 4, FR 9) for a server-side writer outside
    /// synchronisation, from the Synchronisation Rules that will exist once the write completes. A rule being deleted,
    /// or a system being deprovisioned, is left out of <paramref name="survivingSyncRules"/> by the caller: its derived
    /// mappings are going away with it, so they must neither mark its own system nor carry transitivity onwards.
    /// </summary>
    /// <exception cref="DerivedFlowCycleException">The surviving enabled derived flows contain a cycle (decision 11).</exception>
    private static DerivedInputMarkBatch CreateDerivedInputMarkBatch(IEnumerable<SyncRule> survivingSyncRules, string writerName)
    {
        var derivedFlowGraph = DerivedFlowGraphFactory.Create(survivingSyncRules, []);
        return new DerivedInputMarkBatch(derivedFlowGraph, writerName);
    }

    /// <summary>
    /// The Metaverse attributes whose values deleting <paramref name="connectedSystemId"/> with Synchronised
    /// Deprovisioning could withdraw or change, and that a surviving derived flow reads, each with the names of the
    /// Connected Systems hosting those flows (#134). The deletion preview states these rather than showing them: the
    /// real run marks the readers' objects for re-derivation by their host's next synchronisation (the graph built here
    /// is the one <see cref="CreateDerivedInputMarkBatch"/> builds for it, over the surviving rules), so what the
    /// derived values become is not decided by the deletion at all.
    /// </summary>
    /// <exception cref="DerivedFlowCycleException">The surviving derived flows contain a cycle, which the real run
    /// would also refuse.</exception>
    internal async Task<List<(string MetaverseAttributeName, IReadOnlyList<string> HostingSystemNames)>> GetDerivedFlowsReadingDeprovisionedAttributesAsync(int connectedSystemId)
    {
        var allSyncRules = await Application.SyncRepo.GetAllSyncRulesAsync();
        var graph = DerivedFlowGraphFactory.Create(allSyncRules.Where(rule => rule.ConnectedSystemId != connectedSystemId), []);

        // Every import mapping of the system, enabled or not: the residue pass recalls by provenance, so a value an
        // earlier, since-disabled mapping contributed is withdrawn as surely as a current one.
        var withdrawable = allSyncRules
            .Where(rule => rule.ConnectedSystemId == connectedSystemId && rule.Direction == SyncRuleDirection.Import)
            .SelectMany(rule => rule.AttributeFlowRules
                .Select(mapping => (
                    MetaverseObjectTypeId: rule.ResolveMetaverseObjectTypeId(),
                    AttributeId: mapping.TargetMetaverseAttribute?.Id ?? mapping.TargetMetaverseAttributeId ?? 0,
                    AttributeName: mapping.TargetMetaverseAttribute?.Name)))
            .Where(attribute => attribute.AttributeId != 0)
            .DistinctBy(attribute => (attribute.MetaverseObjectTypeId, attribute.AttributeId))
            .ToList();
        if (withdrawable.Count == 0)
            return [];

        var systemNames = await Application.SyncRepo.GetConnectedSystemNamesAsync();
        return withdrawable
            .Select(attribute => (
                Name: attribute.AttributeName ?? $"ID {attribute.AttributeId}",
                Hosts: graph.GetHostingSystemsReading(attribute.MetaverseObjectTypeId, attribute.AttributeId)))
            .Where(attribute => attribute.Hosts.Count > 0)
            // One attribute bound to several Metaverse Object Types is one thing to an administrator reading the warning.
            .GroupBy(attribute => attribute.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.Key, (IReadOnlyList<string>)group
                .SelectMany(attribute => attribute.Hosts)
                .Distinct()
                .Select(id => systemNames.GetValueOrDefault(id) ?? $"ID {id}")
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .ToList();
    }

    /// <summary>
    /// The single-mapping save paths' entry point (create, update, settings update): validates
    /// <paramref name="mapping"/> as a proposal replacing the persisted mapping with the same id on its rule, and
    /// stamps any warnings onto <see cref="SyncRuleMapping.SaveWarnings"/>.
    /// </summary>
    /// <exception cref="DerivedFlowValidationException">The proposal is invalid.</exception>
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

        await EnsureDerivedFlowProposalAllowedAsync(hostRule, [mapping], persistedRules => SubstituteMapping(persistedRules, hostRule, mapping));
    }

    /// <summary>
    /// The import rules of a Metaverse Object Type as they will stand once <paramref name="mapping"/> is saved on
    /// <paramref name="hostRule"/>: the host rule's persisted mappings, with the proposal replacing the one it updates
    /// (matched by id) or joining them as a new mapping. Nothing passed in is changed; the host rule is a new shell.
    /// </summary>
    private static List<SyncRule> SubstituteMapping(List<SyncRule> persistedRules, SyncRule hostRule, SyncRuleMapping mapping)
    {
        var syncRuleId = hostRule.Id;
        var persistedHost = syncRuleId > 0 ? persistedRules.FirstOrDefault(rule => rule.Id == syncRuleId) : null;
        var proposedHost = new SyncRule
        {
            Id = syncRuleId,
            Name = string.IsNullOrEmpty(hostRule.Name) ? persistedHost?.Name ?? string.Empty : hostRule.Name,
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = hostRule.ConnectedSystemId > 0 ? hostRule.ConnectedSystemId : persistedHost?.ConnectedSystemId ?? 0,
            MetaverseObjectTypeId = hostRule.ResolveMetaverseObjectTypeId(),
            Enabled = hostRule.Enabled
        };
        proposedHost.AttributeFlowRules.AddRange((persistedHost?.AttributeFlowRules ?? [])
            .Where(persisted => mapping.Id == 0 || persisted.Id != mapping.Id));
        proposedHost.AttributeFlowRules.Add(mapping);

        return persistedRules
            .Where(rule => syncRuleId == 0 || rule.Id != syncRuleId)
            .Append(proposedHost)
            .ToList();
    }

    /// <summary>
    /// The whole-rule save's entry point (<c>CreateOrUpdateSyncRuleAsync</c>, the portal's save path): validates every
    /// mapping of <paramref name="syncRule"/> as a proposal replacing the persisted rule wholesale,
    /// and stamps any warnings onto each mapping's <see cref="SyncRuleMapping.SaveWarnings"/>.
    /// </summary>
    /// <exception cref="DerivedFlowValidationException">The proposal is invalid.</exception>
    private async Task EnsureDerivedFlowsAllowedAsync(SyncRule syncRule)
    {
        foreach (var mapping in syncRule.AttributeFlowRules)
            mapping.SaveWarnings.Clear();

        if (syncRule.Direction != SyncRuleDirection.Import)
            return;

        await EnsureDerivedFlowProposalAllowedAsync(syncRule, syncRule.AttributeFlowRules, persistedRules => SubstituteWholeRule(persistedRules, syncRule));
    }

    /// <summary>
    /// What saving <paramref name="proposedRule"/> wholesale would mean for Metaverse-Derived Attribute Flows (#1750,
    /// plan Phase 5), for the Configuration Change Preview: the save-time validation it would face, run exactly as the
    /// whole-rule save runs it (every import rule of the Metaverse Object Type, disabled included, with the proposal
    /// substituted), without throwing, plus the rule sets before and after for the FR 3 dependant detector. Reads and
    /// writes nothing for an export rule, and returns null.
    /// </summary>
    /// <remarks>
    /// Unlike the save path it loads the rules even when no proposed mapping reads <c>mv["..."]</c>: a proposal that
    /// removes or retargets an ordinary mapping can still leave a derived flow elsewhere without its input.
    /// </remarks>
    internal async Task<DerivedFlowProposalAssessment?> AssessDerivedFlowProposalAsync(SyncRule proposedRule)
    {
        ArgumentNullException.ThrowIfNull(proposedRule);

        if (proposedRule.Direction != SyncRuleDirection.Import)
            return null;

        var metaverseObjectTypeId = proposedRule.ResolveMetaverseObjectTypeId();
        var persistedRules = await Application.Repository.ConnectedSystems.GetImportSyncRulesForMetaverseObjectTypeAsync(metaverseObjectTypeId);
        var metaverseObjectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(metaverseObjectTypeId, true)
            ?? proposedRule.MetaverseObjectType;
        List<MetaverseObjectType> types = metaverseObjectType == null ? [] : [metaverseObjectType];

        var rulesAfter = SubstituteWholeRule(persistedRules, proposedRule);
        var validation = DerivedFlowValidator.Validate(
            new DerivedFlowGraph(rulesAfter, types, DerivedFlowGraphScope.AllMappings),
            proposedRule.AttributeFlowRules.Where(ReadsMetaverseAsImportMapping).ToList());

        return new DerivedFlowProposalAssessment(validation, persistedRules, rulesAfter, types);
    }

    /// <summary>
    /// The import rules of a Metaverse Object Type as they will stand once <paramref name="syncRule"/> is saved
    /// wholesale: the persisted rule of the same id replaced, or the new rule added.
    /// </summary>
    private static List<SyncRule> SubstituteWholeRule(List<SyncRule> persistedRules, SyncRule syncRule) => persistedRules
        .Where(rule => syncRule.Id == 0 || rule.Id != syncRule.Id)
        .Append(syncRule)
        .ToList();

    /// <summary>
    /// The shared core. A no-op, with no I/O at all, unless a proposed import mapping reads <c>mv["..."]</c>, so every
    /// other save behaves exactly as before. Otherwise loads every import rule of the Metaverse Object Type (all
    /// Connected Systems, disabled included), builds the graph with the proposal substituted
    /// (<see cref="DerivedFlowGraphScope.AllMappings"/>), refuses the save with every error in one message, and stamps
    /// each warning onto the mapping it concerns.
    /// </summary>
    private async Task EnsureDerivedFlowProposalAllowedAsync(
        SyncRule hostRule,
        IReadOnlyCollection<SyncRuleMapping> proposedMappings,
        Func<List<SyncRule>, List<SyncRule>> substituteProposal)
    {
        var readingMetaverse = proposedMappings.Where(ReadsMetaverseAsImportMapping).ToList();
        if (readingMetaverse.Count == 0)
            return;

        var metaverseObjectTypeId = hostRule.ResolveMetaverseObjectTypeId();
        var persistedRules = await Application.Repository.ConnectedSystems.GetImportSyncRulesForMetaverseObjectTypeAsync(metaverseObjectTypeId);

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
        mapping.ResolveTargetMetaverseAttributeId() != null &&
        DerivedFlowGraph.ReadsMetaverse(mapping);
}

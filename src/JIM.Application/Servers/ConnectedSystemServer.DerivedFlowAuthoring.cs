// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging.DTOs;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Metaverse-Derived Attribute Flows' authoring surfaces (#1750, plan Phase 6): the FR 3 dependants every removal and
/// disable path reports, the read-only analysis the portal runs as an administrator types, and the step facts the
/// read surfaces show. Everything here reads; nothing writes.
/// </summary>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// Analyses a proposed import Attribute Flow against the Metaverse-Derived Attribute Flow dependency graph without
    /// saving anything, for the portal to check as an administrator types. The proposal is substituted into every
    /// import Synchronisation Rule of the Metaverse Object Type (replacing the persisted mapping with the same id, or
    /// joining <paramref name="hostRule"/>'s mappings when unsaved), and validated exactly as the save validates it, so
    /// <see cref="DerivedFlowAnalysis.BlockingError"/> is the very message the save would refuse with and
    /// <see cref="DerivedFlowAnalysis.Warnings"/> the very warnings it would raise.
    /// </summary>
    /// <param name="hostRule">The Synchronisation Rule the mapping belongs to, saved or not: its id, name, Connected
    /// System, Metaverse Object Type, direction and enabled state are read; its mappings are not.</param>
    /// <param name="proposedMapping">The mapping as the administrator has it, saved (non-zero id) or not.</param>
    /// <returns><see cref="DerivedFlowAnalysis.NotApplicable"/>, reading nothing, when the rule is an export rule, or
    /// the mapping has no target Metaverse attribute yet.</returns>
    public async Task<DerivedFlowAnalysis> AnalyseDerivedFlowAsync(SyncRule hostRule, SyncRuleMapping proposedMapping)
    {
        ArgumentNullException.ThrowIfNull(hostRule);
        ArgumentNullException.ThrowIfNull(proposedMapping);

        if (hostRule.Direction != SyncRuleDirection.Import || GetTargetMetaverseAttributeId(proposedMapping) is not { } targetAttributeId)
            return DerivedFlowAnalysis.NotApplicable;

        var metaverseObjectTypeId = hostRule.ResolveMetaverseObjectTypeId();
        var (persistedRules, types) = await LoadImportRulesAndTypeAsync(metaverseObjectTypeId, hostRule.MetaverseObjectType);
        var rulesAfter = SubstituteMapping(persistedRules, hostRule, proposedMapping);
        var graph = new DerivedFlowGraph(rulesAfter, types, DerivedFlowGraphScope.AllMappings);
        var stepCount = graph.MaxLevel(metaverseObjectTypeId) + 1;

        var flow = graph.GetDerivedFlow(proposedMapping);
        if (flow == null)
        {
            // An ordinary flow is evaluated in the ordinary pass, step 1; the step only means something to the
            // administrator when a derived flow reads what it writes.
            var readByDerivedFlows = graph.GetHostingSystemsReading(metaverseObjectTypeId, targetAttributeId).Count > 0;
            return new DerivedFlowAnalysis
            {
                Status = DerivedFlowAnalysisStatus.NotDerived,
                Step = readByDerivedFlows ? 1 : null,
                StepCount = stepCount
            };
        }

        var validation = DerivedFlowValidator.Validate(graph, [proposedMapping]);
        return new DerivedFlowAnalysis
        {
            Status = DerivedFlowAnalysisStatus.Derived,
            MetaverseInputs = DerivedFlowGraph.GetMetaverseInputNames(proposedMapping),
            Step = graph.GetLevel(metaverseObjectTypeId, flow.TargetAttributeId) + 1,
            StepCount = stepCount,
            Steps = BuildStepChain(graph, rulesAfter, flow),
            Errors = validation.Errors,
            Cycle = DescribeCycleLinks(graph, proposedMapping, validation.Errors),
            Warnings = validation.Warnings.Select(warning => warning.Message).ToList()
        };
    }

    /// <summary>
    /// The loop the analysed flow closes, link by link from the flow itself, for the portal to list. Built from the
    /// same cycle and in the same order as the validator's message (which starts from the proposed mapping), and
    /// returned only when that message is among the errors, so the links can never describe a loop the save would not
    /// refuse. Null for a knot of several interlocking loops, whose extra members only the message names.
    /// </summary>
    private static DerivedFlowAnalysisCycle? DescribeCycleLinks(DerivedFlowGraph graph, SyncRuleMapping analysedMapping, IReadOnlyList<string> errors)
    {
        var cycle = graph.Cycles.FirstOrDefault(candidate => candidate.Involves(analysedMapping));
        if (cycle == null || cycle.AdditionalMembers.Count > 0)
            return null;

        var startIndex = cycle.Members.ToList().FindIndex(member => ReferenceEquals(member.Mapping, analysedMapping));
        if (startIndex < 0)
            return null;

        var message = "Saving would create a dependency cycle: " + DerivedFlowValidator.DescribeCyclePath(cycle, startIndex);
        if (!errors.Contains(message))
            return null;

        var links = cycle.Members.Skip(startIndex).Concat(cycle.Members.Take(startIndex))
            .Select(member => new DerivedFlowAnalysisCycleLink(
                member.MetaverseAttributeName,
                member.ReadsMetaverseAttributeName,
                member.SyncRule.Name,
                ReferenceEquals(member.Mapping, analysedMapping)))
            .ToList();
        return new DerivedFlowAnalysisCycle(message, links);
    }

    /// <summary>
    /// Where each saved Metaverse-Derived Attribute Flow of <paramref name="syncRule"/> sits in its Metaverse Object
    /// Type's evaluation order, for the read surfaces (REST mapping responses, PowerShell, the portal). Steps are
    /// counted over every import Synchronisation Rule of the type, disabled flows included, exactly as save-time
    /// validation orders them, so enabling a flow never renumbers the others.
    /// </summary>
    /// <param name="syncRule">The rule, with its Attribute Flow mappings loaded.</param>
    /// <returns>The step facts keyed by mapping id, for the rule's derived mappings only. Empty, with nothing read,
    /// when the rule is an export rule, or none of its mappings reads <c>mv["..."]</c>.</returns>
    public async Task<IReadOnlyDictionary<int, DerivedFlowStepInfo>> GetDerivedFlowStepsAsync(SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(syncRule);

        if (syncRule.Direction != SyncRuleDirection.Import)
            return new Dictionary<int, DerivedFlowStepInfo>();

        var readingMetaverse = syncRule.AttributeFlowRules.Where(mapping => mapping.Id > 0 && ReadsMetaverseAsImportMapping(mapping)).ToList();
        if (readingMetaverse.Count == 0)
            return new Dictionary<int, DerivedFlowStepInfo>();

        var metaverseObjectTypeId = syncRule.ResolveMetaverseObjectTypeId();
        var (persistedRules, types) = await LoadImportRulesAndTypeAsync(metaverseObjectTypeId, syncRule.MetaverseObjectType);
        // The caller's own instances stand in for the persisted rule, so each mapping is found in the graph by
        // reference, whatever its loaded graph looks like.
        var graph = new DerivedFlowGraph(SubstituteWholeRule(persistedRules, syncRule), types, DerivedFlowGraphScope.AllMappings);
        var stepCount = graph.MaxLevel(metaverseObjectTypeId) + 1;

        return readingMetaverse
            .Select(mapping => (Mapping: mapping, Flow: graph.GetDerivedFlow(mapping)))
            .Where(candidate => candidate.Flow != null)
            .ToDictionary(
                candidate => candidate.Mapping.Id,
                candidate => new DerivedFlowStepInfo(
                    graph.GetLevel(metaverseObjectTypeId, candidate.Flow!.TargetAttributeId) + 1,
                    stepCount,
                    DerivedFlowGraph.GetMetaverseInputNames(candidate.Mapping)));
    }

    /// <summary>
    /// <see cref="GetDerivedFlowStepsAsync(SyncRule)"/> for a rule the caller has not loaded, such as straight after a
    /// mapping save.
    /// </summary>
    /// <param name="syncRuleId">The Synchronisation Rule's id.</param>
    public async Task<IReadOnlyDictionary<int, DerivedFlowStepInfo>> GetDerivedFlowStepsAsync(int syncRuleId)
    {

        var syncRule = await Application.Repository.ConnectedSystems.GetSyncRuleAsync(syncRuleId);
        return syncRule == null
            ? new Dictionary<int, DerivedFlowStepInfo>()
            : await GetDerivedFlowStepsAsync(syncRule);
    }

    /// <summary>
    /// The Metaverse-Derived Attribute Flows (#1750, FR 3) that removing <paramref name="mapping"/> from the rule as the
    /// editor holds it would leave with a missing input, for the portal to confirm before it stages the removal. Read
    /// only: the staged rule is compared with and without the mapping, every other import rule of the Metaverse Object
    /// Type as persisted. Inputs an earlier staged change already took away are not reported again.
    /// </summary>
    /// <param name="stagedRule">The rule as the editor holds it, with every change staged so far, the mapping included.</param>
    /// <param name="mapping">The mapping about to be removed, by reference.</param>
    /// <returns>Nothing, and nothing read, when the rule is an export rule.</returns>
    public Task<List<DependentDerivedFlow>> GetDependentDerivedFlowsOfMappingRemovalAsync(SyncRule stagedRule, SyncRuleMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(stagedRule);
        ArgumentNullException.ThrowIfNull(mapping);

        return DetectDependentDerivedFlowsOfStagedChangeAsync(
            stagedRule,
            CopyRuleShell(stagedRule, stagedRule.AttributeFlowRules),
            CopyRuleShell(stagedRule, stagedRule.AttributeFlowRules.Where(candidate => !ReferenceEquals(candidate, mapping))),
            $"Removing an Attribute Flow from Synchronisation Rule {stagedRule.Id}");
    }

    /// <summary>
    /// The Metaverse-Derived Attribute Flows (#1750, FR 3) that an edit to one mapping would leave with a missing
    /// input (disabling it, or changing what it reads or writes), for the portal to confirm before it applies the edit
    /// to the rule it holds. Read only.
    /// </summary>
    /// <param name="stagedRule">The rule as the editor holds it, holding <paramref name="editedMapping"/>.</param>
    /// <param name="mappingAsOpened">A copy of the mapping as it stood when the editor opened it.</param>
    /// <param name="editedMapping">The mapping as edited, by reference among the staged rule's mappings.</param>
    /// <returns>Nothing, and nothing read, when the rule is an export rule.</returns>
    public Task<List<DependentDerivedFlow>> GetDependentDerivedFlowsOfMappingEditAsync(SyncRule stagedRule, SyncRuleMapping mappingAsOpened, SyncRuleMapping editedMapping)
    {
        ArgumentNullException.ThrowIfNull(stagedRule);
        ArgumentNullException.ThrowIfNull(mappingAsOpened);
        ArgumentNullException.ThrowIfNull(editedMapping);

        return DetectDependentDerivedFlowsOfStagedChangeAsync(
            stagedRule,
            CopyRuleShell(stagedRule, stagedRule.AttributeFlowRules.Select(candidate => ReferenceEquals(candidate, editedMapping) ? mappingAsOpened : candidate)),
            CopyRuleShell(stagedRule, stagedRule.AttributeFlowRules),
            $"Editing an Attribute Flow on Synchronisation Rule {stagedRule.Id}");
    }

    /// <summary>
    /// The Metaverse-Derived Attribute Flows (#1750, FR 3) that disabling the whole rule as the editor holds it would
    /// leave with a missing input, for the portal to confirm before it saves the rule disabled. Only the disable is
    /// judged: the staged rule is compared enabled and disabled, so changes staged (and confirmed) earlier are not
    /// reported again. Read only.
    /// </summary>
    /// <param name="stagedRule">The rule as the editor holds it.</param>
    /// <returns>Nothing, and nothing read, when the rule is an export rule.</returns>
    public Task<List<DependentDerivedFlow>> GetDependentDerivedFlowsOfRuleDisableAsync(SyncRule stagedRule)
    {
        ArgumentNullException.ThrowIfNull(stagedRule);

        return DetectDependentDerivedFlowsOfStagedChangeAsync(
            stagedRule,
            CopyRuleShell(stagedRule, stagedRule.AttributeFlowRules, enabled: true),
            CopyRuleShell(stagedRule, stagedRule.AttributeFlowRules, enabled: false),
            $"Disabling Synchronisation Rule {stagedRule.Id}");
    }

    /// <summary>
    /// The Metaverse-Derived Attribute Flows (#1750, FR 3) that deleting a saved rule would leave with a missing input,
    /// exactly as the deletion itself reports them, for the portal to confirm before it deletes. Read only.
    /// </summary>
    /// <param name="syncRule">The rule to be deleted.</param>
    /// <returns>Nothing, and nothing read, when the rule is an export rule or it is not saved.</returns>
    public async Task<List<DependentDerivedFlow>> GetDependentDerivedFlowsOfRuleDeletionAsync(SyncRule syncRule)
    {
        ArgumentNullException.ThrowIfNull(syncRule);

        if (syncRule.Id == 0 || syncRule.Direction != SyncRuleDirection.Import)
            return [];

        return await DetectDependentDerivedFlowsAsync(
            () => Task.FromResult<int?>(syncRule.ResolveMetaverseObjectTypeId()),
            rules => WithoutRule(rules, syncRule.Id),
            $"Deleting Synchronisation Rule {syncRule.Id}");
    }

    /// <summary>
    /// The shared core of the staged-change checks: every import rule of the Metaverse Object Type as persisted, with
    /// the rule replaced by <paramref name="ruleBefore"/> on one side and <paramref name="ruleAfter"/> on the other, so
    /// only what the change itself takes away is reported.
    /// </summary>
    private async Task<List<DependentDerivedFlow>> DetectDependentDerivedFlowsOfStagedChangeAsync(
        SyncRule stagedRule, SyncRule ruleBefore, SyncRule ruleAfter, string changeDescription)
    {
        if (stagedRule.Direction != SyncRuleDirection.Import)
            return [];

        return await DetectDependentDerivedFlowsAsync(
            () => Task.FromResult<int?>(stagedRule.ResolveMetaverseObjectTypeId()),
            rules => SubstituteWholeRule(rules, ruleAfter),
            changeDescription,
            rules => SubstituteWholeRule(rules, ruleBefore));
    }

    /// <summary>
    /// What a destructive schema refresh invalidates (#1485), plus the Metaverse-Derived Attribute Flows (#1750, FR 3)
    /// that disabling or removing those Synchronisation Rules and mappings would leave with a missing input. The single
    /// entry point for the schema refresh preview and both destructive apply flavours, so the dependents a caller
    /// reviews are always the ones the apply acts on.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System being refreshed.</param>
    /// <param name="previewResult">The refresh preview's result.</param>
    public async Task<SchemaRefreshDependents> DetectSchemaRefreshDependentsAsync(int connectedSystemId, SchemaRefreshResult previewResult)
    {
        ArgumentNullException.ThrowIfNull(previewResult);

        var syncRules = await GetSyncRulesAsync(connectedSystemId, includeDisabledSyncRules: true);
        var dependents = SchemaRefreshDependentDetector.Detect(previewResult, syncRules, DateTime.UtcNow);
        dependents.DependentDerivedFlows = await DetectSchemaRefreshDerivedDependentsAsync(connectedSystemId, syncRules, dependents);
        return dependents;
    }

    /// <summary>
    /// The derived half of <see cref="DetectSchemaRefreshDependentsAsync"/>: the import rules of every Metaverse
    /// Object Type an invalidated rule or mapping flows to, before and after the invalidated rules are disabled and the
    /// invalidated mappings are taken away (disabling and removing starve a derived flow alike).
    /// </summary>
    private async Task<List<DependentDerivedFlow>> DetectSchemaRefreshDerivedDependentsAsync(
        int connectedSystemId, IReadOnlyList<SyncRule> systemRules, SchemaRefreshDependents dependents)
    {
        if (dependents.InvalidatedSyncRules.Count == 0 && dependents.InvalidatedMappings.Count == 0)
            return [];

        var invalidatedRuleIds = dependents.InvalidatedSyncRules.Select(rule => rule.SyncRuleId).ToHashSet();
        var invalidatedMappingIds = dependents.InvalidatedMappings.Select(mapping => mapping.MappingId).ToHashSet();
        var metaverseObjectTypeIds = systemRules
            .Where(rule => rule.Direction == SyncRuleDirection.Import &&
                (invalidatedRuleIds.Contains(rule.Id) || rule.AttributeFlowRules.Any(mapping => invalidatedMappingIds.Contains(mapping.Id))))
            .Select(rule => rule.ResolveMetaverseObjectTypeId())
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (metaverseObjectTypeIds.Count == 0)
            return [];

        var before = new List<SyncRule>();
        var types = new List<MetaverseObjectType>();
        foreach (var metaverseObjectTypeId in metaverseObjectTypeIds)
        {
            var (rules, typeList) = await LoadImportRulesAndTypeAsync(metaverseObjectTypeId, fallbackType: null);
            before.AddRange(rules);
            types.AddRange(typeList);
        }

        var after = before
            .Select(rule => invalidatedRuleIds.Contains(rule.Id)
                ? CopyRuleShell(rule, rule.AttributeFlowRules, enabled: false)
                : rule.AttributeFlowRules.Any(mapping => invalidatedMappingIds.Contains(mapping.Id))
                    ? CopyRuleShell(rule, rule.AttributeFlowRules.Where(mapping => !invalidatedMappingIds.Contains(mapping.Id)))
                    : rule)
            .ToList();

        return await DescribeDependentDerivedFlowsAsync(
            DerivedFlowDependentDetector.Detect(before, after, types),
            $"Schema refresh of Connected System {connectedSystemId}");
    }

    /// <summary>
    /// Stamps <see cref="SyncRuleMapping.SaveDependentDerivedFlows"/> for a full or settings update of a saved import
    /// mapping: the derived flows left with a missing input once <paramref name="mapping"/>, as changed, replaces the
    /// stored one. Must run before anything writes the change.
    /// </summary>
    private async Task StampDependentDerivedFlowsOfMappingChangeAsync(SyncRuleMapping mapping)
    {
        mapping.SaveDependentDerivedFlows.Clear();

        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId;
        if (mapping.Id == 0 || syncRuleId <= 0 || GetTargetMetaverseAttributeId(mapping) == null)
            return;

        mapping.SaveDependentDerivedFlows.AddRange(await DetectDependentDerivedFlowsAsync(
            () => ResolveImportMappingMetaverseObjectTypeIdAsync(mapping),
            rules => WithMappingReplaced(rules, syncRuleId, mapping),
            $"Updating Attribute Flow mapping {mapping.Id}"));
    }

    /// <summary>
    /// Stamps <see cref="SyncRule.SaveDependentDerivedFlows"/> for a whole-rule save of an existing import rule (the
    /// portal's save path, and the REST and PowerShell rule update): the derived flows left with a missing input once
    /// <paramref name="syncRule"/> replaces the stored rule wholesale, disabled or with mappings removed, disabled or
    /// retargeted. A new rule takes nothing away, so nothing is read for one. Must run before anything writes.
    /// </summary>
    private async Task StampDependentDerivedFlowsOfRuleSaveAsync(SyncRule syncRule)
    {
        syncRule.SaveDependentDerivedFlows.Clear();

        if (syncRule.Id == 0 || syncRule.Direction != SyncRuleDirection.Import)
            return;

        syncRule.SaveDependentDerivedFlows.AddRange(await DetectDependentDerivedFlowsAsync(
            () => Task.FromResult<int?>(syncRule.ResolveMetaverseObjectTypeId()),
            rules => SubstituteWholeRule(rules, syncRule),
            $"Saving Synchronisation Rule {syncRule.Id}"));
    }

    /// <summary>
    /// The FR 3 core (#1750): the Metaverse-Derived Attribute Flows a change leaves with a missing input, because an
    /// attribute they read, directly or through other derived attributes, loses its last enabled contributor. Reads the
    /// import rules of the Metaverse Object Type as they stand (so call it before the change is written), applies the
    /// change to a copy with <paramref name="applyChange"/>, and compares. A change is never refused for this; the
    /// dependants are reported.
    /// </summary>
    /// <param name="resolveMetaverseObjectTypeId">Resolves the Metaverse Object Type the change concerns; null for a
    /// change no import rule is involved in.</param>
    /// <param name="applyChange">Builds the rules as they will stand after the change. Must not modify what it is given.</param>
    /// <param name="changeDescription">Names the change in the log summary.</param>
    /// <param name="applyBaseline">Builds the rules the change is measured from, when that is not the persisted state
    /// (a change the portal's editor is about to stage on top of others it already holds). Must not modify what it is
    /// given; null measures from the rules as persisted.</param>
    private async Task<List<DependentDerivedFlow>> DetectDependentDerivedFlowsAsync(
        Func<Task<int?>> resolveMetaverseObjectTypeId,
        Func<List<SyncRule>, List<SyncRule>> applyChange,
        string changeDescription,
        Func<List<SyncRule>, List<SyncRule>>? applyBaseline = null)
    {
        if (await resolveMetaverseObjectTypeId() is not { } metaverseObjectTypeId)
            return [];

        var persisted = await Application.Repository.ConnectedSystems.GetImportSyncRulesForMetaverseObjectTypeAsync(metaverseObjectTypeId);
        var before = applyBaseline?.Invoke(persisted) ?? persisted;
        var after = applyChange(persisted);

        // With no derived flow on either side there is nothing to starve, so the Metaverse Object Type is not read.
        if (!before.Concat(after).SelectMany(rule => rule.AttributeFlowRules).Any(ReadsMetaverseAsImportMapping))
            return [];

        var type = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(metaverseObjectTypeId, true);
        List<MetaverseObjectType> types = type == null ? [] : [type];
        return await DescribeDependentDerivedFlowsAsync(DerivedFlowDependentDetector.Detect(before, after, types), changeDescription);
    }

    /// <summary>
    /// Flattens the detector's dependants for the surfaces, naming each hosting Connected System, and logs a summary.
    /// The Connected System names are read only when there is a dependant to name.
    /// </summary>
    private async Task<List<DependentDerivedFlow>> DescribeDependentDerivedFlowsAsync(IReadOnlyList<DerivedFlowDependent> dependants, string changeDescription)
    {
        if (dependants.Count == 0)
            return [];

        var systemNames = await Application.Repository.ConnectedSystems.GetConnectedSystemNamesAsync();
        var described = dependants.Select(dependant => new DependentDerivedFlow
        {
            MappingId = dependant.Flow.Mapping.Id,
            TargetMetaverseAttributeName = dependant.Flow.TargetAttributeName,
            SyncRuleId = dependant.Flow.SyncRule.Id,
            SyncRuleName = dependant.Flow.SyncRule.Name,
            ConnectedSystemId = dependant.Flow.SyncRule.ConnectedSystemId,
            ConnectedSystemName = systemNames.GetValueOrDefault(dependant.Flow.SyncRule.ConnectedSystemId)
                ?? dependant.Flow.SyncRule.ConnectedSystem?.Name
                ?? $"ID {dependant.Flow.SyncRule.ConnectedSystemId}",
            MissingInputs = dependant.LostInputs.Select(lost => new DependentDerivedFlowInput
            {
                MetaverseAttributeName = lost.MetaverseAttributeName,
                Indirect = lost.Indirect,
                Via = lost.Via.ToList()
            }).ToList()
        }).ToList();

        Log.Information("{Change}: {Count} Metaverse-Derived Attribute Flow(s) left with a missing input: {Flows}",
            LogSanitiser.Sanitise(changeDescription), described.Count,
            LogSanitiser.Sanitise(string.Join("; ", described.Select(flow =>
                $"{flow.TargetMetaverseAttributeName} (mapping {flow.MappingId}, Synchronisation Rule {flow.SyncRuleId}) reading " +
                string.Join(", ", flow.MissingInputs.Select(input => input.MetaverseAttributeName))))));

        return described;
    }

    /// <summary>
    /// The import rules of a Metaverse Object Type as persisted (every Connected System, disabled included; a fresh
    /// no-tracking read), and the type with its attributes for resolving <c>mv["..."]</c> names.
    /// </summary>
    private async Task<(List<SyncRule> Rules, List<MetaverseObjectType> Types)> LoadImportRulesAndTypeAsync(int metaverseObjectTypeId, MetaverseObjectType? fallbackType)
    {
        var rules = await Application.Repository.ConnectedSystems.GetImportSyncRulesForMetaverseObjectTypeAsync(metaverseObjectTypeId);
        var type = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(metaverseObjectTypeId, true) ?? fallbackType;
        return (rules, type == null ? [] : [type]);
    }

    /// <summary>
    /// The Metaverse Object Type an import mapping's rule flows to, read from its loaded rule when present; null when
    /// the mapping's rule cannot be found or is not an import rule.
    /// </summary>
    private async Task<int?> ResolveImportMappingMetaverseObjectTypeIdAsync(SyncRuleMapping mapping)
    {
        var syncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId;
        var rule = mapping.SyncRule ?? (syncRuleId > 0 ? await Application.Repository.ConnectedSystems.GetSyncRuleAsync(syncRuleId) : null);
        return rule?.Direction == SyncRuleDirection.Import ? rule.ResolveMetaverseObjectTypeId() : null;
    }

    /// <summary>
    /// The rules with one persisted mapping taken away from its rule (a deletion).
    /// </summary>
    private static List<SyncRule> WithoutMapping(List<SyncRule> rules, int syncRuleId, int mappingId) => rules
        .Select(rule => rule.Id == syncRuleId
            ? CopyRuleShell(rule, rule.AttributeFlowRules.Where(mapping => mapping.Id != mappingId))
            : rule)
        .ToList();

    /// <summary>
    /// The rules with one persisted mapping replaced by the caller's changed instance (a full or settings update),
    /// its rule otherwise as persisted.
    /// </summary>
    private static List<SyncRule> WithMappingReplaced(List<SyncRule> rules, int syncRuleId, SyncRuleMapping changed) => rules
        .Select(rule => rule.Id == syncRuleId
            ? CopyRuleShell(rule, rule.AttributeFlowRules.Where(mapping => mapping.Id != changed.Id).Append(changed))
            : rule)
        .ToList();

    /// <summary>
    /// The rules with one rule gone (a deletion).
    /// </summary>
    private static List<SyncRule> WithoutRule(List<SyncRule> rules, int syncRuleId) =>
        rules.Where(rule => rule.Id != syncRuleId).ToList();

    /// <summary>
    /// A copy of a rule carrying what the dependency graph reads (identity, direction, Connected System, Metaverse
    /// Object Type, enabled state), with the given mappings. The original and its mapping list are left untouched.
    /// </summary>
    private static SyncRule CopyRuleShell(SyncRule rule, IEnumerable<SyncRuleMapping> mappings, bool? enabled = null)
    {
        var copy = new SyncRule
        {
            Id = rule.Id,
            Name = rule.Name,
            Direction = rule.Direction,
            ConnectedSystemId = rule.ConnectedSystemId,
            ConnectedSystem = rule.ConnectedSystem,
            MetaverseObjectTypeId = rule.ResolveMetaverseObjectTypeId(),
            MetaverseObjectType = rule.MetaverseObjectType,
            Enabled = enabled ?? rule.Enabled
        };
        copy.AttributeFlowRules.AddRange(mappings);
        return copy;
    }

    /// <summary>
    /// The evaluation order through a derived flow's inputs, transitively, to its target: every attribute the flow
    /// depends on, grouped by the step it is settled at. Attributes that cannot be ordered (a cycle) are left out.
    /// </summary>
    private static List<DerivedFlowAnalysisStep> BuildStepChain(DerivedFlowGraph graph, IEnumerable<SyncRule> rules, DerivedFlow flow)
    {
        var metaverseObjectTypeId = flow.MetaverseObjectTypeId;
        var derivedContributorsByTarget = rules
            .Where(rule => rule.Direction == SyncRuleDirection.Import && rule.ResolveMetaverseObjectTypeId() == metaverseObjectTypeId)
            .SelectMany(rule => rule.AttributeFlowRules)
            .Select(graph.GetDerivedFlow)
            .Where(contributor => contributor != null)
            .Select(contributor => contributor!)
            .ToLookup(contributor => contributor.TargetAttributeId);

        var names = new Dictionary<int, string> { [flow.TargetAttributeId] = flow.TargetAttributeName };
        var frontier = new Queue<int>();
        frontier.Enqueue(flow.TargetAttributeId);
        while (frontier.TryDequeue(out var attributeId))
        {
            var unvisitedInputs = derivedContributorsByTarget[attributeId]
                .SelectMany(contributor => contributor.Inputs)
                .Where(input => names.TryAdd(input.Id, input.Name));
            foreach (var input in unvisitedInputs)
                frontier.Enqueue(input.Id);
        }

        return names
            .Select(entry => (Level: graph.GetLevel(metaverseObjectTypeId, entry.Key), Name: entry.Value))
            .Where(entry => entry.Level.HasValue)
            .GroupBy(entry => entry.Level!.Value)
            .OrderBy(group => group.Key)
            .Select(group => new DerivedFlowAnalysisStep(
                group.Key + 1,
                group.Select(entry => entry.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }
}

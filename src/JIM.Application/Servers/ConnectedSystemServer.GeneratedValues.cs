// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Exceptions;
using JIM.Application.Expressions;
using JIM.Application.Services;
using JIM.Application.UniqueValues;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Security;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Unique Value Generation's Phase 3 configuration and Metaverse Object surfaces (#242, Work Package A):
/// server-side validation for a generated mapping's settings, and the read/administration methods a generated
/// mapping's own edit form, sequence panel and "Start again" action need. The generation engine itself (Phase 2)
/// lives in <see cref="Application.UniqueValues.UniqueValueGenerationServer"/>, which this server calls into for
/// everything that is purely about the target attribute's counter or assignments; this file owns the parts that
/// need a Synchronisation Rule mapping lookup and an audited Activity, which
/// <see cref="Application.UniqueValues.UniqueValueGenerationServer"/> deliberately knows nothing about.
/// </summary>
public partial class ConnectedSystemServer
{
    /// <summary>
    /// The read-only counter state behind a generated Sequence mapping's "next number" preview (plan Phase 3
    /// point 1: the sequence state panel). Allocates nothing. Null when the mapping does not exist, is not a
    /// generated mapping, or is not a Sequence mapping: the state has no meaning for any other token kind.
    /// </summary>
    public async Task<GeneratedValueSequenceState?> GetGeneratedValueSequenceStateAsync(int mappingId)
    {
        var mapping = await Application.Repository.ConnectedSystems.GetSyncRuleMappingAsync(mappingId);
        if (mapping == null)
            return null;

        return await Application.UniqueValues.GetSequenceStateAsync(mapping);
    }

    /// <summary>
    /// "Start again" (plan "The service": <c>StartAgainAsync</c>; FR 33) for a generated mapping: moves the
    /// target attribute's counter back to the flow's configured start value, records the move as an audited
    /// Activity naming the attribute and the counter's from/to, and returns what changed. See
    /// <see cref="Application.UniqueValues.UniqueValueGenerationServer.RestartAsync"/> for the semantics
    /// (a documented no-op for a non-Sequence mapping or a counter that has never been seeded).
    /// </summary>
    /// <param name="mappingId">The generated mapping whose counter should be restarted.</param>
    /// <param name="initiatedBy">The user who initiated the restart.</param>
    /// <exception cref="ArgumentException">No mapping has <paramref name="mappingId"/>, or it is not a
    /// generated mapping.</exception>
    public Task<GeneratedValueRestartResult> RestartGeneratedValuesAsync(int mappingId, MetaverseObject? initiatedBy)
        => RestartGeneratedValuesCoreAsync(mappingId, initiatedBy, null);

    /// <summary>
    /// "Start again" (initiated by API key). See <see cref="RestartGeneratedValuesAsync(int, MetaverseObject?)"/>.
    /// </summary>
    public Task<GeneratedValueRestartResult> RestartGeneratedValuesAsync(int mappingId, ApiKey initiatedByApiKey)
        => RestartGeneratedValuesCoreAsync(mappingId, null, initiatedByApiKey);

    private async Task<GeneratedValueRestartResult> RestartGeneratedValuesCoreAsync(int mappingId, MetaverseObject? initiatedBy, ApiKey? initiatedByApiKey)
    {
        var mapping = await Application.Repository.ConnectedSystems.GetSyncRuleMappingAsync(mappingId)
            ?? throw new ArgumentException($"No Attribute Flow mapping was found with ID {mappingId}.", nameof(mappingId));

        if (mapping.Generation == null)
            throw new ArgumentException("This Attribute Flow is not a generated mapping; \"Start again\" only applies to generated mappings.", nameof(mappingId));

        var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";

        var result = await Application.UniqueValues.RestartAsync(mapping);

        var activity = new Activity
        {
            TargetName = $"{Activity.SyncRuleMappingTargetNamePrefix}{targetName}",
            TargetContext = mapping.SyncRule?.Name,
            TargetType = ActivityTargetType.SynchronisationRule,
            SyncRuleId = mapping.SyncRule?.Id ?? mapping.SyncRuleId,
            TargetOperationType = ActivityTargetOperationType.RestartGeneratedValues,
            Message = DescribeRestart(targetName, result)
        };

        if (initiatedByApiKey != null)
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        else
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        await Application.Activities.CompleteActivityAsync(activity);

        return result;
    }

    /// <summary>
    /// The Activity message for "Start again": what moved, and how many retired values were forgotten (#242, Phase 6).
    /// </summary>
    private static string DescribeRestart(string targetName, GeneratedValueRestartResult result)
    {
        var counter = result.CounterFrom.HasValue
            ? $"Started the {targetName} counter again: moved from {result.CounterFrom} to {result.CounterTo}."
            : $"\"Start again\" was requested for {targetName}, but its counter had not issued any numbers yet, so it did not move.";

        return result.RetiredValuesForgotten switch
        {
            0 => counter,
            1 => $"{counter} 1 retired value was forgotten and can be issued again.",
            var forgotten => $"{counter} {forgotten:N0} retired values were forgotten and can be issued again."
        };
    }

    /// <summary>
    /// How many values the retired values register holds for a generated mapping's target attribute (#242, Phase 6):
    /// what "Start again" would forget, for its confirmation. Zero for an unknown mapping or one with no target.
    /// </summary>
    public async Task<int> GetRetiredGeneratedValueCountAsync(int mappingId)
    {
        var mapping = await Application.Repository.ConnectedSystems.GetSyncRuleMappingAsync(mappingId);
        return mapping == null ? 0 : await Application.UniqueValues.GetRetiredValueCountAsync(mapping);
    }

    /// <summary>
    /// Every Connected System attribute a saved generated mapping's value is exported to, and how its availability is
    /// checked there (release 3): the "Checked for availability in" panel, the REST mapping DTO's <c>participants</c>
    /// and <c>Get-JIMSyncRuleMapping</c>. Empty when the mapping does not exist or is not generated. See
    /// <see cref="GeneratedValueParticipation.DescribeParticipants"/> for the rules.
    /// </summary>
    public async Task<List<GeneratedValueParticipant>> GetGeneratedValueParticipantsAsync(int mappingId)
    {
        var mapping = await Application.Repository.ConnectedSystems.GetSyncRuleMappingAsync(mappingId);
        if (mapping?.Generation == null)
            return [];

        var participants = await GetGeneratedValueParticipantsAsync([mapping], mapping.SyncRule?.ConnectedSystemId ?? 0);
        return participants.GetValueOrDefault(mapping.Id) ?? [];
    }

    /// <summary>
    /// The participants of a generated mapping that may not be saved yet (the portal's form describes the mapping as
    /// it is being edited). <paramref name="exportRules"/> are the export Synchronisation Rules to describe it against;
    /// when omitted, every export rule JIM holds.
    /// </summary>
    /// <param name="mapping">The generated mapping, saved or not.</param>
    /// <param name="hostConnectedSystemId">The Connected System of the Synchronisation Rule holding the mapping, which
    /// is where an export-mode generated value is checked.</param>
    /// <param name="exportRules">The export rules to describe the mapping against, or null for every one JIM holds.</param>
    public async Task<List<GeneratedValueParticipant>> GetGeneratedValueParticipantsAsync(SyncRuleMapping mapping, int hostConnectedSystemId, IEnumerable<SyncRule>? exportRules = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (mapping.Generation == null)
            return [];

        var rules = exportRules?.ToList() ?? await Application.Repository.ConnectedSystems.GetExportSyncRulesWithAttributeFlowsAsync();
        var participants = await DescribeGeneratedValueParticipantsAsync([mapping], hostConnectedSystemId, rules);
        return participants.GetValueOrDefault(mapping.Id) ?? [];
    }

    /// <summary>
    /// The participants of each generated mapping among <paramref name="mappings"/>, keyed by mapping id, reading the
    /// export rules and Connected Systems once however many mappings there are (the REST list read). Mappings that are
    /// not generated have no entry.
    /// </summary>
    /// <param name="mappings">The mappings, typically one Synchronisation Rule's.</param>
    /// <param name="hostConnectedSystemId">The Connected System of the Synchronisation Rule holding them.</param>
    public async Task<Dictionary<int, List<GeneratedValueParticipant>>> GetGeneratedValueParticipantsAsync(IReadOnlyCollection<SyncRuleMapping> mappings, int hostConnectedSystemId)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        var generated = mappings.Where(m => m.Generation != null).ToList();
        if (generated.Count == 0)
            return [];

        var rules = await Application.Repository.ConnectedSystems.GetExportSyncRulesWithAttributeFlowsAsync();
        return await DescribeGeneratedValueParticipantsAsync(generated, hostConnectedSystemId, rules);
    }

    /// <summary>
    /// Describes <paramref name="mappings"/> against <paramref name="exportRules"/>, asking each Connector whether it can
    /// probe. Each Connector is created through the Connector factory and never connected: whether it probes, and which
    /// attributes, is a property of the Connector, not of its target, so nothing here reaches a Connected System.
    /// </summary>
    private async Task<Dictionary<int, List<GeneratedValueParticipant>>> DescribeGeneratedValueParticipantsAsync(
        IReadOnlyList<SyncRuleMapping> mappings, int hostConnectedSystemId, IReadOnlyList<SyncRule> exportRules)
    {
        var connectedSystems = await Application.Repository.ConnectedSystems.GetConnectedSystemsWithConnectorDefinitionsAsync();
        var probes = new Dictionary<string, IConnectorUniquenessProbe?>(StringComparer.Ordinal);
        var created = new List<IConnector>();
        try
        {
            var systems = connectedSystems.ToDictionary(
                cs => cs.Id,
                cs =>
                {
                    var probe = GetUniquenessProbe(cs.ConnectorDefinition, probes, created);
                    return new GeneratedValueParticipantSystem(cs.Id, cs.Name, cs.ConnectorDefinition?.Name ?? string.Empty,
                        probe != null, attributeName => probe != null && probe.CanProbeAttribute(attributeName),
                        ReportsCollisions: cs.ConnectorDefinition?.SupportsUniquenessRejectionClassification == true);
                });

            return mappings
                .DistinctBy(mapping => mapping.Id)
                .ToDictionary(mapping => mapping.Id, mapping => GeneratedValueParticipation.DescribeParticipants(mapping, hostConnectedSystemId, exportRules, systems));
        }
        finally
        {
            foreach (var connector in created.OfType<IDisposable>())
                connector.Dispose();
        }
    }

    /// <summary>
    /// The uniqueness probe a Connector Definition's Connector offers, or null when it does not declare the capability,
    /// does not implement it, or is not a Connector this JIM knows. One Connector per definition per request.
    /// </summary>
    private IConnectorUniquenessProbe? GetUniquenessProbe(ConnectorDefinition? definition, Dictionary<string, IConnectorUniquenessProbe?> probes, List<IConnector> created)
    {
        if (definition is not { SupportsUniquenessProbe: true })
            return null;

        if (probes.TryGetValue(definition.Name, out var existing))
            return existing;

        IConnectorUniquenessProbe? probe = null;
        try
        {
            var connector = ConnectorFactory.Create(definition.Name);
            created.Add(connector);
            probe = connector as IConnectorUniquenessProbe;
        }
        catch (NotSupportedException ex)
        {
            Log.Warning(ex, "GetUniquenessProbe: Connector Definition {Name} declares the uniqueness probe but no Connector of that name exists; describing it as unable to probe.",
                LogSanitiser.Sanitise(definition.Name));
        }

        probes[definition.Name] = probe;
        return probe;
    }

    /// <summary>
    /// Refuses a generated mapping's exclusions unless each names a Connected System the value is exported to unchanged
    /// (release 3), on every save path. An export-mode generated value is checked only in its own Connected System, so
    /// it takes none. Duplicates are left to <see cref="SyncRuleMappingGenerationValidator"/>. Reads nothing for a
    /// mapping with no exclusions.
    /// </summary>
    /// <exception cref="ArgumentException">An exclusion names a Connected System that cannot be excluded.</exception>
    private async Task ValidateGeneratedValueExclusionsAsync(SyncRuleMapping mapping)
    {
        var errors = await GetGeneratedValueExclusionErrorsAsync([mapping], new Lazy<Task<List<SyncRule>>>(Application.Repository.ConnectedSystems.GetExportSyncRulesWithAttributeFlowsAsync));
        ThrowIfExclusionErrors(errors, "ValidateGeneratedValueExclusionsAsync");
    }

    /// <summary>
    /// The whole-rule-save sibling of <see cref="ValidateGeneratedValueExclusionsAsync(SyncRuleMapping)"/>: every
    /// generated mapping on the rule, the export rules read at most once.
    /// </summary>
    /// <exception cref="ArgumentException">An exclusion names a Connected System that cannot be excluded.</exception>
    private async Task ValidateGeneratedValueExclusionsAsync(SyncRule syncRule)
    {
        var errors = await GetGeneratedValueExclusionErrorsAsync(syncRule.AttributeFlowRules, new Lazy<Task<List<SyncRule>>>(Application.Repository.ConnectedSystems.GetExportSyncRulesWithAttributeFlowsAsync));
        ThrowIfExclusionErrors(errors, "CreateOrUpdateSyncRuleAsync");
    }

    private async Task<List<string>> GetGeneratedValueExclusionErrorsAsync(IEnumerable<SyncRuleMapping> mappings, Lazy<Task<List<SyncRule>>> exportRules)
    {
        var errors = new List<string>();
        Dictionary<int, string>? names = null;

        foreach (var mapping in mappings.Where(m => m.Generation is { Exclusions.Count: > 0 }))
        {
            var metaverseAttributeId = mapping.ResolveTargetMetaverseAttributeId();
            if (!metaverseAttributeId.HasValue)
            {
                var targetName = mapping.TargetConnectedSystemAttribute?.Name ?? "this attribute";
                errors.Add($"A value generated for {targetName} is checked only in its own Connected System, so it cannot exclude Connected Systems; remove the exclusions.");
                continue;
            }

            var excludable = GeneratedValueParticipation.ComputeExcludableSystemIds(metaverseAttributeId.Value, await exportRules.Value);
            var refused = mapping.Generation!.Exclusions.Select(e => e.ConnectedSystemId).Distinct().Where(id => !excludable.Contains(id)).ToList();
            if (refused.Count == 0)
                continue;

            names ??= await Application.Repository.ConnectedSystems.GetConnectedSystemNamesAsync();
            var attributeName = mapping.TargetMetaverseAttribute?.Name ?? "the generated attribute";
            errors.AddRange(refused.Select(id => names.TryGetValue(id, out var name)
                ? $"Connected System \"{name}\" cannot be excluded from {attributeName}'s availability checks: no export Attribute Flow sends {attributeName} to it unchanged."
                : $"Connected System {id} cannot be excluded from {attributeName}'s availability checks: it does not exist."));
        }

        return errors;
    }

    private static void ThrowIfExclusionErrors(List<string> errors, string caller)
    {
        if (errors.Count == 0)
            return;

        var message = string.Join(" ", errors);
        Log.Warning("{Caller}: rejecting generated value exclusions; {Message}", caller, LogSanitiser.Sanitise(message));
        throw new ArgumentException(message);
    }

    /// <summary>
    /// The whole-rule-save counterpart of the single-mapping create/update paths' save-time counter raise
    /// (Unique Value Generation, #242, Phase 3 follow-up; plan decision 3). Called from both
    /// <c>CreateOrUpdateSyncRuleAsync</c> overloads, after the rule (and any newly added mappings within it)
    /// has been persisted, so every mapping's id is populated. For every generated Sequence mapping on the rule
    /// whose configured
    /// <see cref="SyncRuleMappingGeneration.SequenceStart"/> now stands above its target attribute's counter,
    /// raises the counter and stamps the move onto that mapping's transient
    /// <see cref="SyncRuleMappingGeneration.SequenceSkippedAhead"/>, exactly as the single-mapping paths do; the
    /// caller (the portal's Attribute Flow tab saves exclusively through <c>CreateOrUpdateSyncRuleAsync</c>, not
    /// the single-mapping endpoints) already holds the same <paramref name="syncRule"/> instance and can read the
    /// stamp off each mapping once this returns. A no-op, and no repository call, for every mapping that is not
    /// a generated Sequence mapping.
    /// </summary>
    private async Task ApplyGeneratedValueSequenceSkipsAsync(SyncRule syncRule)
    {
        foreach (var mapping in syncRule.AttributeFlowRules)
        {
            if (mapping.Generation != null)
                mapping.Generation.SequenceSkippedAhead = await Application.UniqueValues.RaiseSequenceStartIfHigherAsync(mapping);
        }
    }

    /// <summary>
    /// The stored generation settings of the given mappings' generated flows, read before a save flushes anything so
    /// <see cref="ReleaseNeedsDecisionIfGenerationChangedAsync"/> can tell what the save changed (Unique Value Generation,
    /// #242, release 4). No repository call when none of them is a saved generated flow.
    /// </summary>
    private Task<Dictionary<int, SyncRuleMappingGeneration>> ReadStoredGenerationsAsync(IEnumerable<SyncRuleMapping> mappings)
    {
        var ids = mappings.Where(m => m.Generation is { Id: > 0 }).Select(m => m.Generation!.Id).Distinct().ToList();
        return ids.Count == 0
            ? Task.FromResult(new Dictionary<int, SyncRuleMappingGeneration>())
            : Application.Repository.ConnectedSystems.GetSyncRuleMappingGenerationsAsync(ids);
    }

    /// <summary>
    /// Releases the Needs Decision of every generated flow among <paramref name="mappings"/> whose generation
    /// configuration the save changed (FR 16; Unique Value Generation, #242, release 4), mirroring
    /// <c>ReleaseParkedInitialPasswordsIfDeliveryChangedAsync</c>: the change is the administrator's answer, so the
    /// assignments return to Committed and their parked exports to Pending, for the next export run to try under the new
    /// configuration. Gated on an actual change, so an unrelated edit does not set them retrying against settings the
    /// target has already answered.
    /// </summary>
    private async Task ReleaseNeedsDecisionIfGenerationChangedAsync(IEnumerable<SyncRuleMapping> mappings, Dictionary<int, SyncRuleMappingGeneration> previous)
    {
        foreach (var generation in mappings
                     .Select(m => m.Generation)
                     .OfType<SyncRuleMappingGeneration>()
                     .Where(g => previous.TryGetValue(g.Id, out var stored) && !SyncRuleMappingGeneration.WouldGenerateTheSameAs(stored, g)))
        {
            var released = await Application.UniqueValues.ReleaseNeedsDecisionForMappingAsync(generation.Id);
            if (released > 0)
                Log.Information("ReleaseNeedsDecisionIfGenerationChangedAsync: the generation configuration of flow {GenerationId} changed, so " +
                    "{Count} generated value(s) waiting on a decision were released for the next export to try again", generation.Id, released);
        }
    }

    /// <summary>
    /// Why the "Keep unique" switch was refused on a mapping it can never apply to.
    /// </summary>
    internal const string KeepUniqueNotApplicableMessage =
        "Keep unique applies only to an Attribute Flow that builds its value with an Expression (a value built from a generated value), " +
        "not to a direct flow or to a generated value itself.";

    /// <summary>
    /// Whether <paramref name="mapping"/> can carry the "Keep unique" switch (Generated Value Forms, #2023): an expression
    /// mapping that is not itself generated. Whether it is built from a generated value right now depends on the other
    /// mappings, which may be saved in any order, so the switch is accepted on any such mapping and simply has no effect
    /// until it is.
    /// </summary>
    internal static bool CanKeepUnique(SyncRuleMapping mapping) =>
        mapping.Generation == null && mapping.Sources.Any(source => !string.IsNullOrWhiteSpace(source.Expression));

    /// <summary>
    /// The values built from generated values among the Synchronisation Rules of one Metaverse Object Type (Generated
    /// Value Forms, #2023): its import rules and every export rule for the type, as saved, or with
    /// <paramref name="stagedRule"/> in place of its saved self (the portal describes a rule being edited).
    /// </summary>
    internal async Task<GeneratedValueBuiltValues> GetGeneratedValueBuiltValuesAsync(int metaverseObjectTypeId, SyncRule? stagedRule = null,
        IReadOnlyList<SyncRule>? allExportRules = null)
    {
        var importRules = await Application.Repository.ConnectedSystems.GetImportSyncRulesForMetaverseObjectTypeAsync(metaverseObjectTypeId) ?? [];
        var exportRules = (allExportRules ?? await Application.Repository.ConnectedSystems.GetExportSyncRulesWithAttributeFlowsAsync() ?? [])
            .Where(rule => rule.MetaverseObjectTypeId == metaverseObjectTypeId);
        List<SyncRule> rules = [.. importRules, .. exportRules];
        if (stagedRule != null)
        {
            if (stagedRule.Id > 0)
                rules.RemoveAll(rule => rule.Id == stagedRule.Id);
            rules.Add(stagedRule);
        }

        // The rule queries do not load the type, and mv["..."] names are resolved against its attributes.
        var metaverseObjectType = await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(metaverseObjectTypeId, true)
            ?? stagedRule?.MetaverseObjectType;
        List<MetaverseObjectType> types = metaverseObjectType == null ? [] : [metaverseObjectType];

        DerivedFlowGraph? graph = null;
        try
        {
            graph = DerivedFlowGraphFactory.Create(rules, types);
        }
        catch (DerivedFlowCycleException)
        {
            // A cycle is refused at save time; one that predates that check simply means nothing is followed through a
            // derivation until it is broken.
        }

        return GeneratedValueBuiltValues.Create(rules, graph, types);
    }

    /// <summary>
    /// What each expression Attribute Flow among <paramref name="mappings"/> (one Synchronisation Rule's) is built from,
    /// keyed by mapping id (Generated Value Forms, #2023): the REST mapping DTO's <c>builtFromGeneratedValue</c>. A mapping
    /// built from no generated value has no entry.
    /// </summary>
    public async Task<Dictionary<int, GeneratedValueBuiltValueDescription>> GetBuiltFromGeneratedValuesAsync(SyncRule rule, IReadOnlyCollection<SyncRuleMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(mappings);

        if (!mappings.Any(CanKeepUnique))
            return [];

        var context = await LoadBuiltValueContextAsync(rule.MetaverseObjectTypeId, stagedRule: null);
        var described = new Dictionary<int, GeneratedValueBuiltValueDescription>();
        foreach (var mapping in mappings.Where(CanKeepUnique))
        {
            mapping.SyncRule ??= rule;
            var value = context.BuiltValues.BuiltValueOf(mapping);
            if (value != null)
                described[mapping.Id] = context.Describe(value);
        }

        return described;
    }

    /// <summary>
    /// The values built from each import-mode generated Attribute Flow among <paramref name="mappings"/> (one
    /// Synchronisation Rule's), keyed by the generated mapping's id (Generated Value Forms, #2023): the REST mapping DTO's
    /// <c>generation.builtValues</c>. Ordered by Synchronisation Rule, then value name.
    /// </summary>
    public async Task<Dictionary<int, List<GeneratedValueBuiltValueDescription>>> GetValuesBuiltFromGeneratedValuesAsync(SyncRule rule, IReadOnlyCollection<SyncRuleMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(mappings);

        var generated = mappings.Where(m => m.Generation != null && m.ResolveTargetMetaverseAttributeId().HasValue).ToList();
        if (generated.Count == 0)
            return [];

        var context = await LoadBuiltValueContextAsync(rule.MetaverseObjectTypeId, stagedRule: null);
        return generated.ToDictionary(mapping => mapping.Id, mapping =>
        {
            mapping.SyncRule ??= rule;
            return context.DescribeAll(context.BuiltValues.BuiltFrom(mapping));
        });
    }

    /// <summary>
    /// What an Attribute Flow being edited is built from (Generated Value Forms, #2023): the portal's "Built from generated
    /// value" block. <paramref name="stagedRule"/> is the Synchronisation Rule as it is being edited, holding
    /// <paramref name="proposed"/>; every other rule is read as saved. Null when it is built from no generated value.
    /// </summary>
    public async Task<GeneratedValueBuiltValueDescription?> DescribeBuiltFromGeneratedValueAsync(SyncRule stagedRule, SyncRuleMapping proposed)
    {
        ArgumentNullException.ThrowIfNull(stagedRule);
        ArgumentNullException.ThrowIfNull(proposed);

        if (!CanKeepUnique(proposed))
            return null;

        var context = await LoadBuiltValueContextAsync(stagedRule.MetaverseObjectTypeId, StagedWith(stagedRule, proposed));
        proposed.SyncRule ??= stagedRule;
        var value = context.BuiltValues.BuiltValueOf(proposed);
        return value == null ? null : context.Describe(value);
    }

    /// <summary>
    /// The values built from a generated Attribute Flow being edited (Generated Value Forms, #2023): the portal's "Values
    /// built from ..." table. <paramref name="stagedRule"/> is the Synchronisation Rule as it is being edited.
    /// </summary>
    public async Task<List<GeneratedValueBuiltValueDescription>> DescribeValuesBuiltFromGeneratedValueAsync(SyncRule stagedRule, SyncRuleMapping generatedMapping)
    {
        ArgumentNullException.ThrowIfNull(stagedRule);
        ArgumentNullException.ThrowIfNull(generatedMapping);

        if (generatedMapping.Generation == null || !generatedMapping.ResolveTargetMetaverseAttributeId().HasValue)
            return [];

        var context = await LoadBuiltValueContextAsync(stagedRule.MetaverseObjectTypeId, StagedWith(stagedRule, generatedMapping));
        generatedMapping.SyncRule ??= stagedRule;
        return context.DescribeAll(context.BuiltValues.BuiltFrom(generatedMapping));
    }

    /// <summary>
    /// <paramref name="stagedRule"/> with <paramref name="mapping"/> among its Attribute Flows: the portal edits a flow in
    /// a dialog before it is added to the rule it belongs to.
    /// </summary>
    private static SyncRule StagedWith(SyncRule stagedRule, SyncRuleMapping mapping)
    {
        if (stagedRule.AttributeFlowRules.Contains(mapping))
            return stagedRule;

        var copy = new SyncRule
        {
            Id = stagedRule.Id,
            Name = stagedRule.Name,
            Direction = stagedRule.Direction,
            Enabled = stagedRule.Enabled,
            ConnectedSystemId = stagedRule.ConnectedSystemId,
            ConnectedSystem = stagedRule.ConnectedSystem,
            MetaverseObjectTypeId = stagedRule.MetaverseObjectTypeId,
            MetaverseObjectType = stagedRule.MetaverseObjectType
        };
        copy.AttributeFlowRules.AddRange(stagedRule.AttributeFlowRules.Where(m => m.Id == 0 || m.Id != mapping.Id));
        copy.AttributeFlowRules.Add(mapping);
        return copy;
    }

    /// <summary>
    /// The values built from generated values for one Metaverse Object Type, with the names needed to describe them.
    /// </summary>
    private async Task<BuiltValueContext> LoadBuiltValueContextAsync(int metaverseObjectTypeId, SyncRule? stagedRule)
    {
        var exportRules = await Application.Repository.ConnectedSystems.GetExportSyncRulesWithAttributeFlowsAsync() ?? [];
        var builtValues = await GetGeneratedValueBuiltValuesAsync(metaverseObjectTypeId, stagedRule, exportRules);
        var systemNames = await Application.Repository.ConnectedSystems.GetConnectedSystemNamesAsync() ?? [];
        var attributeNames = exportRules
            .SelectMany(r => r.AttributeFlowRules)
            .Where(m => m.TargetConnectedSystemAttribute != null)
            .Select(m => m.TargetConnectedSystemAttribute!)
            .DistinctBy(a => a.Id)
            .ToDictionary(a => a.Id, a => a.Name);
        if (stagedRule != null)
            foreach (var attribute in stagedRule.AttributeFlowRules.Select(m => m.TargetConnectedSystemAttribute).OfType<ConnectedSystemObjectTypeAttribute>())
                attributeNames.TryAdd(attribute.Id, attribute.Name);

        // The export rules the run checks values built from a generated value against (Generated Value Forms, #2023), with
        // an export rule being edited in place of its saved self, and the attributes their Connectors would probe.
        List<SyncRule> formExportRules = stagedRule is { Direction: SyncRuleDirection.Export }
            ? [.. exportRules.Where(rule => stagedRule.Id <= 0 || rule.Id != stagedRule.Id), stagedRule]
            : exportRules;
        var probeable = await GetProbeableExportTargetsAsync(formExportRules);

        return new BuiltValueContext(builtValues, systemNames, attributeNames, formExportRules, probeable);
    }

    /// <summary>
    /// The (Connected System, attribute) pairs among <paramref name="exportRules"/>' targets whose Connector would probe the
    /// attribute (Generated Value Forms, #2023), asking each Connector as <see cref="DescribeGeneratedValueParticipantsAsync"/>
    /// does, without connecting to anything.
    /// </summary>
    private async Task<HashSet<(int ConnectedSystemId, int AttributeId)>> GetProbeableExportTargetsAsync(IReadOnlyList<SyncRule> exportRules)
    {
        var targets = exportRules
            .SelectMany(rule => rule.AttributeFlowRules
                .Select(mapping => mapping.TargetConnectedSystemAttribute)
                .OfType<ConnectedSystemObjectTypeAttribute>()
                .Select(attribute => (rule.ConnectedSystemId, Attribute: attribute)))
            .ToList();
        if (targets.Count == 0)
            return [];

        var connectedSystems = await Application.Repository.ConnectedSystems.GetConnectedSystemsWithConnectorDefinitionsAsync() ?? [];
        var probes = new Dictionary<string, IConnectorUniquenessProbe?>(StringComparer.Ordinal);
        var created = new List<IConnector>();
        try
        {
            var probeBySystem = connectedSystems.ToDictionary(cs => cs.Id, cs => GetUniquenessProbe(cs.ConnectorDefinition, probes, created));
            return targets
                .Where(target => probeBySystem.GetValueOrDefault(target.ConnectedSystemId)?.CanProbeAttribute(target.Attribute.Name) == true)
                .Select(target => (target.ConnectedSystemId, target.Attribute.Id))
                .ToHashSet();
        }
        finally
        {
            foreach (var connector in created.OfType<IDisposable>())
                connector.Dispose();
        }
    }

    /// <summary>
    /// The values built from generated values for one Metaverse Object Type, and the names to describe them with.
    /// </summary>
    private sealed record BuiltValueContext(
        GeneratedValueBuiltValues BuiltValues,
        IReadOnlyDictionary<int, string> SystemNames,
        IReadOnlyDictionary<int, string> AttributeNames,
        IReadOnlyList<SyncRule> FormExportRules,
        IReadOnlySet<(int ConnectedSystemId, int AttributeId)> Probeable)
    {
        /// <summary>
        /// The values checked before each generated value is issued, by generated mapping, worked out once each by the
        /// run's own rule (<see cref="GeneratedValueParticipation.DescribeForms"/>).
        /// </summary>
        private readonly Dictionary<SyncRuleMapping, List<GeneratedValueForm>> _forms = new(ReferenceEqualityComparer.Instance);

        public List<GeneratedValueBuiltValueDescription> DescribeAll(IEnumerable<GeneratedValueBuiltValue> values) =>
            values
                .Select(Describe)
                .OrderBy(d => d.SyncRuleName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.MetaverseAttributeName ?? d.ConnectedSystemAttributeName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public GeneratedValueBuiltValueDescription Describe(GeneratedValueBuiltValue value) => new()
        {
            SyncRuleMappingId = value.Mapping.Id,
            SyncRuleId = value.SyncRule.Id,
            SyncRuleName = value.SyncRule.Name,
            GeneratedSyncRuleMappingId = value.GeneratedMapping.Id,
            GeneratedAttributeName = value.BuiltFromAttributeNames.FirstOrDefault() ?? string.Empty,
            BuiltFromAttributeNames = value.BuiltFromAttributeNames,
            MetaverseAttributeName = value.MetaverseAttributeName,
            ConnectedSystemAttributeName = value.MetaverseAttributeId.HasValue
                ? null
                : value.Mapping.TargetConnectedSystemAttribute?.Name ?? AttributeName(value.Mapping.ResolveTargetConnectedSystemAttributeId()),
            Via = value.Via,
            Kind = value.Kind,
            KeepUnique = value.KeepUnique,
            CollisionRemediation = value.GeneratedMapping.Generation?.CollisionRemediation ?? true,
            ExportedTo = value.ExportedTo
                .Select(t => new GeneratedValueBuiltValueTarget
                {
                    ConnectedSystemId = t.ConnectedSystemId,
                    ConnectedSystemName = SystemName(t.ConnectedSystemId),
                    AttributeId = t.AttributeId,
                    AttributeName = AttributeName(t.AttributeId)
                })
                .OrderBy(t => t.ConnectedSystemName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.AttributeName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            CheckedBeforeIssuing = FormOf(value) != null,
            ProbedIn = (FormOf(value)?.ProbeTargets ?? [])
                .Where(target => Probeable.Contains((target.ConnectedSystemId, target.ConnectedSystemObjectTypeAttributeId)))
                .Select(target => SystemName(target.ConnectedSystemId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            MoveToSyncRuleName = MoveToSyncRuleName(value),
            Expression = value.Mapping.Sources.Count == 1 ? value.Mapping.Sources[0].Expression : null
        };

        /// <summary>
        /// The form <paramref name="value"/> is checked as before its generated value is issued, or null when it is not.
        /// </summary>
        private GeneratedValueForm? FormOf(GeneratedValueBuiltValue value)
        {
            if (!_forms.TryGetValue(value.GeneratedMapping, out var forms))
            {
                forms = GeneratedValueParticipation.DescribeForms(value.GeneratedMapping, BuiltValues, FormExportRules);
                _forms[value.GeneratedMapping] = forms;
            }

            return forms.FirstOrDefault(form => ReferenceEquals(form.BuiltValue.Mapping, value.Mapping)
                || (value.Mapping.Id > 0 && form.BuiltValue.Mapping.Id == value.Mapping.Id));
        }

        /// <summary>
        /// The generated flow's own Synchronisation Rule, when <paramref name="value"/> is a Derived Attribute Flow on
        /// another rule that reads the generated value directly and nothing but Metaverse attributes: moved there, it
        /// would be checked before issuing (PRD requirement 9). Null for anything else, including a value built through
        /// another value, where moving this one flow alone would not be enough.
        /// </summary>
        private static string? MoveToSyncRuleName(GeneratedValueBuiltValue value)
        {
            if (value.Kind != GeneratedValueBuiltValueKind.BuiltOnAnotherRule || !value.MetaverseAttributeId.HasValue || value.Via.Count > 0)
                return null;

            var readsOnlyMetaverse = value.Mapping.Sources.All(source => !string.IsNullOrWhiteSpace(source.Expression)
                && ExpressionInputResolver.ResolveCached(source.Expression).All(input => input.Source == ExpressionInputSource.Metaverse));
            return readsOnlyMetaverse ? value.GeneratedMapping.SyncRule?.Name : null;
        }

        private string SystemName(int connectedSystemId) =>
            SystemNames.TryGetValue(connectedSystemId, out var name) ? name : $"Connected System {connectedSystemId}";

        private string AttributeName(int? attributeId) =>
            attributeId.HasValue && AttributeNames.TryGetValue(attributeId.Value, out var name) ? name : $"Attribute {attributeId}";
    }

    /// <summary>
    /// The stored "Keep unique" switch of every saved expression mapping among <paramref name="mappings"/>, read before a
    /// save can flush, so <see cref="ReleaseNeedsDecisionIfKeepUniqueChangedAsync(IEnumerable{SyncRuleMapping}, IReadOnlyDictionary{int, bool})"/>
    /// can tell what the save changed. No repository call when none could carry the switch.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, bool>> ReadStoredKeepUniqueAsync(IEnumerable<SyncRuleMapping> mappings)
    {
        var ids = mappings.Where(m => m.Id > 0 && CanKeepUnique(m)).Select(m => m.Id).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<int, bool>();

        return await Application.Repository.ConnectedSystems.GetSyncRuleMappingKeepUniqueAsync(ids) ?? [];
    }

    /// <summary>
    /// Releases the Needs Decision of the generated values whose built values' "Keep unique" switch the save changed
    /// (Generated Value Forms, #2023): the change is the administrator's answer (a refusal of the value is no longer
    /// corrected, or now is), so the waiting assignments return to Committed for the next export run to try again, as a
    /// change to the generated flow's own configuration does.
    /// </summary>
    private async Task ReleaseNeedsDecisionIfKeepUniqueChangedAsync(IEnumerable<SyncRuleMapping> mappings, IReadOnlyDictionary<int, bool> previous)
    {
        var changed = mappings.Where(m => previous.TryGetValue(m.Id, out var stored) && stored != m.KeepUnique).ToList();
        foreach (var mapping in changed)
            await ReleaseNeedsDecisionIfKeepUniqueChangedAsync(mapping);
    }

    /// <summary>
    /// Releases the Needs Decision of the generated value <paramref name="mapping"/>'s value is built from, after its
    /// "Keep unique" switch changed.
    /// </summary>
    private async Task ReleaseNeedsDecisionIfKeepUniqueChangedAsync(SyncRuleMapping mapping)
    {
        var typeId = mapping.SyncRule?.MetaverseObjectTypeId;
        if (!typeId.HasValue)
            return;

        var builtValues = await GetGeneratedValueBuiltValuesAsync(typeId.Value);
        foreach (var generation in builtValues.GeneratedValuesBuilding(mapping).Select(m => m.Generation).OfType<SyncRuleMappingGeneration>())
        {
            var released = await Application.UniqueValues.ReleaseNeedsDecisionForMappingAsync(generation.Id);
            if (released > 0)
                Log.Information("ReleaseNeedsDecisionIfKeepUniqueChangedAsync: the Keep unique switch of mapping {MappingId}, built from flow {GenerationId}, " +
                    "changed, so {Count} generated value(s) waiting on a decision were released for the next export to try again", mapping.Id, generation.Id, released);
        }
    }

    /// <summary>
    /// Refuses a "Keep unique" switch turned off on a mapping it can never apply to (a direct flow, or a generated value
    /// itself), so an administrator is never left believing a value was opted out when nothing was.
    /// </summary>
    /// <exception cref="ArgumentException">The switch is off on a mapping that cannot carry it.</exception>
    private static void ValidateKeepUnique(SyncRuleMapping mapping)
    {
        if (!mapping.KeepUnique && !CanKeepUnique(mapping))
        {
            var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";
            throw new ArgumentException($"{targetName}: {KeepUniqueNotApplicableMessage}");
        }
    }

    /// <summary>
    /// <see cref="ValidateKeepUnique(SyncRuleMapping)"/> for every mapping of a whole-rule save.
    /// </summary>
    private static void ValidateKeepUnique(SyncRule syncRule)
    {
        foreach (var mapping in syncRule.AttributeFlowRules)
            ValidateKeepUnique(mapping);
    }

    /// <summary>
    /// Validates a single generated mapping's settings (Unique Value Generation, #242, Phase 3 point 1) via
    /// <see cref="SyncRuleMappingGenerationValidator"/>, and refuses the save with every problem found joined
    /// into one message, the same way <see cref="ValidateMappingTypeCompatibility"/> and
    /// <see cref="ValidateMappingWritability"/> do. A no-op for a mapping that is not a generated mapping.
    /// </summary>
    /// <exception cref="ArgumentException">The generated mapping's settings are invalid.</exception>
    private static void ValidateGeneratedMapping(SyncRuleMapping mapping)
    {
        if (mapping.Generation == null)
            return;

        var errors = SyncRuleMappingGenerationValidator.Validate(mapping);
        if (errors.Count == 0)
            return;

        var message = string.Join(" ", errors);
        Log.Warning("ValidateGeneratedMapping: rejecting generated mapping; {Message}", LogSanitiser.Sanitise(message));
        throw new ArgumentException(message);
    }

    /// <summary>
    /// The whole-rule-save sibling of <see cref="ValidateGeneratedMapping"/>: validates every generated mapping
    /// in <paramref name="syncRule"/>'s Attribute Flow collection, for <see cref="CreateOrUpdateSyncRuleAsync"/>
    /// (Phase 3 point 1). Each mapping's problems are attributed to its target attribute name so a rule with
    /// several generated mappings gets one clear, combined rejection rather than only the first mapping's.
    /// </summary>
    /// <exception cref="ArgumentException">A generated mapping's settings are invalid.</exception>
    private static void ValidateGeneratedMappings(SyncRule syncRule)
    {
        var errors = new List<string>();

        foreach (var mapping in syncRule.AttributeFlowRules)
        {
            if (mapping.Generation == null)
                continue;

            var mappingErrors = SyncRuleMappingGenerationValidator.Validate(mapping);
            if (mappingErrors.Count == 0)
                continue;

            var targetName = mapping.TargetMetaverseAttribute?.Name ?? mapping.TargetConnectedSystemAttribute?.Name ?? "Unknown";
            errors.AddRange(mappingErrors.Select(error => $"{targetName}: {error}"));
        }

        if (errors.Count == 0)
            return;

        var message = string.Join(" ", errors);
        Log.Warning("CreateOrUpdateSyncRuleAsync: rejecting Synchronisation Rule; {Message}", LogSanitiser.Sanitise(message));
        throw new ArgumentException(message);
    }
}

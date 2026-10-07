// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.UniqueValues;
using JIM.Models.Activities;
using JIM.Models.Core;
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
                        probe != null, attributeName => probe != null && probe.CanProbeAttribute(attributeName));
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

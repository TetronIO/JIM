// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
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
            Message = result.CounterFrom.HasValue
                ? $"Started the {targetName} counter again: moved from {result.CounterFrom} to {result.CounterTo}."
                : $"\"Start again\" was requested for {targetName}, but its counter had not issued any numbers yet, so nothing moved."
        };

        if (initiatedByApiKey != null)
            await Application.Activities.CreateActivityAsync(activity, initiatedByApiKey);
        else
            await Application.Activities.CreateActivityAsync(activity, initiatedBy);
        await Application.Activities.CompleteActivityAsync(activity);

        return result;
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

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Servers;

/// <summary>
/// Value provenance (#399): where a Metaverse Object attribute value came from, why it won over the other
/// candidates, and its history. See <c>engineering/plans/done/SYNC_RULE_CAUSALITY_TRACKING.md</c> for the agreed
/// design. Kept as its own partial file (matching the <c>SyncEngine.*.cs</c> convention) so
/// <see cref="MetaverseServer"/>'s main file stays uncluttered.
/// </summary>
public partial class MetaverseServer
{
    /// <summary>
    /// Side-effect-free expression evaluator for evaluating an Attribute Priority candidate's expression mapping
    /// (#399), matching the pattern used by <c>SyncPreviewServer</c> and <c>ExampleDataServer</c>: cheap and
    /// stateless, so constructed inline rather than injected.
    /// </summary>
    private readonly IExpressionEvaluator _provenanceExpressionEvaluator = new DynamicExpressoEvaluator();

    /// <summary>
    /// Returns the origin of every attribute value on a Metaverse Object (#399): which Connected System and
    /// Synchronisation Rule contributed it, or that no contributor is recorded. Drives the Inspect view.
    /// Returns null when the Metaverse Object does not exist.
    /// </summary>
    public async Task<MetaverseObjectProvenance?> GetMetaverseObjectProvenanceAsync(Guid metaverseObjectId)
    {
        var provenance = await Application.Repository.Metaverse.GetMetaverseObjectProvenanceAsync(metaverseObjectId);
        if (provenance == null)
            return null;

        var generatedValues = await GetGeneratedValuesByAttributeAsync(metaverseObjectId);
        if (generatedValues.Count == 0)
            return provenance;

        foreach (var attribute in provenance.Attributes)
        {
            var ownership = generatedValues.GetValueOrDefault(attribute.AttributeId);
            attribute.Origins = attribute.Origins.Select(o => ProvenanceLogic.ApplyGeneratedValue(o, ownership)).ToList();
        }

        return provenance;
    }

    /// <summary>
    /// The Generated Values a Metaverse Object holds, keyed by attribute. A Generated Value only ever targets a
    /// single-valued attribute, so there is at most one per attribute; should that ever not hold, the first wins
    /// rather than failing a read-only display.
    /// </summary>
    private async Task<Dictionary<int, GeneratedValueOwnership>> GetGeneratedValuesByAttributeAsync(Guid metaverseObjectId)
    {
        var ownerships = await Application.Repository.Metaverse.GetGeneratedValueOwnershipsAsync(metaverseObjectId);
        return (ownerships ?? new List<GeneratedValueOwnership>())
            .GroupBy(o => o.AttributeId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>
    /// Returns everything the attribute inspector shows for one attribute on one Metaverse Object (#399): current
    /// values and origin, the contributing Connected System Object, the change that last set the value, every
    /// contributing source in priority order with the value it would supply, and the attribute's history.
    /// Returns null when the Metaverse Object or the attribute does not exist.
    /// </summary>
    public async Task<MetaverseAttributeProvenance?> GetMetaverseAttributeProvenanceAsync(Guid metaverseObjectId, int attributeId)
    {
        const int currentValueCap = 50;
        const int candidateValueCap = 20;
        const int historyCap = 50;
        const int historyRawCap = 150;

        var attribute = await Application.Repository.Metaverse.GetMetaverseAttributeAsync(attributeId);
        if (attribute == null)
            return null;

        var metaverseObjectTypeId = await Application.Repository.Metaverse.GetMetaverseObjectTypeIdAsync(metaverseObjectId);
        if (metaverseObjectTypeId == null)
            return null;

        var (currentValues, currentValueTotalCount) = await Application.Repository.Metaverse.GetMetaverseAttributeCurrentValuesAsync(
            metaverseObjectId, attributeId, currentValueCap);

        var generatedValue = (await GetGeneratedValuesByAttributeAsync(metaverseObjectId)).GetValueOrDefault(attributeId);
        foreach (var value in currentValues)
            value.Origin = ProvenanceLogic.ApplyGeneratedValue(value.Origin, generatedValue);

        var result = new MetaverseAttributeProvenance
        {
            MetaverseObjectId = metaverseObjectId,
            MetaverseObjectTypeId = metaverseObjectTypeId.Value,
            AttributeId = attribute.Id,
            AttributeName = attribute.Name,
            AttributeType = attribute.Type,
            AttributePlurality = attribute.AttributePlurality,
            CurrentValues = currentValues,
            CurrentValueTotalCount = currentValueTotalCount
        };

        // Contributing Connected System Object: derived from the first value whose origin names a Connected
        // System (a multi-valued attribute with several origins shows only the first here; the inspector's
        // per-value origin covers the rest). A Generated Value names its Connected System, but that system's
        // object did not supply it, so it has no contributing Connected System Object.
        var contributingOrigin = currentValues.Select(v => v.Origin)
            .FirstOrDefault(o => o.ConnectedSystemId.HasValue && o.Kind != ValueOriginKind.GeneratedValue);
        if (contributingOrigin != null)
        {
            result.ContributingConnectedSystemObject = await Application.Repository.Metaverse.GetContributingConnectedSystemObjectAsync(
                metaverseObjectId, contributingOrigin.ConnectedSystemId!.Value, contributingOrigin.SyncRuleId);
        }

        result.LastSet = await Application.Repository.Metaverse.GetLastAttributeSetChangeAsync(metaverseObjectId, attributeId);

        // Sources: every import mapping targeting this attribute for this Metaverse Object Type, in priority
        // order, with the value each would currently supply for this Metaverse Object.
        var mappings = await Application.ConnectedSystems.GetAttributePriorityOrderAsync(metaverseObjectTypeId.Value, attributeId);
        var currentContributorSyncRuleIds = currentValues
            .Select(v => v.Origin.SyncRuleId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        var joinedCsoCache = new Dictionary<(int ConnectedSystemId, int ConnectedSystemObjectTypeId), ConnectedSystemObject?>();
        var rank = 0;
        foreach (var mapping in mappings)
        {
            rank++;
            result.Sources.Add(await BuildAttributeSourceCandidateAsync(
                mapping, rank, attribute, currentContributorSyncRuleIds, joinedCsoCache, metaverseObjectId, candidateValueCap, generatedValue));
        }

        // History: raw Add/Remove rows, newest change first, paired into display entries by the pure
        // ProvenanceLogic helper so the pairing rules are unit-testable without a database.
        var rawHistory = await Application.Repository.Metaverse.GetAttributeHistoryRawEntriesAsync(metaverseObjectId, attributeId, historyRawCap);
        var (history, truncatedByPairing) = ProvenanceLogic.PairAttributeHistory(rawHistory, attribute.AttributePlurality, historyCap);
        foreach (var entry in history)
            entry.IsGeneratedValue = ProvenanceLogic.IsGeneratedHistoryValue(entry, generatedValue);
        result.History = history;
        result.HistoryTruncated = truncatedByPairing || rawHistory.Count >= historyRawCap;

        return result;
    }

    /// <summary>
    /// Builds one <see cref="AttributeSourceCandidate"/>: resolves the mapping's joined Connected System Object
    /// (cached per Connected System + Connected System Object Type, since several priority ranks commonly share
    /// one), evaluates the value it would currently supply, and assigns its <see cref="AttributeSourceState"/>
    /// via the pure <see cref="ProvenanceLogic"/> rules.
    /// </summary>
    private async Task<AttributeSourceCandidate> BuildAttributeSourceCandidateAsync(
        SyncRuleMapping mapping,
        int rank,
        MetaverseAttribute targetAttribute,
        HashSet<int> currentContributorSyncRuleIds,
        Dictionary<(int ConnectedSystemId, int ConnectedSystemObjectTypeId), ConnectedSystemObject?> joinedCsoCache,
        Guid metaverseObjectId,
        int candidateValueCap,
        GeneratedValueOwnership? generatedValue)
    {
        var sourceType = mapping.GetSourceType();
        var expressionSource = mapping.Sources
            .OrderBy(s => s.Order)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Expression));

        var candidate = new AttributeSourceCandidate
        {
            Rank = rank,
            MappingId = mapping.Id,
            SyncRuleId = mapping.SyncRuleId,
            SyncRuleName = mapping.SyncRule?.Name ?? string.Empty,
            ConnectedSystemId = mapping.SyncRule?.ConnectedSystemId ?? 0,
            ConnectedSystemName = mapping.SyncRule?.ConnectedSystem?.Name ?? string.Empty,
            IsExpression = sourceType == SyncRuleMappingSourcesType.ExpressionMapping,
            IsGeneratedValue = sourceType == SyncRuleMappingSourcesType.GeneratedMapping,
            Expression = sourceType == SyncRuleMappingSourcesType.ExpressionMapping ? expressionSource?.Expression : null
        };

        var mappingOrRuleDisabled = !mapping.Enabled || mapping.SyncRule is { Enabled: false };
        if (mappingOrRuleDisabled)
        {
            candidate.State = AttributeSourceState.Disabled;
            return candidate;
        }

        if (mapping.SyncRule == null)
        {
            // Defensive: GetAttributePriorityOrderAsync always Includes the owning Synchronisation Rule, but a
            // mapping cannot be evaluated without knowing which Connected System and Object Type join it against.
            candidate.State = AttributeSourceState.NotEvaluated;
            candidate.Note = "Synchronisation Rule not available.";
            return candidate;
        }

        var cacheKey = (mapping.SyncRule.ConnectedSystemId, mapping.SyncRule.ConnectedSystemObjectTypeId);
        if (!joinedCsoCache.TryGetValue(cacheKey, out var cso))
        {
            cso = await Application.Repository.Metaverse.GetJoinedConnectedSystemObjectForProvenanceAsync(
                metaverseObjectId, cacheKey.Item1, cacheKey.Item2);
            joinedCsoCache[cacheKey] = cso;
        }

        var fixedState = ProvenanceLogic.DetermineFixedState(cso != null, sourceType);
        if (fixedState.HasValue)
        {
            candidate.State = fixedState.Value;
            if (fixedState.Value == AttributeSourceState.NotEvaluated)
                candidate.Note = ProvenanceLogic.NoteForFixedState(sourceType);
            return candidate;
        }

        // cso is non-null here: DetermineFixedState returns NotJoined otherwise, which is handled above.
        if (sourceType == SyncRuleMappingSourcesType.AttributeMapping)
        {
            var (values, note) = EvaluateAttributeSourceCandidateValues(mapping, cso!, targetAttribute.Type, candidateValueCap);
            candidate.CandidateValues = values;
            candidate.Note = note;
        }
        else if (sourceType == SyncRuleMappingSourcesType.GeneratedMapping)
        {
            // A Generated Value is generated once and never recomputed, so the value this flow supplies is the one
            // it generated for this Metaverse Object, if it generated one.
            if (generatedValue != null && generatedValue.SyncRuleMappingId == mapping.Id)
                candidate.CandidateValues = new List<string> { generatedValue.Value };
        }
        else if (sourceType == SyncRuleMappingSourcesType.ExpressionMapping)
        {
            var (values, note) = EvaluateExpressionSourceCandidateValues(mapping, cso!, targetAttribute, expressionSource, candidateValueCap);
            candidate.CandidateValues = values;
            if (note != null)
            {
                candidate.State = AttributeSourceState.NotEvaluated;
                candidate.Note = note;
                return candidate;
            }
        }

        candidate.State = ProvenanceLogic.DetermineUsageState(
            currentContributorSyncRuleIds.Contains(mapping.SyncRuleId), candidate.CandidateValues.Count);
        return candidate;
    }

    /// <summary>
    /// The value(s) a plain attribute mapping would currently supply from its joined Connected System Object,
    /// after the mapping's own inbound text processing (#843) for a text source, matching exactly what the sync
    /// engine's <c>ProcessMapping</c> would flow. Values are read as the source attribute's type, as the engine reads
    /// them (#2319), not the target's. A source holding the attribute only under another data type shows the value it
    /// holds, with a note: synchronisation leaves the Metaverse Object's value as it is until an import reads it (#2184).
    /// </summary>
    private static (List<string> Values, string? Note) EvaluateAttributeSourceCandidateValues(
        SyncRuleMapping mapping, ConnectedSystemObject cso, AttributeDataType targetAttributeType, int cap)
    {
        var source = mapping.Sources.OrderBy(s => s.Order).FirstOrDefault(s => s.ConnectedSystemAttributeId.HasValue);
        if (source?.ConnectedSystemAttributeId == null)
            return (new List<string>(), null);

        var sourceAttributeId = source.ConnectedSystemAttributeId.Value;
        var csoValues = cso.AttributeValues.Where(av => av.AttributeId == sourceAttributeId).ToList();

        // The schema's definition is the type the engine dispatches on; the mapping's own copy and the target's type
        // stand in only where it is not loaded.
        var sourceAttributeType = cso.Type?.Attributes.FirstOrDefault(a => a.Id == sourceAttributeId)?.Type
                                  ?? source.ConnectedSystemAttribute?.Type
                                  ?? targetAttributeType;

        if (sourceAttributeType != AttributeDataType.Reference &&
            SyncEngine.HeldOnlyUnderAnotherDataType(csoValues, sourceAttributeId, sourceAttributeType) is { } heldType)
        {
            var heldValues = csoValues
                .Select(v => v.ToStringNoName())
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!)
                .Distinct(StringComparer.Ordinal)
                .Take(cap)
                .ToList();
            return (heldValues,
                $"Held as {heldType} until the next import: a schema refresh changed this attribute to {sourceAttributeType}, so " +
                "synchronisation leaves the Metaverse Object's value as it is until an import reads the attribute again.");
        }

        var values = sourceAttributeType == AttributeDataType.Text
            ? csoValues
                .Select(v => SyncEngine.ApplyInboundTextProcessing(v.StringValue, mapping.InboundValueProcessing, mapping.CaseNormalisation))
                .Where(v => v != null)
                .Select(v => v!)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            // Only the rows the engine flows, those holding a value of the type (#2153), and a date in JIM's canonical
            // text form, so one instant held in two kinds or precisions is one candidate, written as an Expression's is.
            : csoValues
                .Where(v => v.HoldsValueOfType(sourceAttributeType))
                .Select(v => sourceAttributeType == AttributeDataType.DateTime
                    ? ExpressionText.FormatDate(v.DateTimeValue!.Value)
                    : v.ToStringNoName())
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        return (values.Take(cap).ToList(), null);
    }

    /// <summary>
    /// The value an expression mapping would currently supply, evaluated side-effect-free with the same
    /// dictionary-building (<see cref="SyncEngine.BuildCsoAttributeDictionary"/>) and inbound text processing the
    /// engine itself uses, so a candidate value here is exactly what a real synchronisation run would produce.
    /// Returns an empty list with a null note on a clean null result (a legitimate "no value" outcome); on an
    /// evaluation failure returns an empty list with the failure message as the note.
    /// </summary>
    private (List<string> Values, string? Note) EvaluateExpressionSourceCandidateValues(
        SyncRuleMapping mapping, ConnectedSystemObject cso, MetaverseAttribute targetAttribute,
        SyncRuleMappingSource? expressionSource, int cap)
    {
        if (expressionSource?.Expression == null)
            return (new List<string>(), null);

        var csAttributeDictionary = SyncEngine.BuildCsoAttributeDictionary(cso, cso.Type, out var heldUnderAnotherDataType);

        // As synchronisation does (#2319): an Expression reading an input held only under another data type is not
        // evaluated, and the Metaverse Object's value is left as it is until an import reads the input again.
        if (heldUnderAnotherDataType != null)
        {
            var heldInputs = ExpressionInputResolver.FindHeldInputs(expressionSource.Expression, heldUnderAnotherDataType);
            if (heldInputs.Count > 0)
            {
                return (new List<string>(),
                    $"Not evaluated: {string.Join(", ", heldInputs)} until the next import, because a schema refresh changed its data " +
                    "type. Synchronisation leaves the Metaverse Object's value as it is until an import reads it again.");
            }
        }

        var context = new ExpressionContext(metaverseAttributes: null, connectedSystemAttributes: csAttributeDictionary);

        object? result;
        try
        {
            result = _provenanceExpressionEvaluator.Evaluate(expressionSource.Expression, context);
        }
        catch (ExpressionEvaluationException ex)
        {
            return (new List<string>(), ex.Message);
        }

        // The engine's own result handler (#2179), so the inspector shows exactly what a synchronisation would flow,
        // and says so when the engine would flow nothing and report an error instead.
        var resolution = SyncEngine.ResolveInboundExpressionResult(result, targetAttribute, mapping);

        return resolution.Kind switch
        {
            ExpressionResultResolutionKind.Values => (resolution.Values.Take(cap).Select(ExpressionText.Describe).ToList(), null),
            ExpressionResultResolutionKind.NotConvertible => (new List<string>(),
                $"The Expression returned '{resolution.UnconvertibleValue}', which this {targetAttribute.Type} attribute cannot hold, so this source " +
                "would supply no value and synchronisation reports an expression error."),
            ExpressionResultResolutionKind.SeveralValues => (new List<string>(),
                $"The Expression returned {resolution.ValueCount} values, but this attribute is single-valued, so this source would supply " +
                "no value and synchronisation reports an error."),
            _ => (new List<string>(), null)
        };
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using DynamicExpresso.Exceptions;
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
/// candidates, and its history. See <c>engineering/plans/doing/SYNC_RULE_CAUSALITY_TRACKING.md</c> for the agreed
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
        return await Application.Repository.Metaverse.GetMetaverseObjectProvenanceAsync(metaverseObjectId);
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
        // per-value origin covers the rest).
        var contributingOrigin = currentValues.Select(v => v.Origin).FirstOrDefault(o => o.ConnectedSystemId.HasValue);
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
                mapping, rank, attribute.Type, currentContributorSyncRuleIds, joinedCsoCache, metaverseObjectId, candidateValueCap));
        }

        // History: raw Add/Remove rows, newest change first, paired into display entries by the pure
        // ProvenanceLogic helper so the pairing rules are unit-testable without a database.
        var rawHistory = await Application.Repository.Metaverse.GetAttributeHistoryRawEntriesAsync(metaverseObjectId, attributeId, historyRawCap);
        var (history, truncatedByPairing) = ProvenanceLogic.PairAttributeHistory(rawHistory, attribute.AttributePlurality, historyCap);
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
        AttributeDataType targetAttributeType,
        HashSet<int> currentContributorSyncRuleIds,
        Dictionary<(int ConnectedSystemId, int ConnectedSystemObjectTypeId), ConnectedSystemObject?> joinedCsoCache,
        Guid metaverseObjectId,
        int candidateValueCap)
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
            candidate.CandidateValues = EvaluateAttributeSourceCandidateValues(mapping, cso!, targetAttributeType, candidateValueCap);
        }
        else if (sourceType == SyncRuleMappingSourcesType.ExpressionMapping)
        {
            var (values, note) = EvaluateExpressionSourceCandidateValues(mapping, cso!, targetAttributeType, expressionSource, candidateValueCap);
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
    /// after the mapping's own inbound text processing (#843) for a text target, matching exactly what the sync
    /// engine's <c>ProcessMapping</c> would flow.
    /// </summary>
    private static List<string> EvaluateAttributeSourceCandidateValues(
        SyncRuleMapping mapping, ConnectedSystemObject cso, AttributeDataType targetAttributeType, int cap)
    {
        var source = mapping.Sources.OrderBy(s => s.Order).FirstOrDefault(s => s.ConnectedSystemAttributeId.HasValue);
        if (source?.ConnectedSystemAttributeId == null)
            return new List<string>();

        var csoValues = cso.AttributeValues.Where(av => av.AttributeId == source.ConnectedSystemAttributeId.Value).ToList();

        List<string> values;
        if (targetAttributeType == AttributeDataType.Text)
        {
            values = csoValues
                .Select(v => SyncEngine.ApplyInboundTextProcessing(v.StringValue, mapping.InboundValueProcessing, mapping.CaseNormalisation))
                .Where(v => v != null)
                .Select(v => v!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        else
        {
            values = csoValues
                .Select(v => v.ToStringNoName())
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        return values.Take(cap).ToList();
    }

    /// <summary>
    /// The value an expression mapping would currently supply, evaluated side-effect-free with the same
    /// dictionary-building (<see cref="SyncEngine.BuildCsoAttributeDictionary"/>) and inbound text processing the
    /// engine itself uses, so a candidate value here is exactly what a real synchronisation run would produce.
    /// Returns an empty list with a null note on a clean null result (a legitimate "no value" outcome); on an
    /// evaluation failure returns an empty list with the failure message as the note.
    /// </summary>
    private (List<string> Values, string? Note) EvaluateExpressionSourceCandidateValues(
        SyncRuleMapping mapping, ConnectedSystemObject cso, AttributeDataType targetAttributeType,
        SyncRuleMappingSource? expressionSource, int cap)
    {
        if (expressionSource?.Expression == null)
            return (new List<string>(), null);

        var csAttributeDictionary = SyncEngine.BuildCsoAttributeDictionary(cso, cso.Type);
        var context = new ExpressionContext(metaverseAttributes: null, connectedSystemAttributes: csAttributeDictionary);

        object? result;
        try
        {
            result = _provenanceExpressionEvaluator.Evaluate(expressionSource.Expression, context);
        }
        catch (DynamicExpressoException ex) { return (new List<string>(), ex.Message); }
        catch (ArgumentException ex) { return (new List<string>(), ex.Message); }
        catch (FormatException ex) { return (new List<string>(), ex.Message); }
        catch (OverflowException ex) { return (new List<string>(), ex.Message); }
        catch (InvalidOperationException ex) { return (new List<string>(), ex.Message); }
        catch (ArithmeticException ex) { return (new List<string>(), ex.Message); }
        catch (InvalidCastException ex) { return (new List<string>(), ex.Message); }
        catch (KeyNotFoundException ex) { return (new List<string>(), ex.Message); }

        if (result == null)
            return (new List<string>(), null);

        var isTextTarget = targetAttributeType == AttributeDataType.Text;

        if (result is string[] stringArrayResult)
            return (FormatExpressionArrayResult(stringArrayResult, mapping, isTextTarget, cap), null);

        if (result is IEnumerable<string> stringEnumerableResult && result is not string)
            return (FormatExpressionArrayResult(stringEnumerableResult, mapping, isTextTarget, cap), null);

        var resultString = result.ToString();
        if (isTextTarget)
        {
            var processed = SyncEngine.ApplyInboundTextProcessing(resultString, mapping.InboundValueProcessing, mapping.CaseNormalisation);
            return (processed != null ? new List<string> { processed } : new List<string>(), null);
        }

        return (resultString != null ? new List<string> { resultString } : new List<string>(), null);
    }

    private static List<string> FormatExpressionArrayResult(
        IEnumerable<string?> rawValues, SyncRuleMapping mapping, bool isTextTarget, int cap)
    {
        var values = isTextTarget
            ? rawValues
                .Select(v => SyncEngine.ApplyInboundTextProcessing(v, mapping.InboundValueProcessing, mapping.CaseNormalisation))
                .Where(v => v != null)
                .Select(v => v!)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : rawValues
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        return values.Take(cap).ToList();
    }
}

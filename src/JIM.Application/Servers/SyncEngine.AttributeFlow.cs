// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using DynamicExpresso.Exceptions;
using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Utility;
using JIM.Utilities;
using Serilog;
using System.Globalization;
using System.Text.RegularExpressions;

namespace JIM.Application.Servers;

/// <summary>
/// Attribute Flow logic for the sync engine.
/// Contains all Synchronisation Rule mapping processing — formerly SyncRuleMappingProcessor.
/// </summary>
public partial class SyncEngine
{
    /// <summary>
    /// Processes a Synchronisation Rule mapping to flow attribute values from CSO to MVO.
    /// </summary>
    internal static void ProcessMapping(
        ConnectedSystemObject cso,
        SyncRuleMapping syncRuleMapping,
        IReadOnlyList<ConnectedSystemObjectType> objectTypes,
        IExpressionEvaluator? expressionEvaluator = null,
        bool skipReferenceAttributes = false,
        bool onlyReferenceAttributes = false,
        bool isFinalReferencePass = false,
        int? contributingSystemId = null,
        List<AttributeFlowWarning>? warnings = null,
        int mvoObjectTypeId = 0,
        AttributePriorityContext? priorityContext = null)
    {
        if (cso.MetaverseObject == null)
        {
            Log.Error("ProcessMapping: CSO ({CsoId}) is not joined to an MVO!", cso.Id);
            return;
        }

        if (syncRuleMapping.TargetMetaverseAttribute == null)
        {
            Log.Error("ProcessMapping: Synchronisation Rule mapping has no TargetMetaverseAttribute set!");
            return;
        }

        var csoType = objectTypes.Single(t => t.Id == cso.TypeId);
        var mvo = cso.MetaverseObject;

        // Provenance: stamp the winning Synchronisation Rule alongside the contributing system on every value this mapping
        // writes (#91), so the Metaverse Object value records which rule contributed it, not just which system.
        var contributingSyncRuleId = syncRuleMapping.SyncRuleId;

        // Attribute priority gate (#91): when an attribute has more than one contributing rule, a contribution that
        // loses priority resolution to the rule currently owning the value (the incumbent) must never reach the
        // Metaverse Object. Single-contributor attributes, and runs without a priority context, use the unchanged
        // write path: the gate adds one cached lookup and never engages for the common single-contributor case.
        if (priorityContext != null && contributingSyncRuleId.HasValue &&
            priorityContext.GetContributorCount(mvoObjectTypeId, syncRuleMapping.TargetMetaverseAttribute.Id) > 1)
        {
            var incumbentSyncRuleId = FindEffectiveIncumbentSyncRuleId(mvo, syncRuleMapping.TargetMetaverseAttribute.Id);
            if (!priorityContext.ShouldApply(mvoObjectTypeId, syncRuleMapping.TargetMetaverseAttribute.Id, syncRuleMapping, incumbentSyncRuleId))
            {
                Log.Verbose("ProcessMapping: contribution from Synchronisation Rule {RuleId} loses priority resolution for " +
                    "attribute {AttributeId} on MVO {MvoId}; skipping write.",
                    contributingSyncRuleId, syncRuleMapping.TargetMetaverseAttribute.Id, mvo.Id);
                return;
            }
        }

        foreach (var source in syncRuleMapping.Sources.OrderBy(q => q.Order))
        {
            if (source.ConnectedSystemAttributeId.HasValue)
            {
                var csotAttribute = csoType.Attributes.SingleOrDefault(a => a.Id == source.ConnectedSystemAttributeId.Value);
                if (csotAttribute != null)
                {
                    var csoAttributeValues = cso.AttributeValues.Where(av => av.AttributeId == csotAttribute.Id).ToList();
                    if (csoAttributeValues.Count > 0)
                    {
                        var sourceAttributeId = source.ConnectedSystemAttributeId!.Value;
                        if (onlyReferenceAttributes && csotAttribute.Type != AttributeDataType.Reference)
                            continue;

                        // MVA -> SVA truncation: when multiple source values target a single-valued
                        // MV attribute, take only the first value and record a warning (#435)
                        var isMvaToSva = csoAttributeValues.Count > 1 &&
                            syncRuleMapping.TargetMetaverseAttribute.AttributePlurality == AttributePlurality.SingleValued;

                        if (isMvaToSva)
                        {
                            var firstValue = csoAttributeValues.First();
                            var selectedValueDescription = csotAttribute.Type switch
                            {
                                AttributeDataType.Text => firstValue.StringValue ?? "(null)",
                                AttributeDataType.Number => firstValue.IntValue?.ToString() ?? "(null)",
                                AttributeDataType.LongNumber => firstValue.LongValue?.ToString() ?? "(null)",
                                AttributeDataType.DateTime => firstValue.DateTimeValue?.ToString("O") ?? "(null)",
                                AttributeDataType.Boolean => firstValue.BoolValue?.ToString() ?? "(null)",
                                AttributeDataType.Guid => firstValue.GuidValue?.ToString() ?? "(null)",
                                AttributeDataType.Binary => firstValue.ByteValue != null
                                    ? $"[{firstValue.ByteValue.Length} bytes]" : "(null)",
                                _ => "(unknown)"
                            };

                            Log.Warning(
                                "ProcessMapping: Multi-valued source attribute '{SourceAttr}' has {ValueCount} values but target " +
                                "attribute '{TargetAttr}' is single-valued. Using first value: '{SelectedValue}'. CSO {CsoId}",
                                csotAttribute.Name, csoAttributeValues.Count,
                                syncRuleMapping.TargetMetaverseAttribute.Name, LogSanitiser.Sanitise(selectedValueDescription), cso.Id);

                            warnings?.Add(new AttributeFlowWarning
                            {
                                SourceAttributeName = csotAttribute.Name,
                                TargetAttributeName = syncRuleMapping.TargetMetaverseAttribute.Name,
                                ValueCount = csoAttributeValues.Count,
                                SelectedValue = selectedValueDescription
                            });

                            // Truncate to the first value only
                            csoAttributeValues = new List<ConnectedSystemObjectAttributeValue> { firstValue };
                        }

                        switch (csotAttribute.Type)
                        {
                            case AttributeDataType.Text:
                            {
                                // Apply the mapping's inbound value processing (#843) to the source text values,
                                // dropping any that collapse to no value, before diffing against the MVO. An empty
                                // processed set is the ConnectedNoValue state, resolved by priority (#91).
                                var effectiveTextValues = ProcessInboundTextValues(csoAttributeValues.Select(av => av.StringValue), syncRuleMapping);
                                if (effectiveTextValues.Count == 0)
                                    ApplyNoValueOutcome(mvo, syncRuleMapping, contributingSystemId, contributingSyncRuleId, mvoObjectTypeId, priorityContext);
                                else
                                    ProcessTextAttribute(mvo, syncRuleMapping, effectiveTextValues, contributingSystemId, contributingSyncRuleId);
                                break;
                            }
                            case AttributeDataType.Number:
                                ProcessNumberAttribute(mvo, syncRuleMapping, sourceAttributeId, cso, csoAttributeValues, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.DateTime:
                                ProcessDateTimeAttribute(mvo, syncRuleMapping, csoAttributeValues, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.Binary:
                                ProcessBinaryAttribute(mvo, syncRuleMapping, sourceAttributeId, cso, csoAttributeValues, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.Reference:
                                if (!skipReferenceAttributes)
                                    ProcessReferenceAttribute(mvo, syncRuleMapping, source, cso, csoAttributeValues, isFinalReferencePass, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.Guid:
                                ProcessGuidAttribute(mvo, syncRuleMapping, sourceAttributeId, cso, csoAttributeValues, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.Boolean:
                                ProcessBooleanAttribute(mvo, syncRuleMapping, csoAttributeValues, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.NotSet:
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                    else
                    {
                        // Connected, no value for this attribute (ConnectedNoValue): assert null, abstain, or clear
                        // according to priority and "Null is a value" (#91), rather than always clearing.
                        ApplyNoValueOutcome(mvo, syncRuleMapping, contributingSystemId, contributingSyncRuleId, mvoObjectTypeId, priorityContext);
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(source.Expression))
            {
                ProcessExpressionMapping(cso, mvo, syncRuleMapping, source, csoType, expressionEvaluator, contributingSystemId, contributingSyncRuleId, mvoObjectTypeId, priorityContext);
            }
            else if (source.MetaverseAttribute != null)
                throw new InvalidDataException("SyncRuleMappingSource.MetaverseAttribute being populated is not supported for synchronisation operations. " +
                                               "This operation is focused on import flow, so Connected System to Metaverse Object.");
            else
                throw new InvalidDataException("Expected ConnectedSystemAttribute or Expression to be populated in a SyncRuleMappingSource object.");
        }
    }

    /// <summary>
    /// The Metaverse Object's effective current values for an attribute: persisted values not already pending
    /// removal in this run. Attribute flow must diff contributions against this set rather than the raw value list;
    /// diffing against raw values treats a value that is about to be removed (e.g. recalled from a departing
    /// contributor, #91) as still present, so an identical surviving contribution would neither re-add the value nor
    /// take over its provenance, and the value would be silently cleared when the pending removals are applied.
    /// </summary>
    private static IEnumerable<MetaverseObjectAttributeValue> GetEffectiveAttributeValues(MetaverseObject mvo, int attributeId)
        => mvo.AttributeValues.Where(av => av.AttributeId == attributeId && !mvo.PendingAttributeValueRemovals.Contains(av));

    /// <summary>
    /// Identifies the Synchronisation Rule that currently owns a Metaverse Object attribute's value (the incumbent,
    /// for the attribute priority gate, #91). Prefers a value written earlier in this run by another mapping (a
    /// pending addition), otherwise the current persisted row value, ignoring values already pending removal. Under
    /// winner-takes-all-values any value for the attribute identifies the owning rule. Returns null when nothing owns
    /// the attribute or the value is internally managed (no contributing rule stamped).
    /// </summary>
    private static int? FindEffectiveIncumbentSyncRuleId(MetaverseObject mvo, int attributeId)
    {
        var pendingOwner = mvo.PendingAttributeValueAdditions.LastOrDefault(av => av.AttributeId == attributeId);
        if (pendingOwner != null)
            return pendingOwner.ContributedBySyncRuleId;

        var current = GetEffectiveAttributeValues(mvo, attributeId).FirstOrDefault();
        return current?.ContributedBySyncRuleId;
    }

    /// <summary>
    /// Applies the outcome when a contribution that has passed the priority gate yields no value for its target
    /// attribute (the ConnectedNoValue state, #91): the "act on R's contribution state" node of the resolution
    /// decision tree for the no-value branches.
    /// <list type="bullet">
    /// <item>"Null is a value" set: assert null. Strip any real values and ensure a single <c>NullValue</c> marker row
    /// carries this rule's provenance (idempotent: an existing marker from this rule is left untouched).</item>
    /// <item>Otherwise the contribution abstains. A different rule's incumbent value (necessarily lower priority, since
    /// the gate let this contribution proceed) is left in place; only a sole/self-owned attribute is cleared. Re-electing
    /// a lower-priority next contributor when the winner retracts is the recall fallback, handled elsewhere.</item>
    /// </list>
    /// When no priority context is supplied the historic behaviour is preserved: the attribute is cleared.
    /// </summary>
    private static void ApplyNoValueOutcome(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        int? contributingSystemId,
        int? contributingSyncRuleId,
        int mvoObjectTypeId,
        AttributePriorityContext? priorityContext)
    {
        var attributeId = syncRuleMapping.TargetMetaverseAttribute!.Id;
        var existingValues = GetEffectiveAttributeValues(mvo, attributeId).ToList();

        // Inert without a priority context: preserve the historic clear behaviour exactly (no asserted-null markers,
        // no abstention). Markers and abstention only engage once the worker supplies a context, which it does
        // together with the NullValue read-query filter, so a marker is never written before the read paths exclude
        // it (the integrity invariant, #91).
        if (priorityContext == null)
        {
            mvo.PendingAttributeValueRemovals.AddRange(existingValues);
            return;
        }

        // Assert null only when the context honours it (gated on the NullValue read-query filter being in place).
        // Otherwise a no-value contribution falls through to the abstain/clear logic regardless of "Null is a value".
        if (syncRuleMapping.NullIsValue && priorityContext.HonourNullAssertions)
        {
            // Assert null: remove any real values, then ensure exactly one NullValue marker stamped with this rule.
            mvo.PendingAttributeValueRemovals.AddRange(existingValues.Where(av => !av.NullValue));

            var markerFromThisRule = existingValues.Any(av => av.NullValue && av.ContributedBySyncRuleId == contributingSyncRuleId);
            if (!markerFromThisRule)
            {
                // Replace any stale marker from a different rule with one carrying this rule's provenance.
                mvo.PendingAttributeValueRemovals.AddRange(existingValues.Where(av => av.NullValue && av.ContributedBySyncRuleId != contributingSyncRuleId));
                mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
                {
                    MetaverseObject = mvo,
                    Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                    AttributeId = attributeId,
                    NullValue = true,
                    ContributedBySystemId = contributingSystemId,
                    ContributedBySyncRuleId = contributingSyncRuleId
                });
            }

            return;
        }

        // Not asserting null: abstain. Leave a different rule's incumbent in place when this attribute has multiple
        // contributors; otherwise (sole contributor, self-retraction, or no priority context) clear it.
        var incumbentSyncRuleId = FindEffectiveIncumbentSyncRuleId(mvo, attributeId);
        var incumbentIsAnotherRule = incumbentSyncRuleId != null && incumbentSyncRuleId != contributingSyncRuleId;
        // priorityContext is non-null here: the early return above handles the null case.
        if (incumbentIsAnotherRule &&
            priorityContext.GetContributorCount(mvoObjectTypeId, attributeId) > 1)
            return;

        mvo.PendingAttributeValueRemovals.AddRange(existingValues);
    }

    /// <summary>
    /// Processes an expression-based Synchronisation Rule mapping source.
    /// </summary>
    private static void ProcessExpressionMapping(
        ConnectedSystemObject cso,
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        SyncRuleMappingSource source,
        ConnectedSystemObjectType csoType,
        IExpressionEvaluator? expressionEvaluator,
        int? contributingSystemId,
        int? contributingSyncRuleId,
        int mvoObjectTypeId,
        AttributePriorityContext? priorityContext)
    {
        if (expressionEvaluator == null)
        {
            Log.Warning("ProcessExpressionMapping: Expression-based mapping requires an IExpressionEvaluator but none was provided. Expression: {Expression}", source.Expression);
            return;
        }

        var csAttributeDictionary = BuildCsoAttributeDictionary(cso, csoType);

        Log.Debug("ProcessExpressionMapping: Evaluating expression for CSO {CsoId}. Expression: '{Expression}', Available attributes: [{Attributes}]",
            cso.Id, source.Expression, string.Join(", ", csAttributeDictionary.Keys));

        var context = new ExpressionContext(
            metaverseAttributes: null,
            connectedSystemAttributes: csAttributeDictionary);

        // Only the evaluation itself is guarded. A thrown expression must be surfaced as an errored
        // object, never swallowed and never conflated with a deliberate null (which clears the value).
        // Known expression failure modes are rethrown as SyncExpressionEvaluationException for the worker
        // to record as an ExpressionEvaluationError RPEI; anything outside the enumerated set propagates
        // to the worker's UnhandledError handler rather than being mislabelled.
        object? result;
        try
        {
            result = expressionEvaluator.Evaluate(source.Expression!, context);
        }
        catch (DynamicExpressoException ex) { throw BuildExpressionEvaluationException(syncRuleMapping.TargetMetaverseAttribute?.Name, source, ex); }
        catch (ArgumentException ex) { throw BuildExpressionEvaluationException(syncRuleMapping.TargetMetaverseAttribute?.Name, source, ex); }
        catch (FormatException ex) { throw BuildExpressionEvaluationException(syncRuleMapping.TargetMetaverseAttribute?.Name, source, ex); }
        catch (OverflowException ex) { throw BuildExpressionEvaluationException(syncRuleMapping.TargetMetaverseAttribute?.Name, source, ex); }
        catch (InvalidOperationException ex) { throw BuildExpressionEvaluationException(syncRuleMapping.TargetMetaverseAttribute?.Name, source, ex); }
        catch (ArithmeticException ex) { throw BuildExpressionEvaluationException(syncRuleMapping.TargetMetaverseAttribute?.Name, source, ex); }
        catch (InvalidCastException ex) { throw BuildExpressionEvaluationException(syncRuleMapping.TargetMetaverseAttribute?.Name, source, ex); }
        catch (KeyNotFoundException ex) { throw BuildExpressionEvaluationException(syncRuleMapping.TargetMetaverseAttribute?.Name, source, ex); }

        if (result == null)
        {
            Log.Debug("ProcessExpressionMapping: Expression '{Expression}' for CSO {CsoId} returned null. Available attributes: [{Attributes}]",
                source.Expression, cso.Id, string.Join(", ", csAttributeDictionary.Keys));

            // Expression null is a positive "no value" assertion (ConnectedNoValue), not "no opinion": resolve it by
            // priority and "Null is a value" (#91) instead of clearing unconditionally.
            ApplyNoValueOutcome(mvo, syncRuleMapping, contributingSystemId, contributingSyncRuleId, mvoObjectTypeId, priorityContext);
            return;
        }

        if (result is string[] stringArrayResult)
        {
            ApplyExpressionArrayResult(mvo, syncRuleMapping,
                ProcessInboundTextValues(stringArrayResult, syncRuleMapping).ToArray(),
                contributingSystemId, contributingSyncRuleId, mvoObjectTypeId, priorityContext);
        }
        else if (result is IEnumerable<string> stringEnumerableResult && result is not string)
        {
            ApplyExpressionArrayResult(mvo, syncRuleMapping,
                ProcessInboundTextValues(stringEnumerableResult, syncRuleMapping).ToArray(),
                contributingSystemId, contributingSyncRuleId, mvoObjectTypeId, priorityContext);
        }
        else
        {
            var existingMvoValue = GetEffectiveAttributeValues(mvo, syncRuleMapping.TargetMetaverseAttribute!.Id)
                .SingleOrDefault();

            var resultString = result.ToString();
            var isTextTarget = syncRuleMapping.TargetMetaverseAttribute!.Type == AttributeDataType.Text;

            if (isTextTarget)
            {
                // Apply the mapping's inbound value processing (#843) to the scalar expression result.
                var processed = ApplyInboundTextProcessing(resultString, syncRuleMapping.InboundValueProcessing, syncRuleMapping.CaseNormalisation);
                if (processed == null)
                {
                    // The processed result collapses to no value: treat as ConnectedNoValue and resolve by priority
                    // and "Null is a value" (#91), the same as a null expression result.
                    ApplyNoValueOutcome(mvo, syncRuleMapping, contributingSystemId, contributingSyncRuleId, mvoObjectTypeId, priorityContext);
                    return;
                }
                resultString = processed;
            }

            var valueChanged = existingMvoValue == null ||
                !string.Equals(existingMvoValue.StringValue, resultString, StringComparison.Ordinal);

            if (valueChanged)
            {
                if (existingMvoValue != null)
                    mvo.PendingAttributeValueRemovals.Add(existingMvoValue);

                // For a text target use the processed string directly; for other types convert from the raw result.
                var newMvoValue = isTextTarget
                    ? new MetaverseObjectAttributeValue
                    {
                        MetaverseObject = mvo,
                        Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                        AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                        StringValue = resultString,
                        ContributedBySystemId = contributingSystemId,
                        ContributedBySyncRuleId = contributingSyncRuleId
                    }
                    : CreateMvoAttributeValueFromExpressionResult(
                        mvo, syncRuleMapping.TargetMetaverseAttribute!, result, contributingSystemId, contributingSyncRuleId);

                if (newMvoValue != null)
                {
                    mvo.PendingAttributeValueAdditions.Add(newMvoValue);
                    Log.Debug("ProcessExpressionMapping: Expression-based mapping set {AttributeName} to '{Value}' on MVO {MvoId}",
                        syncRuleMapping.TargetMetaverseAttribute!.Name, LogSanitiser.Sanitise(resultString), mvo.Id);
                }
            }
        }
    }

    /// <summary>
    /// Builds a <see cref="SyncExpressionEvaluationException"/> carrying the failing expression and the
    /// target metaverse attribute name, so the worker can record an ExpressionEvaluationError RPEI for
    /// the object being processed.
    /// </summary>
    private static SyncExpressionEvaluationException BuildExpressionEvaluationException(
        string? targetAttributeName, SyncRuleMappingSource source, Exception innerException)
    {
        return new SyncExpressionEvaluationException(source.Expression, targetAttributeName, innerException);
    }

    private static readonly Regex InternalWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Applies a mapping's inbound text value-processing transforms to a single value (#843), in the fixed
    /// canonical order: trim, then collapse internal whitespace, then case normalisation, then the
    /// whitespace-as-no-value decision. Returns the transformed value, or <c>null</c> when the value
    /// collapses to no value (whitespace-only or empty, with <see cref="InboundValueProcessing.TreatWhitespaceAsNoValue"/>
    /// enabled). A <c>null</c> input always returns <c>null</c>. Only meaningful for text attribute values.
    /// </summary>
    internal static string? ApplyInboundTextProcessing(
        string? value,
        InboundValueProcessing processing,
        InboundCaseNormalisation caseNormalisation)
    {
        if (value == null)
            return null;

        if (processing.HasFlag(InboundValueProcessing.TrimWhitespace))
            value = value.Trim();

        if (processing.HasFlag(InboundValueProcessing.CollapseInternalWhitespace))
            value = InternalWhitespaceRegex.Replace(value, " ");

        value = caseNormalisation switch
        {
            InboundCaseNormalisation.Upper => value.ToUpperInvariant(),
            InboundCaseNormalisation.Lower => value.ToLowerInvariant(),
            // Lower-case first so already-upper-case words ("ALICE") are title-cased rather than left as-is.
            InboundCaseNormalisation.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant()),
            _ => value
        };

        if (processing.HasFlag(InboundValueProcessing.TreatWhitespaceAsNoValue) && string.IsNullOrWhiteSpace(value))
            return null;

        return value;
    }

    /// <summary>
    /// Applies a mapping's inbound text value processing to a set of source string values, dropping any that
    /// collapse to no value and de-duplicating the result. Used for multi-valued text flow (direct and
    /// expression-array) so whitespace-only entries are removed from the set when enabled.
    /// </summary>
    private static List<string> ProcessInboundTextValues(IEnumerable<string?> values, SyncRuleMapping syncRuleMapping)
    {
        return values
            .Select(v => ApplyInboundTextProcessing(v, syncRuleMapping.InboundValueProcessing, syncRuleMapping.CaseNormalisation))
            .Where(v => v != null)
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Flows a processed set of text values to the target Metaverse attribute. The supplied
    /// <paramref name="effectiveValues"/> have already had the mapping's inbound value processing applied
    /// (whitespace-as-no-value drops removed, transforms applied, de-duplicated), so this method only
    /// diffs them against the existing MVO values. An empty set clears the attribute.
    /// </summary>
    private static void ProcessTextAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<string> effectiveValues,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        var targetAttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id;
        var currentValues = GetEffectiveAttributeValues(mvo, targetAttributeId).ToList();

        // Remove MVO values that are not present in the processed source set.
        var mvoObsoleteAttributeValues = currentValues.Where(mvoav =>
            !effectiveValues.Any(ev => ev.Equals(mvoav.StringValue, StringComparison.Ordinal)));
        mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

        // Add processed source values not already on the MVO.
        var valuesToAdd = effectiveValues.Where(ev =>
            !currentValues.Any(mvoav => ev.Equals(mvoav.StringValue, StringComparison.Ordinal)));

        foreach (var value in valuesToAdd)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = targetAttributeId,
                StringValue = value,
                ContributedBySystemId = contributingSystemId,
                ContributedBySyncRuleId = contributingSyncRuleId
            });
        }
    }

    private static void ProcessNumberAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        int sourceAttributeId,
        ConnectedSystemObject cso,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        var currentValues = GetEffectiveAttributeValues(mvo, syncRuleMapping.TargetMetaverseAttribute!.Id).ToList();
        var mvoObsoleteAttributeValues = currentValues.Where(mvoav =>
            !csoAttributeValues.Any(csoav => csoav.IntValue != null && csoav.IntValue.Equals(mvoav.IntValue)));
        mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

        // Use the (possibly truncated) csoAttributeValues list rather than cso.AttributeValues
        // to respect MVA->SVA first-value selection (#435)
        var csoNewAttributeValues = csoAttributeValues.Where(csoav =>
            !currentValues.Any(mvoav => mvoav.IntValue != null && mvoav.IntValue.Equals(csoav.IntValue)));

        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                IntValue = newCsoNewAttributeValue.IntValue,
                ContributedBySystemId = contributingSystemId,
                ContributedBySyncRuleId = contributingSyncRuleId
            });
        }
    }

    private static void ProcessDateTimeAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        var csoValue = csoAttributeValues.FirstOrDefault();
        var mvoValue = GetEffectiveAttributeValues(mvo, syncRuleMapping.TargetMetaverseAttribute!.Id).SingleOrDefault();

        if (mvoValue != null && csoValue == null)
        {
            mvo.PendingAttributeValueRemovals.Add(mvoValue);
        }
        else if (csoValue != null && mvoValue == null)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                DateTimeValue = csoValue.DateTimeValue,
                ContributedBySystemId = contributingSystemId,
                ContributedBySyncRuleId = contributingSyncRuleId
            });
        }
        else if (csoValue != null && mvoValue != null && mvoValue.DateTimeValue != csoValue.DateTimeValue)
        {
            mvo.PendingAttributeValueRemovals.Add(mvoValue);
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                DateTimeValue = csoValue.DateTimeValue,
                ContributedBySystemId = contributingSystemId,
                ContributedBySyncRuleId = contributingSyncRuleId
            });
        }
    }

    private static void ProcessBinaryAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        int sourceAttributeId,
        ConnectedSystemObject cso,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        var currentValues = GetEffectiveAttributeValues(mvo, syncRuleMapping.TargetMetaverseAttribute!.Id).ToList();
        var mvoObsoleteAttributeValues = currentValues.Where(mvoav =>
            !csoAttributeValues.Any(csoav =>
                csoav.ByteValue != null && JIM.Utilities.Utilities.AreByteArraysTheSame(csoav.ByteValue, mvoav.ByteValue)));
        mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

        // Use the (possibly truncated) csoAttributeValues list rather than cso.AttributeValues
        // to respect MVA->SVA first-value selection (#435)
        var csoNewAttributeValues = csoAttributeValues.Where(csoav =>
            !currentValues.Any(mvoav => JIM.Utilities.Utilities.AreByteArraysTheSame(mvoav.ByteValue, csoav.ByteValue)));

        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                ByteValue = newCsoNewAttributeValue.ByteValue,
                ContributedBySystemId = contributingSystemId,
                ContributedBySyncRuleId = contributingSyncRuleId
            });
        }
    }

    private static void ProcessReferenceAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        SyncRuleMappingSource source,
        ConnectedSystemObject cso,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        bool isFinalReferencePass,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        // Use ResolvedReferenceMetaverseObjectId (populated via direct SQL) as the primary
        // source, falling back to navigation properties for compatibility with in-memory tests.
        static Guid? GetReferencedMvoId(ConnectedSystemObjectAttributeValue csoav)
        {
            if (csoav.ResolvedReferenceMetaverseObjectId.HasValue)
                return csoav.ResolvedReferenceMetaverseObjectId;
            if (csoav.ReferenceValue == null)
                return null;
            return csoav.ReferenceValue.MetaverseObjectId ?? csoav.ReferenceValue.MetaverseObject?.Id;
        }

        // A reference is resolved if we can determine the MVO it points to — either via
        // ResolvedReferenceMetaverseObjectId (direct SQL) or ReferenceValue navigation (in-memory tests).
        static bool IsResolved(ConnectedSystemObjectAttributeValue csoav)
            => (csoav.ReferenceValueId.HasValue || csoav.ReferenceValue != null) && GetReferencedMvoId(csoav).HasValue;

        var unresolvedReferenceValues = csoAttributeValues.Where(csoav =>
            !IsResolved(csoav) &&
            (csoav.ReferenceValueId != null || !string.IsNullOrEmpty(csoav.UnresolvedReferenceValue))).ToList();

        if (unresolvedReferenceValues.Count > 0)
        {
            foreach (var unresolved in unresolvedReferenceValues)
            {
                if (unresolved.ReferenceValueId.HasValue && !unresolved.ResolvedReferenceMetaverseObjectId.HasValue)
                {
                    // Referenced CSO exists but isn't joined to an MVO yet.
                    if (isFinalReferencePass)
                    {
                        Log.Warning("SyncEngine: CSO {CsoId} has reference attribute {AttrName} pointing to CSO {RefCsoId} which is not joined to an MVO. " +
                            "Ensure referenced objects are synced before referencing objects. The reference will not flow to the MVO.",
                            cso.Id, source.ConnectedSystemAttribute?.Name ?? "unknown", unresolved.ReferenceValueId);
                    }
                    else
                    {
                        Log.Debug("SyncEngine: CSO {CsoId} has reference attribute {AttrName} pointing to CSO {RefCsoId} which is not yet joined to an MVO. " +
                            "This will be retried during cross-page reference resolution.",
                            cso.Id, source.ConnectedSystemAttribute?.Name ?? "unknown", unresolved.ReferenceValueId);
                    }
                }
            }
        }

        var hasUnresolvedReferences = csoAttributeValues.Any(csoav =>
            csoav.ReferenceValueId.HasValue && !IsResolved(csoav));

        var currentValues = GetEffectiveAttributeValues(mvo, syncRuleMapping.TargetMetaverseAttribute!.Id).ToList();

        if (!hasUnresolvedReferences)
        {
            var resolvedMvoIds = new HashSet<Guid>(
                csoAttributeValues
                    .Where(IsResolved)
                    .Select(csoav => GetReferencedMvoId(csoav)!.Value));

            var mvoObsoleteAttributeValues = currentValues.Where(mvoav =>
                (mvoav.ReferenceValueId ?? mvoav.ReferenceValue?.Id) is Guid mvoRefId &&
                !resolvedMvoIds.Contains(mvoRefId));
            mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);
        }
        else
        {
            Log.Debug("ProcessReferenceAttribute: Skipping MVO reference removal for CSO {CsoId} " +
                "because some reference(s) have unresolved MetaverseObject (cross-page references). " +
                "Removals will be handled in the cross-page resolution pass.",
                cso.Id);
        }

        var existingMvoRefIds = new HashSet<Guid>(
            currentValues
                .Select(mvoav => mvoav.ReferenceValueId ?? mvoav.ReferenceValue?.Id)
                .Where(id => id.HasValue)
                .Select(id => id!.Value));

        var csoNewAttributeValues = csoAttributeValues.Where(csoav =>
        {
            var mvoId = GetReferencedMvoId(csoav);
            return mvoId.HasValue && !existingMvoRefIds.Contains(mvoId.Value);
        }).ToList();

        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            var targetMvoId = GetReferencedMvoId(newCsoNewAttributeValue);
            if (!targetMvoId.HasValue)
                continue;

            var newMvoAv = new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                ContributedBySystemId = contributingSystemId,
                ContributedBySyncRuleId = contributingSyncRuleId
            };

            // When the referenced MVO is available as a tracked navigation (same-page reference),
            // set the navigation so EF handles insert ordering (the MVO may not be persisted yet).
            // For cross-page references (navigation unavailable), set the scalar FK directly —
            // the referenced MVO already exists in the database.
            if (newCsoNewAttributeValue.ReferenceValue?.MetaverseObject != null)
                newMvoAv.ReferenceValue = newCsoNewAttributeValue.ReferenceValue.MetaverseObject;
            else
                newMvoAv.ReferenceValueId = targetMvoId.Value;

            mvo.PendingAttributeValueAdditions.Add(newMvoAv);
        }
    }

    private static void ProcessGuidAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        int sourceAttributeId,
        ConnectedSystemObject cso,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        var currentValues = GetEffectiveAttributeValues(mvo, syncRuleMapping.TargetMetaverseAttribute!.Id).ToList();
        var mvoObsoleteAttributeValues = currentValues.Where(mvoav =>
            !csoAttributeValues.Any(csoav => csoav.GuidValue.HasValue && csoav.GuidValue.Equals(mvoav.GuidValue)));
        mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

        // Use the (possibly truncated) csoAttributeValues list rather than cso.AttributeValues
        // to respect MVA->SVA first-value selection (#435)
        var csoNewAttributeValues = csoAttributeValues.Where(csoav =>
            !currentValues.Any(mvoav => mvoav.GuidValue.HasValue && mvoav.GuidValue.Equals(csoav.GuidValue)));

        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                GuidValue = newCsoNewAttributeValue.GuidValue,
                ContributedBySystemId = contributingSystemId,
                ContributedBySyncRuleId = contributingSyncRuleId
            });
        }
    }

    private static void ProcessBooleanAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        var csoValue = csoAttributeValues.FirstOrDefault();
        var mvoValue = GetEffectiveAttributeValues(mvo, syncRuleMapping.TargetMetaverseAttribute!.Id).SingleOrDefault();

        if (mvoValue != null && csoValue == null)
        {
            mvo.PendingAttributeValueRemovals.Add(mvoValue);
        }
        else if (csoValue != null && mvoValue == null)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                BoolValue = csoValue.BoolValue,
                ContributedBySystemId = contributingSystemId,
                ContributedBySyncRuleId = contributingSyncRuleId
            });
        }
        else if (csoValue != null && mvoValue != null && mvoValue.BoolValue != csoValue.BoolValue)
        {
            mvo.PendingAttributeValueRemovals.Add(mvoValue);
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                BoolValue = csoValue.BoolValue,
                ContributedBySystemId = contributingSystemId,
                ContributedBySyncRuleId = contributingSyncRuleId
            });
        }
    }

    private static Dictionary<string, object?> BuildCsoAttributeDictionary(
        ConnectedSystemObject cso,
        ConnectedSystemObjectType csoType)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var attributeValue in cso.AttributeValues)
        {
            var csotAttribute = csoType.Attributes.SingleOrDefault(a => a.Id == attributeValue.AttributeId);
            if (csotAttribute == null)
            {
                Log.Warning("BuildCsoAttributeDictionary: CSO {CsoId} has attribute value with AttributeId={AttrId} but attribute not found in type definition",
                    cso.Id, attributeValue.AttributeId);
                continue;
            }

            var attributeName = csotAttribute.Name;

            object? value = csotAttribute.Type switch
            {
                AttributeDataType.Text => attributeValue.StringValue,
                AttributeDataType.Number => attributeValue.IntValue,
                AttributeDataType.LongNumber => attributeValue.LongValue,
                AttributeDataType.DateTime => attributeValue.DateTimeValue,
                AttributeDataType.Boolean => attributeValue.BoolValue,
                AttributeDataType.Guid => attributeValue.GuidValue,
                AttributeDataType.Binary => attributeValue.ByteValue,
                AttributeDataType.Reference => attributeValue.ReferenceValue?.Id.ToString(),
                _ => null
            };

            if (!attributes.ContainsKey(attributeName))
            {
                attributes[attributeName] = value;
            }
        }

        return attributes;
    }

    /// <summary>
    /// Flows a processed expression array result to the target attribute. An empty result is the ConnectedNoValue
    /// state for a multivalued attribute (the empty set), so it is routed through the no-value outcome (assert-null /
    /// abstain / clear by priority, #91) rather than clearing unconditionally; a non-empty result diffs as usual.
    /// </summary>
    private static void ApplyExpressionArrayResult(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        string[] processedValues,
        int? contributingSystemId,
        int? contributingSyncRuleId,
        int mvoObjectTypeId,
        AttributePriorityContext? priorityContext)
    {
        if (processedValues.Length == 0)
            ApplyNoValueOutcome(mvo, syncRuleMapping, contributingSystemId, contributingSyncRuleId, mvoObjectTypeId, priorityContext);
        else
            ProcessExpressionArrayResult(mvo, syncRuleMapping.TargetMetaverseAttribute!, processedValues, contributingSystemId, contributingSyncRuleId);
    }

    private static void ProcessExpressionArrayResult(
        MetaverseObject mvo,
        MetaverseAttribute targetAttribute,
        string[] values,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        if (values.Length == 0)
        {
            mvo.PendingAttributeValueRemovals.AddRange(GetEffectiveAttributeValues(mvo, targetAttribute.Id));
            return;
        }

        var existingMvoValues = GetEffectiveAttributeValues(mvo, targetAttribute.Id).ToList();

        var valuesToRemove = existingMvoValues
            .Where(existing => !values.Contains(existing.StringValue, StringComparer.Ordinal));
        mvo.PendingAttributeValueRemovals.AddRange(valuesToRemove);

        var existingStringValues = existingMvoValues
            .Select(mvoav => mvoav.StringValue)
            .ToHashSet(StringComparer.Ordinal);

        var valuesToAdd = values
            .Where(v => !existingStringValues.Contains(v));

        foreach (var value in valuesToAdd)
        {
            var newMvoValue = new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = targetAttribute,
                AttributeId = targetAttribute.Id,
                StringValue = value,
                ContributedBySystemId = contributingSystemId,
                ContributedBySyncRuleId = contributingSyncRuleId
            };
            mvo.PendingAttributeValueAdditions.Add(newMvoValue);
        }

        if (valuesToRemove.Any() || valuesToAdd.Any())
        {
            Log.Debug("ProcessExpressionArrayResult: Expression array result for {AttributeName} - removing {RemoveCount}, adding {AddCount} values on MVO {MvoId}",
                targetAttribute.Name, valuesToRemove.Count(), valuesToAdd.Count(), mvo.Id);
        }
    }

    private static MetaverseObjectAttributeValue? CreateMvoAttributeValueFromExpressionResult(
        MetaverseObject mvo,
        MetaverseAttribute targetAttribute,
        object result,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        var newMvoValue = new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo,
            Attribute = targetAttribute,
            AttributeId = targetAttribute.Id,
            ContributedBySystemId = contributingSystemId,
            ContributedBySyncRuleId = contributingSyncRuleId
        };

        switch (targetAttribute.Type)
        {
            case AttributeDataType.Text:
                newMvoValue.StringValue = result?.ToString();
                break;
            case AttributeDataType.Number:
                if (result is int intVal)
                    newMvoValue.IntValue = intVal;
                else if (result is long longVal)
                    newMvoValue.IntValue = (int)longVal;
                else if (int.TryParse(result?.ToString(), out var parsedInt))
                    newMvoValue.IntValue = parsedInt;
                else
                {
                    Log.Warning("CreateMvoAttributeValueFromExpressionResult: Could not convert expression result '{Result}' to Number", LogSanitiser.Sanitise(result?.ToString()));
                    return null;
                }
                break;
            case AttributeDataType.DateTime:
                if (result is DateTime dtVal)
                    newMvoValue.DateTimeValue = dtVal;
                else if (DateTime.TryParse(result?.ToString(), out var parsedDt))
                    newMvoValue.DateTimeValue = parsedDt;
                else
                {
                    Log.Warning("CreateMvoAttributeValueFromExpressionResult: Could not convert expression result '{Result}' to DateTime", LogSanitiser.Sanitise(result?.ToString()));
                    return null;
                }
                break;
            case AttributeDataType.Boolean:
                if (result is bool boolVal)
                    newMvoValue.BoolValue = boolVal;
                else if (bool.TryParse(result?.ToString(), out var parsedBool))
                    newMvoValue.BoolValue = parsedBool;
                else
                {
                    Log.Warning("CreateMvoAttributeValueFromExpressionResult: Could not convert expression result '{Result}' to Boolean", LogSanitiser.Sanitise(result?.ToString()));
                    return null;
                }
                break;
            case AttributeDataType.Guid:
                if (result is Guid guidVal)
                    newMvoValue.GuidValue = guidVal;
                else if (Guid.TryParse(result?.ToString(), out var parsedGuid))
                    newMvoValue.GuidValue = parsedGuid;
                else
                {
                    Log.Warning("CreateMvoAttributeValueFromExpressionResult: Could not convert expression result '{Result}' to Guid", LogSanitiser.Sanitise(result?.ToString()));
                    return null;
                }
                break;
            default:
                Log.Warning("CreateMvoAttributeValueFromExpressionResult: Unsupported target attribute type {Type} for expression result", targetAttribute.Type);
                return null;
        }

        return newMvoValue;
    }
}

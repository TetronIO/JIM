// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using DynamicExpresso.Exceptions;
using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Outbound staging decisions (#288 plan Phase 1b), extracted from <see cref="ExportEvaluationServer"/>'s
/// <c>CreateOrUpdatePendingExport*</c> entry points: what kind of export a Metaverse Object change stages
/// against an export Synchronisation Rule's target, and how newly evaluated attribute changes merge into a
/// Pending Export the run has already staged. Export matching, CSO creation and persistence stay with the
/// orchestrator.
/// </summary>
public partial class SyncEngine
{
    /// <summary>
    /// Decides what kind of export, if any, a Metaverse Object change stages against one export
    /// Synchronisation Rule's target: nothing (a reported Object Type conflict, provisioning declined, a
    /// reference recall against no exportable presence, or changes irrelevant to a pending provisioning), a
    /// Create (provision new, or restage the pending provisioning CSO's Create), or an Update. The
    /// orchestrator interposes export matching before acting on
    /// <see cref="OutboundStagingOutcome.ProvisionNewCso"/>: a matched and claimed CSO becomes an Update.
    /// </summary>
    /// <param name="mvo">The Metaverse Object whose change is being evaluated.</param>
    /// <param name="exportRule">The export Synchronisation Rule under evaluation.</param>
    /// <param name="existingCso">The Metaverse Object's CSO in the rule's Connected System, if any; the caller
    /// resolves this from its cache or the repository.</param>
    /// <param name="changedAttributes">The Metaverse Object attributes that changed, for the pending
    /// provisioning relevance check.</param>
    /// <param name="recallSemantics">True when evaluating a reference recall (#1003), which must never
    /// provision: an object with no presence in the target has nothing there to remove a member from.</param>
    public OutboundStagingDecision DecideOutboundStaging(
        MetaverseObject mvo,
        SyncRule exportRule,
        ConnectedSystemObject? existingCso,
        List<MetaverseObjectAttributeValue> changedAttributes,
        bool recallSemantics)
    {
        // #1331: the Metaverse Object's one CSO in this system may be of a different Object Type than the
        // rule targets; exporting onto it would write this rule's attribute values to the wrong object.
        var conflict = DetectExportObjectTypeConflict(mvo, exportRule, existingCso);
        if (conflict != null)
        {
            return new OutboundStagingDecision
            {
                Outcome = OutboundStagingOutcome.ObjectTypeConflict,
                Conflict = conflict
            };
        }

        // A PendingProvisioning CSO means the object does not exist in the target system yet: it was created
        // by a previous synchronisation to establish the CSO-MVO relationship before export, and it needs a
        // Create operation, not an Update.
        var needsProvisioning = existingCso == null ||
                                existingCso.Status == ConnectedSystemObjectStatus.PendingProvisioning;

        // Reference recall must never provision (#1003): nothing exists in the target to remove a member
        // from, and the guard also protects a pending Create export from being merged away and lost.
        if (recallSemantics && needsProvisioning)
            return new OutboundStagingDecision { Outcome = OutboundStagingOutcome.RecallSkippedNoTargetPresence };

        if (!needsProvisioning)
        {
            return new OutboundStagingDecision
            {
                Outcome = OutboundStagingOutcome.UpdateExistingCso,
                ChangeType = PendingExportChangeType.Update
            };
        }

        if (exportRule.ProvisionToConnectedSystem != true)
            return new OutboundStagingDecision { Outcome = OutboundStagingOutcome.ProvisioningDeclined };

        if (existingCso == null)
        {
            return new OutboundStagingDecision
            {
                Outcome = OutboundStagingOutcome.ProvisionNewCso,
                ChangeType = PendingExportChangeType.Create
            };
        }

        // Reuse the existing PendingProvisioning CSO only when the changes are relevant to this rule:
        // restaging an identical Create export would misattribute it to this synchronisation in the
        // causality tree.
        if (!HasRelevantChangedAttributes(changedAttributes, exportRule))
            return new OutboundStagingDecision { Outcome = OutboundStagingOutcome.PendingProvisioningChangesIrrelevant };

        return new OutboundStagingDecision
        {
            Outcome = OutboundStagingOutcome.ReusePendingProvisioningCso,
            ChangeType = PendingExportChangeType.Create
        };
    }

    /// <summary>
    /// Selects the Object Matching Rules export matching should try for an export Synchronisation Rule, in
    /// the order to try them (#288 plan item 1e). The Connected System's matching mode chooses the source:
    /// Connected System mode reads the Connected System Object Type's shared rules, Advanced mode reads the
    /// Synchronisation Rule's own. An empty answer means matching is not attempted and provisioning proceeds
    /// as though no match existed; a rule whose Connected System or Connected System Object Type navigation is
    /// not loaded answers empty for the same reason, because the mode cannot be read. The per-rule candidate
    /// query stays with the orchestrator, where the data access is.
    /// </summary>
    /// <param name="exportRule">The export Synchronisation Rule about to provision, with its Connected System
    /// and Connected System Object Type navigations loaded.</param>
    public IReadOnlyList<ObjectMatchingRule> SelectExportMatchingRules(SyncRule exportRule)
    {
        if (exportRule.ConnectedSystem == null || exportRule.ConnectedSystemObjectType == null)
            return [];

        var rules = exportRule.ConnectedSystem.ObjectMatchingRuleMode == ObjectMatchingRuleMode.ConnectedSystem
            ? exportRule.ConnectedSystemObjectType.ObjectMatchingRules
            : exportRule.ObjectMatchingRules;

        return rules.OrderBy(r => r.Order).ToList();
    }

    /// <summary>
    /// Decides whether an outbound Synchronisation Rule may export to the Connected System Object already
    /// occupying a Metaverse Object's single slot in the target Connected System, returning a conflict when
    /// that Object is of a different Connected System Object Type than the Rule targets (#1331).
    /// </summary>
    /// <remarks>
    /// A Metaverse Object holds at most one Connected System Object per Connected System (an application
    /// invariant that IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique also backs), so a
    /// second Rule wanting a different Object Type has nowhere to put one. Pending Provisioning Objects count
    /// as occupying the slot. An Object joined to no Metaverse Object occupies nothing, so export matching is
    /// still free to claim it. An unset Object Type on either side is missing information, not a conflict:
    /// blocking every export on it would turn a partially configured Synchronisation Rule into a silent
    /// system-wide export outage.
    /// </remarks>
    internal static ExportObjectTypeConflict? DetectExportObjectTypeConflict(
        MetaverseObject mvo,
        SyncRule exportRule,
        ConnectedSystemObject? existingCso)
    {
        if (existingCso == null || !existingCso.MetaverseObjectId.HasValue)
            return null;

        if (exportRule.ConnectedSystemObjectTypeId == 0 || existingCso.TypeId == 0)
            return null;

        if (existingCso.TypeId == exportRule.ConnectedSystemObjectTypeId)
            return null;

        return new ExportObjectTypeConflict
        {
            MetaverseObjectId = mvo.Id,
            SyncRuleName = exportRule.Name,
            TargetObjectTypeName = exportRule.ConnectedSystemObjectType?.Name ?? exportRule.ConnectedSystemObjectTypeId.ToString(),
            ExistingConnectedSystemObjectId = existingCso.Id,
            ExistingObjectTypeName = existingCso.Type?.Name ?? existingCso.TypeId.ToString()
        };
    }

    /// <summary>
    /// Checks whether any of the changed MVO attributes are relevant to the given export rule: a changed
    /// attribute is relevant when it is a direct source for one of the rule's Attribute Flow mappings, and an
    /// expression-based mapping may depend on any attribute, so any change is conservatively relevant to it.
    /// </summary>
    internal static bool HasRelevantChangedAttributes(
        List<MetaverseObjectAttributeValue> changedAttributes,
        SyncRule exportRule)
    {
        if (changedAttributes.Count == 0)
            return false;

        var changedAttributeIds = new HashSet<int>(changedAttributes.Select(av => av.AttributeId));

        foreach (var source in exportRule.AttributeFlowRules.SelectMany(mapping => mapping.Sources))
        {
            // Expression-based mappings may depend on any MVO attribute, so conservatively
            // treat them as relevant when any attribute has changed.
            if (!string.IsNullOrWhiteSpace(source.Expression))
                return true;

            // Direct attribute mapping: check if the source MVO attribute is in the changed set
            if (source.MetaverseAttribute != null && changedAttributeIds.Contains(source.MetaverseAttribute.Id))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Merges newly evaluated attribute changes into a Pending Export this run has already staged for the
    /// same CSO (typically by drift detection), mutating the staged export in place. Export evaluation wins a
    /// merge-key collision because it derives from the latest Metaverse Object state; an incoming
    /// whole-attribute replace (Update or RemoveAll) first supersedes every staged change for that attribute,
    /// whatever its value or change type (#1199). Sets the staged export's unresolved-references flag when an
    /// incoming change carries one. Pure in-memory mutation: nothing is persisted here.
    /// </summary>
    /// <param name="stagedPendingExport">The Pending Export already staged for the CSO, mutated in place.</param>
    /// <param name="newChanges">The newly evaluated attribute changes to merge in.</param>
    public PendingExportMergeResult MergeAttributeChangesIntoPendingExport(
        PendingExport stagedPendingExport,
        List<PendingExportAttributeValueChange> newChanges)
    {
        var replacedCount = 0;
        var addedCount = 0;

        // Drop the staged changes this evaluation supersedes wholesale before the value-level merge below
        // (#1199): an incoming Update or RemoveAll sets the attribute's entire value set, so any staged
        // change for that attribute is void whatever its own value or change type, and the value-level keys
        // cannot see that.
        var wholeAttributeReplacementIds = GetWholeAttributeReplacementAttributeIds(newChanges);
        var supersededChanges = stagedPendingExport.AttributeValueChanges
            .Where(avc => wholeAttributeReplacementIds.Contains(avc.AttributeId))
            .ToList();
        foreach (var superseded in supersededChanges)
            stagedPendingExport.AttributeValueChanges.Remove(superseded);

        // Build a lookup of the remaining staged changes for deduplication. Merge keys: single-valued
        // attributes key by attribute id only (newest wins), multi-valued attributes key by attribute id
        // plus value (each distinct value preserved).
        var existingChangeKeys = new HashSet<string>();
        foreach (var existing in stagedPendingExport.AttributeValueChanges)
            existingChangeKeys.Add(GetAttributeChangeMergeKey(existing));

        foreach (var newChange in newChanges)
        {
            var key = GetAttributeChangeMergeKey(newChange);
            if (existingChangeKeys.Contains(key))
            {
                // Same attribute (single-valued) or attribute+value (multi-valued) already staged: remove the
                // staged version(s) and add the newly evaluated one (newer Metaverse Object state wins).
                var toRemove = stagedPendingExport.AttributeValueChanges
                    .Where(avc => GetAttributeChangeMergeKey(avc) == key)
                    .ToList();
                foreach (var r in toRemove)
                    stagedPendingExport.AttributeValueChanges.Remove(r);
                stagedPendingExport.AttributeValueChanges.Add(newChange);
                existingChangeKeys.Remove(key);
                existingChangeKeys.Add(GetAttributeChangeMergeKey(newChange));
                replacedCount++;
            }
            else
            {
                stagedPendingExport.AttributeValueChanges.Add(newChange);
                existingChangeKeys.Add(key);
                addedCount++;
            }
        }

        if (newChanges.Any(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue)))
            stagedPendingExport.HasUnresolvedReferences = true;

        return new PendingExportMergeResult { ReplacedCount = replacedCount, AddedCount = addedCount };
    }

    /// <summary>
    /// Creates PendingExportAttributeValueChange objects based on export rule mappings.
    /// Maps MVO attributes → CSO attributes.
    /// For export rules:
    /// - Sources[].MetaverseAttribute = the source MVO attribute
    /// - TargetConnectedSystemAttribute = the target CSO attribute
    /// For Create operations: includes all mapped attributes (to provision the full object)
    /// For Update operations: only includes attributes that actually changed
    /// </summary>
    /// <param name="mvo">The Metaverse Object to create changes for.</param>
    /// <param name="exportRule">The export rule containing attribute mappings.</param>
    /// <param name="changedAttributes">The MVO attributes that changed.</param>
    /// <param name="changeType">Whether this is a Create or Update operation.</param>
    /// <param name="existingCso">The existing CSO (for Update operations only) to compare values against.</param>
    /// <param name="csoAttributeCache">Optional cache of CSO attribute values for no-net-change detection.
    /// Uses ILookup to support multi-valued attributes where a single (CsoId, AttributeId) can have multiple values.</param>
    /// <param name="csoAlreadyCurrentCount">Output: count of attributes skipped because CSO already has the value.</param>
    /// <param name="removedAttributes">Optional set of attribute values that were removed from the MVO.
    /// For multi-valued attributes, values in this set create Remove changes instead of Add changes.
    /// For single-valued attributes, values in this set create null-clearing Update changes.</param>
    /// <param name="mvAttributeDictionary">Optional pre-built MVO attribute dictionary for expression evaluation.</param>
    /// <param name="preResolvedReferenceValues">Optional map of referenced Metaverse Object ID to the resolved
    /// target value for this export rule's Connected System (reference recall, #908). When a reference points
    /// at an object in this map, the change is staged with the resolved value instead of an unresolved
    /// Metaverse Object ID, because export-time resolution cannot resolve a deleted object.</param>
    /// <returns>List of attribute value changes to export.</returns>
    public List<PendingExportAttributeValueChange> ComputeAttributeValueChanges(
        MetaverseObject mvo,
        SyncRule exportRule,
        List<MetaverseObjectAttributeValue> changedAttributes,
        PendingExportChangeType changeType,
        ConnectedSystemObject? existingCso,
        ILookup<(Guid CsoId, int AttributeId), ConnectedSystemObjectAttributeValue>? csoAttributeCache,
        out int csoAlreadyCurrentCount,
        IExpressionEvaluator? expressionEvaluator = null,
        HashSet<MetaverseObjectAttributeValue>? removedAttributes = null,
        Dictionary<string, object?>? mvAttributeDictionary = null,
        IReadOnlyDictionary<Guid, string>? preResolvedReferenceValues = null,
        List<AttributeFlowError>? flowErrors = null)
    {
        var changes = new List<PendingExportAttributeValueChange>();
        var isCreateOperation = changeType == PendingExportChangeType.Create;
        csoAlreadyCurrentCount = 0;

        // The engine holds no evaluator (stateless, zero-dependency by design); callers pass theirs, and a
        // caller that passes none gets a per-call default rather than a shared static, because the
        // interpreter is not guaranteed thread-safe across concurrent evaluations.
        expressionEvaluator ??= new DynamicExpressoEvaluator();

        // For no-net-change detection, we need both the CSO and the attribute cache
        var canDetectNoNetChange = !isCreateOperation && existingCso != null && csoAttributeCache != null;

        // Pre-build O(1) lookup sets from removedAttributes for multi-valued removal detection.
        // Without this, the removal check is O(N) per value — for a 50K-member group that's 50K × 50K = 2.5B comparisons.
        // Three matching strategies in priority order:
        //   1. By ReferenceValueId (most common for group membership)
        //   2. By persisted entity Id (for saved values without ReferenceValueId)
        //   3. By value content (fallback for unsaved non-reference values)
        HashSet<Guid>? removedReferenceValueIds = null;
        HashSet<Guid>? removedEntityIds = null;
        HashSet<(string?, int?, long?, decimal?, Guid?, bool?, DateTime?)>? removedValueContents = null;

        if (removedAttributes is { Count: > 0 })
        {
            removedReferenceValueIds = new HashSet<Guid>();
            removedEntityIds = new HashSet<Guid>();
            removedValueContents = new HashSet<(string?, int?, long?, decimal?, Guid?, bool?, DateTime?)>();

            foreach (var rv in removedAttributes)
            {
                if (rv.ReferenceValueId.HasValue)
                    removedReferenceValueIds.Add(rv.ReferenceValueId.Value);

                if (rv.Id != Guid.Empty)
                    removedEntityIds.Add(rv.Id);

                if (!rv.ReferenceValueId.HasValue && rv.Id == Guid.Empty)
                    removedValueContents.Add((rv.StringValue, rv.IntValue, rv.LongValue, rv.DecimalValue, rv.GuidValue, rv.BoolValue, rv.DateTimeValue));
            }
        }

        // Some mappings flow solely during the provisioning (Create) export and must be skipped for Update
        // exports before any evaluation: Initial Export Only mappings (#223), whose target attribute is
        // unmanaged by JIM once the object is past provisioning, and mappings whose target attribute is
        // WritableOnCreate, which the Connected System accepts only as part of creating the object.
        // FlowsOnUpdateExport() carries both rules; see its documentation for why the second one is a
        // synchronisation integrity guard rather than an optimisation.
        foreach (var mapping in exportRule.AttributeFlowRules.Where(m => isCreateOperation || m.FlowsOnUpdateExport()))
        {
            // For export rules, the target is the CSO attribute
            if (mapping.TargetConnectedSystemAttribute == null)
            {
                Log.Warning("CreateAttributeValueChanges: Export mapping has no TargetConnectedSystemAttribute set");
                continue;
            }

            foreach (var source in mapping.Sources)
            {
                // Handle expression-based mappings
                if (!string.IsNullOrWhiteSpace(source.Expression))
                {
                    // For Update operations with expressions, we need to check if any source attributes changed
                    // For simplicity, always include expression results for Create, but for Update we include them
                    // because expression results may depend on the changed attributes
                    // TODO (#880): Consider optimising by tracking which MVO attributes the expression depends on

                    // Build expression context with MVO attributes (lazy initialization - only build once)
                    mvAttributeDictionary ??= BuildAttributeDictionary(mvo);
                    var context = new ExpressionContext(mvAttributeDictionary, null);

                    // Only the evaluation itself is guarded. A thrown export expression must be surfaced as
                    // an errored object, never swallowed and never conflated with a deliberate null result.
                    // Known failure modes are rethrown as SyncExpressionEvaluationException for the worker to
                    // record as an ExpressionEvaluationError RPEI; anything else propagates to UnhandledError.
                    object? result;
                    try
                    {
                        result = expressionEvaluator.Evaluate(source.Expression, context);
                    }
                    catch (DynamicExpressoException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (ArgumentException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (FormatException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (OverflowException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (InvalidOperationException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (ArithmeticException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (InvalidCastException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (KeyNotFoundException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }

                    if (result == null)
                    {
                        // Null is expected when the referenced attribute doesn't exist on this MVO
                        Log.Debug("CreateAttributeValueChanges: Expression '{Expression}' for MVO {MvoId} returned null. " +
                            "Available attributes: [{Attributes}]",
                            source.Expression, mvo.Id, string.Join(", ", mvAttributeDictionary.Keys));
                    }

                    if (result != null)
                    {
                        var change = new PendingExportAttributeValueChange
                        {
                            Id = Guid.NewGuid(),
                            Attribute = mapping.TargetConnectedSystemAttribute,
                            AttributeId = mapping.TargetConnectedSystemAttribute.Id,
                            ChangeType = PendingExportAttributeChangeType.Update
                        };

                        // Set the value based on the result type
                        switch (result)
                        {
                            case string strValue:
                                change.StringValue = strValue;
                                break;
                            case int intValue:
                                change.IntValue = intValue;
                                break;
                            case long longValue:
                                change.LongValue = longValue;
                                break;
                            case decimal decimalValue:
                                change.DecimalValue = decimalValue;
                                break;
                            case DateTime dtValue:
                                change.DateTimeValue = dtValue;
                                break;
                            case bool boolValue:
                                change.BoolValue = boolValue;
                                break;
                            case Guid guidValue:
                                change.GuidValue = guidValue;
                                break;
                            case byte[] byteValue:
                                change.ByteValue = byteValue;
                                break;
                            default:
                                // Fall back to string representation
                                change.StringValue = result.ToString();
                                break;
                        }

                        // No-net-change detection for expression-based mappings
                        if (canDetectNoNetChange)
                        {
                            var cacheKey = (existingCso!.Id, change.AttributeId);
                            var existingCsoValues = csoAttributeCache![cacheKey];

                            if (IsCsoAttributeAlreadyCurrent(change, existingCsoValues))
                            {
                                Log.Debug("CreateAttributeValueChanges: Skipping attribute {AttrId} for CSO {CsoId} - CSO already has current value (expression)",
                                    change.AttributeId, existingCso.Id);
                                csoAlreadyCurrentCount++;
                                continue;
                            }
                        }

                        changes.Add(change);
                    }

                    continue;
                }

                // Handle direct Attribute Flow mappings
                if (source.MetaverseAttribute == null)
                    continue;

                // MVA -> SVA guard (#435): a multi-valued Metaverse source flowing to a single-valued Connected
                // System attribute can only carry one value. If the Metaverse Object holds more than one value for
                // the source attribute, JIM will not pick one arbitrarily; an arbitrary export could never be
                // reconciled on the next import (JIM would not know which value is authoritative). No Pending Export
                // is generated for this attribute and an error is recorded; the object's other attributes still export.
                if (mapping.TargetConnectedSystemAttribute.AttributePlurality == AttributePlurality.SingleValued &&
                    source.MetaverseAttribute.AttributePlurality == AttributePlurality.MultiValued)
                {
                    var mvoValueCount = mvo.AttributeValues.Count(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue);
                    if (mvoValueCount > 1)
                    {
                        Log.Error("CreateAttributeValueChanges: Multi-valued source attribute '{SourceAttr}' has {ValueCount} values but " +
                            "target attribute '{TargetAttr}' is single-valued. No Pending Export generated for this attribute. MVO {MvoId}",
                            source.MetaverseAttribute.Name, mvoValueCount, mapping.TargetConnectedSystemAttribute.Name, mvo.Id);

                        flowErrors?.Add(new AttributeFlowError
                        {
                            SourceAttributeName = source.MetaverseAttribute.Name,
                            TargetAttributeName = mapping.TargetConnectedSystemAttribute.Name,
                            ValueCount = mvoValueCount
                        });

                        continue;
                    }
                }

                // Get attribute values - handling differs for single-valued vs multi-valued attributes
                // Multi-valued attributes (like member) have multiple MVO attribute values with the same attribute ID
                var isMultiValued = source.MetaverseAttribute.AttributePlurality == AttributePlurality.MultiValued;

                IEnumerable<MetaverseObjectAttributeValue> mvoValues;
                if (isMultiValued)
                {
                    // For multi-valued attributes, get ALL values
                    if (isCreateOperation)
                    {
                        // Exclude asserted-null markers (#91): a NullValue row carries no value and must be
                        // invisible to export sourcing exactly as an absent row (the attribute reads as cleared).
                        var changedValues = changedAttributes
                            .Where(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue)
                            .ToList();

                        // Fall back to the MVO's current attribute values (excluding asserted-null markers,
                        // #91) when nothing relevant changed.
                        mvoValues = changedValues.Count > 0
                            ? changedValues
                            : mvo.AttributeValues.Where(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue);
                    }
                    else
                    {
                        // For Update operations, only include attributes that actually changed (excluding
                        // asserted-null markers, #91, which carry no value to export)
                        var matchingChangedValues = changedAttributes
                            .Where(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue)
                            .ToList();

                        Log.Debug("CreateAttributeValueChanges: Multi-valued Update for attr {AttrName} (Id={AttrId}): " +
                            "changedAttributes has {TotalCount} items, {MatchCount} match this attribute. " +
                            "removedAttributes has {RemovedCount} items",
                            source.MetaverseAttribute.Name, source.MetaverseAttribute.Id,
                            changedAttributes.Count, matchingChangedValues.Count,
                            removedAttributes?.Count ?? 0);

                        mvoValues = matchingChangedValues;
                    }
                }
                else
                {
                    // For single-valued attributes, only get the first value (excluding asserted-null markers, #91)
                    var changedValue = changedAttributes
                        .FirstOrDefault(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue);

                    // Create operations include all mapped attributes (not just changed ones); Update
                    // operations only include attributes that actually changed.
                    var mvoValue = isCreateOperation
                        ? changedValue ?? mvo.AttributeValues
                            .FirstOrDefault(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue)
                        : changedValue;

                    mvoValues = mvoValue != null ? [mvoValue] : [];
                }

                // Process each attribute value (supports multi-valued attributes)
                foreach (var mvoValue in mvoValues)
                {
                    // Note: We only set AttributeId here (not the Attribute navigation property)
                    // to avoid EF Core change tracking overhead during batch evaluation.
                    // The Attribute is loaded via Include when reading Pending Exports.
                    //
                    // For multi-valued attributes, use Add to add each value to the attribute,
                    // or Remove if the value was removed from the MVO.
                    // Using Update (Replace) would cause each value to overwrite the previous one,
                    // resulting in only the last value being exported.
                    // For single-valued attributes, use Update (Replace) for the whole attribute.
                    PendingExportAttributeChangeType attrChangeType;
                    if (isMultiValued)
                    {
                        // Check if this value is in the removals list using pre-built O(1) lookup sets.
                        // Three matching strategies in priority order (same logic as before, now O(1) per check):
                        var isRemoval = removedReferenceValueIds != null && (
                            (mvoValue.ReferenceValueId.HasValue && removedReferenceValueIds.Contains(mvoValue.ReferenceValueId.Value)) ||
                            (mvoValue.Id != Guid.Empty && removedEntityIds!.Contains(mvoValue.Id)) ||
                            (!mvoValue.ReferenceValueId.HasValue && mvoValue.Id == Guid.Empty &&
                                removedValueContents!.Contains((mvoValue.StringValue, mvoValue.IntValue, mvoValue.LongValue,
                                    mvoValue.DecimalValue, mvoValue.GuidValue, mvoValue.BoolValue, mvoValue.DateTimeValue))));

                        Log.Debug("CreateAttributeValueChanges: Processing MVO value Id={MvoValueId}, RefValueId={RefValueId}, isRemoval={IsRemoval}",
                            mvoValue.Id, mvoValue.ReferenceValueId, isRemoval);

                        attrChangeType = isRemoval
                            ? PendingExportAttributeChangeType.Remove
                            : PendingExportAttributeChangeType.Add;
                    }
                    else
                    {
                        attrChangeType = PendingExportAttributeChangeType.Update;
                    }

                    // For single-valued attributes, check if this value was removed from the MVO.
                    // Removals occur when an attribute value is no longer contributed by any source
                    // (e.g. attribute recall on CSO obsoletion, source no longer returning the value,
                    // CSO falling out of Synchronisation Rule scope). The changedAttributes list contains the
                    // original values (pre-removal) — we must create a null-clearing export so the
                    // target system clears the attribute, rather than copying the stale old value.
                    var isSingleValuedRemoval = !isMultiValued && removedAttributes?.Contains(mvoValue) == true;

                    if (isSingleValuedRemoval)
                    {
                        Log.Debug("CreateAttributeValueChanges: Single-valued attribute {AttrName} is a removal - " +
                            "creating null-clearing export change",
                            source.MetaverseAttribute.Name);
                    }

                    var attributeChange = new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        Attribute = mapping.TargetConnectedSystemAttribute,
                        AttributeId = mapping.TargetConnectedSystemAttribute.Id,
                        ChangeType = attrChangeType
                    };

                    // Set the appropriate value based on data type.
                    // For single-valued removals, skip value assignment — all fields remain
                    // null, which tells the target system to clear the attribute.
                    if (!isSingleValuedRemoval)
                    {
                        switch (source.MetaverseAttribute.Type)
                        {
                            case AttributeDataType.Text:
                                attributeChange.StringValue = mvoValue.StringValue;
                                break;
                            case AttributeDataType.Number:
                                attributeChange.IntValue = mvoValue.IntValue;
                                break;
                            case AttributeDataType.DateTime:
                                attributeChange.DateTimeValue = mvoValue.DateTimeValue;
                                break;
                            case AttributeDataType.Boolean:
                                attributeChange.BoolValue = mvoValue.BoolValue;
                                break;
                            case AttributeDataType.Guid:
                                attributeChange.GuidValue = mvoValue.GuidValue;
                                break;
                            case AttributeDataType.Binary:
                                attributeChange.ByteValue = mvoValue.ByteValue;
                                break;
                            case AttributeDataType.LongNumber:
                                attributeChange.LongValue = mvoValue.LongValue;
                                break;
                            case AttributeDataType.Decimal:
                                attributeChange.DecimalValue = mvoValue.DecimalValue;
                                break;
                            case AttributeDataType.Reference:
                                // For reference attributes, store the MVO ID as unresolved reference — will be
                                // resolved during export execution. Use navigation with scalar FK fallback for
                                // test compatibility.
                                // Reference recall (#908) supplies pre-resolved values instead: the referenced
                                // Metaverse Object is being deleted, so export-time resolution (which walks
                                // MVO -> joined CSO) can never succeed for it. In that case store the resolved
                                // target value (for example the DN) directly, exactly as export execution would.
                                var referencedMvoId = mvoValue.ReferenceValue?.Id ?? mvoValue.ReferenceValueId;
                                if (!referencedMvoId.HasValue)
                                {
                                    // A reference row with no referenced object carries nothing exportable, for
                                    // example a ghost row left by a pre-#1019 Metaverse Object deletion; emitting
                                    // it would stage an all-null change. Single-valued removals never reach here
                                    // (they skip value assignment entirely), so the clearing change is unaffected.
                                    Log.Debug("CreateAttributeValueChanges: Skipping valueless reference row {MvoValueId} for attribute {AttrName}",
                                        mvoValue.Id, source.MetaverseAttribute.Name);
                                    continue;
                                }
                                if (preResolvedReferenceValues != null &&
                                    preResolvedReferenceValues.TryGetValue(referencedMvoId.Value, out var preResolvedValue))
                                {
                                    attributeChange.StringValue = preResolvedValue;
                                }
                                else
                                {
                                    attributeChange.UnresolvedReferenceValue = referencedMvoId.Value.ToString();
                                }
                                break;
                        }
                    }

                    // No-net-change detection for direct attribute mappings
                    // Reference attributes: Pending Export stores MVO GUIDs in UnresolvedReferenceValue,
                    // CSO stores resolved references via ReferenceValue.MetaverseObjectId. The ValuesMatch
                    // method now compares these properly by extracting the MVO ID from both representations.
                    if (canDetectNoNetChange)
                    {
                        var cacheKey = (existingCso!.Id, attributeChange.AttributeId);
                        var existingCsoValues = csoAttributeCache![cacheKey];

                        if (IsCsoAttributeAlreadyCurrent(attributeChange, existingCsoValues))
                        {
                            Log.Debug("CreateAttributeValueChanges: Skipping attribute {AttrId} for CSO {CsoId} - CSO already has current value (direct)",
                                attributeChange.AttributeId, existingCso.Id);
                            csoAlreadyCurrentCount++;
                            continue;
                        }
                    }

                    changes.Add(attributeChange);
                }
            }
        }

        return changes;
    }

    /// <summary>
    /// Compares a Pending Export attribute value change against existing CSO attribute values
    /// to determine if they represent a no-net-change (CSO already has the target state).
    /// Supports multi-valued attributes where a single attribute can have multiple values.
    /// </summary>
    /// <param name="pendingChange">The pending change to export.</param>
    /// <param name="existingValues">The existing CSO attribute values for this attribute, may be empty.</param>
    /// <returns>True if the operation is a no-net-change (should be skipped), false otherwise.</returns>
    public static bool IsCsoAttributeAlreadyCurrent(
        PendingExportAttributeValueChange pendingChange,
        IEnumerable<ConnectedSystemObjectAttributeValue>? existingValues)
    {
        // Convert to list once to avoid multiple enumeration
        var valuesList = existingValues?.ToList() ?? [];

        switch (pendingChange.ChangeType)
        {
            case PendingExportAttributeChangeType.Add:
                // For Add: skip if the value already exists in CSO (no-net-change)
                // If the value doesn't exist, we need to add it (not a no-net-change)
                return valuesList.Any(ev => ValuesMatch(pendingChange, ev));

            case PendingExportAttributeChangeType.Remove:
                // For Remove: skip if the value doesn't exist in CSO (no-net-change)
                // If the value exists, we need to remove it (not a no-net-change)
                return !valuesList.Any(ev => ValuesMatch(pendingChange, ev));

            case PendingExportAttributeChangeType.RemoveAll:
                // For RemoveAll: skip if CSO has no values for this attribute (no-net-change)
                // If CSO has values, we need to remove them (not a no-net-change)
                return valuesList.Count == 0;

            case PendingExportAttributeChangeType.Update:
            default:
                // For Update (single-valued): use existing single-value comparison logic
                var existingValue = valuesList.FirstOrDefault();
                return IsSingleValueMatch(pendingChange, existingValue);
        }
    }

    /// <summary>
    /// Checks if a pending change value matches an existing CSO attribute value.
    /// Used for multi-valued attribute comparison (Add/Remove operations).
    /// </summary>
    /// <remarks>
    /// Reference attributes (like group members) may have their DNs stored in different fields:
    /// - Pending Exports from sync store resolved DNs in StringValue
    /// - CSO values from AD import store DNs in UnresolvedReferenceValue
    /// This method handles cross-field comparison for these cases.
    /// </remarks>
    private static bool ValuesMatch(
        PendingExportAttributeValueChange pendingChange,
        ConnectedSystemObjectAttributeValue existingValue)
    {
        // For reference attributes, the Pending Export stores an MVO GUID in UnresolvedReferenceValue,
        // while the CSO stores a resolved reference to another CSO (which has a MetaverseObjectId).
        // Compare using the MVO ID that both ultimately represent.
        var pendingHasUnresolvedRef = !string.IsNullOrEmpty(pendingChange.UnresolvedReferenceValue);

        if (pendingHasUnresolvedRef)
        {
            // Pending Export has an MVO GUID - compare with the MVO that the existing CSO reference points to
            var existingReferencedMvoId = existingValue.ReferenceValue?.MetaverseObjectId;
            if (existingReferencedMvoId.HasValue &&
                Guid.TryParse(pendingChange.UnresolvedReferenceValue, out var pendingMvoId))
            {
                return pendingMvoId == existingReferencedMvoId.Value;
            }

            // Fallback: compare as DN strings if the Pending Export has a resolved DN
            // (This handles cases where the Pending Export was created from an already-resolved reference)
            var existingDn = existingValue.UnresolvedReferenceValue;
            if (existingDn != null)
            {
                // DNs are case-insensitive in LDAP
                return string.Equals(pendingChange.UnresolvedReferenceValue, existingDn, StringComparison.OrdinalIgnoreCase);
            }

            // No match possible - CSO doesn't have this reference
            return false;
        }

        // Cross-field reference comparison (see remarks): a pre-resolved Pending Export change stores
        // the reference value (for example a DN) in StringValue, while an imported CSO reference keeps
        // the raw reference string in UnresolvedReferenceValue. This is the only comparison possible
        // when the referenced object's Metaverse Object has been deleted (reference recall, #908), as
        // the CSO-side navigation no longer resolves to a Metaverse Object. DNs are case-insensitive.
        if (pendingChange.StringValue != null && existingValue.UnresolvedReferenceValue != null)
        {
            return string.Equals(pendingChange.StringValue, existingValue.UnresolvedReferenceValue, StringComparison.OrdinalIgnoreCase);
        }

        // Compare based on which value type is set
        // String comparison (case-sensitive for regular attributes)
        if (pendingChange.StringValue != null || existingValue.StringValue != null)
        {
            return string.Equals(pendingChange.StringValue, existingValue.StringValue, StringComparison.Ordinal);
        }

        // Integer comparison
        if (pendingChange.IntValue.HasValue || existingValue.IntValue.HasValue)
        {
            return pendingChange.IntValue == existingValue.IntValue;
        }

        // Long comparison
        if (pendingChange.LongValue.HasValue || existingValue.LongValue.HasValue)
        {
            return pendingChange.LongValue == existingValue.LongValue;
        }

        // Decimal comparison: nullable decimal == is numeric and scale-insensitive, so 5.0 matches 5.00
        if (pendingChange.DecimalValue.HasValue || existingValue.DecimalValue.HasValue)
        {
            return pendingChange.DecimalValue == existingValue.DecimalValue;
        }

        // DateTime comparison
        if (pendingChange.DateTimeValue.HasValue || existingValue.DateTimeValue.HasValue)
        {
            return pendingChange.DateTimeValue == existingValue.DateTimeValue;
        }

        // Binary comparison
        if (pendingChange.ByteValue != null || existingValue.ByteValue != null)
        {
            if (pendingChange.ByteValue == null && existingValue.ByteValue == null)
                return true;
            if (pendingChange.ByteValue == null || existingValue.ByteValue == null)
                return false;
            return pendingChange.ByteValue.SequenceEqual(existingValue.ByteValue);
        }

        // Unresolved reference comparison (only if neither side used cross-field DN)
        if (pendingChange.UnresolvedReferenceValue != null || existingValue.UnresolvedReferenceValue != null)
        {
            return string.Equals(pendingChange.UnresolvedReferenceValue, existingValue.UnresolvedReferenceValue, StringComparison.Ordinal);
        }

        // Guid comparison (pending stores as StringValue, CSO has GuidValue)
        if (existingValue.GuidValue.HasValue)
        {
            if (Guid.TryParse(pendingChange.StringValue, out var pendingGuid))
                return pendingGuid == existingValue.GuidValue.Value;
            return false;
        }

        // Bool comparison (pending stores as StringValue, CSO has BoolValue)
        if (existingValue.BoolValue.HasValue)
        {
            if (bool.TryParse(pendingChange.StringValue, out var pendingBool))
                return pendingBool == existingValue.BoolValue.Value;
            return false;
        }

        // Both null/empty - consider them matching
        return true;
    }

    /// <summary>
    /// Compares a Pending Export attribute value change against a single existing CSO attribute value
    /// to determine if they represent the same value (no-net-change).
    /// Used for single-valued Update operations.
    /// </summary>
    /// <param name="pendingChange">The pending change to export.</param>
    /// <param name="existingValue">The existing CSO attribute value, or null if no value exists.</param>
    /// <returns>True if the values are identical (no-net-change), false otherwise.</returns>
    private static bool IsSingleValueMatch(
        PendingExportAttributeValueChange pendingChange,
        ConnectedSystemObjectAttributeValue? existingValue)
    {
        // If no existing value, this is a new attribute - not a no-net-change
        if (existingValue == null)
        {
            // Check if the pending change is also null/empty
            var isEmpty = IsPendingChangeEmpty(pendingChange);
            Log.Debug("IsSingleValueMatch: existingValue is null, pendingChange empty={IsEmpty}, IntValue={IntValue}, StringValue={StringValue}",
                isEmpty, pendingChange.IntValue, LogSanitiser.Sanitise(pendingChange.StringValue));
            return isEmpty;
        }

        var result = ValuesMatch(pendingChange, existingValue);
        Log.Debug("IsSingleValueMatch: Comparing pendingChange (IntValue={PendingInt}, StringValue={PendingStr}) with existingValue (IntValue={ExistingInt}, StringValue={ExistingStr}). Result={Result}",
            pendingChange.IntValue, LogSanitiser.Sanitise(pendingChange.StringValue), existingValue.IntValue, LogSanitiser.Sanitise(existingValue.StringValue), result);
        return result;
    }

    /// <summary>
    /// Builds a dictionary of attribute values from a Metaverse Object for expression evaluation.
    /// The dictionary keys are attribute names, and values are the attribute values.
    /// </summary>
    internal static Dictionary<string, object?> BuildAttributeDictionary(MetaverseObject mvo)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (mvo.Type == null)
        {
            Log.Warning("BuildAttributeDictionary: MVO {MvoId} has null Type, cannot build attribute dictionary", mvo.Id);
            return attributes;
        }

        // Exclude asserted-null markers (#91): they carry no value, so the expression context must treat the
        // attribute as absent (mv["x"] resolves to null) rather than seeing a phantom value.
        foreach (var attributeValue in mvo.AttributeValues.Where(av => !av.NullValue))
        {
            if (attributeValue.Attribute == null)
            {
                // Log warning for diagnostic purposes - this indicates a missing Include or EF tracking issue
                Log.Warning("BuildAttributeDictionary: MVO {MvoId} has attribute value with AttributeId={AttrId} but Attribute navigation property is null. " +
                    "This will cause expression-based mappings to fail for this attribute.",
                    mvo.Id, attributeValue.AttributeId);
                continue;
            }

            var attributeName = attributeValue.Attribute.Name;

            // Use the appropriate typed value based on the attribute type
            object? value = attributeValue.Attribute.Type switch
            {
                AttributeDataType.Text => attributeValue.StringValue,
                AttributeDataType.Number => attributeValue.IntValue,
                AttributeDataType.LongNumber => attributeValue.LongValue,
                AttributeDataType.Decimal => attributeValue.DecimalValue,
                AttributeDataType.DateTime => attributeValue.DateTimeValue,
                AttributeDataType.Boolean => attributeValue.BoolValue,
                AttributeDataType.Guid => attributeValue.GuidValue,
                AttributeDataType.Binary => attributeValue.ByteValue,
                // Fall back to the FK scalar when the navigation is not loaded: reconciler-flagged MVOs
                // (#892) arrive via a no-tracking query that deliberately omits the ReferenceValue Include.
                AttributeDataType.Reference => attributeValue.ReferenceValue?.Id.ToString() ?? attributeValue.ReferenceValueId?.ToString(),
                _ => null
            };

            attributes[attributeName] = value;
        }

        return attributes;
    }

    /// <summary>
    /// Builds a <see cref="SyncExpressionEvaluationException"/> carrying the failing export expression and
    /// the target connected system attribute name, so the worker can record an ExpressionEvaluationError
    /// RPEI for the Metaverse Object being evaluated.
    /// </summary>
    private static SyncExpressionEvaluationException BuildExportExpressionEvaluationException(
        SyncRuleMapping mapping, SyncRuleMappingSource source, Exception innerException)
    {
        return new SyncExpressionEvaluationException(source.Expression, mapping.TargetConnectedSystemAttribute?.Name, innerException);
    }

    /// <summary>
    /// Generates a composite key for a PendingExportAttributeValueChange that identifies the specific
    /// attribute+value combination. For multi-valued attributes like group membership, each individual value
    /// gets a distinct key, allowing merge sources to contribute different values for the same attribute.
    /// </summary>
    internal static string GetAttributeChangeKey(PendingExportAttributeValueChange change)
    {
        // Build a value identifier from whichever value field is populated
        var valueId = change.UnresolvedReferenceValue
            ?? change.StringValue
            ?? change.GuidValue?.ToString()
            ?? change.IntValue?.ToString()
            ?? change.LongValue?.ToString()
            // Canonical form is mandatory for decimals: a raw ToString preserves trailing zeros, so 5.0
            // and 5.00 would produce different merge keys and defeat multi-valued dedupe/merge.
            ?? (change.DecimalValue.HasValue ? DecimalAttributeValue.ToCanonicalString(change.DecimalValue.Value) : null)
            ?? change.DateTimeValue?.ToString("O")
            ?? change.BoolValue?.ToString()
            ?? (change.ByteValue != null ? Convert.ToBase64String(change.ByteValue) : null)
            ?? string.Empty;

        return $"{change.AttributeId}:{valueId}";
    }

    /// <summary>
    /// Returns a merge key for deduplicating attribute changes when combining Pending Exports. For
    /// single-valued attributes, the key is just the attribute id: the newest change always wins regardless
    /// of value (both surviving would export "SINGLE-VALUE attribute specified more than once"). For
    /// multi-valued attributes, the key includes the value so distinct values are preserved during merge.
    /// </summary>
    internal static string GetAttributeChangeMergeKey(PendingExportAttributeValueChange change)
    {
        if (change.Attribute?.AttributePlurality != AttributePlurality.MultiValued)
            return change.AttributeId.ToString();

        return GetAttributeChangeKey(change);
    }

    /// <summary>
    /// Selects the changes on an existing (stale, typically drift-staged) Pending Export that survive a merge
    /// with a newly evaluated set of export changes. Export evaluation always wins on a collision, because it
    /// derives from the latest Metaverse Object state.
    /// </summary>
    /// <param name="incomingChanges">The newly evaluated export changes, which take precedence.</param>
    /// <param name="existingChanges">The changes already staged on the Pending Export being merged into.</param>
    /// <returns>The existing changes that are not superseded, in their original order.</returns>
    internal static List<PendingExportAttributeValueChange> SelectSurvivingDriftChanges(
        IReadOnlyCollection<PendingExportAttributeValueChange> incomingChanges,
        IEnumerable<PendingExportAttributeValueChange> existingChanges)
    {
        ArgumentNullException.ThrowIfNull(incomingChanges);
        ArgumentNullException.ThrowIfNull(existingChanges);

        var incomingKeys = incomingChanges.Select(GetAttributeChangeMergeKey).ToHashSet();
        var wholeAttributeReplacementIds = GetWholeAttributeReplacementAttributeIds(incomingChanges);

        return existingChanges
            .Where(existing => !incomingKeys.Contains(GetAttributeChangeMergeKey(existing)))
            .Where(existing => !wholeAttributeReplacementIds.Contains(existing.AttributeId))
            .ToList();
    }

    /// <summary>
    /// The attribute ids among a set of changes whose change type sets the attribute's ENTIRE value set
    /// rather than one value within it (#1199). Update and RemoveAll both export as a replace, so every other
    /// staged change for the same attribute is superseded, whatever its value or change type.
    /// </summary>
    /// <remarks>
    /// The merge key alone is not enough to catch this. It keys multi-valued attributes by value, which is
    /// right for genuine per-value adds and removals, but a change's type follows the Metaverse attribute's
    /// plurality while the key follows the Connected System attribute's: a single-valued Metaverse attribute
    /// flowing to a multi-valued Connected System attribute produces an Update whose key still carries a
    /// value. A stale per-value Remove for the same attribute then survives the merge, and the connector
    /// emits the replace followed by a delete of a value the replace has already removed; LDAP rejects that
    /// modify atomically, so the export never applies.
    /// </remarks>
    internal static HashSet<int> GetWholeAttributeReplacementAttributeIds(IEnumerable<PendingExportAttributeValueChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        return changes
            .Where(c => c.ChangeType is PendingExportAttributeChangeType.Update or PendingExportAttributeChangeType.RemoveAll)
            .Select(c => c.AttributeId)
            .ToHashSet();
    }
}

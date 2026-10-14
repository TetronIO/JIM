// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
    /// <param name="existingPendingExport">The Pending Export already attached to <paramref name="existingCso"/>,
    /// if any; the caller resolves this only for a PendingProvisioning CSO (it is irrelevant otherwise), from
    /// the run's in-memory batch first and the database second. Distinguishes a Create that has never been
    /// sent (<see cref="IsProvisioningNeverExported"/>) from one that has already been sent (or auto-confirmed
    /// away): a change arriving after the Create is sent must stage an Update, never a second Create.</param>
    public OutboundStagingDecision DecideOutboundStaging(
        MetaverseObject mvo,
        SyncRule exportRule,
        ConnectedSystemObject? existingCso,
        List<MetaverseObjectAttributeValue> changedAttributes,
        bool recallSemantics,
        PendingExport? existingPendingExport)
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
        // causality tree. Applies whether the Create is still unsent or has already gone out, below.
        if (!HasRelevantChangedAttributes(changedAttributes, exportRule))
            return new OutboundStagingDecision { Outcome = OutboundStagingOutcome.PendingProvisioningChangesIrrelevant };

        // The Create is still unsent (never attempted): absorb the change into it in place, still a
        // Create, exactly as before.
        if (IsProvisioningNeverExported(existingCso, existingPendingExport))
        {
            return new OutboundStagingDecision
            {
                Outcome = OutboundStagingOutcome.ReusePendingProvisioningCso,
                ChangeType = PendingExportChangeType.Create
            };
        }

        // The Create has already been sent: it is gone (existingPendingExport is null, an auto-confirming
        // file export deleted it the moment it succeeded), it is a Create sitting Exported/ExportNotConfirmed
        // awaiting a confirming import, or it was attempted and is retrying. Either way the object may
        // already exist in the target, so the change must travel as an Update, never a second Create - most
        // connectors reject a Create for an object that already exists. The orchestrator never sends this
        // Update before the Create is confirmed by import (ExportExecutionServer's exportability guard), and
        // once confirmed, SyncEngine.Reconciliation.cs flips this very Pending Export's ChangeType from
        // Create to Update so the queued change goes out on the next export.
        return new OutboundStagingDecision
        {
            Outcome = OutboundStagingOutcome.UpdateExportedProvisioningCso,
            ChangeType = PendingExportChangeType.Update
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

        // A disabled mapping (#1485) reads nothing at all, so it must not make changes relevant; left in,
        // a disabled expression mapping would keep forcing evaluation of every change.
        foreach (var source in exportRule.AttributeFlowRules.Where(mapping => mapping.Enabled).SelectMany(mapping => mapping.Sources))
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
    /// <remarks>
    /// A single-valued attribute holds one value, so it leaves the merge with exactly one change (#2182): incoming
    /// changes that name such an attribute more than once are first folded into one Update by
    /// <see cref="WithOneChangePerSingleValuedAttribute"/>. Keyed by the attribute alone, the merge would otherwise keep
    /// whichever came last, and an Add of the new value followed by a Remove of the old one left only the Remove, which
    /// cleared the target.
    /// </remarks>
    /// <param name="stagedPendingExport">The Pending Export already staged for the CSO, mutated in place.</param>
    /// <param name="newChanges">The newly evaluated attribute changes to merge in.</param>
    public PendingExportMergeResult MergeAttributeChangesIntoPendingExport(
        PendingExport stagedPendingExport,
        List<PendingExportAttributeValueChange> newChanges)
    {
        var replacedCount = 0;
        var addedCount = 0;

        newChanges = WithOneChangePerSingleValuedAttribute(newChanges);

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
    /// Withdraws the changes staged on <paramref name="stagedPendingExport"/> that an export evaluation has just
    /// found unnecessary: <paramref name="alreadyCurrentChanges"/> are the changes the evaluation skipped because the
    /// target Connected System Object already holds what they would set (no-net-change detection, #244). The
    /// Metaverse and the target already agree on those, so any change still queued for the same attribute (or,
    /// for a multi-valued attribute, the same value) no longer reflects the Metaverse and must not be exported:
    /// a staged "set B" when the Metaverse has returned to the "A" the target holds, a staged clear when another
    /// source has since supplied the value the target holds, a staged "remove cn=alice" when the Metaverse wants
    /// cn=alice again. It is the same supersede rule <see cref="MergeAttributeChangesIntoPendingExport"/> applies to
    /// a newly evaluated change (merge keys, and #1199's whole-attribute rule), applied when the evaluation's answer
    /// for the attribute is "no change". A change already sent and awaiting its confirming import is kept: it is
    /// the confirmation of what the target now holds; one held until an Initial Password is delivered is not exempt
    /// (#2039, see <see cref="SelectSurvivingDriftChanges"/>). Mutates in place; returns the staged changes withdrawn.
    /// </summary>
    public List<PendingExportAttributeValueChange> WithdrawChangesAlreadyCurrent(
        PendingExport stagedPendingExport,
        IReadOnlyCollection<PendingExportAttributeValueChange> alreadyCurrentChanges)
    {
        ArgumentNullException.ThrowIfNull(stagedPendingExport);
        ArgumentNullException.ThrowIfNull(alreadyCurrentChanges);

        var withdrawn = SelectChangesWithdrawnAsAlreadyCurrent([], stagedPendingExport.AttributeValueChanges, alreadyCurrentChanges);
        foreach (var change in withdrawn)
            stagedPendingExport.AttributeValueChanges.Remove(change);

        return withdrawn;
    }

    /// <summary>
    /// The queued changes an export evaluation withdraws because the target already holds what the Metaverse now
    /// wants (#2001): the changes on <paramref name="existingChanges"/> that would survive a merge with
    /// <paramref name="incomingChanges"/> but not the evaluation's <paramref name="alreadyCurrentChanges"/>. A queued
    /// change the evaluation's own new change supersedes is replaced, not withdrawn, so it is not among them; neither
    /// is one already sent and awaiting confirmation, unless it is held until an Initial Password is delivered (#2039).
    /// The same rules as <see cref="SelectSurvivingDriftChanges"/>,
    /// which is what the run applies, so the run and the previews agree on what is withdrawn.
    /// </summary>
    /// <param name="incomingChanges">The changes the evaluation stages.</param>
    /// <param name="existingChanges">The changes already queued on the object's Pending Export.</param>
    /// <param name="alreadyCurrentChanges">The changes the evaluation skipped as already current on the target.</param>
    /// <returns>The withdrawn changes, in their original order.</returns>
    internal static List<PendingExportAttributeValueChange> SelectChangesWithdrawnAsAlreadyCurrent(
        IReadOnlyCollection<PendingExportAttributeValueChange> incomingChanges,
        IReadOnlyCollection<PendingExportAttributeValueChange> existingChanges,
        IReadOnlyCollection<PendingExportAttributeValueChange> alreadyCurrentChanges)
    {
        ArgumentNullException.ThrowIfNull(incomingChanges);
        ArgumentNullException.ThrowIfNull(existingChanges);
        ArgumentNullException.ThrowIfNull(alreadyCurrentChanges);

        if (alreadyCurrentChanges.Count == 0 || existingChanges.Count == 0)
            return [];

        var survivors = SelectSurvivingDriftChanges(incomingChanges, existingChanges, alreadyCurrentChanges).ToHashSet();
        return [.. SelectSurvivingDriftChanges(incomingChanges, existingChanges).Where(change => !survivors.Contains(change))];
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
    /// Where both the source and the target attribute are multi-valued, values in this set create Remove changes
    /// instead of Add changes. Everywhere else the target is sent one Update carrying the value that survives, so a
    /// removed value is never it, and an attribute left with none gets a null-clearing Update (#2182).</param>
    /// <param name="mvAttributeDictionary">Optional pre-built MVO attribute dictionary for expression evaluation.</param>
    /// <param name="preResolvedReferenceValues">Optional map of referenced Metaverse Object ID to the resolved
    /// target value for this export rule's Connected System (reference recall, #908). When a reference points
    /// at an object in this map, the change is staged with the resolved value instead of an unresolved
    /// Metaverse Object ID, because export-time resolution cannot resolve a deleted object.</param>
    /// <param name="noNetChangeSkipped">Optional collector for the changes skipped because the Connected System
    /// Object already holds the value (#1443). The value a change was skipped for IS the target's current state for
    /// that attribute, and it is the only thing that can put an old-to-new pair on a configuration change preview:
    /// a preview diffs what two configurations would stage, and where the stored one stages nothing because the
    /// target is already correct, the pair would otherwise have no old side. Opt-in like <paramref name="flowErrors"/>
    /// beside it, so the export hot path allocates nothing and behaves identically when no caller asks.</param>
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
        List<AttributeFlowError>? flowErrors = null,
        List<PendingExportAttributeValueChange>? noNetChangeSkipped = null)
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
        // synchronisation integrity guard rather than an optimisation. A disabled mapping (#1485) is skipped
        // on Create as much as Update, which is why Enabled sits outside the isCreateOperation escape.
        foreach (var mapping in exportRule.AttributeFlowRules.Where(m => m.Enabled && (isCreateOperation || m.FlowsOnUpdateExport())))
        {
            // For export rules, the target is the CSO attribute
            if (mapping.TargetConnectedSystemAttribute == null)
            {
                Log.Warning("CreateAttributeValueChanges: Export mapping has no TargetConnectedSystemAttribute set");
                continue;
            }

            // Unique Value Generation (#242, Phase 2 work package H): a generated mapping evaluates its base
            // expression exactly as an ordinary expression mapping does, but never resolves a value here (the
            // engine performs no I/O, plan decision 6) - it stages a marked, unresolved change for the worker
            // to resolve before the Pending Export is persisted. No-net-change detection cannot decide a
            // pending generation (the value is unknown), so this bypasses it entirely, unconditionally.
            if (mapping.Generation != null)
            {
                mvAttributeDictionary ??= BuildAttributeDictionary(mvo);
                var generatedChange = StageGeneratedExportMapping(mapping, exportRule, mvAttributeDictionary, expressionEvaluator, flowErrors);
                if (generatedChange != null)
                    changes.Add(generatedChange);
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

                    // Every change an expression stages is for its one target attribute, so the Connected System
                    // Object's values for it are read once, for staging and for no-net-change detection alike.
                    var heldValues = canDetectNoNetChange
                        ? csoAttributeCache![(existingCso!.Id, mapping.TargetConnectedSystemAttribute.Id)].ToList()
                        : null;

                    // The whole evaluation pipeline (#2055), the same code the Attribute Flow tester runs, then what
                    // export staging stages from it; Drift Detection stages through the same method (#2180).
                    var expressionChanges = StageExportExpressionMapping(mapping, source, exportRule, mvAttributeDictionary,
                        expressionEvaluator, heldValues, flowErrors);

                    // No-net-change detection for expression-based mappings
                    var alreadyCurrent = heldValues == null
                        ? []
                        : expressionChanges.Where(change => IsCsoAttributeAlreadyCurrent(change, heldValues)).ToList();
                    foreach (var change in alreadyCurrent)
                    {
                        Log.Debug("CreateAttributeValueChanges: Skipping attribute {AttrId} for CSO {CsoId} - CSO already has current value (expression)",
                            change.AttributeId, existingCso!.Id);
                    }

                    csoAlreadyCurrentCount += alreadyCurrent.Count;
                    noNetChangeSkipped?.AddRange(alreadyCurrent);
                    changes.AddRange(expressionChanges.Where(change => !alreadyCurrent.Contains(change)));
                    continue;
                }

                // Handle direct Attribute Flow mappings
                if (source.MetaverseAttribute == null)
                    continue;

                // MVA -> SVA guard (#435), shared with Drift Detection (#2180): no Pending Export is generated for this
                // attribute and an error is recorded; the object's other attributes still export.
                var multiValuedToSingleValuedError = FindMultiValuedToSingleValuedError(mvo, mapping, source);
                if (multiValuedToSingleValuedError != null)
                {
                    flowErrors?.Add(multiValuedToSingleValuedError);
                    continue;
                }

                // The change's shape follows the plurality of the attribute it is written to (#2182), never the
                // plurality of the Metaverse Attribute it comes from: the merge key, every Connector and export
                // confirmation all go by the target's. Per-value Add and Remove only where both are multi-valued.
                // Everywhere else the mapping stages one Update carrying the value the attribute should now hold, or an
                // all-null Update when it should hold none. A single-valued target holds one value, so it is told that
                // value: an Add and a Remove for one became two assignments to one SQL column, and a merge keyed by the
                // attribute kept only the Remove. A multi-valued target fed by a single-valued source is told its whole
                // set is that one value, which is what an Update means to every Connector.
                var perValue = source.MetaverseAttribute.AttributePlurality == AttributePlurality.MultiValued &&
                               mapping.TargetConnectedSystemAttribute.AttributePlurality == AttributePlurality.MultiValued;

                List<(MetaverseObjectAttributeValue? Value, PendingExportAttributeChangeType ChangeType)> stagedValues;
                if (perValue)
                {
                    IEnumerable<MetaverseObjectAttributeValue> mvoValues;
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

                    // Each value is added, or removed if it was removed from the MVO. An Update (a replace) per value
                    // would have each value overwrite the one before, exporting only the last.
                    stagedValues = [.. mvoValues.Select(mvoValue => ((MetaverseObjectAttributeValue?)mvoValue, PerValueChangeType(mvoValue)))];
                }
                else
                {
                    var (stage, wholeValue) = SelectWholeAttributeValue(mvo, source.MetaverseAttribute.Id, changedAttributes,
                        removedAttributes, isCreateOperation);
                    stagedValues = stage ? [(wholeValue, PendingExportAttributeChangeType.Update)] : [];

                    if (stage && wholeValue == null)
                    {
                        Log.Debug("CreateAttributeValueChanges: Attribute {AttrName} holds no value any more - creating a null-clearing export change",
                            source.MetaverseAttribute.Name);
                    }
                }

                foreach (var (mvoValue, attrChangeType) in stagedValues)
                {
                    // The target Attribute navigation is set deliberately, not just AttributeId: the single-valued
                    // fold in the merge (WithOneChangePerSingleValuedAttribute), the whole-set Update checks
                    // (IsWholeSetUpdate) and the pre-send SingleValuedAttributeChangeValidator all read its
                    // plurality (#2182), and would silently stop working without it.
                    var attributeChange = new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        Attribute = mapping.TargetConnectedSystemAttribute,
                        AttributeId = mapping.TargetConnectedSystemAttribute.Id,
                        ChangeType = attrChangeType,
                        SyncRuleId = exportRule.Id,
                        SyncRuleName = exportRule.Name
                    };

                    // Set the appropriate value based on data type. A clear carries no value: all fields remain
                    // null, which tells the target system to clear the attribute.
                    if (mvoValue != null)
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
                                    // it would stage an all-null change. A clear never reaches here (it carries no
                                    // value), so the clearing change is unaffected.
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
                            noNetChangeSkipped?.Add(attributeChange);
                            continue;
                        }
                    }

                    changes.Add(attributeChange);
                }
            }
        }

        return changes;

        // Whether a value of a multi-valued source, exported per value, is a removal: matched against the removals by
        // the pre-built O(1) lookup sets, in priority order (referenced object, persisted row, then value content).
        PendingExportAttributeChangeType PerValueChangeType(MetaverseObjectAttributeValue mvoValue)
        {
            var isRemoval = removedReferenceValueIds != null && (
                (mvoValue.ReferenceValueId.HasValue && removedReferenceValueIds.Contains(mvoValue.ReferenceValueId.Value)) ||
                (mvoValue.Id != Guid.Empty && removedEntityIds!.Contains(mvoValue.Id)) ||
                (!mvoValue.ReferenceValueId.HasValue && mvoValue.Id == Guid.Empty &&
                    removedValueContents!.Contains((mvoValue.StringValue, mvoValue.IntValue, mvoValue.LongValue,
                        mvoValue.DecimalValue, mvoValue.GuidValue, mvoValue.BoolValue, mvoValue.DateTimeValue))));

            Log.Debug("CreateAttributeValueChanges: Processing MVO value Id={MvoValueId}, RefValueId={RefValueId}, isRemoval={IsRemoval}",
                mvoValue.Id, mvoValue.ReferenceValueId, isRemoval);

            return isRemoval ? PendingExportAttributeChangeType.Remove : PendingExportAttributeChangeType.Add;
        }
    }

    /// <summary>
    /// The MVA -> SVA guard (#435): a multi-valued Metaverse source flowing to a single-valued Connected System attribute
    /// can only carry one value. When the Metaverse Object holds more than one value for the source attribute, JIM will
    /// not pick one arbitrarily, since an arbitrary export could never be reconciled on the next import (JIM would not
    /// know which value is authoritative). Export staging and Drift Detection (#2180) both refuse the mapping through
    /// this one predicate, so neither writes a value the other would not.
    /// </summary>
    /// <returns>The error to record when the mapping must stage nothing; null when it may flow.</returns>
    internal static AttributeFlowError? FindMultiValuedToSingleValuedError(MetaverseObject mvo, SyncRuleMapping mapping, SyncRuleMappingSource source)
    {
        var target = mapping.TargetConnectedSystemAttribute;
        if (target == null || source.MetaverseAttribute == null ||
            target.AttributePlurality != AttributePlurality.SingleValued ||
            source.MetaverseAttribute.AttributePlurality != AttributePlurality.MultiValued)
            return null;

        var sourceAttributeId = source.MetaverseAttribute.Id;
        var mvoValueCount = mvo.AttributeValues.Count(av => av.AttributeId == sourceAttributeId && !av.NullValue);
        if (mvoValueCount <= 1)
            return null;

        Log.Error("CreateAttributeValueChanges: Multi-valued source attribute '{SourceAttr}' has {ValueCount} values but " +
            "target attribute '{TargetAttr}' is single-valued. No Pending Export generated for this attribute. MVO {MvoId}",
            source.MetaverseAttribute.Name, mvoValueCount, target.Name, mvo.Id);

        return new AttributeFlowError
        {
            Kind = AttributeFlowErrorKind.MultiValuedToSingleValued,
            SourceAttributeName = source.MetaverseAttribute.Name,
            TargetAttributeName = target.Name,
            ValueCount = mvoValueCount
        };
    }

    /// <summary>
    /// The value a mapping exports as a whole (#2182): the one change it stages where the source and target are not both
    /// multi-valued, so the target is told what the attribute now holds rather than which values came and went. That is
    /// the value this pass gave the attribute (additions precede removals in <paramref name="changedAttributes"/>), else
    /// the one the Metaverse Object holds; a value removed this pass is never it. Removals are matched by identity, not by
    /// content: a reference removed and given again is a new row naming the same object, and it survives.
    /// </summary>
    /// <param name="mvo">The Metaverse Object, holding its values as this pass leaves them.</param>
    /// <param name="sourceAttributeId">The mapping's source Metaverse Attribute.</param>
    /// <param name="changedAttributes">The values this pass changed: additions, then removals.</param>
    /// <param name="removedAttributes">The values this pass removed.</param>
    /// <param name="isCreateOperation">True for a Create, which provisions every mapped attribute whether or not it changed.</param>
    /// <returns>Whether anything is staged, and the value to carry: null for a clear, which is staged when the attribute
    /// changed and no value survives. An Update stages nothing for an attribute this pass did not change, and a Create
    /// nothing for one with no value to provision.</returns>
    private static (bool Stage, MetaverseObjectAttributeValue? Value) SelectWholeAttributeValue(
        MetaverseObject mvo,
        int sourceAttributeId,
        List<MetaverseObjectAttributeValue> changedAttributes,
        HashSet<MetaverseObjectAttributeValue>? removedAttributes,
        bool isCreateOperation)
    {
        // Asserted-null markers (#91) carry no value, so they are invisible here exactly as an absent row.
        var changedValues = changedAttributes
            .Where(av => av.AttributeId == sourceAttributeId && !av.NullValue)
            .ToList();

        if (!isCreateOperation && changedValues.Count == 0)
            return (false, null);

        var survivor = changedValues.Find(av => !IsRemoved(av)) ??
                       mvo.AttributeValues.FirstOrDefault(av => av.AttributeId == sourceAttributeId && !av.NullValue && !IsRemoved(av));

        return survivor != null
            ? (true, survivor)
            : (changedValues.Count > 0, null);

        bool IsRemoved(MetaverseObjectAttributeValue value) => removedAttributes?.Contains(value) == true;
    }

    /// <summary>
    /// The Pending Export changes an export Expression mapping stages for a Metaverse Object whose values are
    /// <paramref name="mvAttributeDictionary"/>: the evaluation pipeline (#2055), then what export staging does with its
    /// outcome. Missing Input Behaviour (#1361) and the expression's own exception mapping are shared with a generated
    /// mapping's base expression (Unique Value Generation, #242) through <see cref="ContinueAfterExportSource"/>.
    /// Stopped (FailMapping, its <see cref="AttributeFlowError"/> already recorded) and no value (ContributeNoValue, or a
    /// null result, expected when the referenced attribute does not exist on this Metaverse Object) alike stage nothing,
    /// so the Connected System keeps whatever it holds; the difference is whether it is reported. Drift Detection stages
    /// its corrections for an expression mapping through here too (#2180), so it never asserts a state export staging
    /// would not stage.
    /// </summary>
    /// <param name="heldValues">The Connected System Object's current values for the target attribute, or null when
    /// there is no object yet (a Create) or none was supplied.</param>
    /// <exception cref="SyncExpressionMissingInputException">An input is missing and Missing Input Behaviour is
    /// FailObject.</exception>
    /// <exception cref="SyncExpressionEvaluationException">The expression failed to evaluate.</exception>
    internal static List<PendingExportAttributeValueChange> StageExportExpressionMapping(
        SyncRuleMapping mapping,
        SyncRuleMappingSource source,
        SyncRule exportRule,
        Dictionary<string, object?> mvAttributeDictionary,
        IExpressionEvaluator expressionEvaluator,
        IEnumerable<ConnectedSystemObjectAttributeValue>? heldValues,
        List<AttributeFlowError>? flowErrors)
    {
        var evaluation = AttributeFlowPipeline.EvaluateExport(mvAttributeDictionary, mapping, source, expressionEvaluator);

        if (!ContinueAfterExportSource(evaluation.Source, mapping, source, mvAttributeDictionary, flowErrors) ||
            evaluation.Outcome == AttributeFlowOutcome.NoValue)
            return [];

        return StageExportExpressionResult(mapping, source, exportRule, evaluation, heldValues, flowErrors);
    }

    /// <summary>
    /// The Pending Export changes an export Expression's result stages (#2179), resolved through the one result
    /// handler: every value converted to the target attribute's type, so text a Number attribute cannot hold is
    /// reported rather than sent and "512" stages the number 512, and several values never become their type name.
    /// <list type="bullet">
    /// <item>No value (an empty collection, or blank text for a typed attribute): nothing, the same as a null result.</item>
    /// <item>A value the target's type cannot hold: nothing, and an <see cref="AttributeFlowErrorKind.ExpressionResultNotConvertible"/> error.</item>
    /// <item>Several values for a single-valued target: nothing, and an <see cref="AttributeFlowErrorKind.ExpressionResultMultiValued"/> error.</item>
    /// <item>A single value (not a list), or the one value a single-valued target takes: one Update, as an expression
    /// has always staged.</item>
    /// <item>A list for a multi-valued target, whatever its length: an Add for each value and a Remove for each value
    /// the Connected System Object holds that the result does not, so the target ends up holding exactly the
    /// result. A list of one value is the whole set as much as a list of two is.</item>
    /// </list>
    /// </summary>
    /// <param name="evaluation">The pipeline's evaluation, which produced a result to resolve.</param>
    /// <param name="heldValues">The Connected System Object's current values for the target attribute, or null when
    /// there is no object yet (a Create) or none was supplied.</param>
    private static List<PendingExportAttributeValueChange> StageExportExpressionResult(
        SyncRuleMapping mapping,
        SyncRuleMappingSource source,
        SyncRule exportRule,
        AttributeFlowEvaluation evaluation,
        IEnumerable<ConnectedSystemObjectAttributeValue>? heldValues,
        List<AttributeFlowError>? flowErrors)
    {
        var target = mapping.TargetConnectedSystemAttribute!;

        // Resolved by the pipeline (AttributeFlowPipeline.EvaluateExport): an export expression into a Reference
        // attribute supplies the reference's text (for example a DN), staged as text exactly as it always has been;
        // every other type converts to that type.
        var resolution = evaluation.Resolution!;
        var result = evaluation.Source.Result;

        switch (resolution.Kind)
        {
            case ExpressionResultResolutionKind.NoValue:
                return [];

            case ExpressionResultResolutionKind.NotConvertible:
                // The value itself is source data, so it goes to the Run Profile Execution Item (AttributeFlowError.Value),
                // never to the log.
                Log.Warning("CreateAttributeValueChanges: Expression result cannot be stored in {Type} attribute {AttrName}; " +
                    "no Pending Export generated for this attribute.",
                    target.Type, target.Name);
                flowErrors?.Add(new AttributeFlowError
                {
                    Kind = AttributeFlowErrorKind.ExpressionResultNotConvertible,
                    TargetAttributeName = target.Name,
                    TargetAttributeType = target.Type,
                    Expression = source.Expression,
                    Value = resolution.UnconvertibleValue
                });
                return [];

            case ExpressionResultResolutionKind.SeveralValues:
                Log.Warning("CreateAttributeValueChanges: Expression returned {ValueCount} values for single-valued attribute {AttrName}; " +
                    "no Pending Export generated for this attribute.",
                    resolution.ValueCount, target.Name);
                flowErrors?.Add(new AttributeFlowError
                {
                    Kind = AttributeFlowErrorKind.ExpressionResultMultiValued,
                    TargetAttributeName = target.Name,
                    SourceAttributeName = resolution.SourceAttributeName,
                    Expression = source.Expression,
                    ValueCount = resolution.ValueCount
                });
                return [];
        }

        // A single value keeps the Update an expression has always staged; how a single value meets a multi-valued
        // target is the target-plurality rule (#2182). A list is never routed here by its length: an Update is compared
        // only with the first value the Connected System Object holds, so a list reduced to one value would leave a
        // stale value behind, or not, depending on the order the held values were read in.
        if (target.AttributePlurality != AttributePlurality.MultiValued || !ExpressionResultValues.IsCollection(result))
            return [NewExpressionChange(mapping, exportRule, PendingExportAttributeChangeType.Update, resolution.Values[0])];

        // A list for a multi-valued target. Whether this becomes one whole-set change is the target-plurality rule
        // (#2182); until then each value is an Add, and each held value the result no longer has is a Remove, which
        // every Connector already applies to a multi-valued attribute.
        var changes = resolution.Values
            .Select(value => NewExpressionChange(mapping, exportRule, PendingExportAttributeChangeType.Add, value))
            .ToList();

        if (heldValues != null)
        {
            var wantedKeys = resolution.Values.Select(ExpressionResultValues.Key).ToHashSet();
            changes.AddRange(heldValues
                .Select(held => (Held: held, Value: HeldTypedValue(held, target.Type)))
                .Where(held => held.Value != null && !wantedKeys.Contains(ExpressionResultValues.Key(held.Value)))
                .Select(held => NewExpressionChange(mapping, exportRule, PendingExportAttributeChangeType.Remove, held.Value!)));
        }

        return changes;
    }

    /// <summary>
    /// A Connected System Object attribute value's value in the field of <paramref name="type"/>, or null when it holds
    /// none there. Reference values are not compared with expression results, which supply them as text.
    /// </summary>
    private static object? HeldTypedValue(ConnectedSystemObjectAttributeValue held, AttributeDataType type) => type switch
    {
        AttributeDataType.Text => held.StringValue,
        AttributeDataType.Number => held.IntValue,
        AttributeDataType.LongNumber => held.LongValue,
        AttributeDataType.Decimal => held.DecimalValue,
        AttributeDataType.DateTime => held.DateTimeValue,
        AttributeDataType.Boolean => held.BoolValue,
        AttributeDataType.Guid => held.GuidValue,
        AttributeDataType.Binary => held.ByteValue,
        _ => null
    };

    /// <summary>
    /// A Pending Export change carrying one converted expression value in its typed field.
    /// </summary>
    private static PendingExportAttributeValueChange NewExpressionChange(
        SyncRuleMapping mapping,
        SyncRule exportRule,
        PendingExportAttributeChangeType changeType,
        object value)
    {
        var change = new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = mapping.TargetConnectedSystemAttribute!,
            AttributeId = mapping.TargetConnectedSystemAttribute!.Id,
            ChangeType = changeType,
            SyncRuleId = exportRule.Id,
            SyncRuleName = exportRule.Name
        };

        switch (value)
        {
            case string text: change.StringValue = text; break;
            case int number: change.IntValue = number; break;
            case long longNumber: change.LongValue = longNumber; break;
            case decimal decimalNumber: change.DecimalValue = decimalNumber; break;
            case DateTime date: change.DateTimeValue = date; break;
            case bool flag: change.BoolValue = flag; break;
            case Guid guid: change.GuidValue = guid; break;
            case byte[] bytes: change.ByteValue = bytes; break;
            default:
                // ExpressionResultConverter only produces the types above; anything else is a JIM fault.
                throw new InvalidOperationException(
                    $"An expression value of type '{value.GetType().Name}' has no field on a Pending Export change.");
        }

        return change;
    }

    /// <summary>
    /// What an export expression mapping's Expression source stages for a Metaverse Object whose values are
    /// <paramref name="mvAttributeDictionary"/>: its result, or null when it stages nothing (no value, or a mapping-level
    /// fault export staging reports for itself). Generated Value Forms (#2023) checks an export expression built from a
    /// generated value with it before the value is issued, so what is checked is what export staging then sends. Throws
    /// exactly as export staging does for an expression that fails.
    /// </summary>
    internal static object? EvaluateExportExpressionValue(
        SyncRuleMapping mapping,
        SyncRuleMappingSource source,
        Dictionary<string, object?> mvAttributeDictionary,
        IExpressionEvaluator expressionEvaluator)
    {
        var evaluation = EvaluateExportExpressionSource(mapping, source, mvAttributeDictionary, expressionEvaluator, flowErrors: null);
        return evaluation?.Outcome == ExpressionSourceOutcome.Result ? evaluation.Result : null;
    }

    /// <summary>
    /// Runs the first half of the evaluation pipeline (<see cref="AttributeFlowPipeline.EvaluateSource"/>, #2055) for one
    /// export mapping's Expression source against the Metaverse Object, then applies what export staging does with a
    /// stopped source (<see cref="ContinueAfterExportSource"/>). Used by a generated mapping's base expression (Unique
    /// Value Generation, #242, Phase 2 work package H) and by Generated Value Forms.
    /// </summary>
    /// <returns>Null when Missing Input Behaviour is FailMapping (its <see cref="AttributeFlowError"/> is already
    /// recorded); otherwise a result or no value.</returns>
    private static ExpressionSourceEvaluation? EvaluateExportExpressionSource(
        SyncRuleMapping mapping,
        SyncRuleMappingSource source,
        Dictionary<string, object?> mvAttributeDictionary,
        IExpressionEvaluator expressionEvaluator,
        List<AttributeFlowError>? flowErrors)
    {
        var evaluated = AttributeFlowPipeline.EvaluateSource(expressionEvaluator, source.Expression!, source.MissingInputBehaviour,
            connectedSystemAttributes: null, mvAttributeDictionary);

        return ContinueAfterExportSource(evaluated, mapping, source, mvAttributeDictionary, flowErrors) ? evaluated : null;
    }

    /// <summary>
    /// What export staging does with the first half of the pipeline's outcome (#1361 Missing Input Behaviour): an
    /// Expression whose input is absent evaluates cleanly and produces a structurally broken value that the Connected
    /// System is then asked to accept, so the administrator chooses what an absent input means. Only Metaverse inputs
    /// are considered, because export evaluation runs against the Metaverse Object alone. <c>FailObject</c> throws
    /// <see cref="SyncExpressionMissingInputException"/> for the worker to record; <c>FailMapping</c> records an
    /// <see cref="AttributeFlowError"/> and stages nothing. A thrown export Expression is surfaced as an errored object,
    /// never swallowed and never conflated with a deliberate null result: it is rethrown as
    /// <see cref="SyncExpressionEvaluationException"/> for the worker to record as an ExpressionEvaluationError RPEI.
    /// </summary>
    /// <returns>True to continue with the result or no value; false when the mapping stops here.</returns>
    private static bool ContinueAfterExportSource(
        ExpressionSourceEvaluation evaluated,
        SyncRuleMapping mapping,
        SyncRuleMappingSource source,
        Dictionary<string, object?> mvAttributeDictionary,
        List<AttributeFlowError>? flowErrors)
    {
        switch (evaluated.Outcome)
        {
            case ExpressionSourceOutcome.MissingInputFailedObject:
                throw new SyncExpressionMissingInputException(source.Expression,
                    mapping.TargetConnectedSystemAttribute?.Name, evaluated.MissingInputs);

            case ExpressionSourceOutcome.MissingInputFailedMapping:
                flowErrors?.Add(new AttributeFlowError
                {
                    Kind = AttributeFlowErrorKind.ExpressionMissingInput,
                    TargetAttributeName = mapping.TargetConnectedSystemAttribute!.Name,
                    Expression = source.Expression,
                    MissingInputs = evaluated.MissingInputs
                });
                return false;

            case ExpressionSourceOutcome.EvaluationFailed:
                throw BuildExportExpressionEvaluationException(mapping, source, evaluated.EvaluationException!);

            case ExpressionSourceOutcome.NoValue when evaluated.MissingInputs.Count == 0:
                // Null is expected when the referenced attribute doesn't exist on this MVO.
                Log.Debug("EvaluateExportExpressionSource: Expression '{Expression}' returned null. Available attributes: [{Attributes}]",
                    source.Expression, string.Join(", ", mvAttributeDictionary.Keys));
                return true;

            default:
                return true;
        }
    }

    /// <summary>
    /// Stages a generated export mapping's (Unique Value Generation, #242, Phase 2 work package H) pending
    /// change: evaluates the base expression exactly as <see cref="EvaluateExportExpressionSource"/> evaluates
    /// an ordinary one, then produces an unresolved, marked <see cref="PendingExportAttributeValueChange"/>
    /// (Update, single value, every value field left null) carrying a <see cref="PendingGeneratedExportValue"/>
    /// for the worker to resolve before the Pending Export is persisted. Returns null only when nothing is
    /// staged at all: the base expression's Missing Input Behaviour is <c>FailMapping</c> (its
    /// <see cref="AttributeFlowError"/> was already recorded) - the only outcome with nothing to resolve later,
    /// since even a missing input otherwise becomes a marked change carrying <c>BaseUnavailable</c> so the
    /// worker can still reassert a live assignment (FR 10).
    /// </summary>
    private static PendingExportAttributeValueChange? StageGeneratedExportMapping(
        SyncRuleMapping mapping,
        SyncRule exportRule,
        Dictionary<string, object?> mvAttributeDictionary,
        IExpressionEvaluator expressionEvaluator,
        List<AttributeFlowError>? flowErrors)
    {
        var source = mapping.Sources.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Expression));

        string? baseValue;
        bool baseUnavailable;

        if (source == null)
        {
            // No base expression: valid for Sequence and Random tokens (SyncRuleMappingGenerationValidator).
            baseValue = null;
            baseUnavailable = false;
        }
        else
        {
            var evaluation = EvaluateExportExpressionSource(mapping, source, mvAttributeDictionary, expressionEvaluator, flowErrors);

            if (evaluation == null)
            {
                // FailMapping already recorded its AttributeFlowError; nothing is staged for this attribute at
                // all, exactly as an ordinary mapping in the same state stages nothing.
                return null;
            }

            if (evaluation.Outcome == ExpressionSourceOutcome.NoValue)
            {
                // Missing inputs (ContributeNoValue, the default Missing Input Behaviour for a generated
                // mapping, FR 29) or a null result: wait. The worker resolves this as StickyOnly, which reasserts
                // whatever the Connected System Object already holds rather than clearing it (FR 10): a
                // committed generated value must never be recomputed or cleared by its inputs.
                baseValue = null;
                baseUnavailable = true;
            }
            else if (ExpressionResultValues.IsCollection(evaluation.Result))
            {
                // A generated value's base must be a single text value: a uniqueness token is appended to
                // exactly one candidate, so a collection of anything (#2179, not only a list of text) is a
                // misconfiguration, not data to select from. Nothing is staged for this attribute this pass.
                flowErrors?.Add(new AttributeFlowError
                {
                    Kind = AttributeFlowErrorKind.GeneratedBaseNotSingleValue,
                    TargetAttributeName = mapping.TargetConnectedSystemAttribute!.Name,
                    Expression = source.Expression
                });
                return null;
            }
            else if (!ExpressionText.TryFormat(evaluation.Result, out baseValue))
            {
                // A single value in JIM's text form (#2153, N1); a Binary value has none, so it cannot be a base.
                flowErrors?.Add(new AttributeFlowError
                {
                    Kind = AttributeFlowErrorKind.ExpressionResultNotConvertible,
                    TargetAttributeName = mapping.TargetConnectedSystemAttribute!.Name,
                    TargetAttributeType = AttributeDataType.Text,
                    Expression = source.Expression,
                    Value = ExpressionText.Describe(evaluation.Result!)
                });
                return null;
            }
            else
            {
                baseUnavailable = baseValue == null;
            }
        }

        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = mapping.TargetConnectedSystemAttribute!,
            AttributeId = mapping.TargetConnectedSystemAttribute!.Id,
            ChangeType = PendingExportAttributeChangeType.Update,
            SyncRuleId = exportRule.Id,
            SyncRuleName = exportRule.Name,
            PendingGeneration = new PendingGeneratedExportValue
            {
                Mapping = mapping,
                BaseValue = baseValue,
                BaseUnavailable = baseUnavailable
            }
        };
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

            case PendingExportAttributeChangeType.Update when IsWholeSetUpdate(pendingChange):
                // An Update against a multi-valued attribute sets its whole value set (#2182), so it is a no-net-change
                // only when the target holds exactly that value, or nothing for a clear. Comparing with the first value
                // alone counted [a, stray] as current and [stray, a] as not, so whether a stray value was ever
                // corrected depended on the order the target returned its values in.
                if (IsPendingChangeEmpty(pendingChange))
                    return valuesList.Count == 0;

                return valuesList.Count == 1 && ValuesMatch(pendingChange, valuesList[0]);

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
        // String comparison: exact for ordinary attributes; an identifier ignores case unless declared case-exact,
        // because the directory reports a name in the case it stored (#2195, #2051)
        if (pendingChange.StringValue != null || existingValue.StringValue != null)
        {
            return string.Equals(pendingChange.StringValue, existingValue.StringValue, AttributeValueComparison.For(pendingChange.Attribute));
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

        // Boolean comparison
        if (pendingChange.BoolValue.HasValue || existingValue.BoolValue.HasValue)
        {
            return pendingChange.BoolValue == existingValue.BoolValue;
        }

        // Guid comparison
        if (pendingChange.GuidValue.HasValue || existingValue.GuidValue.HasValue)
        {
            return pendingChange.GuidValue == existingValue.GuidValue;
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
            return string.Equals(pendingChange.UnresolvedReferenceValue, existingValue.UnresolvedReferenceValue, AttributeValueComparison.For(pendingChange.Attribute));
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
        => BuildAttributeDictionary(mvo, mvo.AttributeValues);

    /// <summary>
    /// The single implementation of a Metaverse expression dictionary, over whichever set of the object's values the
    /// caller supplies: its persisted values for export evaluation (<see cref="BuildAttributeDictionary(MetaverseObject)"/>),
    /// or its effective values as of this pass for a Metaverse-Derived Attribute Flow
    /// (<see cref="BuildEffectiveAttributeDictionary"/>, #1750). Sharing it is what guarantees an export expression
    /// and a derived flow reading the same attribute see the same value: keys case-insensitive, values typed by the
    /// attribute's data type, asserted-null markers absent, and every value of a multi-valued attribute read as
    /// <see cref="ExpressionAttributeDictionary"/> reads them (#2053).
    /// </summary>
    private static Dictionary<string, object?> BuildAttributeDictionary(MetaverseObject mvo, IEnumerable<MetaverseObjectAttributeValue> values)
    {
        var attributes = new ExpressionAttributeDictionary();

        if (mvo.Type == null)
        {
            Log.Warning("BuildAttributeDictionary: MVO {MvoId} has null Type, cannot build attribute dictionary", mvo.Id);
            return attributes.Build();
        }

        // Exclude asserted-null markers (#91): they carry no value, so the expression context must treat the
        // attribute as absent (mv["x"] resolves to null) rather than seeing a phantom value.
        foreach (var attributeValue in values.Where(av => !av.NullValue))
        {
            if (attributeValue.Attribute == null)
            {
                // Log warning for diagnostic purposes - this indicates a missing Include or EF tracking issue
                Log.Warning("BuildAttributeDictionary: MVO {MvoId} has attribute value with AttributeId={AttrId} but Attribute navigation property is null. " +
                    "This will cause expression-based mappings to fail for this attribute.",
                    mvo.Id, attributeValue.AttributeId);
                continue;
            }

            attributes.Add(attributeValue.Attribute.Name, ToExpressionValue(attributeValue));
        }

        return attributes.Build();
    }

    /// <summary>
    /// The value an expression sees for one Metaverse Object attribute value, typed by the attribute's data type.
    /// </summary>
    internal static object? ToExpressionValue(MetaverseObjectAttributeValue attributeValue) => attributeValue.Attribute.Type switch
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
    /// The changes, with every single-valued attribute named more than once among them folded into one Update (#2182).
    /// The last change that is not a removal decides it, carrying its value (or, for an all-null Update, the clear) and
    /// becoming the Update itself; a removal is implied by whatever replaces the value, so it never decides while
    /// anything else is present. An attribute with only removals among them is cleared: a new all-null Update stands for
    /// them. Order is kept, the fold taking the place of the attribute's first change.
    /// </summary>
    /// <remarks>
    /// Export staging gives a single-valued attribute exactly one change, so in the ordinary case there is nothing to
    /// fold and <paramref name="changes"/> itself is returned, allocation-free. The deciding change is converted in place
    /// rather than copied, so anything resolved against it by identity afterwards (a generated value's marker, Unique
    /// Value Generation) still finds it.
    /// </remarks>
    /// <param name="changes">The changes about to be merged into a Pending Export.</param>
    internal static List<PendingExportAttributeValueChange> WithOneChangePerSingleValuedAttribute(List<PendingExportAttributeValueChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var foldedAttributeIds = changes
            .Where(change => change.Attribute?.AttributePlurality == AttributePlurality.SingleValued)
            .GroupBy(change => change.AttributeId)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet();

        if (foldedAttributeIds.Count == 0)
            return changes;

        var folded = new List<PendingExportAttributeValueChange>(changes.Count);
        var placed = new HashSet<int>();
        foreach (var change in changes)
        {
            if (!foldedAttributeIds.Contains(change.AttributeId))
                folded.Add(change);
            else if (placed.Add(change.AttributeId))
                folded.Add(FoldSingleValuedAttributeChanges([.. changes.Where(c => c.AttributeId == change.AttributeId)]));
        }

        return folded;
    }

    /// <summary>
    /// The one Update a single-valued attribute's several changes come to: see
    /// <see cref="WithOneChangePerSingleValuedAttribute"/>.
    /// </summary>
    private static PendingExportAttributeValueChange FoldSingleValuedAttributeChanges(List<PendingExportAttributeValueChange> attributeChanges)
    {
        var deciding = attributeChanges.LastOrDefault(change =>
            change.ChangeType is PendingExportAttributeChangeType.Add or PendingExportAttributeChangeType.Update);

        if (deciding != null)
        {
            deciding.ChangeType = PendingExportAttributeChangeType.Update;
            return deciding;
        }

        var last = attributeChanges[^1];
        return new PendingExportAttributeValueChange
        {
            Id = Guid.NewGuid(),
            Attribute = last.Attribute,
            AttributeId = last.AttributeId,
            ChangeType = PendingExportAttributeChangeType.Update,
            SyncRuleId = last.SyncRuleId,
            SyncRuleName = last.SyncRuleName
        };
    }

    /// <summary>
    /// Selects the changes on an existing (stale, typically drift-staged) Pending Export that survive a merge
    /// with a newly evaluated set of export changes. Export evaluation always wins on a collision, because it
    /// derives from the latest Metaverse Object state.
    /// </summary>
    /// <param name="incomingChanges">The newly evaluated export changes, which take precedence.</param>
    /// <param name="existingChanges">The changes already staged on the Pending Export being merged into.</param>
    /// <param name="alreadyCurrentChanges">The changes the same evaluation skipped because the target already holds
    /// what they would set (no-net-change detection). They supersede a staged change exactly as an incoming change
    /// does, except one already sent and awaiting confirmation (but not one held until an Initial Password is
    /// delivered; see <see cref="IsKeptAgainstAlreadyCurrent"/>): the Metaverse and the target already agree, so a
    /// queued change for the same attribute (or value) is stale. See <see cref="WithdrawChangesAlreadyCurrent"/>.</param>
    /// <returns>The existing changes that are not superseded, in their original order.</returns>
    internal static List<PendingExportAttributeValueChange> SelectSurvivingDriftChanges(
        IReadOnlyCollection<PendingExportAttributeValueChange> incomingChanges,
        IEnumerable<PendingExportAttributeValueChange> existingChanges,
        IReadOnlyCollection<PendingExportAttributeValueChange>? alreadyCurrentChanges = null)
    {
        ArgumentNullException.ThrowIfNull(incomingChanges);
        ArgumentNullException.ThrowIfNull(existingChanges);

        var incomingKeys = incomingChanges.Select(GetAttributeChangeMergeKey).ToHashSet();
        var wholeAttributeReplacementIds = GetWholeAttributeReplacementAttributeIds(incomingChanges);

        var survivors = existingChanges
            .Where(existing => !incomingKeys.Contains(GetAttributeChangeMergeKey(existing)))
            .Where(existing => !wholeAttributeReplacementIds.Contains(existing.AttributeId));

        if (alreadyCurrentChanges is { Count: > 0 })
        {
            var currentKeys = alreadyCurrentChanges.Select(GetAttributeChangeMergeKey).ToHashSet();
            var wholeAttributeCurrentIds = GetWholeAttributeReplacementAttributeIds(alreadyCurrentChanges);
            var currentAssertions = alreadyCurrentChanges.Select(HeldChangeAssertion).ToHashSet();
            survivors = survivors.Where(existing =>
                IsKeptAgainstAlreadyCurrent(existing, currentAssertions) ||
                (!currentKeys.Contains(GetAttributeChangeMergeKey(existing)) && !wholeAttributeCurrentIds.Contains(existing.AttributeId)));
        }

        return survivors.ToList();
    }

    /// <summary>
    /// Whether a queued change stands even though the evaluation found its attribute (or value) already current on the
    /// target, for <see cref="SelectSurvivingDriftChanges"/>.
    /// <list type="bullet">
    /// <item>A change already sent and awaiting its confirming import stands: it is what the target has just been told
    /// to hold (and, applied optimistically, what JIM already records it as holding), so it is the confirmation of that
    /// change, not a stale instruction.</item>
    /// <item>Except one held until an Initial Password is delivered (<see cref="IsAwaitingInitialPassword"/>, #2039).
    /// The target was given an interim value instead of the one it asserts, so "already current" says nothing about
    /// whether it was confirmed; when the Metaverse now wants what the target already holds, the held change no longer
    /// reflects the Metaverse and is withdrawn like any queued change. The leaver during the hold is the case that
    /// matters: the held change asserts the enabled value, the person leaves, the rule now yields the disabled value
    /// the account was created with, and the held change, kept, would be released and enable the leaver's account.
    /// It stands only when the evaluation's already-current answer restates exactly what it asserts, the same
    /// restatement <see cref="WithoutChangesRestatingAHeldChange"/> drops from the incoming side: then it does reflect
    /// the Metaverse, and withdrawing it on a record of the target its confirming import has yet to judge could leave
    /// the account disabled with nothing to enable it.</item>
    /// </list>
    /// </summary>
    private static bool IsKeptAgainstAlreadyCurrent(
        PendingExportAttributeValueChange existing,
        HashSet<(string MergeKey, string ValueKey, PendingExportAttributeChangeType ChangeType)> currentAssertions)
    {
        if (IsAwaitingInitialPassword(existing))
            return currentAssertions.Contains(HeldChangeAssertion(existing));

        return existing.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation;
    }

    /// <summary>
    /// The newly evaluated changes, less any that restates exactly what a held change on the existing Pending Export
    /// already asserts (#2039): a change written with an interim value until an Initial Password is delivered
    /// (<see cref="IsAwaitingInitialPassword"/>). "Exactly" means the same merge key, value and change type. Such a
    /// change is no change at all, but merged in it would supersede the held one and go out as a Pending write of the
    /// very value the target refuses until the password arrives. Expression mappings are re-evaluated on any Metaverse
    /// Object change, so this restatement is the ordinary case for every change to the person between the account's
    /// creation and its password's delivery. An incoming change with a different value is a genuine change and still
    /// supersedes the held one.
    /// </summary>
    /// <param name="incomingChanges">The newly evaluated export changes.</param>
    /// <param name="existingChanges">The changes on the Pending Export they are about to be merged into.</param>
    /// <returns><paramref name="incomingChanges"/> itself when nothing is held (the ordinary case, allocation-free),
    /// otherwise a new list without the restatements, in their original order.</returns>
    internal static List<PendingExportAttributeValueChange> WithoutChangesRestatingAHeldChange(
        List<PendingExportAttributeValueChange> incomingChanges,
        IEnumerable<PendingExportAttributeValueChange> existingChanges)
    {
        ArgumentNullException.ThrowIfNull(incomingChanges);
        ArgumentNullException.ThrowIfNull(existingChanges);

        var heldAssertions = existingChanges
            .Where(IsAwaitingInitialPassword)
            .Select(HeldChangeAssertion)
            .ToHashSet();
        if (heldAssertions.Count == 0)
            return incomingChanges;

        return incomingChanges.Where(change => !heldAssertions.Contains(HeldChangeAssertion(change))).ToList();
    }

    /// <summary>
    /// What a change asserts, for <see cref="WithoutChangesRestatingAHeldChange"/>: its merge key (the attribute, plus
    /// the value for a multi-valued attribute), its value, and whether it adds, removes or replaces.
    /// </summary>
    private static (string MergeKey, string ValueKey, PendingExportAttributeChangeType ChangeType) HeldChangeAssertion(
        PendingExportAttributeValueChange change) =>
        (GetAttributeChangeMergeKey(change), GetAttributeChangeKey(change), change.ChangeType);

    /// <summary>
    /// The attribute ids among a set of changes whose change type sets the attribute's ENTIRE value set
    /// rather than one value within it (#1199). Update and RemoveAll both export as a replace, so every other
    /// staged change for the same attribute is superseded, whatever its value or change type.
    /// </summary>
    /// <remarks>
    /// The merge key alone is not enough to catch this. It keys multi-valued attributes by value, which is
    /// right for genuine per-value adds and removals, but a single-valued Metaverse attribute flowing to a
    /// multi-valued Connected System attribute is staged as an Update (#2182), which replaces the whole set
    /// while its key still carries a value. A stale per-value Remove for the same attribute would then survive
    /// the merge, and the connector would emit the replace followed by a delete of a value the replace has
    /// already removed; LDAP rejects that modify atomically, so the export would never apply.
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

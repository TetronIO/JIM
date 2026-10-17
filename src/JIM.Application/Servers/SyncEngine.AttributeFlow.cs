// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
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
    /// <param name="metaverseAttributes">The Metaverse Object's effective values, supplied only by the derived pass
    /// of Metaverse-Derived Attribute Flows (#1750) so an expression's <c>mv["..."]</c> inputs resolve; null for the
    /// ordinary inbound pass, whose expressions read the Connected System Object alone.</param>
    internal static void ProcessMapping(
        ConnectedSystemObject cso,
        SyncRuleMapping syncRuleMapping,
        IReadOnlyList<ConnectedSystemObjectType> objectTypes,
        IExpressionEvaluator? expressionEvaluator = null,
        bool skipReferenceAttributes = false,
        bool onlyReferenceAttributes = false,
        bool isFinalReferencePass = false,
        int? contributingSystemId = null,
        List<AttributeFlowError>? errors = null,
        int mvoObjectTypeId = 0,
        AttributePriorityContext? priorityContext = null,
        IDictionary<string, object?>? metaverseAttributes = null)
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

        // A generated mapping (Unique Value Generation, #242) never targets a Reference attribute
        // (SyncRuleMappingGenerationValidator), so it has no part to play in the deferred reference-only pass.
        // Without this guard, the reference pass would call ProcessGeneratedMapping a second time for the same
        // attribute after the worker has already resolved and cleared the first pass's pending generation
        // (Phase 2 work package G): the second call would record a NEW pending generation that nothing then
        // resolves, tripping the worker's "leftover pending generation" integrity guard before the next page
        // persists.
        if (onlyReferenceAttributes && syncRuleMapping.Generation != null)
            return;

        var csoType = objectTypes.Single(t => t.Id == cso.TypeId);
        var mvo = cso.MetaverseObject;

        // Provenance: stamp the winning Synchronisation Rule alongside the contributing system on every value this mapping
        // writes (#91), so the Metaverse Object value records which rule contributed it, not just which system.
        var contributingSyncRuleId = syncRuleMapping.SyncRuleId;

        List<PendingGeneratedValue>? supersededGenerations = null;

        // Attribute priority gate (#91): when an attribute has more than one contributing rule, a contribution that
        // loses priority resolution to the rule currently owning the value (the incumbent) must never reach the
        // Metaverse Object. Single-contributor attributes, and runs without a priority context, use the unchanged
        // write path: the gate adds one cached lookup and never engages for the common single-contributor case.
        if (priorityContext != null &&
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

            // This contribution has won the gate, so under winner-takes-all-values a losing rule contributes
            // nothing. Discard any values a losing rule staged for this attribute earlier in this same pass
            // (#1199). The Process*Attribute writers below diff against the Metaverse Object's PERSISTED values,
            // which cannot see a pending addition, so without this a second contributing rule appends its value
            // beside the loser's instead of replacing it, leaving a single-valued attribute holding two values.
            // Reachable only when two contributing rules are evaluated in the same pass, which in turn requires
            // two import rules on the SAME Connected System contributing to one attribute: the fine-grained
            // authority topology. Contributions from different systems arrive in different runs, by which point
            // the incumbent's value is persisted and the ordinary diff removes it.
            mvo.PendingAttributeValueAdditions.RemoveAll(av =>
                av.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id &&
                av.ContributedBySyncRuleId != contributingSyncRuleId);

            // A later winner also supersedes a pending generation request (Unique Value Generation, #242, plan
            // decision 6), but only if it actually contributes. Winning the gate is not the same as contributing: a
            // higher-priority rule whose source is silent abstains, and FR 6 says generation is exactly what fills
            // that silence. So the superseded requests are held here and restored after the writers below have run
            // if this rule staged nothing for the attribute (see the end of this method). A value, or a "Null is a
            // value" marker, is a contribution and keeps them removed.
            supersededGenerations = mvo.PendingGeneratedValues
                .Where(p => p.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id && p.ContributedBySyncRuleId != contributingSyncRuleId)
                .ToList();
            if (supersededGenerations.Count > 0)
                mvo.PendingGeneratedValues.RemoveAll(supersededGenerations.Contains);
        }

        // A generated mapping (Unique Value Generation, #242) produces no value here: the synchronous engine
        // performs no I/O and cannot itself generate one. Having reached this point it is the attribute's winning
        // contributor (the priority gate above, or being the sole contributor), so it records a pending generation
        // request in place of a value and takes no further part in this call; the per-source loop below is for
        // ordinary attribute and expression mappings only.
        if (syncRuleMapping.Generation != null)
        {
            ProcessGeneratedMapping(cso, mvo, syncRuleMapping, csoType, expressionEvaluator, contributingSystemId, contributingSyncRuleId, errors, metaverseAttributes);
            return;
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
                        if (onlyReferenceAttributes && csotAttribute.Type != AttributeDataType.Reference)
                            continue;

                        // Every type except Reference flows only the rows that hold a value of its type (#2153): a row
                        // held under another type (#2128) or holding nothing is neither a value to flow nor a value to
                        // count. The usual case, every row holding the type, reuses the list as it is.
                        List<ConnectedSystemObjectAttributeValue>? typedValues = null;
                        if (csotAttribute.Type != AttributeDataType.Reference)
                        {
                            typedValues = RowsHoldingValueOfType(csoAttributeValues, csotAttribute.Type);
                            if (typedValues.Count == 0)
                            {
                                // None of the type, but a value held under another one (#2184): a schema refresh
                                // redefined the attribute and no import has read it since. A value JIM cannot read as
                                // the attribute's type is not the absence of a value (#2066), so the Metaverse Object
                                // is left exactly as it is: no removal, no addition, no asserted-null marker and no
                                // provenance takeover. The next import that reads the attribute replaces the row.
                                var heldUnderAnotherType = csoAttributeValues.Find(av => av.IsHeldUnderAnotherDataType(csotAttribute.Type));
                                if (heldUnderAnotherType != null)
                                {
                                    Log.Warning(
                                        "ProcessMapping: Connected System Object {CsoId} holds attribute '{SourceAttr}' only as " +
                                        "{HeldType}, not as its {AttrType} type, so no value flowed to '{TargetAttr}' and the " +
                                        "Metaverse Object's value is left as it is until an import reads the attribute again.",
                                        cso.Id, LogSanitiser.Sanitise(csotAttribute.Name), heldUnderAnotherType.HeldDataType(),
                                        csotAttribute.Type, LogSanitiser.Sanitise(syncRuleMapping.TargetMetaverseAttribute.Name));
                                    continue;
                                }

                                // Rows that hold nothing at all: the ConnectedNoValue state, resolved by "Null is a
                                // value" and priority (#91) exactly as an attribute with no rows is.
                                ApplyNoValueOutcome(mvo, syncRuleMapping, contributingSystemId, contributingSyncRuleId, mvoObjectTypeId, priorityContext);
                                continue;
                            }
                        }

                        // For text, apply the mapping's inbound value processing (#843) up front: the processed,
                        // de-duplicated set is both what flows and what the MVA->SVA guard below counts, so two
                        // whitespace/case variants that collapse to the same value count as one value.
                        List<string>? effectiveTextValues = null;
                        if (csotAttribute.Type == AttributeDataType.Text)
                            effectiveTextValues = ProcessInboundTextValues(typedValues!.Select(av => av.StringValue), syncRuleMapping);

                        // MVA -> SVA guard (#435): flowing more than one value to a single-valued target attribute
                        // is an error. A single-valued attribute can hold only one value, and JIM will not pick one
                        // arbitrarily; the attribute is skipped and an error is recorded (surfaced as an RPEI by the
                        // worker), while the object's other attributes still synchronise. Reference attributes are
                        // inherently multi-valued and resolved in a later pass, so they are out of scope here. Values
                        // are counted as a set (#2153): two rows holding the same value, such as one date in two
                        // kinds, are one value.
                        if (syncRuleMapping.TargetMetaverseAttribute.AttributePlurality == AttributePlurality.SingleValued &&
                            csotAttribute.Type != AttributeDataType.Reference)
                        {
                            var effectiveValueCount = effectiveTextValues?.Count ?? CountDistinctValues(typedValues!, csotAttribute.Type);
                            if (effectiveValueCount > 1)
                            {
                                Log.Error(
                                    "ProcessMapping: Multi-valued source attribute '{SourceAttr}' has {ValueCount} values but target " +
                                    "attribute '{TargetAttr}' is single-valued. No value flowed for this attribute. CSO {CsoId}",
                                    csotAttribute.Name, effectiveValueCount, syncRuleMapping.TargetMetaverseAttribute.Name, cso.Id);

                                errors?.Add(new AttributeFlowError
                                {
                                    Kind = AttributeFlowErrorKind.MultiValuedToSingleValued,
                                    SourceAttributeName = csotAttribute.Name,
                                    TargetAttributeName = syncRuleMapping.TargetMetaverseAttribute.Name,
                                    ValueCount = effectiveValueCount
                                });

                                continue;
                            }
                        }

                        switch (csotAttribute.Type)
                        {
                            case AttributeDataType.Text:
                            {
                                // effectiveTextValues was computed above; an empty processed set is the
                                // ConnectedNoValue state, resolved by priority (#91).
                                if (effectiveTextValues!.Count == 0)
                                    ApplyNoValueOutcome(mvo, syncRuleMapping, contributingSystemId, contributingSyncRuleId, mvoObjectTypeId, priorityContext);
                                else
                                    ProcessTextAttribute(mvo, syncRuleMapping, effectiveTextValues, contributingSystemId, contributingSyncRuleId);
                                break;
                            }
                            case AttributeDataType.Number:
                                ProcessNumberAttribute(mvo, syncRuleMapping, typedValues!, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.LongNumber:
                                ProcessLongNumberAttribute(mvo, syncRuleMapping, typedValues!, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.Decimal:
                                ProcessDecimalAttribute(mvo, syncRuleMapping, typedValues!, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.DateTime:
                                ProcessDateTimeAttribute(mvo, syncRuleMapping, typedValues!, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.Binary:
                                ProcessBinaryAttribute(mvo, syncRuleMapping, typedValues!, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.Reference:
                                if (!skipReferenceAttributes)
                                    ProcessReferenceAttribute(mvo, syncRuleMapping, source, cso, csoAttributeValues, isFinalReferencePass, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.Guid:
                                ProcessGuidAttribute(mvo, syncRuleMapping, typedValues!, contributingSystemId, contributingSyncRuleId);
                                break;
                            case AttributeDataType.Boolean:
                                ProcessBooleanAttribute(mvo, syncRuleMapping, typedValues!, contributingSystemId, contributingSyncRuleId);
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
                ProcessExpressionMapping(cso, mvo, syncRuleMapping, source, csoType, expressionEvaluator, contributingSystemId, contributingSyncRuleId, mvoObjectTypeId, priorityContext, errors, metaverseAttributes);
            }
            else if (source.MetaverseAttribute != null)
                throw new InvalidDataException("SyncRuleMappingSource.MetaverseAttribute being populated is not supported for synchronisation operations. " +
                                               "This operation is focused on import flow, so Connected System to Metaverse Object.");
            else
                throw new InvalidDataException("Expected ConnectedSystemAttribute or Expression to be populated in a SyncRuleMappingSource object.");
        }

        // The other half of the superseded-generation hand-off above: this rule won the gate but staged nothing for
        // the attribute (it abstained), so the lower-priority generation it displaced still stands (FR 6).
        if (supersededGenerations is { Count: > 0 } &&
            !mvo.PendingAttributeValueAdditions.Any(av =>
                av.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id && av.ContributedBySyncRuleId == contributingSyncRuleId))
        {
            mvo.PendingGeneratedValues.AddRange(supersededGenerations);
        }
    }

    /// <summary>
    /// The Metaverse Object's effective current values for an attribute: persisted values not already pending
    /// removal in this run. Attribute flow must diff contributions against this set rather than the raw value list;
    /// diffing against raw values treats a value that is about to be removed (e.g. recalled from a departing
    /// contributor, #91) as still present, so an identical surviving contribution would neither re-add the value nor
    /// take over its provenance, and the value would be silently cleared when the pending removals are applied.
    /// </summary>
    internal static IEnumerable<MetaverseObjectAttributeValue> GetEffectiveAttributeValues(MetaverseObject mvo, int attributeId)
        => mvo.AttributeValues.Where(av => av.AttributeId == attributeId && !mvo.PendingAttributeValueRemovals.Contains(av));

    /// <summary>
    /// Takes over provenance on the values a winning contribution left in place (#1292). The typed writers below
    /// diff by value, so a contribution that supplies the value the Metaverse Object already holds stages neither an
    /// addition nor a removal, and the surviving row keeps whichever rule and system stamped it last. That stamp is
    /// not cosmetic: it is what <see cref="FindEffectiveIncumbentSyncRuleId"/> reads, so leaving it stale hands the
    /// priority gate the wrong incumbent. Two consequences, both reachable through ordinary administration:
    /// <list type="bullet">
    /// <item>Deleting a contributing Synchronisation Rule nulls the stamp (the FK is <c>ON DELETE SET NULL</c>). A
    /// null incumbent opens the gate to every contributor, and the surviving contributor's identical value would
    /// never repair it.</item>
    /// <item>A higher-priority rule that contributes the same value as the incumbent would leave the lower-priority
    /// rule recorded as owner. The gate always lets a rule overwrite its own value, so the loser could then change
    /// the attribute out from under the winner.</item>
    /// </list>
    /// Call this from each writer AFTER its removals are staged: <see cref="GetEffectiveAttributeValues"/> then
    /// returns exactly the rows this contribution matched, and the call is a no-op when the value was replaced.
    /// A contribution with no persisted Synchronisation Rule id takes nothing over, so an unpersisted rule (tests,
    /// preview evaluation) cannot erase a real stamp. Only rows whose provenance actually differs are touched, so
    /// the persistence layer writes nothing for the overwhelmingly common unchanged case.
    /// </summary>
    private static void TakeOverProvenance(
        MetaverseObject mvo,
        int attributeId,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        if (!contributingSyncRuleId.HasValue)
            return;

        foreach (var surviving in GetEffectiveAttributeValues(mvo, attributeId)
            .Where(av => av.ContributedBySyncRuleId != contributingSyncRuleId || av.ContributedBySystemId != contributingSystemId))
        {
            Log.Verbose("TakeOverProvenance: Synchronisation Rule {RuleId} takes over attribute {AttributeId} on MVO {MvoId} " +
                "from Synchronisation Rule {PreviousRuleId}.",
                contributingSyncRuleId, attributeId, mvo.Id, surviving.ContributedBySyncRuleId);

            surviving.ContributedBySyncRuleId = contributingSyncRuleId;
            surviving.ContributedBySystemId = contributingSystemId;
        }
    }

    /// <summary>
    /// Identifies the Synchronisation Rule that currently owns a Metaverse Object attribute's value (the incumbent,
    /// for the attribute priority gate, #91). Prefers a pending generation request recorded earlier in this run by
    /// a generated mapping (Unique Value Generation, #242, plan decision 6: the placeholder), then a value written
    /// earlier in this run by another mapping (a pending addition), then the current persisted row value, ignoring
    /// values already pending removal. Under winner-takes-all-values any value, or request, for the attribute
    /// identifies the owning rule. Returns null when nothing owns the attribute or the value is internally managed
    /// (no contributing rule stamped).
    /// </summary>
    internal static int? FindEffectiveIncumbentSyncRuleId(MetaverseObject mvo, int attributeId)
    {
        var pendingGeneration = mvo.PendingGeneratedValues.LastOrDefault(p => p.AttributeId == attributeId);
        if (pendingGeneration != null)
            return pendingGeneration.ContributedBySyncRuleId;

        var pendingOwner = mvo.PendingAttributeValueAdditions.LastOrDefault(av => av.AttributeId == attributeId);
        if (pendingOwner != null)
            return pendingOwner.ContributedBySyncRuleId;

        var current = GetEffectiveAttributeValues(mvo, attributeId).FirstOrDefault();
        return current?.ContributedBySyncRuleId;
    }

    /// <summary>
    /// Recalls Metaverse Object attribute values whose contributing Attribute Flow mapping has been DELETED
    /// (#1533). A value's provenance (<c>ContributedBySystemId</c> + <c>ContributedBySyncRuleId</c>) names the
    /// mapping's rule; when the priority contributor cache holds neither a live import mapping from that rule
    /// targeting the attribute nor a dormant one, the mapping row is gone, nothing any longer asserts the value,
    /// and it is staged for removal exactly like an in-place withdrawal. The caller's withdrawal re-election pass
    /// then re-elects the next surviving contributor, or the attribute is genuinely cleared and reported as a
    /// NoContributor outcome.
    /// <para>
    /// A DISABLED mapping, or a mapping on a disabled Synchronisation Rule, is deliberately the opposite case
    /// (#1537): the flow is dormant, not gone. The administrator has paused it and may re-enable it, so its
    /// contributed values are retained; a surviving contributor still takes the attribute over (dormant flows are
    /// absent from the contributor cache proper), but with no survivor the value stays.
    /// </para>
    /// <para>
    /// Deliberately narrow, because a recall is a destructive act:
    /// <list type="bullet">
    /// <item>Only values stamped with THIS Connected System's id are considered; another system's stale value is
    /// recalled by that system's own run.</item>
    /// <item>Values without a Synchronisation Rule stamp are never touched: internally managed data, pre-provenance
    /// values, and a deleted rule's <c>ON DELETE SET NULL</c> all look identical, and none may be cleared on the
    /// strength of a cache miss.</item>
    /// <item>Call AFTER the CSO's in-scope import rules have flowed: a live mapping that re-flowed the attribute
    /// this pass has already staged its own diff, and one that supplied the identical value has taken the
    /// provenance over (<see cref="TakeOverProvenance"/>), so its rows resolve as owned and are left alone.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="cso">The joined Connected System Object being re-evaluated.</param>
    /// <param name="priorityContext">The per-run attribute priority contributor cache (#91).</param>
    /// <returns>The values staged for removal, empty if none.</returns>
    public List<MetaverseObjectAttributeValue> RecallOrphanedContributions(
        ConnectedSystemObject cso,
        AttributePriorityContext priorityContext)
    {
        ArgumentNullException.ThrowIfNull(cso);
        ArgumentNullException.ThrowIfNull(priorityContext);

        var mvo = cso.MetaverseObject;
        if (mvo == null)
            return [];

        if (mvo.Type == null)
        {
            // Without the Metaverse Object Type the contributor cache cannot be keyed, so no liveness claim can
            // be made; recalling on unknown liveness would risk clearing legitimate values. Not an error: some
            // callers legitimately process partially hydrated objects.
            Log.Verbose("RecallOrphanedContributions: MVO {MvoId} has no Type loaded; skipping mapping-liveness recall for CSO {CsoId}.",
                mvo.Id, cso.Id);
            return [];
        }

        var objectTypeId = mvo.Type.Id;
        var orphaned = mvo.AttributeValues
            .Where(av => av.ContributedBySystemId == cso.ConnectedSystemId &&
                         av.ContributedBySyncRuleId is int contributingRuleId &&
                         !mvo.PendingAttributeValueRemovals.Contains(av) &&
                         priorityContext.GetContributor(objectTypeId, av.AttributeId, contributingRuleId) == null &&
                         !priorityContext.HasDormantContributor(objectTypeId, av.AttributeId, contributingRuleId))
            .ToList();

        if (orphaned.Count == 0)
            return orphaned;

        mvo.PendingAttributeValueRemovals.AddRange(orphaned);
        Log.Information("RecallOrphanedContributions: recalled {Count} attribute value(s) on MVO {MvoId} contributed by " +
            "Connected System {SystemId} whose Attribute Flow mapping no longer exists. Attribute ids: {AttributeIds}.",
            orphaned.Count, mvo.Id, cso.ConnectedSystemId, string.Join(", ", orphaned.Select(av => av.AttributeId).Distinct()));

        return orphaned;
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
    /// Runs the first half of the evaluation pipeline (<see cref="AttributeFlowPipeline.EvaluateSource"/>, #2055) for an
    /// expression source against the Connected System Object and, for a Metaverse-Derived Attribute Flow (#1750), the
    /// Metaverse Object's effective values, then applies what synchronisation does with a stopped source. Used by
    /// <see cref="ProcessGeneratedMapping"/>'s base expression (Unique Value Generation, #242, plan decision 6);
    /// <see cref="ProcessExpressionMapping"/> runs the whole pipeline through <see cref="AttributeFlowPipeline.EvaluateInbound"/>
    /// and applies the same handling.
    /// </summary>
    /// <param name="metaverseAttributes">The Metaverse Object's effective values
    /// (<see cref="BuildEffectiveAttributeDictionary"/>), supplied only by the derived pass; null for the ordinary
    /// inbound pass.</param>
    /// <returns>Null when nothing further should happen: no <see cref="IExpressionEvaluator"/> was supplied, or Missing
    /// Input Behaviour is FailMapping (its <see cref="AttributeFlowError"/> is already recorded). Otherwise a result or
    /// no value, which each caller interprets.</returns>
    private static ExpressionSourceEvaluation? EvaluateExpressionSource(
        ConnectedSystemObject cso,
        SyncRuleMapping syncRuleMapping,
        SyncRuleMappingSource source,
        ConnectedSystemObjectType csoType,
        IExpressionEvaluator? expressionEvaluator,
        List<AttributeFlowError>? errors,
        IDictionary<string, object?>? metaverseAttributes = null)
    {
        if (!HasExpressionEvaluator(expressionEvaluator, source))
            return null;

        var csAttributeDictionary = BuildCsoAttributeDictionary(cso, csoType);
        var evaluated = AttributeFlowPipeline.EvaluateSource(expressionEvaluator!, source.Expression!, source.MissingInputBehaviour,
            csAttributeDictionary, metaverseAttributes);

        return ContinueAfterSource(evaluated, cso, syncRuleMapping, source, errors) ? evaluated : null;
    }

    private static bool HasExpressionEvaluator(IExpressionEvaluator? expressionEvaluator, SyncRuleMappingSource source)
    {
        if (expressionEvaluator != null)
            return true;

        Log.Warning("EvaluateExpressionSource: Expression-based mapping requires an IExpressionEvaluator but none was provided. Expression: {Expression}", source.Expression);
        return false;
    }

    /// <summary>
    /// What synchronisation does with the first half of the pipeline's outcome. Missing Input Behaviour (#1361): an
    /// Expression whose input is absent does not fail, it evaluates cleanly and returns a structurally broken value
    /// ("ada.@corp.local" for a person with no surname) that no layer downstream can tell from a good one, so the
    /// administrator chooses what an absent input means. FailMapping records an <see cref="AttributeFlowError"/> and
    /// stops (one attribute lost, the object's others unaffected, as MultiValuedToSingleValued already does);
    /// FailObject throws, leaving the whole object untouched exactly as for an Expression that threw; ContributeNoValue
    /// is not a fault, and continues as no value. A thrown Expression is surfaced as an errored object, never swallowed
    /// and never conflated with a deliberate null: it is rethrown as <see cref="SyncExpressionEvaluationException"/>
    /// for the worker to record as an ExpressionEvaluationError RPEI.
    /// </summary>
    /// <returns>True to continue with the result or no value; false when the mapping stops here.</returns>
    private static bool ContinueAfterSource(
        ExpressionSourceEvaluation evaluated,
        ConnectedSystemObject cso,
        SyncRuleMapping syncRuleMapping,
        SyncRuleMappingSource source,
        List<AttributeFlowError>? errors)
    {
        switch (evaluated.Outcome)
        {
            case ExpressionSourceOutcome.MissingInputFailedMapping:
                errors?.Add(new AttributeFlowError
                {
                    Kind = AttributeFlowErrorKind.ExpressionMissingInput,
                    TargetAttributeName = syncRuleMapping.TargetMetaverseAttribute!.Name,
                    Expression = source.Expression,
                    MissingInputs = evaluated.MissingInputs
                });
                return false;

            case ExpressionSourceOutcome.MissingInputFailedObject:
                throw new SyncExpressionMissingInputException(source.Expression,
                    syncRuleMapping.TargetMetaverseAttribute?.Name, evaluated.MissingInputs);

            case ExpressionSourceOutcome.EvaluationFailed:
                throw BuildExpressionEvaluationException(syncRuleMapping.TargetMetaverseAttribute?.Name, source, evaluated.EvaluationException!);

            case ExpressionSourceOutcome.NoValue when evaluated.MissingInputs.Count > 0:
                // Not a fault: the same outcome an Expression returning null produces.
                Log.Debug("EvaluateExpressionSource: not evaluating for CSO {CsoId}; no value for {MissingInputs}. Contributing no value.",
                    cso.Id, string.Join(", ", evaluated.MissingInputs));
                return true;

            case ExpressionSourceOutcome.NoValue:
                // Expression null is a positive "no value" assertion (ConnectedNoValue), not "no opinion".
                Log.Debug("EvaluateExpressionSource: Expression '{Expression}' for CSO {CsoId} returned null.", source.Expression, cso.Id);
                return true;

            default:
                return true;
        }
    }

    /// <summary>
    /// Processes an expression-based Synchronisation Rule mapping source: the whole evaluation pipeline
    /// (<see cref="AttributeFlowPipeline.EvaluateInbound"/>, #2055), the same code the Attribute Flow tester runs, then
    /// what synchronisation does with its outcome.
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
        AttributePriorityContext? priorityContext,
        List<AttributeFlowError>? errors,
        IDictionary<string, object?>? metaverseAttributes)
    {
        if (!HasExpressionEvaluator(expressionEvaluator, source))
            return;

        var evaluation = AttributeFlowPipeline.EvaluateInbound(cso, csoType, syncRuleMapping, source, expressionEvaluator!, metaverseAttributes);

        if (!ContinueAfterSource(evaluation.Source, cso, syncRuleMapping, source, errors))
            return;

        // The one result handler (#2179): every value converted to the target's type before anything is staged, so a
        // value the type cannot hold is reported and the existing value kept, never removed first and then not
        // replaced. Inbound text processing (#843) applies to a Text target's values only.
        var target = syncRuleMapping.TargetMetaverseAttribute!;

        switch (evaluation.ErrorReason)
        {
            case AttributeFlowErrorReason.ResultNotConvertible:
                // The value itself is source data, so it goes to the Run Profile Execution Item (AttributeFlowError.Value),
                // never to the log.
                Log.Warning("ProcessExpressionMapping: Expression result cannot be stored in {Type} attribute {AttributeName}; " +
                    "the attribute keeps its value on MVO {MvoId}. CSO {CsoId}",
                    target.Type, target.Name, mvo.Id, cso.Id);
                errors?.Add(new AttributeFlowError
                {
                    Kind = AttributeFlowErrorKind.ExpressionResultNotConvertible,
                    TargetAttributeName = target.Name,
                    TargetAttributeType = target.Type,
                    Expression = source.Expression,
                    Value = evaluation.Resolution!.UnconvertibleValue
                });
                return;

            case AttributeFlowErrorReason.ResultMultiValued:
                Log.Warning("ProcessExpressionMapping: Expression returned {ValueCount} values for single-valued attribute {AttributeName}; " +
                    "the attribute keeps its value on MVO {MvoId}. CSO {CsoId}",
                    evaluation.Resolution!.ValueCount, target.Name, mvo.Id, cso.Id);
                errors?.Add(new AttributeFlowError
                {
                    Kind = AttributeFlowErrorKind.ExpressionResultMultiValued,
                    TargetAttributeName = target.Name,
                    SourceAttributeName = evaluation.Resolution.SourceAttributeName,
                    Expression = source.Expression,
                    ValueCount = evaluation.Resolution.ValueCount
                });
                return;
        }

        if (evaluation.Outcome == AttributeFlowOutcome.NoValue)
        {
            // A missing input under ContributeNoValue, a null result, an empty collection, or values that all collapse
            // to no value under inbound processing: the ConnectedNoValue state, resolved by priority and "Null is a
            // value" (#91).
            ApplyNoValueOutcome(mvo, syncRuleMapping, contributingSystemId, contributingSyncRuleId, mvoObjectTypeId, priorityContext);
            return;
        }

        ApplyExpressionValues(mvo, target, evaluation.Values, contributingSystemId, contributingSyncRuleId);
    }

    /// <summary>
    /// Resolves an inbound Expression's result against the mapping's target attribute through the one result handler
    /// (#2179), applying the mapping's inbound text processing (#843) to a Text target. Internal so the provenance
    /// inspector (#399) shows exactly the values the engine would flow.
    /// </summary>
    internal static ExpressionResultResolution ResolveInboundExpressionResult(object? result, MetaverseAttribute target, SyncRuleMapping syncRuleMapping) =>
        ExpressionResultValues.Resolve(result, target.Type, target.AttributePlurality, AttributeFlowPipeline.InboundTextProcessing(syncRuleMapping, target));

    /// <summary>
    /// Makes the target attribute hold exactly <paramref name="values"/> (distinct, already converted to its type):
    /// a typed diff against the Metaverse Object's effective values, so an unchanged value stages nothing, a value no
    /// longer supplied is removed, and a new one is added in the attribute's own typed field. Any other row the
    /// attribute holds (an asserted-null marker, a duplicate, a value in another type's field) is removed, so a
    /// single value into a multi-valued attribute replaces the set.
    /// </summary>
    private static void ApplyExpressionValues(
        MetaverseObject mvo,
        MetaverseAttribute targetAttribute,
        IReadOnlyList<object> values,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        var wantedKeys = values.Select(ExpressionResultValues.Key).ToHashSet();

        // One existing row is kept for each wanted value it already holds (the first, should two rows hold the same
        // value); every other row is removed.
        var existing = GetEffectiveAttributeValues(mvo, targetAttribute.Id)
            .Select(row => (Row: row, Key: TypedValueKey(row, targetAttribute.Type)))
            .ToList();
        var kept = existing
            .Where(row => row.Key != null && wantedKeys.Contains(row.Key))
            .GroupBy(row => row.Key!)
            .Select(group => group.First())
            .ToList();
        var keptRows = kept.Select(row => row.Row).ToHashSet(ReferenceEqualityComparer.Instance);
        var heldKeys = kept.Select(row => row.Key!).ToHashSet();

        var removals = existing.Where(row => !keptRows.Contains(row.Row)).Select(row => row.Row).ToList();
        mvo.PendingAttributeValueRemovals.AddRange(removals);

        var additions = values
            .Where(value => !heldKeys.Contains(ExpressionResultValues.Key(value)))
            .Select(value => CreateExpressionValue(mvo, targetAttribute, value, contributingSystemId, contributingSyncRuleId))
            .ToList();
        mvo.PendingAttributeValueAdditions.AddRange(additions);

        if (removals.Count > 0 || additions.Count > 0)
        {
            Log.Debug("ApplyExpressionValues: Expression result for {AttributeName} - removing {RemoveCount}, adding {AddCount} values on MVO {MvoId}",
                targetAttribute.Name, removals.Count, additions.Count, mvo.Id);
        }

        TakeOverProvenance(mvo, targetAttribute.Id, contributingSystemId, contributingSyncRuleId);
    }

    /// <summary>
    /// The key a Metaverse Object attribute value is compared by against converted expression values
    /// (<see cref="ExpressionResultValues.Key"/>): its value in the attribute's own typed field, or null for a row
    /// holding no value of that type (an asserted-null marker, or a value in another type's field).
    /// </summary>
    private static object? TypedValueKey(MetaverseObjectAttributeValue value, AttributeDataType type)
    {
        if (value.NullValue)
            return null;

        object? typed = type switch
        {
            AttributeDataType.Text => value.StringValue,
            AttributeDataType.Number => value.IntValue,
            AttributeDataType.LongNumber => value.LongValue,
            AttributeDataType.Decimal => value.DecimalValue,
            AttributeDataType.DateTime => value.DateTimeValue,
            AttributeDataType.Boolean => value.BoolValue,
            AttributeDataType.Guid => value.GuidValue,
            AttributeDataType.Binary => value.ByteValue,
            _ => null
        };

        return typed == null ? null : ExpressionResultValues.Key(typed);
    }

    /// <summary>
    /// A new Metaverse Object attribute value holding one converted expression value in its typed field.
    /// </summary>
    private static MetaverseObjectAttributeValue CreateExpressionValue(
        MetaverseObject mvo,
        MetaverseAttribute targetAttribute,
        object value,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        var newValue = new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo,
            Attribute = targetAttribute,
            AttributeId = targetAttribute.Id,
            ContributedBySystemId = contributingSystemId,
            ContributedBySyncRuleId = contributingSyncRuleId
        };

        switch (value)
        {
            case string text: newValue.StringValue = text; break;
            case int number: newValue.IntValue = number; break;
            case long longNumber: newValue.LongValue = longNumber; break;
            case decimal decimalNumber: newValue.DecimalValue = decimalNumber; break;
            case DateTime date: newValue.DateTimeValue = DateTimeAttributeValue.ToCanonical(date); break;
            case bool flag: newValue.BoolValue = flag; break;
            case Guid guid: newValue.GuidValue = guid; break;
            case byte[] bytes: newValue.ByteValue = bytes; break;
            default:
                // ExpressionResultConverter only produces the types above; anything else is a JIM fault.
                throw new InvalidOperationException(
                    $"An expression value of type '{value.GetType().Name}' has no field on a Metaverse Object attribute value.");
        }

        return newValue;
    }

    /// <summary>
    /// Processes a generated mapping (Unique Value Generation, #242): a <see cref="SyncRuleMapping"/> whose
    /// <see cref="SyncRuleMapping.Generation"/> row is set. <see cref="ProcessMapping"/> only reaches this method
    /// once the mapping is already the target attribute's winning contributor (the priority gate, or being the
    /// sole contributor), so this always records a <see cref="PendingGeneratedValue"/> request in place of a value
    /// rather than competing for one: the synchronous engine performs no I/O and knows nothing of the unique value
    /// generation service that would actually produce one (plan decision 6). <see cref="FindEffectiveIncumbentSyncRuleId"/>
    /// reads the recorded request as ownership of the attribute, so a lower-priority mapping evaluated later in
    /// this same pass sees it as taken and is blocked by the gate.
    /// </summary>
    private static void ProcessGeneratedMapping(
        ConnectedSystemObject cso,
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        ConnectedSystemObjectType csoType,
        IExpressionEvaluator? expressionEvaluator,
        int? contributingSystemId,
        int? contributingSyncRuleId,
        List<AttributeFlowError>? errors,
        IDictionary<string, object?>? metaverseAttributes)
    {
        var attributeId = syncRuleMapping.TargetMetaverseAttribute!.Id;

        // Winner takes the attribute (plan decision 6): clear whatever an ordinary contribution staged for this
        // attribute this pass (from any rule) and any earlier generation request for it (this mapping's own
        // earlier attempt in this pass, or a lower-priority mapping's). ProcessMapping's own #1199 RemoveAll above
        // only runs with more than one contributor; a generated mapping that is the attribute's SOLE contributor
        // never reaches it, so this repeats the same clear unconditionally for the generated case.
        mvo.PendingAttributeValueAdditions.RemoveAll(av => av.AttributeId == attributeId);
        mvo.PendingGeneratedValues.RemoveAll(p => p.AttributeId == attributeId);

        var source = AttributeFlowPipeline.GeneratedBaseSource(syncRuleMapping);
        if (source == null)
        {
            // No base expression: valid for Sequence and Random tokens (SyncRuleMappingGenerationValidator rule 3).
            AddRequest(baseValue: null, baseUnavailable: false);
            return;
        }

        var evaluation = EvaluateExpressionSource(cso, syncRuleMapping, source, csoType, expressionEvaluator, errors, metaverseAttributes);

        // Stopped: no evaluator, or FailMapping already recorded its AttributeFlowError. Either way nothing is
        // recorded for this attribute this pass; unlike an ordinary mapping there is no value for a generated
        // mapping to leave untouched here, only the absence of a request, which is the correct outcome: a
        // temporarily unresolvable mapping simply generates nothing this pass rather than failing the object.
        if (evaluation == null)
            return;

        // The checks a base must pass, shared with export staging and the tester (#2055): one value, with a text form,
        // after the same inbound text processing (#843) an ordinary text expression mapping applies.
        var generated = AttributeFlowPipeline.ResolveGeneratedBase(evaluation, AttributeFlowPipeline.GeneratedBaseTextProcessing(syncRuleMapping));
        switch (generated.Outcome)
        {
            case GeneratedBaseOutcome.Base:
                AddRequest(generated.BaseValue, baseUnavailable: false);
                return;

            // Missing inputs (ContributeNoValue, the default Missing Input Behaviour for a generated mapping, FR 29),
            // a null result, or value processing that left nothing: wait. NEVER ApplyNoValueOutcome here, unlike
            // ProcessExpressionMapping: that clears an EXISTING value under Attribute Priority, and a committed
            // generated value must never be recomputed or cleared by its inputs (FR 10) - "contribute no value" for a
            // generated mapping means wait, not withdraw.
            case GeneratedBaseOutcome.Waits:
                AddRequest(baseValue: null, baseUnavailable: true);
                return;

            case GeneratedBaseOutcome.Error:
                errors?.Add(GeneratedBaseError(generated, syncRuleMapping.TargetMetaverseAttribute!.Name, source.Expression));
                return;

            default:
                throw new InvalidOperationException($"Unexpected generated base outcome '{generated.Outcome}' for a mapping with a base expression.");
        }

        void AddRequest(string? baseValue, bool baseUnavailable) => mvo.PendingGeneratedValues.Add(new PendingGeneratedValue
        {
            Mapping = syncRuleMapping,
            AttributeId = attributeId,
            ContributedBySystemId = contributingSystemId,
            ContributedBySyncRuleId = contributingSyncRuleId,
            SourceConnectedSystemObjectId = cso.Id,
            SourceConnectedSystemObject = cso,
            BaseValue = baseValue,
            BaseUnavailable = baseUnavailable
        });
    }

    /// <summary>
    /// The <see cref="AttributeFlowError"/> a base that cannot be used records (#242): several values, or a value with
    /// no text form. Nothing is generated for the attribute this pass. Missing-input and evaluation failures never get
    /// here: they are handled before the base is resolved, as for any expression source.
    /// </summary>
    internal static AttributeFlowError GeneratedBaseError(GeneratedBaseEvaluation generated, string targetAttributeName, string? expression) =>
        generated.ErrorReason switch
        {
            AttributeFlowErrorReason.GeneratedBaseNotSingleValue => new AttributeFlowError
            {
                Kind = AttributeFlowErrorKind.GeneratedBaseNotSingleValue,
                TargetAttributeName = targetAttributeName,
                Expression = expression
            },
            AttributeFlowErrorReason.ResultNotConvertible => new AttributeFlowError
            {
                Kind = AttributeFlowErrorKind.ExpressionResultNotConvertible,
                TargetAttributeName = targetAttributeName,
                TargetAttributeType = AttributeDataType.Text,
                Expression = expression,
                Value = generated.UnconvertibleValue
            },
            _ => throw new InvalidOperationException($"A generated base error of '{generated.ErrorReason}' is handled before the base is resolved.")
        };

    /// <inheritdoc cref="Interfaces.ISyncEngine.ApplyGeneratedValue"/>
    public void ApplyGeneratedValue(MetaverseObject mvo, PendingGeneratedValue pending, string? textValue, long? numericValue)
    {
        ArgumentNullException.ThrowIfNull(mvo);
        ArgumentNullException.ThrowIfNull(pending);

        var targetAttribute = pending.Mapping.TargetMetaverseAttribute!;
        var attributeId = pending.AttributeId;

        // Winner takes the attribute: clear whatever another rule staged for it this pass. Ordinarily nothing
        // is left to clear (ProcessGeneratedMapping already cleared it when it recorded the pending request),
        // but a later-evaluated rule that abstained and left this generation standing (plan decision 6, the
        // "supersededGenerations" hand-off in ProcessMapping) could not itself have written anything either, so
        // this is a defensive repeat of that same clear for the point at which the value is actually written.
        mvo.PendingAttributeValueAdditions.RemoveAll(av => av.AttributeId == attributeId);

        var existingValue = GetEffectiveAttributeValues(mvo, attributeId).SingleOrDefault();

        bool valueChanged;
        int? intValue = null;
        switch (targetAttribute.Type)
        {
            case AttributeDataType.Text:
                valueChanged = existingValue == null || !string.Equals(existingValue.StringValue, textValue, StringComparison.Ordinal);
                break;

            case AttributeDataType.Number:
                // Checked conversion: a candidate that does not fit a 32-bit Number is a caller error (the
                // mapping's target and the token settings that produced this number disagree), never a value
                // to silently truncate and corrupt (the same #871 lossy-cast class ExpressionResultConverter
                // guards against).
                var numberCandidate = numericValue!.Value;
                try
                {
                    intValue = checked((int)numberCandidate);
                }
                catch (OverflowException ex)
                {
                    throw new InvalidOperationException(
                        $"Generated value {numberCandidate} for {targetAttribute.Name} does not fit a Number (32-bit) attribute. " +
                        "This indicates a sequence or random-digits token producing a value too wide for the mapping's target type.", ex);
                }
                valueChanged = existingValue == null || existingValue.IntValue != intValue;
                break;

            case AttributeDataType.LongNumber:
                valueChanged = existingValue == null || existingValue.LongValue != numericValue;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(pending), targetAttribute.Type,
                    $"ApplyGeneratedValue does not support target attribute type {targetAttribute.Type}.");
        }

        if (valueChanged)
        {
            if (existingValue != null)
                mvo.PendingAttributeValueRemovals.Add(existingValue);

            var newValue = new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = targetAttribute,
                AttributeId = attributeId,
                ContributedBySystemId = pending.ContributedBySystemId,
                ContributedBySyncRuleId = pending.ContributedBySyncRuleId
            };

            switch (targetAttribute.Type)
            {
                case AttributeDataType.Text: newValue.StringValue = textValue; break;
                case AttributeDataType.Number: newValue.IntValue = intValue; break;
                case AttributeDataType.LongNumber: newValue.LongValue = numericValue; break;
            }

            mvo.PendingAttributeValueAdditions.Add(newValue);
            Log.Debug("ApplyGeneratedValue: generated mapping set {AttributeName} to '{Value}' on MVO {MvoId}",
                targetAttribute.Name, LogSanitiser.Sanitise(textValue ?? numericValue?.ToString()), mvo.Id);
        }

        TakeOverProvenance(mvo, attributeId, pending.ContributedBySystemId, pending.ContributedBySyncRuleId);
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
    /// collapse to no value and de-duplicating the result. Used for direct text flow so whitespace-only entries are
    /// removed from the set when enabled; an Expression's values get the same processing through
    /// <see cref="ResolveInboundExpressionResult"/>.
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

        TakeOverProvenance(mvo, targetAttributeId, contributingSystemId, contributingSyncRuleId);
    }

    /// <summary>
    /// Whether a Connected System Object row holds a value of <paramref name="type"/> in that type's own field (#2153).
    /// A row held under another type (#2128) or holding nothing does not. Internal so the provenance inspector (#399)
    /// shows exactly the rows the engine would flow. Reference rows are always taken as holding a value: their writer
    /// resolves them itself.
    /// </summary>
    internal static bool HoldsValueOfType(ConnectedSystemObjectAttributeValue value, AttributeDataType type) => type switch
    {
        AttributeDataType.Text => value.StringValue != null,
        AttributeDataType.Number => value.IntValue.HasValue,
        AttributeDataType.LongNumber => value.LongValue.HasValue,
        AttributeDataType.Decimal => value.DecimalValue.HasValue,
        AttributeDataType.DateTime => value.DateTimeValue.HasValue,
        AttributeDataType.Boolean => value.BoolValue.HasValue,
        AttributeDataType.Guid => value.GuidValue.HasValue,
        AttributeDataType.Binary => value.ByteValue != null,
        AttributeDataType.Reference => true,
        _ => false
    };

    /// <summary>
    /// The rows that hold a value of <paramref name="type"/>. When every row does, which is the usual case, the list
    /// itself is returned and nothing is allocated (plain loops rather than predicates, which would allocate a closure
    /// over <paramref name="type"/> on this hot path).
    /// </summary>
    private static List<ConnectedSystemObjectAttributeValue> RowsHoldingValueOfType(
        List<ConnectedSystemObjectAttributeValue> rows, AttributeDataType type)
    {
        var firstWithout = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!HoldsValueOfType(rows[i], type))
            {
                firstWithout = i;
                break;
            }
        }

        if (firstWithout < 0)
            return rows;

        var typedRows = new List<ConnectedSystemObjectAttributeValue>(rows.Count - 1);
        for (var i = 0; i < rows.Count; i++)
        {
            if (i != firstWithout && HoldsValueOfType(rows[i], type))
                typedRows.Add(rows[i]);
        }

        return typedRows;
    }

    /// <summary>
    /// How many distinct values <paramref name="typedRows"/> (rows already known to hold <paramref name="type"/>) hold,
    /// compared as the typed writers compare them: dates canonically, decimals numerically, binary by content. Zero or
    /// one row is answered without allocating.
    /// </summary>
    private static int CountDistinctValues(List<ConnectedSystemObjectAttributeValue> typedRows, AttributeDataType type)
    {
        if (typedRows.Count <= 1)
            return typedRows.Count;

        return type switch
        {
            AttributeDataType.Number => typedRows.Select(row => row.IntValue!.Value).Distinct().Count(),
            AttributeDataType.LongNumber => typedRows.Select(row => row.LongValue!.Value).Distinct().Count(),
            AttributeDataType.Decimal => typedRows.Select(row => row.DecimalValue!.Value).Distinct().Count(),
            AttributeDataType.DateTime => typedRows.Select(row => DateTimeAttributeValue.Key(row.DateTimeValue!.Value)).Distinct().Count(),
            AttributeDataType.Boolean => typedRows.Select(row => row.BoolValue!.Value).Distinct().Count(),
            AttributeDataType.Guid => typedRows.Select(row => row.GuidValue!.Value).Distinct().Count(),
            AttributeDataType.Binary => typedRows.Select(row => row.ByteValue!).Distinct(ByteArrayContentComparer.Instance).Count(),
            _ => typedRows.Count
        };
    }

    /// <summary>Reads a Metaverse Object value's key in the target's typed field; false when the row holds none.</summary>
    private delegate bool TryGetHeldKey<TKey>(MetaverseObjectAttributeValue value, out TKey key);

    /// <summary>
    /// Makes the target attribute hold exactly the set of values in <paramref name="sourceRows"/> (#2153): a set diff
    /// against the Metaverse Object's effective values, so order never matters, duplicates collapse and an unchanged
    /// set stages nothing. One held row is kept per wanted value (the first, should two rows hold it); every other row
    /// is removed, including an asserted-null marker, a duplicate, or a value held in another type's field. Then
    /// <see cref="TakeOverProvenance"/>. A single source value, the usual case, takes a path that allocates nothing
    /// beyond the value it adds.
    /// </summary>
    /// <param name="sourceRows">The Connected System Object's rows for the attribute, every one holding the type.</param>
    /// <param name="sourceKey">The key of a source row's value.</param>
    /// <param name="heldKey">Reads a Metaverse Object row's key; false for a row holding no value of the type.</param>
    /// <param name="assign">Writes a source row's value onto a new Metaverse Object value, in the form JIM holds it.</param>
    /// <param name="comparer">How keys compare; null for the key type's default equality.</param>
    private static void ApplyTypedValueSet<TKey>(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> sourceRows,
        Func<ConnectedSystemObjectAttributeValue, TKey> sourceKey,
        TryGetHeldKey<TKey> heldKey,
        Action<MetaverseObjectAttributeValue, ConnectedSystemObjectAttributeValue> assign,
        IEqualityComparer<TKey>? comparer,
        int? contributingSystemId,
        int? contributingSyncRuleId) where TKey : notnull
    {
        var targetAttribute = syncRuleMapping.TargetMetaverseAttribute!;
        comparer ??= EqualityComparer<TKey>.Default;

        if (sourceRows.Count == 1)
        {
            var source = sourceRows[0];
            var wantedKey = sourceKey(source);
            var isHeld = false;

            foreach (var row in GetEffectiveAttributeValues(mvo, targetAttribute.Id))
            {
                if (!isHeld && heldKey(row, out var key) && comparer.Equals(key, wantedKey))
                    isHeld = true;
                else
                    mvo.PendingAttributeValueRemovals.Add(row);
            }

            if (!isHeld)
                mvo.PendingAttributeValueAdditions.Add(NewTypedValue(mvo, targetAttribute, source, assign, contributingSystemId, contributingSyncRuleId));
        }
        else
        {
            var wantedKeys = new HashSet<TKey>(comparer);
            var distinctSources = sourceRows.Where(row => wantedKeys.Add(sourceKey(row))).ToList();

            var heldKeys = new HashSet<TKey>(comparer);
            var removals = GetEffectiveAttributeValues(mvo, targetAttribute.Id)
                .Where(row => !(heldKey(row, out var key) && wantedKeys.Contains(key) && heldKeys.Add(key)))
                .ToList();
            mvo.PendingAttributeValueRemovals.AddRange(removals);

            mvo.PendingAttributeValueAdditions.AddRange(distinctSources
                .Where(row => !heldKeys.Contains(sourceKey(row)))
                .Select(row => NewTypedValue(mvo, targetAttribute, row, assign, contributingSystemId, contributingSyncRuleId)));
        }

        TakeOverProvenance(mvo, targetAttribute.Id, contributingSystemId, contributingSyncRuleId);
    }

    private static MetaverseObjectAttributeValue NewTypedValue(
        MetaverseObject mvo,
        MetaverseAttribute targetAttribute,
        ConnectedSystemObjectAttributeValue source,
        Action<MetaverseObjectAttributeValue, ConnectedSystemObjectAttributeValue> assign,
        int? contributingSystemId,
        int? contributingSyncRuleId)
    {
        var value = new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo,
            Attribute = targetAttribute,
            AttributeId = targetAttribute.Id,
            ContributedBySystemId = contributingSystemId,
            ContributedBySyncRuleId = contributingSyncRuleId
        };
        assign(value, source);
        return value;
    }

    /// <summary>Reads a nullable typed field as a key: false when it holds nothing.</summary>
    private static bool TryGetValue<T>(T? field, out T key) where T : struct
    {
        key = field.GetValueOrDefault();
        return field.HasValue;
    }

    private static void ProcessNumberAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> typedRows,
        int? contributingSystemId,
        int? contributingSyncRuleId) =>
        ApplyTypedValueSet(mvo, syncRuleMapping, typedRows,
            static row => row.IntValue!.Value,
            static (MetaverseObjectAttributeValue held, out int key) => TryGetValue(held.IntValue, out key),
            static (value, row) => value.IntValue = row.IntValue,
            null, contributingSystemId, contributingSyncRuleId);

    private static void ProcessLongNumberAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> typedRows,
        int? contributingSystemId,
        int? contributingSyncRuleId) =>
        ApplyTypedValueSet(mvo, syncRuleMapping, typedRows,
            static row => row.LongValue!.Value,
            static (MetaverseObjectAttributeValue held, out long key) => TryGetValue(held.LongValue, out key),
            static (value, row) => value.LongValue = row.LongValue,
            null, contributingSystemId, contributingSyncRuleId);

    /// <summary>
    /// Decimal comparison is numeric and scale-insensitive (5.0m equals 5.00m, with equal hash codes), so a scale-only
    /// difference between the Connected System Object and Metaverse Object values never stages a change.
    /// </summary>
    private static void ProcessDecimalAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> typedRows,
        int? contributingSystemId,
        int? contributingSyncRuleId) =>
        ApplyTypedValueSet(mvo, syncRuleMapping, typedRows,
            static row => row.DecimalValue!.Value,
            static (MetaverseObjectAttributeValue held, out decimal key) => TryGetValue(held.DecimalValue, out key),
            static (value, row) => value.DecimalValue = row.DecimalValue,
            null, contributingSystemId, contributingSyncRuleId);

    /// <summary>
    /// Dates are keyed canonically (#2153): a value read from the database is Kind Local and to the microsecond, one
    /// made in memory is Kind Utc and perhaps to the 100ns, so the same date in two forms is one value and not a
    /// change. Every date added is written canonical.
    /// </summary>
    private static void ProcessDateTimeAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> typedRows,
        int? contributingSystemId,
        int? contributingSyncRuleId) =>
        ApplyTypedValueSet(mvo, syncRuleMapping, typedRows,
            static row => DateTimeAttributeValue.Key(row.DateTimeValue!.Value),
            static (MetaverseObjectAttributeValue held, out long key) =>
            {
                key = held.DateTimeValue.HasValue ? DateTimeAttributeValue.Key(held.DateTimeValue.Value) : 0;
                return held.DateTimeValue.HasValue;
            },
            static (value, row) => value.DateTimeValue = CanonicalDate(row.DateTimeValue),
            null, contributingSystemId, contributingSyncRuleId);

    /// <summary>
    /// Booleans take the set shape too. Under the Boolean rule a source holds at most one value, but a legacy
    /// multi-valued Boolean target or Connected System Object is handled as any other set rather than throwing.
    /// </summary>
    private static void ProcessBooleanAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> typedRows,
        int? contributingSystemId,
        int? contributingSyncRuleId) =>
        ApplyTypedValueSet(mvo, syncRuleMapping, typedRows,
            static row => row.BoolValue!.Value,
            static (MetaverseObjectAttributeValue held, out bool key) => TryGetValue(held.BoolValue, out key),
            static (value, row) => value.BoolValue = row.BoolValue,
            null, contributingSystemId, contributingSyncRuleId);

    private static void ProcessGuidAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> typedRows,
        int? contributingSystemId,
        int? contributingSyncRuleId) =>
        ApplyTypedValueSet(mvo, syncRuleMapping, typedRows,
            static row => row.GuidValue!.Value,
            static (MetaverseObjectAttributeValue held, out Guid key) => TryGetValue(held.GuidValue, out key),
            static (value, row) => value.GuidValue = row.GuidValue,
            null, contributingSystemId, contributingSyncRuleId);

    private static void ProcessBinaryAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> typedRows,
        int? contributingSystemId,
        int? contributingSyncRuleId) =>
        ApplyTypedValueSet(mvo, syncRuleMapping, typedRows,
            static row => row.ByteValue!,
            static (MetaverseObjectAttributeValue held, out byte[] key) =>
            {
                key = held.ByteValue ?? [];
                return held.ByteValue != null;
            },
            static (value, row) => value.ByteValue = row.ByteValue,
            ByteArrayContentComparer.Instance, contributingSystemId, contributingSyncRuleId);

    /// <summary>
    /// A date in the form JIM holds it (#2153), or null for none. Every date the engine creates is written this way.
    /// </summary>
    internal static DateTime? CanonicalDate(DateTime? value) =>
        value.HasValue ? DateTimeAttributeValue.ToCanonical(value.Value) : null;

    /// <summary>
    /// A date as a Connected System of <paramref name="resolution"/> holds it (#2153, D2): canonical, then floored to the
    /// resolution; or null for none. Every date export staging sends is written this way.
    /// </summary>
    internal static DateTime? DateAtResolution(DateTime? value, DateTimeResolution? resolution) =>
        value.HasValue ? DateTimeAttributeValue.ToResolution(value.Value, resolution) : null;

    /// <summary>
    /// A date's canonical key (#2153), or null for none: what a set or tuple of values compares a date by.
    /// </summary>
    internal static long? DateKey(DateTime? value) =>
        value.HasValue ? DateTimeAttributeValue.Key(value.Value) : null;

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

        // Only take provenance over when this pass saw the whole picture. With unresolved references the removals are
        // deferred to the cross-page resolution pass, so the surviving rows are not yet this contribution's set.
        if (!hasUnresolvedReferences)
            TakeOverProvenance(mvo, syncRuleMapping.TargetMetaverseAttribute!.Id, contributingSystemId, contributingSyncRuleId);
    }

    /// <summary>
    /// Builds the <c>cs[...]</c> attribute dictionary an Expression evaluates against: every value of each attribute,
    /// as <see cref="ExpressionAttributeDictionary"/> reads them (#2053). Internal (rather than
    /// private) so the provenance inspector (#399) can evaluate an Attribute Priority candidate's expression
    /// exactly as the engine would, without duplicating this projection logic.
    /// </summary>
    internal static Dictionary<string, object?> BuildCsoAttributeDictionary(
        ConnectedSystemObject cso,
        ConnectedSystemObjectType csoType)
    {
        var attributes = new ExpressionAttributeDictionary();

        foreach (var attributeValue in cso.AttributeValues)
        {
            var csotAttribute = csoType.Attributes.SingleOrDefault(a => a.Id == attributeValue.AttributeId);
            if (csotAttribute == null)
            {
                Log.Warning("BuildCsoAttributeDictionary: CSO {CsoId} has attribute value with AttributeId={AttrId} but attribute not found in type definition",
                    cso.Id, attributeValue.AttributeId);
                continue;
            }

            attributes.Add(csotAttribute.Name, ToExpressionValue(attributeValue, csotAttribute.Type));
        }

        return attributes.Build();
    }

    /// <summary>
    /// The value an expression sees for one Connected System Object attribute value, typed by the attribute's data
    /// type. Shared with Object Matching (#2052) so a matching expression sees exactly what an Attribute Flow
    /// expression sees.
    /// </summary>
    internal static object? ToExpressionValue(ConnectedSystemObjectAttributeValue attributeValue, AttributeDataType type) => type switch
    {
        AttributeDataType.Text => attributeValue.StringValue,
        AttributeDataType.Number => attributeValue.IntValue,
        AttributeDataType.LongNumber => attributeValue.LongValue,
        AttributeDataType.Decimal => attributeValue.DecimalValue,
        AttributeDataType.DateTime => attributeValue.DateTimeValue,
        AttributeDataType.Boolean => attributeValue.BoolValue,
        AttributeDataType.Guid => attributeValue.GuidValue,
        AttributeDataType.Binary => attributeValue.ByteValue,
        AttributeDataType.Reference => attributeValue.ReferenceValue?.Id.ToString(),
        _ => null
    };
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;

namespace JIM.Application.Services;

/// <summary>
/// The next-contributor recall fallback core (#91, extracted for #1537): when a contribution to a Metaverse
/// Object attribute is withdrawn (a Connected System Object obsoletes, a still-joined winner withdraws its
/// value, or the contributing Synchronisation Rule is deleted), re-elect any surviving contributor for the
/// affected attributes so each attribute is handed to the next source rather than blanked. Surviving Connected
/// System Objects are re-flowed through the normal inbound attribute-flow gate; with the recalled values
/// already marked for removal, the gate elects the highest-priority survivor (lower-priority survivors lose
/// and write nothing). A no-op when a recalled attribute has no other contributor: it is then genuinely
/// cleared by the caller.
/// <para>
/// The recall's SCOPE (whose contribution is going, and who may take over) is an input
/// (<see cref="ContributorRecallScope"/>), so the same core serves the run-time obsoletion path in the worker
/// and the queued rule-deletion recall task, and future scopes (#809, #1549) plug in without forking it.
/// </para>
/// </summary>
public static class ContributorReElectionService
{
    /// <summary>
    /// Re-elects surviving contributors for the recalled attribute values of one Metaverse Object.
    /// Behaviour-preserving extraction of the worker's obsoletion re-election pass; see the class summary.
    /// </summary>
    /// <param name="mvo">The Metaverse Object whose attributes are being recalled (Type must be loaded).</param>
    /// <param name="recalledValues">The withdrawn attribute values, already marked for removal on the object.</param>
    /// <param name="scope">Whose contribution is being withdrawn, and who may take over.</param>
    /// <param name="priorityContext">The attribute priority contributor cache (#91), built from all Synchronisation Rules.</param>
    /// <param name="syncEngine">The synchronisation decision engine, for the inbound attribute re-flow.</param>
    /// <param name="syncRepository">The synchronisation repository, for survivor discovery and hydration.</param>
    /// <param name="isCsoInScopeForImportRule">The import-rule scoping gate; a survivor out of the rule's scope is never re-elected.</param>
    /// <param name="objectTypes">The caller's Connected System Object Type cache; each survivor's own type is
    /// appended per re-flow when absent. May be null only when no survivors need re-flowing.</param>
    /// <param name="expressionEvaluator">The evaluator for expression-based mappings.</param>
    public static async Task ReElectSurvivingContributorsAsync(
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> recalledValues,
        ContributorRecallScope scope,
        AttributePriorityContext priorityContext,
        ISyncEngine syncEngine,
        ISyncRepository syncRepository,
        Func<ConnectedSystemObject, SyncRule, bool> isCsoInScopeForImportRule,
        IReadOnlyList<ConnectedSystemObjectType>? objectTypes,
        IExpressionEvaluator expressionEvaluator)
    {
        if (mvo.Type == null)
            return;

        var objectTypeId = mvo.Type.Id;
        var recalledAttributeIds = recalledValues.Select(av => av.AttributeId).Distinct().ToList();

        // An attribute is re-electable when the contributor cache holds a mapping from an ELIGIBLE
        // Synchronisation Rule under the recall scope. Counting contributors (> 1) is not equivalent: when the
        // withdrawn contribution's own mapping has been deleted or disabled (#1533) it is absent from the
        // cache, so a sole surviving contributor counts as 1 yet must still be re-elected.
        var reElectableAttributeIds = recalledAttributeIds
            .Where(id => priorityContext.GetContributors(objectTypeId, id)
                .Any(c => c.SyncRule != null && scope.IsEligibleContributorRule(c.SyncRule)))
            .ToList();
        if (reElectableAttributeIds.Count == 0)
            return;

        // Survivor discovery must query the repository, not the mvo.ConnectedSystemObjects navigation: the sync
        // page loads hydrate the Metaverse Object with Type and AttributeValues only, so on PostgreSQL that
        // navigation holds just the sibling CSOs EF happens to be tracking in this run (typically only the leaving
        // system's own page), and survivors joined via other Connected Systems are invisible, silently disabling
        // re-election. The in-memory test database auto-fixes the navigation up and masks this; only a
        // real-database run can catch a regression here.
        var joinedCsos = await syncRepository.GetConnectedSystemObjectsByMetaverseObjectIdAsync(mvo.Id);

        // Gather the distinct (surviving CSO, contributing rule) pairs to re-flow, highest priority first so the
        // strongest surviving contributor is written first and the gate skips the rest.
        var survivorsToReflow = new List<(ConnectedSystemObject Cso, SyncRule Rule)>();
        var seen = new HashSet<(Guid CsoId, int RuleId)>();

        foreach (var attributeId in reElectableAttributeIds)
        {
            // Contributing rules eligible under the recall scope. Project to the rule and filter in one
            // pipeline so ineligible rules and any rule-less mapping are excluded before the body.
            foreach (var rule in priorityContext.GetContributors(objectTypeId, attributeId)
                         .Select(c => c.SyncRule)
                         .Where(r => r != null && scope.IsEligibleContributorRule(r))
                         .Select(r => r!))
            {
                foreach (var survivor in joinedCsos.Where(c =>
                             c.ConnectedSystemId == rule.ConnectedSystemId &&
                             scope.IsEligibleSurvivor(c) &&
                             c.Status != ConnectedSystemObjectStatus.Obsolete))
                {
                    if (seen.Add((survivor.Id, rule.Id)))
                        survivorsToReflow.Add((survivor, rule));
                }
            }
        }

        if (objectTypes == null)
            throw new MissingMemberException("objectTypes is null!");

        foreach (var (survivor, rule) in survivorsToReflow)
        {
            // The discovery load does not eagerly fetch the survivor's object type or reference-value navigations;
            // load the full Connected System Object so the re-flow evaluates real values, can resolve the
            // survivor's type, and can resolve its reference attributes on PostgreSQL (the in-memory test database
            // auto-tracks navigations and would mask a missing load). A tracked survivor resolves to the same
            // instance, now fully hydrated.
            if (survivor.Type == null || survivor.AttributeValues.Count == 0 ||
                survivor.AttributeValues.Any(av => av.ReferenceValueId.HasValue && av.ReferenceValue == null))
            {
                var loaded = await syncRepository.GetConnectedSystemObjectAsync(survivor.ConnectedSystemId, survivor.Id);
                if (loaded != null)
                {
                    survivor.AttributeValues = loaded.AttributeValues;
                    survivor.Type ??= loaded.Type;
                }
            }

            // The gate writes to the survivor's joined Metaverse Object; ensure the back-reference is the MVO in hand.
            survivor.MetaverseObject = mvo;

            // A survivor out of the rule's scope is not a legitimate contributor, so it must not be re-elected.
            if (!isCsoInScopeForImportRule(survivor, rule))
                continue;

            // The survivor may belong to a different Connected System than the caller's cache covers, so its
            // Connected System Object Type may not be in objectTypes; include it so the engine can resolve the
            // survivor's type. Reference attributes are re-flowed too: unlike import-time flow (where a referenced
            // object may not exist yet, needing deferred passes), every object a surviving CSO references already
            // exists and is joined at recall time, so its references resolve in this single pass. This is the final
            // opportunity to resolve them in this operation, hence isFinalReferencePass (an unresolvable reference warns).
            var objectTypesForSurvivor = new List<ConnectedSystemObjectType>(objectTypes);
            if (survivor.Type != null && objectTypesForSurvivor.All(t => t.Id != survivor.Type.Id))
                objectTypesForSurvivor.Add(survivor.Type);

            syncEngine.FlowInboundAttributes(survivor, rule, objectTypesForSurvivor, expressionEvaluator,
                skipReferenceAttributes: false, onlyReferenceAttributes: false, isFinalReferencePass: true, priorityContext);
        }
    }

    /// <summary>
    /// Identifies the attributes genuinely cleared by a set of pending Metaverse Object changes: a value was
    /// removed, no replacement value (nor asserted-null marker) was added, and no other value remains for the
    /// attribute (a multi-valued attribute shrinking is not a clear). An attribute that was already blank is not
    /// included. Callable before or after the pending changes are applied.
    /// </summary>
    /// <param name="mvo">The Metaverse Object the changes apply to.</param>
    /// <param name="additions">The attribute values added this run.</param>
    /// <param name="removals">The attribute values removed this run.</param>
    public static HashSet<int> GetClearedAttributeIds(
        MetaverseObject mvo,
        IReadOnlyCollection<MetaverseObjectAttributeValue> additions,
        IReadOnlyCollection<MetaverseObjectAttributeValue> removals)
    {
        if (removals.Count == 0)
            return new HashSet<int>();

        var reAddedAttributeIds = additions.Select(av => av.AttributeId).ToHashSet();
        return removals
            .Select(av => av.AttributeId)
            .Distinct()
            .Where(attributeId => !reAddedAttributeIds.Contains(attributeId) &&
                                  !mvo.AttributeValues.Any(av => av.AttributeId == attributeId && !removals.Contains(av)))
            .ToHashSet();
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Sync;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// The per-object obsoletion core (#809 Phase 1, extracted behaviour-preservingly from the worker's sync
/// processor): given an obsolete Connected System Object, decide and stage everything obsoleting it
/// through synchronisation entails, with the outputs as data
/// (<see cref="ConnectedSystemObjectObsoletionResult"/>) rather than mutations of processor state:
/// <list type="bullet">
/// <item>a quiet delete for a pre-disconnected object (its disconnection was already recorded);</item>
/// <item>the once-managed-always-managed path (InboundOutOfScopeAction = RemainJoined) that deletes the
/// object but preserves the Metaverse Object join;</item>
/// <item>the Disconnect path: Metaverse Object deletion-rule evaluation (via the caller's delegate, which
/// owns applying the decision), attribute recall gated on the Object Type's
/// RemoveContributedAttributesOnObsoletion, surviving-contributor re-election
/// (<see cref="ContributorReElectionService"/>) under the caller's <see cref="ContributorRecallScope"/>,
/// grace-period value freezing, join breaking, and the export-relevant bookkeeping.</item>
/// </list>
/// Collaborators are parameters in the <see cref="ContributorReElectionService"/> style, so the run-time
/// sync path in the worker and the queued Connected System deprovisioning run (#809 Phase 2) share one
/// implementation, and the future #134/#827 preview adapter can run the same core in a read-only harness.
/// </summary>
public static class ConnectedSystemObjectObsoletionService
{
    /// <summary>
    /// Checks whether a Connected System Object has been obsoleted and stages its deletion, deciding any
    /// joined Metaverse Object changes as necessary. Respects the InboundOutOfScopeAction setting on
    /// import Synchronisation Rules to determine whether to disconnect. Behaviour-preserving extraction of
    /// the worker sync processor's per-object obsoletion; see the class summary for the paths.
    /// When a joined Connected System Object is obsoleted with the Disconnect action, a single execution
    /// item records both the disconnection and the deletion (one-RPEI-per-CSO rule).
    /// </summary>
    /// <param name="connectedSystemObject">The Connected System Object to process; a no-op unless its status is Obsolete.</param>
    /// <param name="activeSyncRules">The active Synchronisation Rules for the Connected System, for the out-of-scope action determination.</param>
    /// <param name="recallScope">Whose contribution the recall withdraws and who may take over; the obsoleting-object
    /// scope for the run-time sync path, the deleted-system scope for the deprovisioning run.</param>
    /// <param name="priorityContext">The attribute priority contributor cache (#91); when null, surviving-contributor
    /// re-election is skipped (recalled values are simply cleared).</param>
    /// <param name="remainingImportSourceEvaluator">Answers whether any remaining joined system is still a
    /// contributing source for the object's type; selects between recalling sole-contributed values and preserving
    /// them as last known state (#1570). Create one per run; it caches the Synchronisation Rule map.</param>
    /// <param name="syncEngine">The synchronisation decision engine, for the out-of-scope action, pending-change
    /// application and the re-election re-flow.</param>
    /// <param name="syncRepository">The synchronisation repository, for joined-system discovery and survivor hydration.</param>
    /// <param name="isCsoInScopeForImportRule">The import-rule scoping gate; a survivor out of the rule's scope is never re-elected.</param>
    /// <param name="objectTypes">The caller's Connected System Object Type cache, for the re-election re-flow.</param>
    /// <param name="expressionEvaluator">The evaluator for expression-based mappings in the re-election re-flow.</param>
    /// <param name="executionItemFactory">Creates an execution item bound to the caller's Activity.</param>
    /// <param name="syncOutcomeTrackingLevel">How much sync outcome detail to record on the execution items.</param>
    /// <param name="processMvoDeletionRuleAsync">Evaluates AND applies the Metaverse Object deletion rule for a
    /// disconnection (mvo, disconnecting system id, remaining joined system ids), returning the applied decision and
    /// any decision-time policy snapshot (#119). Left with the caller because applying the decision is entangled with
    /// its batch flush machinery (immediate-deletion queueing with cross-object dedup, grace-period persistence with
    /// Activity initiator attribution) and is shared with the withdrawal-recall path; a read-only harness supplies an
    /// evaluate-only implementation.</param>
    /// <param name="recordPreRecallAttributeSnapshot">Called with the joined Metaverse Object BEFORE the deletion-rule
    /// evaluation and attribute recall run, so the caller can snapshot its attribute values for the deletion change
    /// record should the object be deleted (the caller keeps first-snapshot-wins semantics across objects).</param>
    /// <returns>The staged outcome of the operation, as data; see <see cref="ConnectedSystemObjectObsoletionResult"/>.</returns>
    public static async Task<ConnectedSystemObjectObsoletionResult> ProcessObsoleteConnectedSystemObjectAsync(
        ConnectedSystemObject connectedSystemObject,
        List<SyncRule> activeSyncRules,
        ContributorRecallScope recallScope,
        AttributePriorityContext? priorityContext,
        RemainingImportSourceEvaluator remainingImportSourceEvaluator,
        ISyncEngine syncEngine,
        ISyncRepository syncRepository,
        Func<ConnectedSystemObject, SyncRule, bool> isCsoInScopeForImportRule,
        IReadOnlyList<ConnectedSystemObjectType>? objectTypes,
        IExpressionEvaluator expressionEvaluator,
        Func<ActivityRunProfileExecutionItem> executionItemFactory,
        ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel syncOutcomeTrackingLevel,
        Func<MetaverseObject, int, IReadOnlyCollection<int>, Task<(MvoDeletionDecision Decision, string? PolicySnapshotJson)>> processMvoDeletionRuleAsync,
        Action<MetaverseObject> recordPreRecallAttributeSnapshot)
    {
        var result = new ConnectedSystemObjectObsoletionResult();
        if (connectedSystemObject.Status != ConnectedSystemObjectStatus.Obsolete)
            return result;

        // Create the execution item for the CSO deletion
        // Note: RPEI uses Delete (user-facing), CSO status uses Obsolete (internal state)
        var deletionExecutionItem = executionItemFactory();
        deletionExecutionItem.ConnectedSystemObject = connectedSystemObject;
        deletionExecutionItem.ConnectedSystemObjectId = connectedSystemObject.Id;
        deletionExecutionItem.ObjectChangeType = ObjectChangeType.Deleted;
        // Snapshot CSO display fields eagerly; the caller's flush will null the CSO reference before the
        // RPEIs are flushed, so the centralised snapshot would find nothing.
        deletionExecutionItem.SnapshotCsoDisplayFields(connectedSystemObject);

        if (connectedSystemObject.MetaverseObject == null)
        {
            // CSO is not joined to an MVO. Check if it was pre-disconnected as part of MVO deletion.
            if (connectedSystemObject.JoinType == ConnectedSystemObjectJoinType.NotJoined)
            {
                // CSO was already disconnected (e.g., by EvaluateMvoDeletionAsync during synchronous MVO deletion).
                // This is expected during the confirming import/sync cycle after a delete export.
                // Just delete the CSO quietly - no RPEI needed as the disconnection was already recorded.
                result.QuietCsoDeletions.Add(connectedSystemObject);
                Log.Debug("ProcessObsoleteConnectedSystemObjectAsync: CSO {CsoId} already disconnected (JoinType=NotJoined), deleting quietly",
                    connectedSystemObject.Id);
                return result;
            }

            // Not joined but has a different JoinType (e.g., Explicit) - this is a regular orphan deletion
            if (syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                SyncOutcomeBuilder.AddRootOutcome(deletionExecutionItem, ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted,
                    targetEntityId: connectedSystemObject.Id, targetEntityDescription: connectedSystemObject.NameOrId);

            result.CsoDeletions.Add((connectedSystemObject, deletionExecutionItem));
            result.ExecutionItems.Add(deletionExecutionItem);
            return result;
        }

        // CSO is joined to an MVO - check InboundOutOfScopeAction to determine behaviour
        var inboundOutOfScopeAction = syncEngine.DetermineOutOfScopeAction(connectedSystemObject, activeSyncRules);

        if (inboundOutOfScopeAction == InboundOutOfScopeAction.RemainJoined)
        {
            // Keep the join intact - just delete the CSO record but don't disconnect from MVO
            // This implements "once managed, always managed" behaviour
            Log.Information($"ProcessObsoleteConnectedSystemObjectAsync: InboundOutOfScopeAction=RemainJoined for CSO {connectedSystemObject.Id}. " +
                "CSO will be deleted but MVO join state preserved (object considered 'always managed').");

            // Note: We still delete the CSO as it's obsolete in the source system,
            // but we don't disconnect from MVO or trigger deletion rules
            if (syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                SyncOutcomeBuilder.AddRootOutcome(deletionExecutionItem, ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted,
                    targetEntityId: connectedSystemObject.Id, targetEntityDescription: connectedSystemObject.NameOrId);

            result.CsoDeletions.Add((connectedSystemObject, deletionExecutionItem));
            result.ExecutionItems.Add(deletionExecutionItem);
            return result;
        }

        // InboundOutOfScopeAction = Disconnect (default) - break the join and handle MVO deletion rules
        var mvo = connectedSystemObject.MetaverseObject;
        var connectedSystemId = connectedSystemObject.ConnectedSystemId;
        var mvoId = mvo.Id;
        var mvoDisplayName = mvo.NameOrId;

        // Single RPEI for both disconnection and deletion (one-RPEI-per-CSO rule).
        // The ObjectChangeType is Disconnected (the meaningful event); CsoDeleted is recorded
        // as an outcome on the same RPEI since the deletion is a consequence of the disconnection.
        // Reuse deletionExecutionItem (already created above) and change its type to Disconnected.
        deletionExecutionItem.ObjectChangeType = ObjectChangeType.Disconnected;

        // Query the joined Connected System ids BEFORE breaking the join so the list includes all current
        // connectors, then exclude ONE occurrence of this CSO's system id (the CSO about to be
        // disconnected). A second CSO joined from the same system must remain in the list; this mirrors
        // the previous count-minus-one logic exactly.
        var remainingConnectedSystemIds = await syncRepository.GetJoinedConnectedSystemIdsByMetaverseObjectIdAsync(mvoId);
        remainingConnectedSystemIds.Remove(connectedSystemId);

        // Snapshot the MVO's current attribute values before recall removes them.
        // The caller's snapshot store is consulted when the MVO is queued for deletion, to capture the
        // final state on the deletion change record for audit purposes.
        recordPreRecallAttributeSnapshot(mvo);

        // Evaluate the MVO deletion rule BEFORE attribute recall (#390 optimisation).
        // If the MVO will be deleted immediately, attribute recall is nugatory work;
        // the attributes, MVO update, and export evaluations would all be discarded
        // when the MVO is deleted moments later in the caller's deletion flush.
        var (mvoDeletionDecision, mvoDeletionPolicySnapshotJson) = await processMvoDeletionRuleAsync(mvo, connectedSystemId, remainingConnectedSystemIds);
        var mvoDeletionFate = mvoDeletionDecision.Fate;
        result.MvoDeletionDecision = mvoDeletionDecision;
        result.MvoDeletionPolicySnapshotJson = mvoDeletionPolicySnapshotJson;

        // Attach the decision-time policy snapshot (#119) to the outcome-bearing execution item, so the
        // decision stays explainable after the deletion configuration changes. Recorded for triggered
        // decisions AND evaluated-but-not-triggered ones (a listed source disconnected but mode semantics
        // held); independent of the sync outcome tracking level, like the other RPEI audit columns.
        if (mvoDeletionPolicySnapshotJson != null)
            deletionExecutionItem.DeletionPolicySnapshotJson = mvoDeletionPolicySnapshotJson;

        // Recall the obsoleting system's contributed attributes (where the object type opts in), re-electing a
        // surviving contributor where one exists. An attribute with another contributor is always handed to the
        // survivor (a safe change-of-value, not a clear); the question the freeze answers is what happens to the
        // values with NO surviving contributor, and the rule is: recall them only when a remaining joined system is
        // still a contributing source for the object's type (#1570). A source remaining means the object is actively
        // managed and the departed system's leftovers are pure staleness, which nothing would otherwise ever revisit
        // (the stranding defect). The freeze holds in the two situations where an immediate recall does damage:
        // a deletion is pending (scheduled by this disconnection or an earlier one), where recall would churn target
        // systems ahead of a deletion that removes everything anyway and would gut an object whose source may return
        // within the grace window; and no import source remains (only provisioned targets hold the join), where the
        // frozen values are the target account's last known state and recalling them would blank live accounts and
        // feed expression-based mappings such as a Distinguished Name with nothing. Recall is still skipped entirely
        // when the MVO will be deleted immediately, since the work would be discarded when the MVO is deleted moments
        // later (#390).
        var mvoDeletionPending = mvoDeletionFate == MvoDeletionFate.DeletionScheduled || mvo.DeletionEligibleDate != null;
        var skipRecallForImmediateDeletion = mvoDeletionFate == MvoDeletionFate.DeletedImmediately;
        var recallClearedAttributeCount = 0;
        var preservedNoSourceAttributeCount = 0;
        if (connectedSystemObject.Type.RemoveContributedAttributesOnObsoletion && !skipRecallForImmediateDeletion)
        {
            // Find all MVO attribute values contributed by this Connected System and mark them for removal
            var contributedAttributes = mvo.AttributeValues
                .Where(av => av.ContributedBySystemId == connectedSystemId)
                .ToList();

            foreach (var attributeValue in contributedAttributes)
            {
                mvo.PendingAttributeValueRemovals.Add(attributeValue);
                Log.Verbose($"ProcessObsoleteConnectedSystemObjectAsync: Marking attribute '{attributeValue.Attribute?.Name}' for removal from MVO {mvo.Id}.");
            }

            // Next-contributor recall fallback (#91): before clearing, re-elect any still-joined lower-priority
            // contributor for the recalled attributes so an authoritative source leaving hands the attribute to the
            // next source rather than blanking it. With the leaver's values now marked for removal, re-flowing the
            // surviving CSOs through the normal attribute-flow gate elects the highest-priority survivor; survivors
            // for attributes with no other contributor add nothing, so those attributes are still cleared.
            // A no-op when attribute priority is inactive or the MVO type is unavailable.
            if (priorityContext != null && mvo.Type != null)
            {
                await ContributorReElectionService.ReElectSurvivingContributorsAsync(
                    mvo,
                    contributedAttributes,
                    recallScope,
                    priorityContext,
                    syncEngine,
                    syncRepository,
                    isCsoInScopeForImportRule,
                    objectTypes,
                    expressionEvaluator);
            }

            // The no-source preservation applies only to disappearances, never to a deliberate deletion of a
            // Synchronisation Rule or Connected System: there the administrator explicitly ordered the
            // withdrawal (its consequences were surfaced before confirming), and the deprovisioning run's
            // residue pass would sweep preserved values by provenance moments later anyway.
            var noImportSourceRemains = !recallScope.IsDeliberateWithdrawal
                && mvo.Type != null
                && !await remainingImportSourceEvaluator.AnyImportSourceRemainsAsync(remainingConnectedSystemIds, mvo.Type.Id);
            if (mvoDeletionPending || noImportSourceRemains)
            {
                // Freeze: an attribute with no surviving contributor is preserved, not cleared, either until the
                // grace window resolves (pending deletion) or as the object's last known state (no import source
                // remains). Re-elected attributes are still replaced (their leaver value stays marked for
                // removal); only the leaver's non-re-elected values are unmarked.
                var reElectedDuringFreeze = mvo.PendingAttributeValueAdditions.Select(a => a.AttributeId).ToHashSet();
                var frozenValues = contributedAttributes.Where(av => !reElectedDuringFreeze.Contains(av.AttributeId)).ToList();
                foreach (var frozen in frozenValues)
                    mvo.PendingAttributeValueRemovals.Remove(frozen);

                // A pending deletion already explains itself via the MvoDeletionScheduled outcome below; the
                // no-source preservation is otherwise silent, so it gets its own outcome (#1570).
                if (!mvoDeletionPending)
                {
                    preservedNoSourceAttributeCount = frozenValues.Count;
                    result.PreservedNoSourceAttributeCount = preservedNoSourceAttributeCount;
                }
            }

            // Apply attribute changes and queue the MVO for export evaluation and persistence.
            // The caller's ordinary Metaverse Object change processing is skipped for obsolete CSOs (it's
            // guarded by Status != Obsolete), so we must handle this here to ensure target systems are
            // notified of the recalled (and any re-elected) attributes via Pending Exports.
            if (mvo.PendingAttributeValueRemovals.Count > 0 || mvo.PendingAttributeValueAdditions.Count > 0)
            {
                // Capture pending changes BEFORE applying (which clears the pending lists), using the same
                // construction as the normal Attribute Flow path: additions
                // FIRST in changedAttributes, and every removal in removedAttributes. Both orderings matter to
                // export evaluation (CreateAttributeValueChanges): a single-valued attribute exports its FIRST
                // matching changed value, so the re-elected survivor's addition must precede the leaver's removal
                // or the target would be staged with the stale value (then dropped by no-net-change detection,
                // leaving the target stale forever); and a removal only null-clears (single-valued) or stages a
                // Remove (multi-valued) when present in removedAttributes, so a re-elected attribute exports as a
                // change-of-value (the addition wins) while a genuinely cleared one exports as a clear.
                var additions = mvo.PendingAttributeValueAdditions.ToList();
                var removals = mvo.PendingAttributeValueRemovals.ToList();
                var changedAttributes = additions.Concat(removals).ToList();
                var removedAttributes = removals.ToHashSet();

                // Tally the attributes genuinely cleared (recalled with no surviving contributor re-elected and no
                // other value remaining) for the NoContributor observability outcome (#91), built further below.
                recallClearedAttributeCount = ContributorReElectionService.GetClearedAttributeIds(mvo, additions, removals).Count;
                result.RecallClearedAttributeCount = recallClearedAttributeCount;

                // Track attribute changes on the RPEI (these are part of the disconnection)
                deletionExecutionItem.AttributeFlowCount = changedAttributes.Count;

                // Capture MVO changes for change tracking; enables the RPEI detail page to show the recalled (and
                // re-elected) attribute values in the causality tree.
                result.MvoAttributeChange = (mvo, additions, removals, ObjectChangeType.Disconnected, deletionExecutionItem);

                Log.Information("ProcessObsoleteConnectedSystemObjectAsync: Applying {RemovalCount} attribute removal(s) and {AdditionCount} re-elected value(s) to MVO {MvoId} and queueing for export evaluation",
                    removals.Count, additions.Count, mvo.Id);

                syncEngine.ApplyPendingAttributeChanges(mvo);

                // Queue for batch persistence (MVO attributes have changed)
                result.MvoToUpdate = mvo;

                // Queue for export evaluation so target systems receive Pending Exports for the recalled (and any
                // re-elected) attribute values.
                result.ExportEvaluation = (mvo, changedAttributes, removedAttributes);
            }
        }
        else if (skipRecallForImmediateDeletion)
        {
            Log.Debug("ProcessObsoleteConnectedSystemObjectAsync: Skipping attribute recall for CSO {CsoId} " +
                "because MVO {MvoId} will be deleted immediately (#390 optimisation).",
                connectedSystemObject.Id, mvo.Id);
        }

        // Break the CSO-MVO join
        mvo.ConnectedSystemObjects.Remove(connectedSystemObject);
        connectedSystemObject.MetaverseObject = null;
        connectedSystemObject.MetaverseObjectId = null;
        connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.NotJoined;
        connectedSystemObject.DateJoined = null;
        Log.Verbose($"ProcessObsoleteConnectedSystemObjectAsync: Broke join between CSO {connectedSystemObject.Id} and MVO {mvoId}.");

        // Report the disconnection so the caller can account for it before it is flushed to the database
        // (e.g. so a join attempt in the same page does not see a stale join count).
        result.DisconnectedMetaverseObjectId = mvoId;

        // Queue the CSO for batch deletion (deletion will happen at end of page processing).
        // The same RPEI is used for both the disconnection record and the deletion tracking.
        result.CsoDeletions.Add((connectedSystemObject, deletionExecutionItem));

        // Build sync outcomes: Disconnected as root, CsoDeleted as child (causal chain).
        // The disconnection is the primary event; CSO deletion is a consequential outcome.
        if (syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
        {
            var disconnectedRoot = SyncOutcomeBuilder.AddRootOutcome(deletionExecutionItem,
                ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected,
                targetEntityId: mvoId,
                targetEntityDescription: mvoDisplayName,
                detailCount: deletionExecutionItem.AttributeFlowCount);

            // In Detailed mode, add AttributeFlow child under Disconnected when attributes were recalled.
            if (syncOutcomeTrackingLevel == ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed
                && deletionExecutionItem.AttributeFlowCount is > 0)
            {
                SyncOutcomeBuilder.AddChildOutcome(deletionExecutionItem, disconnectedRoot,
                    ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                    detailCount: deletionExecutionItem.AttributeFlowCount);
            }

            // In Detailed mode, surface recalled attributes with no surviving contributor (#91): genuinely cleared
            // (not re-elected, not frozen for a pending deletion's grace window), so the blank is an event an admin
            // may act on.
            if (syncOutcomeTrackingLevel == ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed
                && recallClearedAttributeCount > 0)
            {
                SyncOutcomeBuilder.AddChildOutcome(deletionExecutionItem, disconnectedRoot,
                    ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor,
                    targetEntityDescription: mvoDisplayName,
                    detailCount: recallClearedAttributeCount);
            }

            // In Detailed mode, surface values preserved because no import source remains (#1570): without this
            // outcome the preservation is silent, and "why does this object still have values" has no answer in
            // the causality view.
            if (syncOutcomeTrackingLevel == ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed
                && preservedNoSourceAttributeCount > 0)
            {
                SyncOutcomeBuilder.AddChildOutcome(deletionExecutionItem, disconnectedRoot,
                    ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved,
                    targetEntityDescription: mvoDisplayName,
                    detailCount: preservedNoSourceAttributeCount,
                    detailMessage: "No remaining Connected System carries an enabled import Synchronisation Rule for this " +
                        "Metaverse Object's type, so the disconnecting system's values were preserved as last known state.");
            }

            // The id is captured here, before the record is deleted: ActivityRunProfileExecutionItems'
            // ConnectedSystemObjectId is a foreign key and is nulled with the object, so this outcome is
            // the only durable statement of which record the run deleted, and the only way to reach its
            // deletion record afterwards.
            SyncOutcomeBuilder.AddChildOutcome(deletionExecutionItem, disconnectedRoot,
                ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted,
                targetEntityId: connectedSystemObject.Id, targetEntityDescription: connectedSystemObject.NameOrId);

            // Add MVO deletion fate outcome when the deletion rule was triggered. The outcome carries
            // the deleted Metaverse Object's id and display name snapshot (captured before deletion)
            // plus the Deletion Rule reason and any grace period in the detail message (#1086).
            if (mvoDeletionFate == MvoDeletionFate.DeletedImmediately)
            {
                SyncOutcomeBuilder.AddChildOutcome(deletionExecutionItem, disconnectedRoot,
                    ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
                    targetEntityId: mvoId,
                    targetEntityDescription: mvoDisplayName,
                    detailMessage: BuildMvoDeletionDetailMessage(mvoDeletionFate, mvoDeletionDecision.Reason, null, null));
            }
            else if (mvoDeletionFate == MvoDeletionFate.DeletionScheduled)
            {
                var gracePeriod = mvoDeletionDecision.GracePeriod ?? mvo.Type?.DeletionGracePeriod;

                SyncOutcomeBuilder.AddChildOutcome(deletionExecutionItem, disconnectedRoot,
                    ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled,
                    targetEntityId: mvoId,
                    targetEntityDescription: mvoDisplayName,
                    detailMessage: BuildMvoDeletionDetailMessage(mvoDeletionFate, mvoDeletionDecision.Reason, gracePeriod, mvo.DeletionEligibleDate));
            }
        }

        // Return single RPEI with both Disconnected and CsoDeleted outcomes
        result.ExecutionItems.Add(deletionExecutionItem);
        return result;
    }

    /// <summary>
    /// Builds the detail message for an MvoDeleted or MvoDeletionScheduled outcome node: the
    /// Metaverse Object Deletion Rule reason (when known), plus the grace period and the resulting due
    /// date for scheduled deletions, e.g. "Deletion Rule: last connector disconnected. Grace period:
    /// 7 days. Eligible for deletion: 10 Aug 2026 08:00:57 UTC" (#1086, due date added under #119).
    /// Returns null when no part is available.
    /// </summary>
    /// <remarks>
    /// NOTE FOR THE CAUSALITY VIEW REDESIGN (#1087): the due date is a deliberate capability, added
    /// because "scheduled for deletion" without a date makes the reader derive it from the disconnection
    /// time and the grace period. Carry it into the new views; the structured value is also available on
    /// the decision-time policy snapshot (<see cref="MvoDeletionPolicySnapshot.DeletionEligibleDate"/>),
    /// which is the better source for a view that formats dates itself.
    /// </remarks>
    public static string? BuildMvoDeletionDetailMessage(MvoDeletionFate fate, string? reason, TimeSpan? gracePeriod, DateTime? deletionEligibleDate)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(reason))
            parts.Add($"Deletion Rule: {reason}");
        if (fate == MvoDeletionFate.DeletionScheduled && gracePeriod.HasValue)
            parts.Add($"Grace period: {FormatGracePeriod(gracePeriod.Value)}");
        if (fate == MvoDeletionFate.DeletionScheduled && deletionEligibleDate.HasValue)
        {
            // Stored text cannot be localised when it is later rendered, so state the zone explicitly.
            parts.Add($"Eligible for deletion: {deletionEligibleDate.Value:dd MMM yyyy HH:mm:ss} UTC");
        }
        return parts.Count > 0 ? string.Join(". ", parts) : null;
    }

    /// <summary>
    /// Formats a grace period TimeSpan into a human-readable string for outcome detail messages.
    /// </summary>
    private static string FormatGracePeriod(TimeSpan period)
    {
        var parts = new List<string>();
        if (period.Days > 0) parts.Add($"{period.Days} day{(period.Days != 1 ? "s" : "")}");
        if (period.Hours > 0) parts.Add($"{period.Hours} hour{(period.Hours != 1 ? "s" : "")}");
        if (period.Minutes > 0) parts.Add($"{period.Minutes} minute{(period.Minutes != 1 ? "s" : "")}");
        return parts.Count > 0 ? string.Join(", ", parts) : "0";
    }

    /// <summary>
    /// Builds the detail message for an <see cref="ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled"/>
    /// outcome node: which system's rejoin cancelled the scheduled deletion, when the deletion had been due,
    /// which system's earlier disconnection had triggered it, and the Deletion Rule in force at the time
    /// (#1620), e.g. "Scheduled deletion (due 8 September 2026 UTC, triggered by HR CSV Source disconnecting)
    /// cancelled: HR CSV Source rejoined; rule When Authoritative Source Disconnected, mode Specific Sources
    /// Disconnect."
    /// </summary>
    /// <param name="rejoiningSystemName">The Connected System whose Connected System Object rejoined the Metaverse Object.</param>
    /// <param name="triggeringSystemName">The Connected System whose disconnection originally triggered the scheduled deletion, when recorded (null for rows marked before #119).</param>
    /// <param name="deletionEligibleDate">When the cancelled deletion had been due, captured before the marker was cleared.</param>
    /// <param name="deletionRule">The Metaverse Object Type's Deletion Rule at decision time, when the Type is known.</param>
    /// <param name="triggerMode">The Deletion Trigger Mode at decision time; only meaningful (and only rendered) for <see cref="MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected"/>.</param>
    public static string BuildMvoDeletionCancelledDetailMessage(
        string rejoiningSystemName,
        string? triggeringSystemName,
        DateTime? deletionEligibleDate,
        MetaverseObjectDeletionRule? deletionRule,
        AuthoritativeSourceTriggerMode? triggerMode)
    {
        var dueClause = deletionEligibleDate.HasValue
            ? $"due {deletionEligibleDate.Value:d MMMM yyyy} UTC"
            : "due immediately";
        var triggerClause = !string.IsNullOrWhiteSpace(triggeringSystemName)
            ? $", triggered by {triggeringSystemName} disconnecting"
            : string.Empty;

        var ruleClause = string.Empty;
        if (deletionRule.HasValue)
        {
            var ruleLabel = FormatDeletionRule(deletionRule.Value);
            ruleClause = deletionRule.Value == MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected && triggerMode.HasValue
                ? $"; rule {ruleLabel}, mode {FormatTriggerMode(triggerMode.Value)}"
                : $"; rule {ruleLabel}";
        }

        return $"Scheduled deletion ({dueClause}{triggerClause}) cancelled: {rejoiningSystemName} rejoined{ruleClause}.";
    }

    /// <summary>
    /// Formats a Deletion Rule for the cancellation detail message, matching the labels used on the
    /// Metaverse Object Type edit page.
    /// </summary>
    private static string FormatDeletionRule(MetaverseObjectDeletionRule rule) => rule switch
    {
        MetaverseObjectDeletionRule.Manual => "Manual",
        MetaverseObjectDeletionRule.WhenLastConnectorDisconnected => "When Last Connector Disconnected",
        MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected => "When Authoritative Source Disconnected",
        _ => rule.ToString()
    };

    /// <summary>
    /// Formats a Deletion Trigger Mode for the cancellation detail message.
    /// </summary>
    private static string FormatTriggerMode(AuthoritativeSourceTriggerMode mode) => mode switch
    {
        AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect => "Specific Sources Disconnect",
        AuthoritativeSourceTriggerMode.AllSourcesDisconnect => "All Sources Disconnect",
        _ => mode.ToString()
    };
}

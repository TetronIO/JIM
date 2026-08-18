// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Services;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;

namespace JIM.Application.Interfaces;

/// <summary>
/// Pure domain engine for synchronisation decisions.
/// All methods are synchronous — no I/O, no async, no database access.
/// Takes plain objects in, returns decision records out.
/// The orchestrator (processor) is responsible for loading data, calling the engine,
/// and persisting the decisions.
///
/// Note: Two pure methods intentionally remain on ISyncServer rather than here:
/// - IsCsoInScopeForImportRule — delegates to ScopingEvaluationServer (has its own state/dependencies)
/// - EvaluateDrift — delegates to DriftDetectionService (has its own state/dependencies)
/// Both are synchronous and pure, but moving them here would require SyncEngine to take
/// constructor dependencies, breaking the stateless/zero-dependency design. The orchestrator
/// calls them via ISyncServer directly.
/// </summary>
public interface ISyncEngine
{
    /// <summary>
    /// Evaluates whether a new MVO should be projected for a CSO.
    /// Called when the CSO did not join an existing MVO.
    /// </summary>
    /// <param name="cso">The CSO to evaluate for projection.</param>
    /// <param name="activeSyncRules">Active Synchronisation Rules for the Connected System.</param>
    /// <returns>A decision indicating whether to project and the MVO type to use.</returns>
    ProjectionDecision EvaluateProjection(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> activeSyncRules);

    /// <summary>
    /// Flows inbound attribute values from a CSO to its joined MVO using a Synchronisation Rule's Attribute Flow mappings.
    /// Mutates the MVO's PendingAttributeValueAdditions and PendingAttributeValueRemovals collections.
    /// Returns any warnings generated during Attribute Flow (e.g. multi-valued to single-valued truncation).
    /// </summary>
    /// <param name="cso">The source CSO (must have MetaverseObject set).</param>
    /// <param name="syncRule">The Synchronisation Rule defining Attribute Flow mappings.</param>
    /// <param name="objectTypes">CSO object types for attribute lookup.</param>
    /// <param name="expressionEvaluator">Expression evaluator for expression-based mappings.</param>
    /// <param name="skipReferenceAttributes">If true, skip reference attributes (deferred to second pass).</param>
    /// <param name="onlyReferenceAttributes">If true, process only reference attributes.</param>
    /// <param name="isFinalReferencePass">If true, this is the final cross-page resolution pass.</param>
    /// <param name="priorityContext">
    /// Optional per-run attribute priority cache (#91). When supplied, multi-contributor attributes are resolved by
    /// the inline incumbent-comparison gate so a lower-priority contribution does not overwrite a higher-priority
    /// one; when null (the default), every mapping flows as before (last-writer-wins).
    /// </param>
    /// <returns>A list of errors raised during Attribute Flow (for example, a multi-valued source flowing
    /// to a single-valued target), empty if none.</returns>
    List<AttributeFlowError> FlowInboundAttributes(
        ConnectedSystemObject cso,
        SyncRule syncRule,
        IReadOnlyList<ConnectedSystemObjectType> objectTypes,
        IExpressionEvaluator? expressionEvaluator = null,
        bool skipReferenceAttributes = false,
        bool onlyReferenceAttributes = false,
        bool isFinalReferencePass = false,
        AttributePriorityContext? priorityContext = null);

    /// <summary>
    /// Evaluates whether Pending Exports have been confirmed by a CSO's current attribute state.
    /// Confirmed exports are marked for deletion; partially confirmed exports are updated.
    /// </summary>
    /// <param name="cso">The CSO whose current attributes to check against Pending Exports.</param>
    /// <param name="pendingExportsByCsoId">Pre-loaded Pending Exports keyed by CSO ID.</param>
    /// <returns>A result indicating which Pending Exports to delete or update.</returns>
    PendingExportConfirmationResult EvaluatePendingExportConfirmation(
        ConnectedSystemObject cso,
        Dictionary<Guid, List<PendingExport>>? pendingExportsByCsoId);

    /// <summary>
    /// Evaluates the MVO deletion rule after a CSO is disconnected.
    /// Pure decision only — the orchestrator is responsible for persisting
    /// (queuing immediate deletion or setting LastConnectorDisconnectedDate).
    /// </summary>
    /// <param name="mvo">The MVO to evaluate.</param>
    /// <param name="disconnectingSystemId">The ID of the Connected System whose CSO was disconnected.</param>
    /// <param name="remainingConnectedSystemIds">The Connected System ID of each CSO still joined to the
    /// MVO after disconnection: one entry per CSO, so a system with multiple joined CSOs appears once per
    /// CSO. The remaining CSO count is derived from this collection's size.</param>
    /// <param name="disconnectingSystemName">
    /// The name of the disconnecting Connected System, used to make the human-readable deletion
    /// reason name the system rather than a bare id. When null, the reason falls back to the id.
    /// </param>
    /// <returns>A decision indicating whether/how to delete the MVO.</returns>
    MvoDeletionDecision EvaluateMvoDeletionRule(
        MetaverseObject mvo,
        int disconnectingSystemId,
        IReadOnlyCollection<int> remainingConnectedSystemIds,
        string? disconnectingSystemName = null);

    /// <summary>
    /// Determines whether a system rejoining an MVO during its deletion grace period should cancel the
    /// scheduled deletion. Pure decision only; the orchestrator clears the deletion markers.
    /// The answer is mode-aware (#119): under WhenLastConnectorDisconnected any rejoin cancels; under
    /// WhenAuthoritativeSourceDisconnected in Specific sources mode only the recorded triggering system's
    /// rejoin cancels, while in All sources mode any listed source's rejoin cancels. MVOs marked before
    /// the triggering system was recorded (null <see cref="MetaverseObject.DeletionTriggeredBySystemId"/>)
    /// fall back to cancel-on-any-rejoin.
    /// </summary>
    /// <param name="mvo">The MVO with a scheduled deletion. Its Type must be loaded.</param>
    /// <param name="rejoiningSystemId">The ID of the Connected System whose CSO has rejoined.</param>
    /// <returns>True when the scheduled deletion should be cancelled.</returns>
    bool ShouldCancelScheduledDeletion(MetaverseObject mvo, int rejoiningSystemId);

    /// <summary>
    /// Applies pending attribute value changes to a Metaverse Object.
    /// Moves values from PendingAttributeValueAdditions to AttributeValues
    /// and removes values listed in PendingAttributeValueRemovals.
    /// </summary>
    /// <param name="mvo">The MVO to apply pending changes to.</param>
    void ApplyPendingAttributeChanges(MetaverseObject mvo);

    /// <summary>
    /// Determines the InboundOutOfScopeAction for a CSO based on applicable import Synchronisation Rules.
    /// </summary>
    /// <param name="cso">The CSO to evaluate.</param>
    /// <param name="activeSyncRules">Active Synchronisation Rules for the Connected System.</param>
    /// <returns>The out-of-scope action from the first matching import Synchronisation Rule, or Disconnect as default.</returns>
    InboundOutOfScopeAction DetermineOutOfScopeAction(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> activeSyncRules);

    /// <summary>
    /// Reconciles a Connected System Object against a pre-loaded Pending Export.
    /// Compares imported CSO attribute values against Pending Export assertions to confirm,
    /// mark for retry, or mark as failed. This method does NOT perform any database operations —
    /// the caller is responsible for persistence.
    /// </summary>
    /// <param name="connectedSystemObject">The CSO that was just imported/updated.</param>
    /// <param name="pendingExport">The pre-loaded Pending Export for this CSO (or null if none).</param>
    /// <param name="result">The result object to populate with reconciliation outcomes.</param>
    void ReconcileCsoAgainstPendingExport(
        ConnectedSystemObject connectedSystemObject,
        PendingExport? pendingExport,
        PendingExportReconciliationResult result);

    /// <summary>
    /// Determines if an attribute change has been confirmed by comparing the exported value
    /// against the imported CSO attribute value. Handles all attribute data types comprehensively.
    /// </summary>
    /// <param name="cso">The CSO whose current attributes to check.</param>
    /// <param name="attrChange">The Pending Export attribute change to verify.</param>
    /// <returns>True if the attribute change has been confirmed by the CSO's current state.</returns>
    bool IsAttributeChangeConfirmed(
        ConnectedSystemObject cso,
        PendingExportAttributeValueChange attrChange);

    /// <summary>
    /// Identifies Pending Export pairs (CREATE+DELETE or UPDATE+DELETE) targeting the same CSO
    /// that cancel each other out and should not be exported.
    /// Only reconciles pairs where both exports have Pending status — already-exported
    /// operations are left untouched since the object may exist in the target system.
    /// </summary>
    /// <param name="pendingExports">All Pending Exports to scan for reconcilable pairs.</param>
    /// <returns>Result describing which exports should be cancelled.</returns>
    PreExportReconciliationResult ReconcileCreateDeletePairs(IReadOnlyList<PendingExportSummary> pendingExports);

    /// <summary>
    /// Decides whether deleting a Metaverse Object stages a Delete export for one of its joined CSOs (#655:
    /// the matching export Synchronisation Rules' OutboundDeprovisionAction drives the verdict, Delete wins a
    /// conflict, and the one-Pending-Export-per-CSO collision policy chooses reuse, replace or create). The
    /// disconnect itself is unconditional and is the orchestrator's to apply.
    /// </summary>
    /// <param name="cso">The joined CSO, with attribute values loaded so the secondary external identifier can be captured.</param>
    /// <param name="metaverseObjectTypeId">The deleted Metaverse Object's type id, or null when it carries none.</param>
    /// <param name="exportRulesByMetaverseObjectTypeId">Enabled export Synchronisation Rules grouped by Metaverse Object Type id.</param>
    /// <param name="existingPendingExport">The Pending Export already attached to the CSO, if any, from the caller's pre-read or working set.</param>
    MvoDeletionExportDecision DecideMvoDeletionExport(
        ConnectedSystemObject cso,
        int? metaverseObjectTypeId,
        IReadOnlyDictionary<int, List<SyncRule>> exportRulesByMetaverseObjectTypeId,
        PendingExport? existingPendingExport);

    /// <summary>
    /// Decides what an export Synchronisation Rule's OutboundDeprovisionAction means for a CSO that has fallen
    /// out of the rule's scope: disconnect, stage a Delete export (with the one-Pending-Export-per-CSO collision
    /// policy choosing reuse, replace or create), or nothing at all for an unrecognised action, which is
    /// deliberately never defaulted to disconnect.
    /// </summary>
    /// <param name="exportRule">The export Synchronisation Rule the CSO fell out of scope for.</param>
    /// <param name="existingPendingExport">The Pending Export already attached to the CSO, if any, from the run's working set or the database.</param>
    OutOfScopeDeprovisioningDecision DecideOutOfScopeDeprovisioning(
        SyncRule exportRule,
        PendingExport? existingPendingExport);

    /// <summary>
    /// Decides whether a disconnect that removed a Metaverse Object's last connector should stamp
    /// LastConnectorDisconnectedDate, starting the deletion grace period. Ask AFTER removing the disconnected
    /// CSO from the object's collection. Only a Projected object whose Type's Deletion Rule is
    /// WhenLastConnectorDisconnected qualifies.
    /// </summary>
    /// <param name="mvo">The Metaverse Object the CSO was just disconnected from.</param>
    bool ShouldMarkLastConnectorDisconnected(MetaverseObject mvo);

    /// <summary>
    /// Decides what kind of export, if any, a Metaverse Object change stages against one export
    /// Synchronisation Rule's target: nothing (a reported Object Type conflict, provisioning declined, a
    /// reference recall against no exportable presence, or changes irrelevant to a pending provisioning), a
    /// Create (provision new, or restage the pending provisioning CSO's Create), or an Update. The
    /// orchestrator interposes export matching before acting on a ProvisionNewCso verdict.
    /// </summary>
    /// <param name="mvo">The Metaverse Object whose change is being evaluated.</param>
    /// <param name="exportRule">The export Synchronisation Rule under evaluation.</param>
    /// <param name="existingCso">The Metaverse Object's CSO in the rule's Connected System, if any.</param>
    /// <param name="changedAttributes">The changed attributes, for the pending provisioning relevance check.</param>
    /// <param name="recallSemantics">True when evaluating a reference recall (#1003), which must never provision.</param>
    OutboundStagingDecision DecideOutboundStaging(
        MetaverseObject mvo,
        SyncRule exportRule,
        ConnectedSystemObject? existingCso,
        List<MetaverseObjectAttributeValue> changedAttributes,
        bool recallSemantics);

    /// <summary>
    /// Merges newly evaluated attribute changes into a Pending Export this run has already staged for the
    /// same CSO, mutating the staged export in place. Export evaluation wins a merge-key collision; an
    /// incoming whole-attribute replace supersedes every staged change for that attribute first (#1199).
    /// Pure in-memory mutation: nothing is persisted.
    /// </summary>
    /// <param name="stagedPendingExport">The Pending Export already staged for the CSO, mutated in place.</param>
    /// <param name="newChanges">The newly evaluated attribute changes to merge in.</param>
    PendingExportMergeResult MergeAttributeChangesIntoPendingExport(
        PendingExport stagedPendingExport,
        List<PendingExportAttributeValueChange> newChanges);

    /// <summary>
    /// Creates the Pending Export attribute value changes an export Synchronisation Rule's Attribute Flow
    /// mappings produce for a Metaverse Object change (the outbound delta computation, #288 extraction):
    /// Create operations carry all mapped attributes, Update operations only what changed, with optional
    /// no-net-change detection against the target CSO's cached attribute values.
    /// </summary>
    /// <param name="mvo">The Metaverse Object to create changes for.</param>
    /// <param name="exportRule">The export rule containing attribute mappings.</param>
    /// <param name="changedAttributes">The MVO attributes that changed.</param>
    /// <param name="changeType">Whether this is a Create or Update operation.</param>
    /// <param name="existingCso">The existing CSO (for Update operations only) to compare values against.</param>
    /// <param name="csoAttributeCache">Optional cache of target CSO attribute values for no-net-change detection.</param>
    /// <param name="csoAlreadyCurrentCount">Output: count of attributes skipped because the CSO already has the value.</param>
    /// <param name="expressionEvaluator">The evaluator for expression-based mappings; a caller that passes
    /// none gets a per-call default.</param>
    /// <param name="removedAttributes">Optional set of attribute values removed from the MVO (multi-valued
    /// removals become Remove changes; single-valued removals become null-clearing Updates).</param>
    /// <param name="mvAttributeDictionary">Optional pre-built MVO attribute dictionary for expression evaluation.</param>
    /// <param name="preResolvedReferenceValues">Optional pre-resolved reference values (reference recall, #908).</param>
    /// <param name="flowErrors">Optional collector for Attribute Flow errors (multi-valued to single-valued truncation).</param>
    List<PendingExportAttributeValueChange> ComputeAttributeValueChanges(
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
        List<AttributeFlowError>? flowErrors = null);

    /// <summary>
    /// Decides the removal change a reference recall stages for one matched target row (#908/#1003): a
    /// multi-valued source synthesises a Remove carrying the resolved value, a single-valued source a
    /// null-clearing Update, and a multi-valued removal with no resolvable value returns null (nothing can be
    /// staged; the orchestrator counts it as dropped).
    /// </summary>
    /// <param name="flow">The direct reference flow whose target attribute still holds the deleted value.</param>
    /// <param name="resolvedRemovalValue">The deleted object's resolved value in the flow's target system, or null.</param>
    PendingExportAttributeValueChange? DecideRecallRemovalChange(
        ReferenceRecallDirectFlow flow,
        string? resolvedRemovalValue);

    /// <summary>
    /// Decides how recall changes combine with the Pending Export already attached to the target CSO (#1003):
    /// an existing Delete wins, an existing Create is protected, and an existing Update merges into the recall
    /// changes (recall wins a merge-key collision; surviving changes cloned with fresh ids; changes whose
    /// unresolved reference is a deleted object purged). Pure in-memory mutation of the dictionary.
    /// </summary>
    /// <param name="recallChangesByMergeKey">The recall changes staged for the CSO, keyed by merge key; mutated in place.</param>
    /// <param name="existingPendingExport">The Pending Export already attached to the CSO, if any.</param>
    /// <param name="deletedMvoIds">The Metaverse Objects deleted in this operation.</param>
    RecallPendingExportMergeResult MergeRecallChangesWithExistingPendingExport(
        Dictionary<string, PendingExportAttributeValueChange> recallChangesByMergeKey,
        PendingExport? existingPendingExport,
        HashSet<Guid> deletedMvoIds);

    /// <summary>
    /// Removes from a Pending Export every attribute value change whose unresolved reference is one of the
    /// deleted Metaverse Objects: the removal is a no-op in that target and could never resolve at export
    /// time. Pure in-memory mutation; returns how many changes were removed.
    /// </summary>
    /// <param name="pendingExport">The Pending Export to purge, mutated in place.</param>
    /// <param name="deletedMvoIds">The Metaverse Objects deleted in this operation.</param>
    int PurgeChangesReferencingDeletedObjects(PendingExport pendingExport, HashSet<Guid> deletedMvoIds);
}

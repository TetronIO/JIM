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
/// Note: Scoping evaluation (IsCsoInScopeForImportRule) is intentionally NOT on this interface.
/// Scoping is already pure and lives in ISyncServer/ScopingEvaluationServer. The orchestrator
/// handles the I/O (loading CSO attributes) and delegates scoping to ISyncServer directly.
/// </summary>
public interface ISyncEngine
{
    /// <summary>
    /// Evaluates whether a CSO should join an existing MVO based on matching rules.
    /// The orchestrator is responsible for pre-loading the join candidate and existing join count.
    /// </summary>
    /// <param name="cso">The CSO to evaluate for joining.</param>
    /// <param name="joinCandidate">The MVO found by the matching engine, or null if no candidate.</param>
    /// <param name="existingJoinCount">Number of CSOs from this connected system already joined to the candidate MVO.</param>
    /// <returns>A decision indicating whether and how to join.</returns>
    JoinDecision EvaluateJoin(
        ConnectedSystemObject cso,
        MetaverseObject? joinCandidate,
        int existingJoinCount);

    /// <summary>
    /// Evaluates whether a new MVO should be projected for a CSO.
    /// Called when the CSO did not join an existing MVO.
    /// </summary>
    /// <param name="cso">The CSO to evaluate for projection.</param>
    /// <param name="activeSyncRules">Active sync rules for the connected system.</param>
    /// <returns>A decision indicating whether to project and the MVO type to use.</returns>
    ProjectionDecision EvaluateProjection(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> activeSyncRules);

    /// <summary>
    /// Flows inbound attribute values from a CSO to its joined MVO using a sync rule's attribute flow mappings.
    /// Mutates the MVO's PendingAttributeValueAdditions and PendingAttributeValueRemovals collections.
    /// </summary>
    /// <param name="cso">The source CSO (must have MetaverseObject set).</param>
    /// <param name="syncRule">The sync rule defining attribute flow mappings.</param>
    /// <param name="objectTypes">CSO object types for attribute lookup.</param>
    /// <param name="expressionEvaluator">Expression evaluator for expression-based mappings.</param>
    /// <param name="skipReferenceAttributes">If true, skip reference attributes (deferred to second pass).</param>
    /// <param name="onlyReferenceAttributes">If true, process only reference attributes.</param>
    /// <param name="isFinalReferencePass">If true, this is the final cross-page resolution pass.</param>
    void FlowInboundAttributes(
        ConnectedSystemObject cso,
        SyncRule syncRule,
        IReadOnlyList<ConnectedSystemObjectType> objectTypes,
        IExpressionEvaluator? expressionEvaluator = null,
        bool skipReferenceAttributes = false,
        bool onlyReferenceAttributes = false,
        bool isFinalReferencePass = false);

    /// <summary>
    /// Evaluates whether pending exports have been confirmed by a CSO's current attribute state.
    /// Confirmed exports are marked for deletion; partially confirmed exports are updated.
    /// </summary>
    /// <param name="cso">The CSO whose current attributes to check against pending exports.</param>
    /// <param name="pendingExportsByCsoId">Pre-loaded pending exports keyed by CSO ID.</param>
    /// <returns>A result indicating which pending exports to delete or update.</returns>
    PendingExportConfirmationResult EvaluatePendingExportConfirmation(
        ConnectedSystemObject cso,
        Dictionary<Guid, List<PendingExport>>? pendingExportsByCsoId);

    /// <summary>
    /// Evaluates the MVO deletion rule after a CSO is disconnected.
    /// Pure decision only — the orchestrator is responsible for persisting
    /// (queuing immediate deletion or setting LastConnectorDisconnectedDate).
    /// </summary>
    /// <param name="mvo">The MVO to evaluate.</param>
    /// <param name="disconnectingSystemId">The ID of the connected system whose CSO was disconnected.</param>
    /// <param name="remainingCsoCount">The count of CSOs still joined to the MVO after disconnection.</param>
    /// <returns>A decision indicating whether/how to delete the MVO.</returns>
    MvoDeletionDecision EvaluateMvoDeletionRule(
        MetaverseObject mvo,
        int disconnectingSystemId,
        int remainingCsoCount);

    /// <summary>
    /// Applies pending attribute value changes to a Metaverse Object.
    /// Moves values from PendingAttributeValueAdditions to AttributeValues
    /// and removes values listed in PendingAttributeValueRemovals.
    /// </summary>
    /// <param name="mvo">The MVO to apply pending changes to.</param>
    void ApplyPendingAttributeChanges(MetaverseObject mvo);

    /// <summary>
    /// Determines the InboundOutOfScopeAction for a CSO based on applicable import sync rules.
    /// </summary>
    /// <param name="cso">The CSO to evaluate.</param>
    /// <param name="activeSyncRules">Active sync rules for the connected system.</param>
    /// <returns>The out-of-scope action from the first matching import sync rule, or Disconnect as default.</returns>
    InboundOutOfScopeAction DetermineOutOfScopeAction(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> activeSyncRules);

    /// <summary>
    /// Checks if a CSO attribute value matches a pending export attribute change value.
    /// Used during export confirmation to verify whether exported changes were persisted.
    /// </summary>
    /// <param name="csoValue">The CSO's current attribute value.</param>
    /// <param name="pendingChange">The pending export attribute change to compare against.</param>
    /// <returns>True if the values match.</returns>
    bool AttributeValuesMatch(
        ConnectedSystemObjectAttributeValue csoValue,
        PendingExportAttributeValueChange pendingChange);
}

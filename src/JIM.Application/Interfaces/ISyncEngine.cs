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
    /// <param name="activeSyncRules">Active sync rules for the connected system.</param>
    /// <returns>A decision indicating whether to project and the MVO type to use.</returns>
    ProjectionDecision EvaluateProjection(
        ConnectedSystemObject cso,
        IReadOnlyList<SyncRule> activeSyncRules);

    /// <summary>
    /// Flows inbound attribute values from a CSO to its joined MVO using a sync rule's attribute flow mappings.
    /// Mutates the MVO's PendingAttributeValueAdditions and PendingAttributeValueRemovals collections.
    /// Returns any warnings generated during attribute flow (e.g. multi-valued to single-valued truncation).
    /// </summary>
    /// <param name="cso">The source CSO (must have MetaverseObject set).</param>
    /// <param name="syncRule">The sync rule defining attribute flow mappings.</param>
    /// <param name="objectTypes">CSO object types for attribute lookup.</param>
    /// <param name="expressionEvaluator">Expression evaluator for expression-based mappings.</param>
    /// <param name="skipReferenceAttributes">If true, skip reference attributes (deferred to second pass).</param>
    /// <param name="onlyReferenceAttributes">If true, process only reference attributes.</param>
    /// <param name="isFinalReferencePass">If true, this is the final cross-page resolution pass.</param>
    /// <returns>A list of warnings generated during attribute flow, empty if none.</returns>
    List<AttributeFlowWarning> FlowInboundAttributes(
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
    /// Reconciles a Connected System Object against a pre-loaded pending export.
    /// Compares imported CSO attribute values against pending export assertions to confirm,
    /// mark for retry, or mark as failed. This method does NOT perform any database operations —
    /// the caller is responsible for persistence.
    /// </summary>
    /// <param name="connectedSystemObject">The CSO that was just imported/updated.</param>
    /// <param name="pendingExport">The pre-loaded pending export for this CSO (or null if none).</param>
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
    /// <param name="attrChange">The pending export attribute change to verify.</param>
    /// <returns>True if the attribute change has been confirmed by the CSO's current state.</returns>
    bool IsAttributeChangeConfirmed(
        ConnectedSystemObject cso,
        PendingExportAttributeValueChange attrChange);

    /// <summary>
    /// Identifies pending export pairs (CREATE+DELETE or UPDATE+DELETE) targeting the same CSO
    /// that cancel each other out and should not be exported.
    /// Only reconciles pairs where both exports have Pending status — already-exported
    /// operations are left untouched since the object may exist in the target system.
    /// </summary>
    /// <param name="pendingExports">All pending exports to scan for reconcilable pairs.</param>
    /// <returns>Result describing which exports should be cancelled.</returns>
    PreExportReconciliationResult ReconcileCreateDeletePairs(IReadOnlyList<PendingExport> pendingExports);
}

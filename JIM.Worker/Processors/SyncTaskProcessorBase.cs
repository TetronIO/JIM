using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Application.Expressions;
using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Utility;
using JIM.Utilities;
using JIM.Worker.Models;
using Serilog;

namespace JIM.Worker.Processors;

/// <summary>
/// Abstract base class containing shared logic for Full Sync and Delta Sync processors.
/// Both sync types perform the same core operations (join, project, attribute flow, export evaluation)
/// but differ in which CSOs they process:
/// - Full Sync: processes ALL CSOs in the Connected System
/// - Delta Sync: processes only CSOs modified since the last sync (based on LastUpdated timestamp)
/// </summary>
public abstract class SyncTaskProcessorBase
{
    protected readonly JimApplication _jim;
    protected readonly ConnectedSystem _connectedSystem;
    protected readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
    protected readonly Activity _activity;
    protected readonly CancellationTokenSource _cancellationTokenSource;
    protected List<ConnectedSystemObjectType>? _objectTypes;
    protected Dictionary<Guid, List<JIM.Models.Transactional.PendingExport>>? _pendingExportsByCsoId;
    protected ExportEvaluationServer.ExportEvaluationCache? _exportEvaluationCache;

    // Cache for drift detection: maps (ConnectedSystemId, MvoAttributeId) to import mappings
    // Used to check if a connected system is a legitimate contributor for an attribute
    protected Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>? _importMappingCache;

    // Cache of export rules for drift detection (filtered to EnforceState = true)
    protected List<SyncRule>? _driftDetectionExportRules;

    // Aggregate no-net-change counts for the entire sync run
    // Note: Target CSO attribute values for no-net-change detection are now pre-loaded in ExportEvaluationCache
    // (built at sync start) rather than per-page, since we need target system CSO attributes not source CSO attributes.
    protected int _totalCsoAlreadyCurrentCount;

    // Batch collections for deferred MVO persistence and export evaluation
    protected readonly List<MetaverseObject> _pendingMvoCreates = [];
    protected readonly List<MetaverseObject> _pendingMvoUpdates = [];
    protected readonly List<(MetaverseObject Mvo, List<MetaverseObjectAttributeValue> ChangedAttributes, HashSet<MetaverseObjectAttributeValue>? RemovedAttributes)> _pendingExportEvaluations = [];

    // Batch collections for deferred pending export operations (avoid per-CSO database calls)
    protected readonly List<JIM.Models.Transactional.PendingExport> _pendingExportsToCreate = [];
    protected readonly List<JIM.Models.Transactional.PendingExport> _pendingExportsToDelete = [];
    protected readonly List<JIM.Models.Transactional.PendingExport> _pendingExportsToUpdate = [];

    // Batch collection for deferred provisioning CSO creation (avoid per-CSO database calls)
    protected readonly List<ConnectedSystemObject> _provisioningCsosToCreate = [];

    // Batch collection for deferred CSO deletions (avoid per-CSO database calls)
    protected readonly List<(ConnectedSystemObject Cso, ActivityRunProfileExecutionItem ExecutionItem)> _obsoleteCsosToDelete = [];

    // Batch collection for quiet CSO deletions (pre-disconnected CSOs that don't need RPEIs)
    // These are CSOs that were already disconnected during synchronous MVO deletion and just need cleanup
    protected readonly List<ConnectedSystemObject> _quietCsosToDelete = [];

    // Batch collection for deferred MVO deletions (for immediate 0-grace-period deletions)
    protected readonly List<MetaverseObject> _pendingMvoDeletions = [];

    // Batch collection for MVO change object creation (deferred to page boundary for performance)
    // Stores: (MVO, Additions, Removals, IsNew, RPEI) - captured BEFORE applying pending changes
    // IsNew indicates whether this MVO is being created (true) or updated (false), used to set ChangeType correctly
    // RPEI links MVO changes to the Activity for initiator context (User, ApiKey, etc.)
    protected readonly List<(MetaverseObject Mvo, List<MetaverseObjectAttributeValue> Additions, List<MetaverseObjectAttributeValue> Removals, bool IsNew, ActivityRunProfileExecutionItem Rpei)> _pendingMvoChanges = [];

    // Batch collection for deferred reference attribute processing.
    // Reference attributes must be processed AFTER all CSOs in the page have been processed (joined/projected)
    // because group member references may point to user CSOs that come later in the processing order.
    // By deferring reference attributes, we ensure all MVOs exist before resolving references.
    protected readonly List<(ConnectedSystemObject Cso, List<SyncRule> SyncRules)> _pendingReferenceAttributeProcessing = [];

    // Expression evaluator for expression-based sync rule mappings
    protected readonly IExpressionEvaluator _expressionEvaluator = new DynamicExpressoEvaluator();

    protected SyncTaskProcessorBase(
        JimApplication jimApplication,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile connectedSystemRunProfile,
        Activity activity,
        CancellationTokenSource cancellationTokenSource)
    {
        _jim = jimApplication;
        _connectedSystem = connectedSystem;
        _connectedSystemRunProfile = connectedSystemRunProfile;
        _activity = activity;
        _cancellationTokenSource = cancellationTokenSource;
    }

    /// <summary>
    /// Updates the Connected System's LastDeltaSyncCompletedAt timestamp to mark the sync as complete.
    /// This becomes the watermark for the next delta sync.
    /// Full sync also sets this to establish a baseline for subsequent delta syncs.
    /// </summary>
    protected async Task UpdateDeltaSyncWatermarkAsync()
    {
        using var span = Diagnostics.Sync.StartSpan("UpdateDeltaSyncWatermark");

        _connectedSystem.LastDeltaSyncCompletedAt = DateTime.UtcNow;

        // Use repository directly to avoid validation that expects RunProfiles to be loaded
        await _jim.Repository.ConnectedSystems.UpdateConnectedSystemAsync(_connectedSystem);

        Log.Information("UpdateDeltaSyncWatermarkAsync: Updated delta sync watermark to {Timestamp}",
            _connectedSystem.LastDeltaSyncCompletedAt);

        span.SetSuccess();
    }

    /// <summary>
    /// Attempts to join/project/delete/flow attributes to the Metaverse for a single Connected System Object.
    /// Records successful operations (joins, projections, attribute flow) and errors to ActivityRunProfileExecutionItems.
    /// Only creates execution items for CSOs that had actual changes to avoid unnecessary allocations.
    /// </summary>
    protected async Task ProcessConnectedSystemObjectAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        Log.Verbose($"ProcessConnectedSystemObjectAsync: Performing sync on Connected System Object: {connectedSystemObject}.");

        // Track changes to determine if we need to create an execution item.
        // Only create items for CSOs that had actual changes (optimisation to avoid allocations for unchanged objects).
        var changeResult = MetaverseObjectChangeResult.NoChanges();
        ActivityRunProfileExecutionItem? obsoleteExecutionItem = null;

        try
        {
            using (Diagnostics.Sync.StartSpan("ProcessPendingExport"))
            {
                // Note: ProcessPendingExport handles pending export confirmation, not CSO/MVO changes
                // Queues operations for batch processing at end of page (avoids per-CSO database calls)
                ProcessPendingExport(connectedSystemObject);
            }

            using (Diagnostics.Sync.StartSpan("ProcessObsoleteConnectedSystemObject"))
            {
                obsoleteExecutionItem = await ProcessObsoleteConnectedSystemObjectAsync(activeSyncRules, connectedSystemObject);
            }

            // if the CSO isn't marked as obsolete (it might just have been), look to see if we need to make any related Metaverse Object changes.
            // this requires that we have sync rules defined.
            if (activeSyncRules.Count > 0 && connectedSystemObject.Status != ConnectedSystemObjectStatus.Obsolete)
            {
                using (Diagnostics.Sync.StartSpan("ProcessMetaverseObjectChanges"))
                {
                    changeResult = await ProcessMetaverseObjectChangesAsync(activeSyncRules, connectedSystemObject);
                }
            }

            // Add execution item for obsolete CSO (created by ProcessObsoleteConnectedSystemObjectAsync)
            if (obsoleteExecutionItem != null)
            {
                _activity.RunProfileExecutionItems.Add(obsoleteExecutionItem);
            }
            // Handle execution item for successful changes (join, projection, attribute flow)
            else if (changeResult.HasChanges)
            {
                // Check if an RPEI was already created for this CSO (in ProcessMetaverseObjectChangesAsync when MVO changes were captured)
                var existingRpei = _activity.RunProfileExecutionItems.FirstOrDefault(r => r.ConnectedSystemObject?.Id == connectedSystemObject.Id);
                if (existingRpei != null)
                {
                    // Update the existing RPEI with the ObjectChangeType
                    existingRpei.ObjectChangeType = changeResult.ChangeType;
                }
                else
                {
                    // Create a new RPEI (for cases like join/project without attribute changes)
                    var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
                    runProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
                    runProfileExecutionItem.ObjectChangeType = changeResult.ChangeType;
                    _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);
                }
            }
        }
        catch (SyncJoinException joinEx)
        {
            // Create execution item for join-specific errors with proper error type
            var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
            runProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
            runProfileExecutionItem.ErrorType = joinEx.ErrorType;
            runProfileExecutionItem.ErrorMessage = joinEx.Message;
            _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);

            Log.Warning(joinEx, "ProcessConnectedSystemObjectAsync: Join error for {Cso}: {Message}",
                connectedSystemObject, joinEx.Message);
        }
        catch (Exception e)
        {
            // Create execution item for unhandled error tracking
            var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
            runProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
            runProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError;
            runProfileExecutionItem.ErrorMessage = e.Message;
            runProfileExecutionItem.ErrorStackTrace = e.StackTrace;
            _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);

            Log.Error(e, "ProcessConnectedSystemObjectAsync: Unhandled {RunProfile} sync error whilst processing {Cso}.",
                _connectedSystemRunProfile, connectedSystemObject);
        }
    }

    /// <summary>
    /// See if a Pending Export Object for a Connected System Object can be invalidated and deleted.
    /// This would occur when the Pending Export changes are visible on the Connected System Object after a confirming import.
    /// Queues pending export operations for batch processing at the end of page processing (avoids per-CSO database calls).
    /// </summary>
    protected void ProcessPendingExport(ConnectedSystemObject connectedSystemObject)
    {
        Log.Verbose($"ProcessPendingExport: Executing for: {connectedSystemObject}.");

        // use pre-loaded pending exports dictionary for O(1) lookup instead of O(n) database query
        if (_pendingExportsByCsoId == null || !_pendingExportsByCsoId.TryGetValue(connectedSystemObject.Id, out var pendingExportsForThisCso))
        {
            Log.Verbose($"ProcessPendingExport: No pending exports found for CSO {connectedSystemObject.Id}.");
            return;
        }

        if (pendingExportsForThisCso.Count == 0)
        {
            Log.Verbose($"ProcessPendingExport: No pending exports found for CSO {connectedSystemObject.Id}.");
            return;
        }

        Log.Verbose($"ProcessPendingExport: Found {pendingExportsForThisCso.Count} pending export(s) for CSO {connectedSystemObject.Id}.");

        // Create a copy to iterate over, since we may remove items from the original list during processing
        foreach (var pendingExport in pendingExportsForThisCso.ToList())
        {
            // Skip pending exports that have not been exported yet.
            // This method is for confirming whether already-exported changes were persisted.
            // Pending exports with Status=Pending haven't been sent to the connector yet -
            // comparing CSO attributes against unexported changes would incorrectly detect
            // "partial success" and change ChangeType from Create to Update, causing exports to fail.
            if (pendingExport.Status == JIM.Models.Transactional.PendingExportStatus.Pending)
            {
                Log.Verbose($"ProcessPendingExport: Skipping pending export {pendingExport.Id} - not yet exported (Status=Pending).");
                continue;
            }

            // Skip pending exports that are awaiting confirmation via PendingExportReconciliationService.
            // The "Exported" status means the export was successfully sent to the connector and is now
            // waiting for a confirming import to verify the values were persisted. The reconciliation
            // service (called during import) handles this state - we should not interfere here.
            if (pendingExport.Status == JIM.Models.Transactional.PendingExportStatus.Exported)
            {
                Log.Verbose($"ProcessPendingExport: Skipping pending export {pendingExport.Id} - awaiting confirmation via import (Status=Exported).");
                continue;
            }

            // track which attribute changes succeeded and which failed
            var successfulChanges = new List<JIM.Models.Transactional.PendingExportAttributeValueChange>();
            var failedChanges = new List<JIM.Models.Transactional.PendingExportAttributeValueChange>();

            foreach (var attributeChange in pendingExport.AttributeValueChanges)
            {
                // find the corresponding attribute value on the CSO
                var csoAttributeValue = connectedSystemObject.AttributeValues
                    .FirstOrDefault(av => av.AttributeId == attributeChange.AttributeId);

                // check if the attribute change matches the CSO's current state
                var changeMatches = attributeChange.ChangeType switch
                {
                    JIM.Models.Transactional.PendingExportAttributeChangeType.Add or
                    JIM.Models.Transactional.PendingExportAttributeChangeType.Update =>
                        csoAttributeValue != null && AttributeValuesMatch(csoAttributeValue, attributeChange),

                    JIM.Models.Transactional.PendingExportAttributeChangeType.Remove or
                    JIM.Models.Transactional.PendingExportAttributeChangeType.RemoveAll =>
                        csoAttributeValue == null || string.IsNullOrEmpty(csoAttributeValue.StringValue),

                    _ => false
                };

                if (changeMatches)
                {
                    successfulChanges.Add(attributeChange);
                    Log.Verbose($"ProcessPendingExport: Attribute change for {attributeChange.AttributeId} confirmed on CSO.");
                }
                else
                {
                    failedChanges.Add(attributeChange);
                    Log.Verbose($"ProcessPendingExport: Attribute change for {attributeChange.AttributeId} does not match CSO state.");
                }
            }

            // if all changes have been confirmed, queue pending export for deletion
            if (failedChanges.Count == 0)
            {
                Log.Information($"ProcessPendingExport: All changes confirmed for pending export {pendingExport.Id}. Queuing for deletion.");
                _pendingExportsToDelete.Add(pendingExport);

                // remove from in-memory cache to keep it consistent
                pendingExportsForThisCso.Remove(pendingExport);
            }
            else if (successfulChanges.Count > 0)
            {
                // partial success: remove successful attribute changes, keep failed ones
                Log.Information($"ProcessPendingExport: Partial success for pending export {pendingExport.Id}. " +
                    $"{successfulChanges.Count} succeeded, {failedChanges.Count} failed. Queuing for update.");

                // remove the successful attribute changes from the pending export
                foreach (var successfulChange in successfulChanges)
                {
                    pendingExport.AttributeValueChanges.Remove(successfulChange);
                }

                // If this was a Create operation and it partially succeeded, the object now exists.
                // Change to Update so subsequent export attempts update the existing object
                // rather than trying to create it again (which would fail without DN).
                if (pendingExport.ChangeType == JIM.Models.Transactional.PendingExportChangeType.Create)
                {
                    Log.Information($"ProcessPendingExport: Changing pending export {pendingExport.Id} from Create to Update (object was created, updating remaining attributes).");
                    pendingExport.ChangeType = JIM.Models.Transactional.PendingExportChangeType.Update;
                }

                // increment error count and update status
                pendingExport.ErrorCount++;
                pendingExport.Status = JIM.Models.Transactional.PendingExportStatus.ExportNotConfirmed;

                _pendingExportsToUpdate.Add(pendingExport);
            }
            else
            {
                // complete failure: all attribute changes failed
                Log.Warning($"ProcessPendingExport: Complete failure for pending export {pendingExport.Id}. " +
                    $"All {failedChanges.Count} attribute changes failed. Queuing for update.");

                // increment error count and update status
                pendingExport.ErrorCount++;
                pendingExport.Status = JIM.Models.Transactional.PendingExportStatus.ExportNotConfirmed;

                _pendingExportsToUpdate.Add(pendingExport);
            }
        }
    }

    /// <summary>
    /// Checks if a CSO attribute value matches a pending export attribute change.
    /// </summary>
    protected static bool AttributeValuesMatch(ConnectedSystemObjectAttributeValue csoValue, JIM.Models.Transactional.PendingExportAttributeValueChange pendingChange)
    {
        // compare based on the data type
        if (pendingChange.StringValue != null && csoValue.StringValue != pendingChange.StringValue)
            return false;

        if (pendingChange.IntValue.HasValue && csoValue.IntValue != pendingChange.IntValue)
            return false;

        if (pendingChange.DateTimeValue.HasValue && csoValue.DateTimeValue != pendingChange.DateTimeValue)
            return false;

        if (pendingChange.ByteValue != null && !Utilities.Utilities.AreByteArraysTheSame(csoValue.ByteValue, pendingChange.ByteValue))
            return false;

        if (pendingChange.UnresolvedReferenceValue != null && csoValue.UnresolvedReferenceValue != pendingChange.UnresolvedReferenceValue)
            return false;

        return true;
    }

    /// <summary>
    /// Check if a CSO has been obsoleted and delete it, applying any joined Metaverse Object changes as necessary.
    /// Respects the InboundOutOfScopeAction setting on import sync rules to determine whether to disconnect.
    /// Deleting a Metaverse Object can have downstream impacts on other Connected System objects.
    /// CSO deletions are batched for performance - call FlushObsoleteCsoOperationsAsync() at page boundaries.
    /// </summary>
    /// <returns>The execution item if CSO was obsoleted (for the caller to add to the activity), null otherwise.</returns>
    protected async Task<ActivityRunProfileExecutionItem?> ProcessObsoleteConnectedSystemObjectAsync(
        List<SyncRule> activeSyncRules,
        ConnectedSystemObject connectedSystemObject)
    {
        if (connectedSystemObject.Status != ConnectedSystemObjectStatus.Obsolete)
            return null;

        // Create the execution item for this obsolete CSO
        // Note: RPEI uses Delete (user-facing), CSO status uses Obsolete (internal state)
        var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
        runProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
        runProfileExecutionItem.ObjectChangeType = ObjectChangeType.Deleted;

        if (connectedSystemObject.MetaverseObject == null)
        {
            // CSO is not joined to an MVO. Check if it was pre-disconnected as part of MVO deletion.
            if (connectedSystemObject.JoinType == ConnectedSystemObjectJoinType.NotJoined)
            {
                // CSO was already disconnected (e.g., by EvaluateMvoDeletionAsync during synchronous MVO deletion).
                // This is expected during the confirming import/sync cycle after a delete export.
                // Just delete the CSO quietly - no RPEI needed as the disconnection was already recorded.
                _quietCsosToDelete.Add(connectedSystemObject);
                Log.Debug("ProcessObsoleteConnectedSystemObjectAsync: CSO {CsoId} already disconnected (JoinType=NotJoined), deleting quietly",
                    connectedSystemObject.Id);
                return null;
            }

            // Not joined but has a different JoinType (e.g., Explicit) - this is a regular orphan deletion
            _obsoleteCsosToDelete.Add((connectedSystemObject, runProfileExecutionItem));
            return runProfileExecutionItem;
        }

        // CSO is joined to an MVO - check InboundOutOfScopeAction to determine behaviour
        var inboundOutOfScopeAction = DetermineInboundOutOfScopeAction(activeSyncRules, connectedSystemObject);

        if (inboundOutOfScopeAction == InboundOutOfScopeAction.RemainJoined)
        {
            // Keep the join intact - just delete the CSO record but don't disconnect from MVO
            // This implements "once managed, always managed" behaviour
            Log.Information($"ProcessObsoleteConnectedSystemObjectAsync: InboundOutOfScopeAction=RemainJoined for CSO {connectedSystemObject.Id}. " +
                "CSO will be deleted but MVO join state preserved (object considered 'always managed').");

            // Note: We still delete the CSO as it's obsolete in the source system,
            // but we don't disconnect from MVO or trigger deletion rules
            _obsoleteCsosToDelete.Add((connectedSystemObject, runProfileExecutionItem));
            return runProfileExecutionItem;
        }

        // InboundOutOfScopeAction = Disconnect (default) - break the join and handle MVO deletion rules
        var mvo = connectedSystemObject.MetaverseObject;
        var connectedSystemId = connectedSystemObject.ConnectedSystemId;
        var mvoId = mvo.Id;

        // Query remaining CSO count BEFORE breaking the join so the count includes all current connectors.
        // Then subtract 1 to exclude this CSO which is about to be disconnected.
        var totalCsoCount = await _jim.ConnectedSystems.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(mvoId);
        var remainingCsoCount = Math.Max(0, totalCsoCount - 1);

        // Check if we should remove contributed attributes based on the object type setting
        if (connectedSystemObject.Type.RemoveContributedAttributesOnObsoletion)
        {
            // Find all MVO attribute values contributed by this connected system and mark them for removal
            var contributedAttributes = mvo.AttributeValues
                .Where(av => av.ContributedBySystem?.Id == connectedSystemId)
                .ToList();

            foreach (var attributeValue in contributedAttributes)
            {
                mvo.PendingAttributeValueRemovals.Add(attributeValue);
                Log.Verbose($"ProcessObsoleteConnectedSystemObjectAsync: Marking attribute '{attributeValue.Attribute?.Name}' for removal from MVO {mvo.Id}.");
            }
        }

        // Break the CSO-MVO join
        mvo.ConnectedSystemObjects.Remove(connectedSystemObject);
        connectedSystemObject.MetaverseObject = null;
        connectedSystemObject.MetaverseObjectId = null;
        connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.NotJoined;
        connectedSystemObject.DateJoined = null;
        Log.Verbose($"ProcessObsoleteConnectedSystemObjectAsync: Broke join between CSO {connectedSystemObject.Id} and MVO {mvoId}.");

        // Queue the CSO for batch deletion (deletion will happen at end of page processing)
        _obsoleteCsosToDelete.Add((connectedSystemObject, runProfileExecutionItem));

        // Evaluate MVO deletion rule based on type configuration
        await ProcessMvoDeletionRuleAsync(mvo, connectedSystemId, remainingCsoCount);

        return runProfileExecutionItem;
    }

    /// <summary>
    /// Determines the InboundOutOfScopeAction to use for a CSO by finding the applicable import sync rule.
    /// If multiple import sync rules exist for this CSO type, the first one's setting is used.
    /// Uses pre-loaded sync rules to avoid database round trips.
    /// </summary>
    protected static InboundOutOfScopeAction DetermineInboundOutOfScopeAction(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        // Find import sync rule for this CSO type from the already-loaded sync rules
        var importSyncRule = activeSyncRules.FirstOrDefault(sr =>
            sr.Direction == SyncRuleDirection.Import &&
            sr.Enabled &&
            sr.ConnectedSystemObjectTypeId == connectedSystemObject.TypeId);

        if (importSyncRule == null)
        {
            // No import sync rule found - default to Disconnect
            return InboundOutOfScopeAction.Disconnect;
        }

        return importSyncRule.InboundOutOfScopeAction;
    }

    /// <summary>
    /// Processes the MVO deletion rule when a connector is disconnected.
    /// Handles three deletion rules:
    /// - Manual: No automatic deletion
    /// - WhenLastConnectorDisconnected: Delete when ALL CSOs are disconnected
    /// - WhenAuthoritativeSourceDisconnected: Delete when ANY authoritative source disconnects
    /// Following industry-standard identity management practices, this method NEVER deletes the MVO directly.
    /// Instead, it sets LastConnectorDisconnectedDate and lets housekeeping handle actual deletion.
    /// </summary>
    /// <param name="mvo">The Metaverse Object to evaluate for deletion.</param>
    /// <param name="disconnectingSystemId">The ID of the Connected System whose CSO was disconnected.</param>
    /// <param name="remainingCsoCount">The count of remaining CSOs still joined to the MVO.</param>
    protected async Task ProcessMvoDeletionRuleAsync(MetaverseObject mvo, int disconnectingSystemId, int remainingCsoCount)
    {
        if (mvo.Type == null)
        {
            Log.Warning($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} has no Type set. Cannot determine deletion rule.");
            return;
        }

        // Only apply to Projected MVOs (Internal MVOs like admin accounts are protected)
        if (mvo.Origin == MetaverseObjectOrigin.Internal)
        {
            Log.Verbose($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} has Origin=Internal. Protected from automatic deletion.");
            return;
        }

        switch (mvo.Type.DeletionRule)
        {
            case MetaverseObjectDeletionRule.Manual:
                // No automatic deletion - MVO remains intact
                Log.Verbose($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} has DeletionRule=Manual. No automatic deletion.");
                break;

            case MetaverseObjectDeletionRule.WhenLastConnectorDisconnected:
                // Only delete when ALL CSOs are disconnected
                if (remainingCsoCount > 0)
                {
                    Log.Verbose($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} has {remainingCsoCount} remaining connector(s). Not marking for deletion yet.");
                    break;
                }

                await MarkMvoForDeletionAsync(mvo, "last connector disconnected");
                break;

            case MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected:
                // Delete when ANY authoritative source disconnects
                var triggerIds = mvo.Type.DeletionTriggerConnectedSystemIds;
                if (triggerIds == null || triggerIds.Count == 0)
                {
                    // No authoritative sources configured - fall back to last connector behaviour
                    Log.Warning($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} has DeletionRule=WhenAuthoritativeSourceDisconnected but no DeletionTriggerConnectedSystemIds configured. " +
                        "Falling back to WhenLastConnectorDisconnected behaviour.");
                    if (remainingCsoCount == 0)
                    {
                        await MarkMvoForDeletionAsync(mvo, "last connector disconnected (no authoritative sources configured)");
                    }
                    break;
                }

                // Check if the disconnecting system is an authoritative source
                if (triggerIds.Contains(disconnectingSystemId))
                {
                    Log.Information($"ProcessMvoDeletionRuleAsync: Authoritative source (system ID {disconnectingSystemId}) disconnected from MVO {mvo.Id}. " +
                        "Triggering deletion even though {RemainingCount} connector(s) remain.", remainingCsoCount);
                    await MarkMvoForDeletionAsync(mvo, $"authoritative source (system ID {disconnectingSystemId}) disconnected");
                }
                else
                {
                    Log.Verbose($"ProcessMvoDeletionRuleAsync: System ID {disconnectingSystemId} disconnected from MVO {mvo.Id} but is not an authoritative source. " +
                        "Authoritative sources: [{AuthSources}]. Not marking for deletion.", string.Join(", ", triggerIds));
                }
                break;

            default:
                Log.Warning($"ProcessMvoDeletionRuleAsync: Unknown DeletionRule {mvo.Type.DeletionRule} for MVO {mvo.Id}.");
                break;
        }
    }

    /// <summary>
    /// Processes MVO deletion based on grace period configuration.
    /// For 0-grace-period: queues for immediate synchronous deletion at page flush.
    /// For grace period > 0: marks for deferred deletion by housekeeping.
    /// </summary>
    /// <param name="mvo">The Metaverse Object to process for deletion.</param>
    /// <param name="reason">A description of why the MVO is being deleted (for logging).</param>
    private async Task MarkMvoForDeletionAsync(MetaverseObject mvo, string reason)
    {
        var gracePeriodDays = mvo.Type!.DeletionGracePeriodDays;

        if (!gracePeriodDays.HasValue || gracePeriodDays.Value == 0)
        {
            // No grace period - delete synchronously during this sync page flush
            // Check if already queued (multiple CSOs from same MVO may disconnect in same page)
            if (!_pendingMvoDeletions.Any(m => m.Id == mvo.Id))
            {
                Log.Information(
                    "MarkMvoForDeletionAsync: MVO {MvoId} queued for immediate deletion ({Reason}). No grace period configured.",
                    mvo.Id, reason);
                _pendingMvoDeletions.Add(mvo);
            }
        }
        else
        {
            // Grace period configured - mark for deferred deletion by housekeeping
            mvo.LastConnectorDisconnectedDate = DateTime.UtcNow;
            Log.Information(
                "MarkMvoForDeletionAsync: MVO {MvoId} marked for deletion ({Reason}). Eligible after {Days} days.",
                mvo.Id, reason, gracePeriodDays.Value);

            // Persist the LastConnectorDisconnectedDate
            await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo);
        }
    }

    /// <summary>
    /// Checks if the not-Obsolete CSO is joined to a Metaverse Object and updates it per any sync rules,
    /// or checks to see if a Metaverse Object needs creating (projecting the CSO) according to any sync rules.
    /// Changes to Metaverse Objects can have downstream impacts on other Connected System objects.
    /// </summary>
    /// <returns>A result indicating what MVO changes occurred (projection, join, attribute flow).</returns>
    protected async Task<MetaverseObjectChangeResult> ProcessMetaverseObjectChangesAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        Log.Verbose($"ProcessMetaverseObjectChangesAsync: Executing for: {connectedSystemObject}.");
        if (connectedSystemObject.Status == ConnectedSystemObjectStatus.Obsolete)
            return MetaverseObjectChangeResult.NoChanges();

        if (activeSyncRules.Count == 0)
            return MetaverseObjectChangeResult.NoChanges();

        // Track what kind of change occurred
        var wasJoined = false;
        var wasProjected = false;

        // Get import sync rules for this CSO type
        var importSyncRules = activeSyncRules
            .Where(sr => sr.Direction == SyncRuleDirection.Import && sr.ConnectedSystemObjectTypeId == connectedSystemObject.TypeId)
            .ToList();

        // Check if CSO is in scope for any import sync rule before attempting join/projection
        List<SyncRule> inScopeImportRules;
        using (Diagnostics.Sync.StartSpan("GetInScopeImportRules"))
        {
            inScopeImportRules = await GetInScopeImportRulesAsync(connectedSystemObject, importSyncRules);
        }

        if (inScopeImportRules.Count == 0 && importSyncRules.Any(sr => sr.ObjectScopingCriteriaGroups.Count > 0))
        {
            // CSO is out of scope for all import sync rules that have scoping criteria
            Log.Debug("ProcessMetaverseObjectChangesAsync: CSO {CsoId} is out of scope for all import sync rules", connectedSystemObject.Id);

            // Handle out of scope based on InboundOutOfScopeAction
            using (Diagnostics.Sync.StartSpan("HandleCsoOutOfScope"))
            {
                var outOfScopeResult = await HandleCsoOutOfScopeAsync(connectedSystemObject, importSyncRules);
                // Return the result directly - it will be DisconnectedOutOfScope, OutOfScopeRetainJoin, or NoChanges
                return outOfScopeResult;
            }
        }

        // do we need to join, or project the CSO to the Metaverse?
        if (connectedSystemObject.MetaverseObject == null)
        {
            // CSO is not joined to a Metaverse Object.
            // inspect sync rules to determine if we have any join or projection requirements.
            // try to join first, then project. the aim is to ensure we don't end up with duplicate Identities in the Metaverse.
            // Only use in-scope sync rules for join/projection
            var scopedSyncRules = inScopeImportRules.Count > 0 ? inScopeImportRules : activeSyncRules;

            using (Diagnostics.Sync.StartSpan("AttemptJoin"))
            {
                wasJoined = await AttemptJoinAsync(scopedSyncRules, connectedSystemObject);
            }

            // were we able to join to an existing MVO?
            if (!wasJoined && connectedSystemObject.MetaverseObject == null)
            {
                // try and project the CSO to the Metaverse.
                // this may cause onward sync operations, so may take time.
                // Only use in-scope sync rules for projection
                using (Diagnostics.Sync.StartSpan("AttemptProjection"))
                {
                    wasProjected = AttemptProjection(scopedSyncRules, connectedSystemObject);
                }
            }
        }

        // are we joined yet?
        if (connectedSystemObject.MetaverseObject != null)
        {
            // Get the inbound sync rules for this CSO type
            var inboundSyncRules = activeSyncRules
                .Where(sr => sr.Direction == SyncRuleDirection.Import && sr.ConnectedSystemObjectTypeId == connectedSystemObject.TypeId)
                .ToList();

            // process sync rules to see if we need to flow any attribute updates from the CSO to the MVO.
            // IMPORTANT: Skip reference attributes in the first pass. Reference attributes (e.g., group members)
            // may point to CSOs that haven't been processed yet (processed later in this page).
            // Reference attributes will be processed in a second pass after all CSOs have MVOs.
            using (Diagnostics.Sync.StartSpan("ProcessInboundAttributeFlow"))
            {
                foreach (var inboundSyncRule in inboundSyncRules)
                {
                    // evaluate inbound attribute flow rules, skipping reference attributes
                    ProcessInboundAttributeFlow(connectedSystemObject, inboundSyncRule, skipReferenceAttributes: true);
                }
            }

            // Queue this CSO for deferred reference attribute processing
            // This ensures reference attributes are processed after all CSOs in the page have MVOs
            _pendingReferenceAttributeProcessing.Add((connectedSystemObject, inboundSyncRules));

            // Count actual attribute changes that were queued
            var attributesAdded = connectedSystemObject.MetaverseObject.PendingAttributeValueAdditions.Count;
            var attributesRemoved = connectedSystemObject.MetaverseObject.PendingAttributeValueRemovals.Count;

            // Collect changed attributes BEFORE applying pending changes (we need them for export evaluation)
            // Also capture removals separately so export can create Remove changes for multi-valued attributes
            var changedAttributes = connectedSystemObject.MetaverseObject.PendingAttributeValueAdditions
                .Concat(connectedSystemObject.MetaverseObject.PendingAttributeValueRemovals)
                .ToList();
            var removedAttributes = connectedSystemObject.MetaverseObject.PendingAttributeValueRemovals.Count > 0
                ? connectedSystemObject.MetaverseObject.PendingAttributeValueRemovals.ToHashSet()
                : null;

            // Capture MVO changes for change tracking (before applying, so we have the pending lists)
            // Change objects will be created in batch at page boundary for performance
            // Create RPEI here so MVO changes can link to it for initiator context (User, ApiKey)
            if (attributesAdded > 0 || attributesRemoved > 0)
            {
                var additions = connectedSystemObject.MetaverseObject.PendingAttributeValueAdditions.ToList();
                var removals = connectedSystemObject.MetaverseObject.PendingAttributeValueRemovals.ToList();
                // Track whether this is a new MVO (Id == Guid.Empty) for correct ChangeType (Added vs Updated)
                var isNewMvo = connectedSystemObject.MetaverseObject.Id == Guid.Empty;
                // Create RPEI for this CSO change - will be used to link MVO change to Activity for initiator context
                var rpei = _activity.PrepareRunProfileExecutionItem();
                rpei.ConnectedSystemObject = connectedSystemObject;
                _activity.RunProfileExecutionItems.Add(rpei);
                _pendingMvoChanges.Add((connectedSystemObject.MetaverseObject, additions, removals, isNewMvo, rpei));
            }

            // Apply pending attribute value changes to the MVO
            ApplyPendingMetaverseObjectAttributeChanges(connectedSystemObject.MetaverseObject);

            // Queue MVO for batch persistence at end of page (reduces database round trips)
            if (connectedSystemObject.MetaverseObject.Id == Guid.Empty)
            {
                // New MVO - queue for batch create
                _pendingMvoCreates.Add(connectedSystemObject.MetaverseObject);
            }
            else
            {
                // Existing MVO - queue for batch update
                _pendingMvoUpdates.Add(connectedSystemObject.MetaverseObject);
            }

            // Queue for export evaluation after MVOs are persisted (need valid IDs for pending export FKs)
            if (changedAttributes.Count > 0)
            {
                _pendingExportEvaluations.Add((connectedSystemObject.MetaverseObject, changedAttributes, removedAttributes));
            }

            // Evaluate drift detection: check if the CSO has drifted from expected state
            // This detects unauthorized changes made directly in the target system
            // and stages corrective pending exports to remediate the drift
            using (Diagnostics.Sync.StartSpan("EvaluateDrift"))
            {
                await EvaluateDriftAndEnforceStateAsync(connectedSystemObject, connectedSystemObject.MetaverseObject);
            }

            // Return appropriate result based on what happened
            if (wasProjected)
            {
                return MetaverseObjectChangeResult.Projected(attributesAdded);
            }
            if (wasJoined)
            {
                return MetaverseObjectChangeResult.Joined(attributesAdded, attributesRemoved);
            }
            if (attributesAdded > 0 || attributesRemoved > 0)
            {
                return MetaverseObjectChangeResult.AttributeFlow(attributesAdded, attributesRemoved);
            }
        }

        return MetaverseObjectChangeResult.NoChanges();
    }

    /// <summary>
    /// Evaluates export rules for an MVO that has changed during inbound sync.
    /// Creates PendingExports for any connected systems that need to be updated.
    /// Also evaluates if MVO has fallen out of scope for any export rules (deprovisioning).
    /// Implements Q1 decision: evaluate exports immediately when MVO changes.
    /// Uses pre-cached export rules and CSO lookups for O(1) access instead of O(N×M) database queries.
    /// Includes no-net-change detection to skip creating pending exports when CSO already has current values.
    /// Pending exports are deferred for batch saving to reduce database round trips.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed (must have a valid Id assigned).</param>
    /// <param name="changedAttributes">The list of attribute values that changed.</param>
    /// <param name="removedAttributes">Optional set of attribute values that were removed (for multi-valued attr handling).</param>
    protected async Task EvaluateOutboundExportsAsync(
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> changedAttributes,
        HashSet<MetaverseObjectAttributeValue>? removedAttributes = null)
    {
        if (_exportEvaluationCache == null)
        {
            Log.Warning("EvaluateOutboundExportsAsync: Export evaluation cache not initialised, skipping export evaluation for MVO {MvoId}", mvo.Id);
            return;
        }

        // Evaluate export rules for MVOs that are IN scope, using cached data for O(1) lookups
        // Uses no-net-change detection (against target CSO attributes in cache) to skip pending exports when CSO already has current values
        // Pending exports and provisioning CSOs are deferred (deferSave=true) and collected for batch saving
        using (Diagnostics.Sync.StartSpan("EvaluateExportRules"))
        {
            var result = await _jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
                mvo,
                changedAttributes,
                _connectedSystem,
                _exportEvaluationCache,
                deferSave: true,
                removedAttributes: removedAttributes);

            // Aggregate no-net-change counts for statistics
            _totalCsoAlreadyCurrentCount += result.CsoAlreadyCurrentCount;

            // Collect provisioning CSOs for batch creation at end of page (must be created before pending exports)
            if (result.ProvisioningCsosToCreate.Count > 0)
            {
                _provisioningCsosToCreate.AddRange(result.ProvisioningCsosToCreate);
            }

            // Collect pending exports for batch saving at end of page
            if (result.PendingExports.Count > 0)
            {
                _pendingExportsToCreate.AddRange(result.PendingExports);
            }
        }

        // Evaluate if MVO has fallen OUT of scope for any export rules (deprovisioning), using cached data
        using (Diagnostics.Sync.StartSpan("EvaluateOutOfScopeExports"))
        {
            await _jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(
                mvo,
                _connectedSystem,
                _exportEvaluationCache);
        }
    }

    /// <summary>
    /// Batch persists all pending MVO creates and updates collected during the current page.
    /// This reduces database round trips from n writes to 2 writes (one for creates, one for updates).
    /// After creates are persisted, CSO foreign keys are updated with the newly assigned MVO IDs.
    /// </summary>
    protected async Task PersistPendingMetaverseObjectsAsync()
    {
        if (_pendingMvoCreates.Count == 0 && _pendingMvoUpdates.Count == 0)
            return;

        using var span = Diagnostics.Sync.StartSpan("PersistPendingMetaverseObjects");
        span.SetTag("createCount", _pendingMvoCreates.Count);
        span.SetTag("updateCount", _pendingMvoUpdates.Count);

        // Batch create new MVOs
        if (_pendingMvoCreates.Count > 0)
        {
            await _jim.Metaverse.CreateMetaverseObjectsAsync(_pendingMvoCreates);
            Log.Verbose("PersistPendingMetaverseObjectsAsync: Created {Count} MVOs in batch", _pendingMvoCreates.Count);
            _pendingMvoCreates.Clear();
        }

        // Batch update existing MVOs
        if (_pendingMvoUpdates.Count > 0)
        {
            await _jim.Metaverse.UpdateMetaverseObjectsAsync(_pendingMvoUpdates);
            Log.Verbose("PersistPendingMetaverseObjectsAsync: Updated {Count} MVOs in batch", _pendingMvoUpdates.Count);
            _pendingMvoUpdates.Clear();
        }

        span.SetSuccess();
    }

    /// <summary>
    /// Processes deferred reference attributes for all CSOs queued during the current page.
    /// This is the second pass of attribute flow processing - reference attributes are deferred
    /// because they may reference CSOs that are processed later in the same page.
    /// By processing references after all CSOs have MVOs, we ensure all referenced MVOs exist.
    /// </summary>
    /// <returns>The total number of reference attribute changes (additions + removals).</returns>
    protected int ProcessDeferredReferenceAttributes()
    {
        if (_pendingReferenceAttributeProcessing.Count == 0)
            return 0;

        using var span = Diagnostics.Sync.StartSpan("ProcessDeferredReferenceAttributes");
        span.SetTag("count", _pendingReferenceAttributeProcessing.Count);

        var totalChanges = 0;

        foreach (var (cso, syncRules) in _pendingReferenceAttributeProcessing)
        {
            if (cso.MetaverseObject == null)
                continue;

            var mvo = cso.MetaverseObject;
            var beforeAdditions = mvo.PendingAttributeValueAdditions.Count;
            var beforeRemovals = mvo.PendingAttributeValueRemovals.Count;

            // Process ONLY reference attributes (onlyReferenceAttributes = true)
            // This is more efficient than re-processing all attributes
            foreach (var syncRule in syncRules)
            {
                ProcessInboundAttributeFlow(cso, syncRule, skipReferenceAttributes: false, onlyReferenceAttributes: true);
            }

            // Count changes from reference attribute processing
            var additionsFromReferences = mvo.PendingAttributeValueAdditions.Count - beforeAdditions;
            var removalsFromReferences = mvo.PendingAttributeValueRemovals.Count - beforeRemovals;

            if (additionsFromReferences > 0 || removalsFromReferences > 0)
            {
                totalChanges += additionsFromReferences + removalsFromReferences;
                Log.Verbose("ProcessDeferredReferenceAttributes: CSO {CsoId} had {Adds} reference additions, {Removes} removals",
                    cso.Id, additionsFromReferences, removalsFromReferences);

                // Check if an RPEI already exists for this CSO (from projection/join/non-reference attribute flow).
                // If no RPEI exists, we need to create one because reference attribute changes are still
                // real changes that should be visible to operators. This handles the case where ONLY
                // reference attributes changed (e.g., group membership updated during delta sync).
                // Note: Check both ConnectedSystemObjectId (FK) and ConnectedSystemObject (navigation property)
                // because the FK may not be set yet for newly created RPEIs that haven't been saved.
                var existingRpei = _activity.RunProfileExecutionItems
                    .FirstOrDefault(r => r.ConnectedSystemObjectId == cso.Id ||
                                        (r.ConnectedSystemObject != null && r.ConnectedSystemObject.Id == cso.Id));

                if (existingRpei == null)
                {
                    // No RPEI exists for this CSO - create one for the reference attribute flow
                    var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
                    runProfileExecutionItem.ConnectedSystemObject = cso;
                    runProfileExecutionItem.ObjectChangeType = ObjectChangeType.AttributeFlow;
                    _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);
                    Log.Debug("ProcessDeferredReferenceAttributes: Created RPEI for CSO {CsoId} with reference-only changes",
                        cso.Id);
                }

                // Capture removals BEFORE applying changes (they get cleared by ApplyPendingMetaverseObjectAttributeChanges)
                // This is needed so export can create Remove changes for multi-valued reference attributes
                var refRemovedAttributes = mvo.PendingAttributeValueRemovals.Count > 0
                    ? mvo.PendingAttributeValueRemovals.ToHashSet()
                    : null;

                // Apply the reference attribute changes to the MVO
                ApplyPendingMetaverseObjectAttributeChanges(mvo);

                // Queue MVO for update if not already pending creation (new MVOs will be created with all attributes)
                if (mvo.Id != Guid.Empty && !_pendingMvoUpdates.Contains(mvo))
                {
                    _pendingMvoUpdates.Add(mvo);
                }

                // Queue for export evaluation (reference changes may trigger exports)
                // Include all reference attributes as changed, with removals tracked separately
                // Note: Check both ReferenceValue (navigation) and ReferenceValueId (FK) as navigation may not be loaded
                var currentRefAttributes = mvo.AttributeValues
                    .Where(av => av.ReferenceValue != null || av.ReferenceValueId.HasValue)
                    .ToList();
                var removedRefAttributesFiltered = refRemovedAttributes?
                    .Where(av => av.ReferenceValue != null || av.ReferenceValueId.HasValue)
                    .ToList() ?? [];

                var changedRefAttributes = currentRefAttributes
                    .Concat(removedRefAttributesFiltered)
                    .Cast<MetaverseObjectAttributeValue>()
                    .ToList();
                if (changedRefAttributes.Count > 0 && !_pendingExportEvaluations.Any(e => e.Mvo == mvo))
                {
                    _pendingExportEvaluations.Add((mvo, changedRefAttributes, refRemovedAttributes));
                }
            }
        }

        Log.Verbose("ProcessDeferredReferenceAttributes: Processed {Count} CSOs, {Changes} total reference changes",
            _pendingReferenceAttributeProcessing.Count, totalChanges);

        _pendingReferenceAttributeProcessing.Clear();
        span.SetSuccess();

        return totalChanges;
    }

    /// <summary>
    /// Batch evaluates export rules for all MVOs that changed during the current page.
    /// Must be called after PersistPendingMetaverseObjectsAsync so MVOs have valid IDs for pending export FKs.
    /// </summary>
    protected async Task EvaluatePendingExportsAsync()
    {
        if (_pendingExportEvaluations.Count == 0)
            return;

        using var span = Diagnostics.Sync.StartSpan("EvaluatePendingExports");
        span.SetTag("count", _pendingExportEvaluations.Count);

        foreach (var (mvo, changedAttributes, removedAttributes) in _pendingExportEvaluations)
        {
            using (Diagnostics.Sync.StartSpan("EvaluateSingleMvoExports")
                .SetTag("mvoId", mvo.Id)
                .SetTag("changedAttributeCount", changedAttributes.Count))
            {
                await EvaluateOutboundExportsAsync(mvo, changedAttributes, removedAttributes);
            }
        }

        Log.Verbose("EvaluatePendingExportsAsync: Evaluated exports for {Count} MVOs", _pendingExportEvaluations.Count);
        _pendingExportEvaluations.Clear();

        span.SetSuccess();
    }

    /// <summary>
    /// Batch persists all provisioning CSOs and pending export creates, deletes, and updates collected during the current page.
    /// CSOs must be created before pending exports since pending exports reference CSOs by ID.
    /// This reduces database round trips from n writes to 4 writes (CSOs, creates, deletes, updates).
    /// </summary>
    protected async Task FlushPendingExportOperationsAsync()
    {
        if (_provisioningCsosToCreate.Count == 0 && _pendingExportsToCreate.Count == 0 &&
            _pendingExportsToDelete.Count == 0 && _pendingExportsToUpdate.Count == 0)
            return;

        using var span = Diagnostics.Sync.StartSpan("FlushPendingExportOperations");
        span.SetTag("csoCreateCount", _provisioningCsosToCreate.Count);
        span.SetTag("createCount", _pendingExportsToCreate.Count);
        span.SetTag("deleteCount", _pendingExportsToDelete.Count);
        span.SetTag("updateCount", _pendingExportsToUpdate.Count);

        // Batch create provisioning CSOs first (pending exports reference CSOs by ID)
        if (_provisioningCsosToCreate.Count > 0)
        {
            await _jim.ConnectedSystems.CreateConnectedSystemObjectsAsync(_provisioningCsosToCreate);
            Log.Verbose("FlushPendingExportOperationsAsync: Created {Count} provisioning CSOs in batch", _provisioningCsosToCreate.Count);
            _provisioningCsosToCreate.Clear();
        }

        // Batch create new pending exports (evaluated during export evaluation phase)
        if (_pendingExportsToCreate.Count > 0)
        {
            await _jim.ConnectedSystems.CreatePendingExportsAsync(_pendingExportsToCreate);
            Log.Verbose("FlushPendingExportOperationsAsync: Created {Count} pending exports in batch", _pendingExportsToCreate.Count);
            _pendingExportsToCreate.Clear();
        }

        // Batch delete confirmed pending exports
        if (_pendingExportsToDelete.Count > 0)
        {
            await _jim.ConnectedSystems.DeletePendingExportsAsync(_pendingExportsToDelete);
            Log.Verbose("FlushPendingExportOperationsAsync: Deleted {Count} confirmed pending exports in batch", _pendingExportsToDelete.Count);
            _pendingExportsToDelete.Clear();
        }

        // Batch update pending exports that need error tracking
        if (_pendingExportsToUpdate.Count > 0)
        {
            await _jim.ConnectedSystems.UpdatePendingExportsAsync(_pendingExportsToUpdate);
            Log.Verbose("FlushPendingExportOperationsAsync: Updated {Count} pending exports in batch", _pendingExportsToUpdate.Count);
            _pendingExportsToUpdate.Clear();
        }

        span.SetSuccess();
    }

    /// <summary>
    /// Batch deletes all obsolete CSOs collected during the current page.
    /// This reduces database round trips from n deletes to 1 batch delete operation.
    /// Also handles quiet deletions (pre-disconnected CSOs that don't need RPEIs).
    /// </summary>
    protected async Task FlushObsoleteCsoOperationsAsync()
    {
        // First, handle quiet deletions (pre-disconnected CSOs from synchronous MVO deletion)
        if (_quietCsosToDelete.Count > 0)
        {
            await _jim.ConnectedSystems.DeleteConnectedSystemObjectsAsync(_quietCsosToDelete);
            Log.Debug("FlushObsoleteCsoOperationsAsync: Quietly deleted {Count} pre-disconnected CSOs", _quietCsosToDelete.Count);
            _quietCsosToDelete.Clear();
        }

        // Then handle normal obsolete CSO deletions (with RPEI tracking)
        if (_obsoleteCsosToDelete.Count == 0)
            return;

        using var span = Diagnostics.Sync.StartSpan("FlushObsoleteCsoOperations");
        span.SetTag("deleteCount", _obsoleteCsosToDelete.Count);

        // Batch delete obsolete CSOs
        var csosToDelete = _obsoleteCsosToDelete.Select(x => x.Cso).ToList();
        var executionItems = _obsoleteCsosToDelete.Select(x => x.ExecutionItem).ToList();

        await _jim.ConnectedSystems.DeleteConnectedSystemObjectsAsync(csosToDelete, executionItems);
        Log.Verbose("FlushObsoleteCsoOperationsAsync: Deleted {Count} obsolete CSOs in batch", _obsoleteCsosToDelete.Count);

        _obsoleteCsosToDelete.Clear();
        span.SetSuccess();
    }

    /// <summary>
    /// Batch deletes all MVOs collected for synchronous deletion during the current page.
    /// Creates delete pending exports for any remaining Provisioned CSOs before deletion.
    /// This handles 0-grace-period deletions inline during sync rather than deferring to housekeeping.
    /// </summary>
    protected async Task FlushPendingMvoDeletionsAsync()
    {
        if (_pendingMvoDeletions.Count == 0)
            return;

        using var span = Diagnostics.Sync.StartSpan("FlushPendingMvoDeletions");
        span.SetTag("deleteCount", _pendingMvoDeletions.Count);

        foreach (var mvo in _pendingMvoDeletions)
        {
            try
            {
                // Create delete pending exports for any remaining Provisioned CSOs
                // This handles WhenAuthoritativeSourceDisconnected where target CSOs still exist
                var deleteExports = await _jim.ExportEvaluation.EvaluateMvoDeletionAsync(mvo);
                if (deleteExports.Count > 0)
                {
                    Log.Information(
                        "FlushPendingMvoDeletionsAsync: Created {Count} delete pending exports for MVO {MvoId}",
                        deleteExports.Count, mvo.Id);
                }

                // Delete the MVO
                await _jim.Metaverse.DeleteMetaverseObjectAsync(mvo);
                Log.Information(
                    "FlushPendingMvoDeletionsAsync: Deleted MVO {MvoId} ({DisplayName})",
                    mvo.Id, mvo.DisplayName ?? "No display name");
            }
            catch (Exception ex)
            {
                // Log error but continue with other deletions
                // Set LastConnectorDisconnectedDate as fallback so housekeeping can retry
                Log.Error(ex,
                    "FlushPendingMvoDeletionsAsync: Failed to delete MVO {MvoId}, marking for housekeeping retry",
                    mvo.Id);
                mvo.LastConnectorDisconnectedDate = DateTime.UtcNow;
                await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo);
            }
        }

        _pendingMvoDeletions.Clear();
        span.SetSuccess();
    }

    /// <summary>
    /// Creates MetaverseObjectChange records for all pending MVO changes in the current page batch.
    /// Called at page boundary after MVOs are persisted (so IDs are available).
    /// Respects the MVO change tracking feature flag.
    /// </summary>
    protected async Task CreatePendingMvoChangeObjectsAsync()
    {
        if (_pendingMvoChanges.Count == 0)
            return;

        // Check feature flag
        var changeTrackingEnabled = await _jim.ServiceSettings.GetMvoChangeTrackingEnabledAsync();
        if (!changeTrackingEnabled)
        {
            _pendingMvoChanges.Clear();
            return;
        }

        using var span = Diagnostics.Sync.StartSpan("CreatePendingMvoChangeObjects");
        span.SetTag("changeCount", _pendingMvoChanges.Count);

        foreach (var (mvo, additions, removals, isNew, rpei) in _pendingMvoChanges)
        {
            // Create MVO change object with correct ChangeType based on whether MVO is new or existing
            // Link to RPEI so we can trace back to the Activity for initiator context (User, ApiKey, etc.)
            var change = new MetaverseObjectChange
            {
                MetaverseObject = mvo,
                ChangeType = isNew ? ObjectChangeType.Added : ObjectChangeType.Updated,
                ChangeTime = DateTime.UtcNow,
                ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule,
                // Link to RPEI for Activity initiator context - EF will resolve the FK when persisted
                ActivityRunProfileExecutionItem = rpei
            };

            // Create attribute change records for additions
            foreach (var addition in additions)
            {
                AddMvoChangeAttributeValueObject(change, addition, ValueChangeType.Add);
            }

            // Create attribute change records for removals
            foreach (var removal in removals)
            {
                AddMvoChangeAttributeValueObject(change, removal, ValueChangeType.Remove);
            }

            // Add to MVO's Changes collection - will be persisted when Activity is saved (MVO already tracked)
            mvo.Changes.Add(change);
        }

        _pendingMvoChanges.Clear();
        span.SetSuccess();
    }

    /// <summary>
    /// Creates the necessary attribute change audit item for when an MVO is updated or deleted, and adds it to the change object.
    /// </summary>
    /// <param name="metaverseObjectChange">The MetaverseObjectChange being built.</param>
    /// <param name="metaverseObjectAttributeValue">The attribute and value pair for the change.</param>
    /// <param name="valueChangeType">The type of change (Add or Remove).</param>
    private static void AddMvoChangeAttributeValueObject(MetaverseObjectChange metaverseObjectChange, MetaverseObjectAttributeValue metaverseObjectAttributeValue, ValueChangeType valueChangeType)
    {
        var attributeChange = metaverseObjectChange.AttributeChanges.SingleOrDefault(ac => ac.Attribute.Id == metaverseObjectAttributeValue.Attribute.Id);
        if (attributeChange == null)
        {
            // Create the attribute change object that provides an audit trail of changes to an MVO's attributes
            attributeChange = new MetaverseObjectChangeAttribute
            {
                Attribute = metaverseObjectAttributeValue.Attribute,
                MetaverseObjectChange = metaverseObjectChange
            };
            metaverseObjectChange.AttributeChanges.Add(attributeChange);
        }

        switch (metaverseObjectAttributeValue.Attribute.Type)
        {
            case AttributeDataType.Text when metaverseObjectAttributeValue.StringValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(attributeChange, valueChangeType, metaverseObjectAttributeValue.StringValue));
                break;
            case AttributeDataType.Number when metaverseObjectAttributeValue.IntValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(attributeChange, valueChangeType, (int)metaverseObjectAttributeValue.IntValue));
                break;
            case AttributeDataType.LongNumber when metaverseObjectAttributeValue.LongValue != null:
                // TODO: MetaverseObjectChangeAttributeValue model needs LongValue property and constructor
                // For now, cast to int (may lose precision for very large longs)
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(attributeChange, valueChangeType, (int)metaverseObjectAttributeValue.LongValue.Value));
                break;
            case AttributeDataType.Guid when metaverseObjectAttributeValue.GuidValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(attributeChange, valueChangeType, (Guid)metaverseObjectAttributeValue.GuidValue));
                break;
            case AttributeDataType.Boolean when metaverseObjectAttributeValue.BoolValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(attributeChange, valueChangeType, (bool)metaverseObjectAttributeValue.BoolValue));
                break;
            case AttributeDataType.DateTime when metaverseObjectAttributeValue.DateTimeValue.HasValue:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(attributeChange, valueChangeType, metaverseObjectAttributeValue.DateTimeValue.Value));
                break;
            case AttributeDataType.Binary when metaverseObjectAttributeValue.ByteValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(attributeChange, valueChangeType, true, metaverseObjectAttributeValue.ByteValue.Length));
                break;
            case AttributeDataType.Reference when metaverseObjectAttributeValue.ReferenceValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(attributeChange, valueChangeType, metaverseObjectAttributeValue.ReferenceValue));
                break;
            case AttributeDataType.Reference when metaverseObjectAttributeValue.UnresolvedReferenceValue != null:
                // We do not log changes for unresolved references. Only resolved references get change tracked.
                break;
            default:
                throw new NotImplementedException($"Attribute data type {metaverseObjectAttributeValue.Attribute.Type} is not yet supported for MVO change tracking.");
        }
    }

    /// <summary>
    /// Attempts to find a Metaverse Object that matches the CSO using Object Matching Rules on any applicable Sync Rules for this system and object type.
    /// </summary>
    /// <param name="activeSyncRules">The active sync rules that contain all possible join rules to be evaluated.</param>
    /// <param name="connectedSystemObject">The Connected System Object to try and find a matching Metaverse Object for.</param>
    /// <returns>True if a join was established, false if no matching MVO was found.</returns>
    /// <exception cref="SyncJoinException">Thrown when a join cannot be established due to ambiguous match or existing join.</exception>
    /// <exception cref="InvalidDataException">Thrown if an unsupported join state is found preventing processing.</exception>
    protected async Task<bool> AttemptJoinAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        // Enumerate import sync rules for this CSO type to attempt matching.
        // Uses ObjectMatchingServer which handles both ConnectedSystem mode (rules on object type)
        // and SyncRule mode (rules on sync rule).
        foreach (var importSyncRule in activeSyncRules.Where(sr => sr.Direction == SyncRuleDirection.Import && sr.ConnectedSystemObjectTypeId == connectedSystemObject.TypeId))
        {
            // Use ObjectMatchingServer to find a matching MVO - this properly handles both matching modes
            MetaverseObject? mvo;
            try
            {
                mvo = await _jim.ObjectMatching.FindMatchingMetaverseObjectAsync(
                    connectedSystemObject,
                    _connectedSystem,
                    importSyncRule);
            }
            catch (JIM.Models.Exceptions.MultipleMatchesException ex)
            {
                throw new SyncJoinException(
                    ActivityRunProfileExecutionItemErrorType.AmbiguousMatch,
                    $"Multiple Metaverse Objects ({ex.Matches.Count}) match this Connected System Object. " +
                    $"An MVO can only be joined to a single CSO per Connected System. " +
                    $"Check your Object Matching Rules to ensure unique matches. Matching MVO IDs: {string.Join(", ", ex.Matches)}");
            }

            if (mvo == null)
                continue;

            // MVO must not already be joined to a connected system object in this connected system. Joins are 1:1.
            // Use a count query to check for existing joins without loading the CSO entities.
            var existingCsoJoinCount = await _jim.ConnectedSystems.GetConnectedSystemObjectCountByMvoAsync(
                _connectedSystem.Id, mvo.Id);

            if (existingCsoJoinCount > 1)
                throw new InvalidDataException($"More than one CSO is already joined to the MVO {mvo} we found that matches the matching rules. This is not good!");

            if (existingCsoJoinCount == 1)
            {
                throw new SyncJoinException(
                    ActivityRunProfileExecutionItemErrorType.CouldNotJoinDueToExistingJoin,
                    $"Cannot join this Connected System Object to Metaverse Object ({mvo}) - it already has a connector from this Connected System. " +
                    $"Check for duplicate data in your source system, or review your Object Matching Rules for uniqueness.");
            }

            // Establish join! First rule to match, wins.
            connectedSystemObject.MetaverseObject = mvo;
            connectedSystemObject.MetaverseObjectId = mvo.Id;
            connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.Joined;
            connectedSystemObject.DateJoined = DateTime.UtcNow;
            mvo.ConnectedSystemObjects.Add(connectedSystemObject);

            // If the MVO was marked for deletion (reconnection scenario), clear the disconnection date
            if (mvo.LastConnectorDisconnectedDate.HasValue)
            {
                Log.Information($"AttemptJoinAsync: Clearing LastConnectorDisconnectedDate for MVO {mvo.Id} as connector has reconnected.");
                mvo.LastConnectorDisconnectedDate = null;
            }

            Log.Information("AttemptJoinAsync: Established join between CSO {CsoId} and MVO {MvoId}", connectedSystemObject.Id, mvo.Id);
            return true;
        }

        // No join could be established.
        return false;
    }

    /// <summary>
    /// Attempts to create a Metaverse Object from the Connected System Object using the first Sync Rule for the object type that has Projection enabled.
    /// </summary>
    /// <param name="activeSyncRules">The active sync rules that contain projection and attribute flow information.</param>
    /// <param name="connectedSystemObject">The Connected System Object to attempt to project to the Metaverse.</param>
    /// <returns>True if projection occurred, false otherwise.</returns>
    /// <exception cref="InvalidDataException">Will be thrown if not all required properties are populated on the Sync Rule.</exception>
    /// <exception cref="NotImplementedException">Will be thrown if a Sync Rule attempts to use a Function as a source.</exception>
    protected static bool AttemptProjection(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        // see if there are any sync rules for this object type where projection is enabled. first to project, wins.
        var projectionSyncRule = activeSyncRules?.FirstOrDefault(sr =>
            sr.ProjectToMetaverse.HasValue && sr.ProjectToMetaverse.Value &&
            sr.ConnectedSystemObjectType.Id == connectedSystemObject.TypeId);

        if (projectionSyncRule == null)
            return false;

        // create the MVO using type from the Sync Rule.
        // Note: Do NOT assign Id here - let it remain Guid.Empty so that
        // ProcessMetaverseObjectChangesAsync knows to call CreateMetaverseObjectAsync.
        // The Id will be assigned by EF when the MVO is saved to the database.
        var mvo = new MetaverseObject();
        mvo.Type = projectionSyncRule.MetaverseObjectType;
        connectedSystemObject.MetaverseObject = mvo;
        mvo.ConnectedSystemObjects.Add(connectedSystemObject);
        // Don't set MetaverseObjectId yet - it will be set after the MVO is persisted
        connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.Projected;
        connectedSystemObject.DateJoined = DateTime.UtcNow;

        // do not flow attributes at this point. let that happen separately, so we don't re-process sync rules later.
        return true;
    }

    /// <summary>
    /// Assigns values to a Metaverse Object, from a Connected System Object using a Sync Rule.
    /// Does not perform any delta processing. This is for MVO create scenarios where there are not MVO attribute values already.
    /// </summary>
    /// <param name="connectedSystemObject">The source Connected System Object to map values from.</param>
    /// <param name="syncRule">The Sync Rule to use to determine which attributes, and how should be assigned to the Metaverse Object.</param>
    /// <param name="skipReferenceAttributes">If true, skip reference attributes (they will be processed in a second pass after all MVOs exist).</param>
    /// <param name="onlyReferenceAttributes">If true, process ONLY reference attributes (for deferred second pass). Takes precedence over skipReferenceAttributes.</param>
    /// <exception cref="InvalidDataException">Can be thrown if a Sync Rule Mapping Source is not properly formed.</exception>
    /// <exception cref="NotImplementedException">Will be thrown whilst Functions have not been implemented, but are being used in the Sync Rule.</exception>
    protected void ProcessInboundAttributeFlow(ConnectedSystemObject connectedSystemObject, SyncRule syncRule, bool skipReferenceAttributes = false, bool onlyReferenceAttributes = false)
    {
        if (connectedSystemObject.MetaverseObject == null)
        {
            Log.Error($"AssignMetaverseObjectAttributeValues: CSO ({connectedSystemObject}) has no MVO!");
            return;
        }

        if (_objectTypes == null)
            throw new MissingMemberException("_objectTypes is null!");

        foreach (var syncRuleMapping in syncRule.AttributeFlowRules)
        {
            if (syncRuleMapping.TargetMetaverseAttribute == null)
                throw new InvalidDataException("SyncRuleMapping.TargetMetaverseAttribute must not be null.");

            SyncRuleMappingProcessor.Process(connectedSystemObject, syncRuleMapping, _objectTypes, _expressionEvaluator, skipReferenceAttributes, onlyReferenceAttributes);
        }
    }

    /// <summary>
    /// Applies pending attribute value changes to a Metaverse Object.
    /// This moves values from PendingAttributeValueAdditions to AttributeValues
    /// and removes values listed in PendingAttributeValueRemovals.
    /// </summary>
    /// <param name="mvo">The Metaverse Object to apply pending changes to.</param>
    protected static void ApplyPendingMetaverseObjectAttributeChanges(MetaverseObject mvo)
    {
        var addCount = mvo.PendingAttributeValueAdditions.Count;
        var removeCount = mvo.PendingAttributeValueRemovals.Count;

        // If there are no pending changes, nothing to do
        if (addCount == 0 && removeCount == 0)
            return;

        // Apply removals first
        foreach (var removal in mvo.PendingAttributeValueRemovals)
        {
            mvo.AttributeValues.Remove(removal);
        }

        // Apply additions
        foreach (var addition in mvo.PendingAttributeValueAdditions)
        {
            mvo.AttributeValues.Add(addition);
        }

        // Clear the pending lists now that changes have been applied
        mvo.PendingAttributeValueRemovals.Clear();
        mvo.PendingAttributeValueAdditions.Clear();

        Log.Verbose("ApplyPendingMetaverseObjectAttributeChanges: Applied {AddCount} additions and {RemoveCount} removals to MVO {MvoId}",
            addCount, removeCount, mvo.Id);
    }

    /// <summary>
    /// As part of updating or creating reference Metaverse Attribute Values from Connected System Object Attribute Values, references would have been staged
    /// as unresolved, pointing to the Connected System Object. This converts those CSO unresolved references to MVO references.
    /// </summary>
    protected Task ResolveReferencesAsync()
    {
        // find all Metaverse Attribute Values with unresolved reference values
        // get the joined Metaverse Object and add it to the Metaverse Object Attribute Value
        // remove the unresolved reference value.
        // update the Metaverse Object Attribute Value.

        // TODO: "Is this still needed? We're assigning MVO references from CSO reference values on sync rule processing."
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the list of import sync rules for which the CSO is in scope.
    /// If a sync rule has no scoping criteria, the CSO is considered in scope.
    /// </summary>
    protected async Task<List<SyncRule>> GetInScopeImportRulesAsync(ConnectedSystemObject connectedSystemObject, List<SyncRule> importSyncRules)
    {
        var inScopeRules = new List<SyncRule>();
        var csoWithAttributes = connectedSystemObject;

        foreach (var syncRule in importSyncRules)
        {
            // No scoping criteria means CSO is in scope
            if (syncRule.ObjectScopingCriteriaGroups.Count == 0)
            {
                inScopeRules.Add(syncRule);
                continue;
            }

            // Need to load CSO attributes for scoping evaluation if not already loaded
            if (csoWithAttributes.AttributeValues.Count == 0)
            {
                var fullCso = await _jim.ConnectedSystems.GetConnectedSystemObjectAsync(_connectedSystem.Id, connectedSystemObject.Id);
                if (fullCso != null)
                {
                    csoWithAttributes = fullCso;
                    // Copy attributes to original CSO so we have them for later
                    connectedSystemObject.AttributeValues = fullCso.AttributeValues;
                }
            }

            // Evaluate scoping criteria
            if (_jim.ScopingEvaluation.IsCsoInScopeForImportRule(csoWithAttributes, syncRule))
            {
                inScopeRules.Add(syncRule);
            }
        }

        return inScopeRules;
    }

    /// <summary>
    /// Handles a CSO that has fallen out of scope for all import sync rules.
    /// If the CSO is currently joined to an MVO, applies the InboundOutOfScopeAction.
    /// </summary>
    /// <returns>A result indicating what happened (DisconnectedOutOfScope, OutOfScopeRetainJoin, or NoChanges).</returns>
    protected async Task<MetaverseObjectChangeResult> HandleCsoOutOfScopeAsync(
        ConnectedSystemObject connectedSystemObject,
        List<SyncRule> importSyncRules)
    {
        // If not joined, nothing special to do - just skip processing
        if (connectedSystemObject.MetaverseObject == null)
        {
            Log.Verbose("HandleCsoOutOfScopeAsync: CSO {CsoId} is not joined, skipping out-of-scope processing", connectedSystemObject.Id);
            return MetaverseObjectChangeResult.NoChanges();
        }

        // Find the first sync rule's InboundOutOfScopeAction (or default to Disconnect)
        var inboundOutOfScopeAction = importSyncRules
            .Where(sr => sr.ObjectScopingCriteriaGroups.Count > 0)
            .Select(sr => sr.InboundOutOfScopeAction)
            .FirstOrDefault();

        switch (inboundOutOfScopeAction)
        {
            case InboundOutOfScopeAction.RemainJoined:
                // Keep the join intact - CSO remains connected to MVO
                // No attribute flow will occur since CSO is out of scope
                Log.Information("HandleCsoOutOfScopeAsync: CSO {CsoId} is out of scope but InboundOutOfScopeAction=RemainJoined. " +
                    "Join preserved, no attribute flow.", connectedSystemObject.Id);
                return MetaverseObjectChangeResult.OutOfScopeRetainJoin();

            case InboundOutOfScopeAction.Disconnect:
            default:
                // Break the join between CSO and MVO
                Log.Information("HandleCsoOutOfScopeAsync: CSO {CsoId} is out of scope. InboundOutOfScopeAction=Disconnect. Breaking join.",
                    connectedSystemObject.Id);

                var mvo = connectedSystemObject.MetaverseObject;
                var mvoId = mvo.Id;

                // Query remaining CSO count BEFORE breaking the join so the count includes all current connectors.
                // Then subtract 1 to exclude this CSO which is about to be disconnected.
                var totalCsoCount = await _jim.ConnectedSystems.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(mvoId);
                var remainingCsoCount = Math.Max(0, totalCsoCount - 1);

                // Check if we should remove contributed attributes based on the object type setting
                if (connectedSystemObject.Type.RemoveContributedAttributesOnObsoletion)
                {
                    var contributedAttributes = mvo.AttributeValues
                        .Where(av => av.ContributedBySystem?.Id == _connectedSystem.Id)
                        .ToList();

                    foreach (var attributeValue in contributedAttributes)
                    {
                        mvo.PendingAttributeValueRemovals.Add(attributeValue);
                        Log.Verbose("HandleCsoOutOfScopeAsync: Marking attribute '{AttrName}' for removal from MVO {MvoId}",
                            attributeValue.Attribute?.Name, mvo.Id);
                    }
                }

                // Break the CSO-MVO join
                mvo.ConnectedSystemObjects.Remove(connectedSystemObject);
                connectedSystemObject.MetaverseObject = null;
                connectedSystemObject.MetaverseObjectId = null;
                connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.NotJoined;
                connectedSystemObject.DateJoined = null;
                Log.Verbose("HandleCsoOutOfScopeAsync: Broke join between CSO {CsoId} and MVO {MvoId}", connectedSystemObject.Id, mvoId);

                // Apply pending attribute changes and update MVO
                ApplyPendingMetaverseObjectAttributeChanges(mvo);
                await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo);

                // Evaluate MVO deletion rule based on type configuration
                await ProcessMvoDeletionRuleAsync(mvo, _connectedSystem.Id, remainingCsoCount);

                return MetaverseObjectChangeResult.DisconnectedOutOfScope();
        }
    }

    /// <summary>
    /// Builds the drift detection cache from sync rules.
    /// This cache is used to efficiently determine if a connected system is a legitimate
    /// contributor for an attribute (has import rules) vs. just a recipient (only export rules).
    /// Call this once at the start of sync, after loading sync rules.
    /// </summary>
    /// <param name="allSyncRules">All sync rules from ALL connected systems (needed to build complete import mapping cache).</param>
    /// <param name="currentSystemSyncRules">Sync rules for the current connected system being synced.</param>
    protected void BuildDriftDetectionCache(List<SyncRule> allSyncRules, List<SyncRule> currentSystemSyncRules)
    {
        using var span = Diagnostics.Sync.StartSpan("BuildDriftDetectionCache");

        // Build import mapping cache from ALL sync rules across ALL connected systems.
        // This is critical for drift detection: we need to know which systems contribute to which MVO attributes
        // so we can skip drift detection when the CSO's system is a legitimate contributor.
        // Without all import rules, export-only systems would have an empty cache and incorrectly detect
        // drift on attributes that are legitimately sourced from other systems.
        _importMappingCache = DriftDetectionService.BuildImportMappingCache(allSyncRules);

        // Cache export rules with EnforceState = true for THIS connected system only
        _driftDetectionExportRules = currentSystemSyncRules
            .Where(sr => sr.Enabled &&
                        sr.Direction == SyncRuleDirection.Export &&
                        sr.EnforceState &&
                        sr.ConnectedSystemId == _connectedSystem.Id)
            .ToList();

        Log.Debug("BuildDriftDetectionCache: Built import mapping cache with {ImportMappings} entries from all systems, " +
            "{ExportRules} export rules with EnforceState=true for system {SystemId}",
            _importMappingCache.Count, _driftDetectionExportRules.Count, _connectedSystem.Id);

        span.SetTag("importMappingCount", _importMappingCache.Count);
        span.SetTag("enforceStateExportRuleCount", _driftDetectionExportRules.Count);
        span.SetSuccess();
    }

    /// <summary>
    /// Evaluates drift for a CSO that has been imported/synced and stages corrective pending exports.
    /// Only evaluates if drift detection is enabled (export rules with EnforceState = true exist).
    /// </summary>
    /// <param name="cso">The Connected System Object that was just processed.</param>
    /// <param name="mvo">The Metaverse Object the CSO is joined to.</param>
    protected async Task EvaluateDriftAndEnforceStateAsync(ConnectedSystemObject cso, MetaverseObject? mvo)
    {
        // Skip if no drift detection export rules exist
        if (_driftDetectionExportRules == null || _driftDetectionExportRules.Count == 0)
        {
            return;
        }

        // Skip if CSO is not joined to an MVO
        if (mvo == null && cso.MetaverseObject == null)
        {
            return;
        }

        var targetMvo = mvo ?? cso.MetaverseObject!;

        using var span = Diagnostics.Sync.StartSpan("EvaluateDriftAndEnforceState");
        span.SetTag("csoId", cso.Id);
        span.SetTag("mvoId", targetMvo.Id);

        var result = await _jim.DriftDetection.EvaluateDriftAsync(
            cso,
            targetMvo,
            _driftDetectionExportRules,
            _importMappingCache);

        if (result.HasDrift)
        {
            Log.Information("EvaluateDriftAndEnforceStateAsync: Detected {DriftCount} drifted attributes on CSO {CsoId}, " +
                "created {ExportCount} corrective pending exports",
                result.DriftedAttributes.Count, cso.Id, result.CorrectiveExports.Count);

            span.SetTag("driftedAttributeCount", result.DriftedAttributes.Count);
            span.SetTag("correctiveExportCount", result.CorrectiveExports.Count);

            // Create RPEI for drift correction to provide visibility in Activity UI
            // This shows that the delta sync detected unauthorised changes and staged corrective exports
            var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
            runProfileExecutionItem.ConnectedSystemObject = cso;
            runProfileExecutionItem.ObjectChangeType = ObjectChangeType.DriftCorrection;
            _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);
        }

        span.SetSuccess();
    }
}

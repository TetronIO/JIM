using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Utility;
using JIM.Utilities;
using Serilog;

namespace JIM.Worker.Processors;

public class SyncFullSyncTaskProcessor
{
    private readonly JimApplication _jim;
    private readonly ConnectedSystem _connectedSystem;
    private readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
    private readonly Activity _activity;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private List<ConnectedSystemObjectType>? _objectTypes;
    private Dictionary<Guid, List<JIM.Models.Transactional.PendingExport>>? _pendingExportsByCsoId;
    private ExportEvaluationServer.ExportEvaluationCache? _exportEvaluationCache;

    // Batch collections for deferred MVO persistence and export evaluation
    private readonly List<MetaverseObject> _pendingMvoCreates = [];
    private readonly List<MetaverseObject> _pendingMvoUpdates = [];
    private readonly List<(MetaverseObject Mvo, List<MetaverseObjectAttributeValue> ChangedAttributes)> _pendingExportEvaluations = [];

    public SyncFullSyncTaskProcessor(
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

    public async Task PerformFullSyncAsync()
    {
        using var syncSpan = Diagnostics.Sync.StartSpan("FullSync");
        syncSpan.SetTag("connectedSystemId", _connectedSystem.Id);
        syncSpan.SetTag("connectedSystemName", _connectedSystem.Name);

        Log.Verbose("PerformFullSyncAsync: Starting");

        // what needs to happen:
        // - confirm pending exports
        // - establish new joins to existing Metaverse Objects
        // - project CSO to the MV if there are no join matches and if a Sync Rule for this CS has Projection enabled.
        // - work out if we CAN update any Metaverse Objects (where there's attribute flow) and whether we SHOULD (where there's attribute flow priority).
        // - update the Metaverse Objects accordingly.
        // - work out if this requires other Connected System to be updated by way of creating new Pending Export Objects.

        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Preparing");

        // how many objects are we processing? that = CSO count + Pending Export Object count.
        // update the activity with this info so a progress bar can be shown.
        var totalCsosToProcess = await _jim.ConnectedSystems.GetConnectedSystemObjectCountAsync(_connectedSystem.Id);
        var totalPendingExportObjectsToProcess = await _jim.ConnectedSystems.GetPendingExportsCountAsync(_connectedSystem.Id);
        var totalObjectsToProcess = totalCsosToProcess + totalPendingExportObjectsToProcess;
        _activity.ObjectsToProcess = totalObjectsToProcess;
        _activity.ObjectsProcessed = 0;
        await _jim.Activities.UpdateActivityAsync(_activity);

        // get all the active sync rules for this system
        List<SyncRule> activeSyncRules;
        using (Diagnostics.Sync.StartSpan("LoadSyncRules"))
        {
            activeSyncRules = await _jim.ConnectedSystems.GetSyncRulesAsync(_connectedSystem.Id, false);
        }

        // get the schema for all object types upfront in this Connected System, so we can retrieve lightweight CSOs without this data.
        using (Diagnostics.Sync.StartSpan("LoadObjectTypes"))
        {
            _objectTypes = await _jim.ConnectedSystems.GetObjectTypesAsync(_connectedSystem.Id);
        }

        // load all pending exports once upfront and index by CSO ID for O(1) lookup
        // this avoids O(n²) behaviour from loading all pending exports for every CSO
        using (Diagnostics.Sync.StartSpan("LoadPendingExports"))
        {
            var allPendingExports = await _jim.ConnectedSystems.GetPendingExportsAsync(_connectedSystem.Id);
            _pendingExportsByCsoId = allPendingExports
                .Where(pe => pe.ConnectedSystemObject?.Id != null)
                .GroupBy(pe => pe.ConnectedSystemObject!.Id)
                .ToDictionary(g => g.Key, g => g.ToList());
            Log.Verbose("PerformFullSyncAsync: Loaded {Count} pending exports into lookup dictionary", allPendingExports.Count);
        }

        // Pre-load export evaluation cache (export rules + CSO lookups) for O(1) access
        // This eliminates O(N×M) database queries during export evaluation
        using (Diagnostics.Sync.StartSpan("LoadExportEvaluationCache"))
        {
            _exportEvaluationCache = await _jim.ExportEvaluation.BuildExportEvaluationCacheAsync(_connectedSystem.Id);
        }

        // process CSOs in batches. this enables us to respond to cancellation requests in a reasonable timeframe.
        // it also enables us to update the Activity with progress info as we go, allowing the UI to be updated and keep users informed.
        const int pageSize = 200;
        var totalCsoPages = Convert.ToInt16(Math.Ceiling((double)totalCsosToProcess / pageSize));
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Processing Connected System Objects");

        using var processCsosSpan = Diagnostics.Sync.StartSpan("ProcessConnectedSystemObjects");
        processCsosSpan.SetTag("totalObjects", totalCsosToProcess);
        processCsosSpan.SetTag("pageSize", pageSize);
        processCsosSpan.SetTag("totalPages", totalCsoPages);

        for (var i = 0; i < totalCsoPages; i++)
        {
            PagedResultSet<ConnectedSystemObject> csoPagedResult;
            using (Diagnostics.Sync.StartSpan("LoadCsoPage"))
            {
                csoPagedResult = await _jim.ConnectedSystems.GetConnectedSystemObjectsAsync(_connectedSystem.Id, i, pageSize, returnAttributes: false);
            }

            using (Diagnostics.Sync.StartSpan("ProcessCsoLoop").SetTag("csoCount", csoPagedResult.Results.Count))
            {
                foreach (var connectedSystemObject in csoPagedResult.Results)
                {
                    // check for cancellation request, and stop work if cancelled.
                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        Log.Information("PerformFullSyncAsync: Cancellation requested. Stopping CSO enumeration.");
                        return;
                    }

                    // what kind of result do we want? we want to see:
                    // - mvo joins (list)
                    // - mvo projections (list)
                    // - cso deletions (list)
                    // - mvo deletions (list)
                    // - mvo updates (list)
                    // - mvo objects not updated (count)

                    // todo: record changes to MV objects and other Connected Systems via Pending Export objects on the Activity Run Profile Execution.
                    // todo: work out how a sync-preview would work. we don't want to repeat ourselves unnecessarily (D.R.Y).
                    // I have thought about creating a preview response object and passing it in below, and if present, then the code stack builds the preview,
                    // so the same code stack can be used.

                    await ProcessConnectedSystemObjectAsync(activeSyncRules, connectedSystemObject);

                    _activity.ObjectsProcessed++;
                }
            }

            // batch persist all MVOs collected during this page
            await PersistPendingMetaverseObjectsAsync();

            // batch evaluate exports for all MVOs that changed during this page
            await EvaluatePendingExportsAsync();

            // update activity progress once per page instead of per object to reduce database round trips
            using (Diagnostics.Sync.StartSpan("UpdateActivityProgress"))
            {
                await _jim.Activities.UpdateActivityAsync(_activity);
            }
        }

        // TODO: work out if CSO changes have been persisted. Is a dedicated db update call needed?
        // ensure the activity and any pending db updates are applied.
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Resolving references");

        using (Diagnostics.Sync.StartSpan("ResolveReferences"))
        {
            await ResolveReferencesAsync();
        }

        syncSpan.SetSuccess();
    }

    /// <summary>
    /// Attempts to join/project/delete/flow attributes to the Metaverse for a single Connected System Object.
    /// </summary>
    private async Task ProcessConnectedSystemObjectAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        Log.Verbose($"ProcessConnectedSystemObjectAsync: Performing a full sync on Connected System Object: {connectedSystemObject}.");

        // we'll track all results to MVO and CSOs, both good and bad, using an Activity Run Profile Execution Item.
        var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();

        try
        {
            using (Diagnostics.Sync.StartSpan("ProcessPendingExport"))
            {
                await ProcessPendingExportAsync(connectedSystemObject, runProfileExecutionItem);
            }

            using (Diagnostics.Sync.StartSpan("ProcessObsoleteConnectedSystemObject"))
            {
                await ProcessObsoleteConnectedSystemObjectAsync(activeSyncRules, connectedSystemObject, runProfileExecutionItem);
            }

            // if the CSO isn't marked as obsolete (it might just have been), look to see if we need to make any related Metaverse Object changes.
            // this requires that we have sync rules defined.
            if (activeSyncRules.Count > 0 && connectedSystemObject.Status != ConnectedSystemObjectStatus.Obsolete)
            {
                using (Diagnostics.Sync.StartSpan("ProcessMetaverseObjectChanges"))
                {
                    await ProcessMetaverseObjectChangesAsync(activeSyncRules, connectedSystemObject, runProfileExecutionItem);
                }
            }
        }
        catch (Exception e)
        {
            // log the unhandled exception to the run profile execution item, so admins can see the error via a client.
            runProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError;
            runProfileExecutionItem.ErrorMessage = e.Message;
            runProfileExecutionItem.ErrorStackTrace = e.StackTrace;
            _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);

            // still perform system logging.
            Log.Error(e, $"ProcessConnectedSystemObjectAsync: Unhandled {_connectedSystemRunProfile} sync error whilst processing {connectedSystemObject}.");
        }
    }

    /// <summary>
    /// See if a Pending Export Object for a Connected System Object can be invalidated and deleted.
    /// This would occur when the Pending Export changes are visible on the Connected System Object after a confirming import.
    /// </summary>
    private async Task ProcessPendingExportAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        Log.Verbose($"ProcessPendingExportAsync: Executing for: {connectedSystemObject}.");

        // use pre-loaded pending exports dictionary for O(1) lookup instead of O(n) database query
        if (_pendingExportsByCsoId == null || !_pendingExportsByCsoId.TryGetValue(connectedSystemObject.Id, out var pendingExportsForThisCso))
        {
            Log.Verbose($"ProcessPendingExportAsync: No pending exports found for CSO {connectedSystemObject.Id}.");
            return;
        }

        if (pendingExportsForThisCso.Count == 0)
        {
            Log.Verbose($"ProcessPendingExportAsync: No pending exports found for CSO {connectedSystemObject.Id}.");
            return;
        }

        Log.Verbose($"ProcessPendingExportAsync: Found {pendingExportsForThisCso.Count} pending export(s) for CSO {connectedSystemObject.Id}.");

        foreach (var pendingExport in pendingExportsForThisCso)
        {
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
                    Log.Verbose($"ProcessPendingExportAsync: Attribute change for {attributeChange.AttributeId} confirmed on CSO.");
                }
                else
                {
                    failedChanges.Add(attributeChange);
                    Log.Verbose($"ProcessPendingExportAsync: Attribute change for {attributeChange.AttributeId} does not match CSO state.");
                }
            }

            // if all changes have been confirmed, delete the pending export
            if (failedChanges.Count == 0)
            {
                Log.Information($"ProcessPendingExportAsync: All changes confirmed for pending export {pendingExport.Id}. Deleting.");
                await _jim.ConnectedSystems.DeletePendingExportAsync(pendingExport);

                // remove from in-memory cache to keep it consistent
                pendingExportsForThisCso.Remove(pendingExport);
            }
            else if (successfulChanges.Count > 0)
            {
                // partial success: remove successful attribute changes, keep failed ones
                Log.Information($"ProcessPendingExportAsync: Partial success for pending export {pendingExport.Id}. " +
                    $"{successfulChanges.Count} succeeded, {failedChanges.Count} failed. Updating pending export.");

                // remove the successful attribute changes from the pending export
                foreach (var successfulChange in successfulChanges)
                {
                    pendingExport.AttributeValueChanges.Remove(successfulChange);
                }

                // increment error count and update status
                pendingExport.ErrorCount++;
                pendingExport.Status = JIM.Models.Transactional.PendingExportStatus.ExportNotImported;

                await _jim.ConnectedSystems.UpdatePendingExportAsync(pendingExport);
            }
            else
            {
                // complete failure: all attribute changes failed
                Log.Warning($"ProcessPendingExportAsync: Complete failure for pending export {pendingExport.Id}. " +
                    $"All {failedChanges.Count} attribute changes failed. Incrementing error count.");

                // increment error count and update status
                pendingExport.ErrorCount++;
                pendingExport.Status = JIM.Models.Transactional.PendingExportStatus.ExportNotImported;

                await _jim.ConnectedSystems.UpdatePendingExportAsync(pendingExport);
            }
        }
    }

    /// <summary>
    /// Checks if a CSO attribute value matches a pending export attribute change.
    /// </summary>
    private bool AttributeValuesMatch(ConnectedSystemObjectAttributeValue csoValue, JIM.Models.Transactional.PendingExportAttributeValueChange pendingChange)
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
    /// </summary>
    private async Task ProcessObsoleteConnectedSystemObjectAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        if (connectedSystemObject.Status != ConnectedSystemObjectStatus.Obsolete)
            return;

        if (connectedSystemObject.MetaverseObject == null)
        {
            // Not joined, just delete the CSO.
            await _jim.ConnectedSystems.DeleteConnectedSystemObjectAsync(connectedSystemObject, runProfileExecutionItem);
            return;
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
            await _jim.ConnectedSystems.DeleteConnectedSystemObjectAsync(connectedSystemObject, runProfileExecutionItem);
            return;
        }

        // InboundOutOfScopeAction = Disconnect (default) - break the join and handle MVO deletion rules
        var mvo = connectedSystemObject.MetaverseObject;
        var connectedSystemId = connectedSystemObject.ConnectedSystemId;

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
        var mvoId = mvo.Id;
        mvo.ConnectedSystemObjects.Remove(connectedSystemObject);
        connectedSystemObject.MetaverseObject = null;
        connectedSystemObject.MetaverseObjectId = null;
        connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.NotJoined;
        connectedSystemObject.DateJoined = null;
        Log.Verbose($"ProcessObsoleteConnectedSystemObjectAsync: Broke join between CSO {connectedSystemObject.Id} and MVO {mvoId}.");

        // Delete the CSO first so the database reflects the disconnection
        await _jim.ConnectedSystems.DeleteConnectedSystemObjectAsync(connectedSystemObject, runProfileExecutionItem);

        // Check if this was the last connector by querying the database for remaining CSOs
        // (the in-memory collection may not reflect CSOs from other connected systems)
        var remainingCsoCount = await _jim.ConnectedSystems.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(mvoId);
        if (remainingCsoCount == 0)
        {
            await ProcessMvoDeletionRuleAsync(mvo, runProfileExecutionItem);
        }
    }

    /// <summary>
    /// Determines the InboundOutOfScopeAction to use for a CSO by finding the applicable import sync rule.
    /// If multiple import sync rules exist for this CSO type, the first one's setting is used.
    /// Uses pre-loaded sync rules to avoid database round trips.
    /// </summary>
    private static InboundOutOfScopeAction DetermineInboundOutOfScopeAction(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
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
    /// Processes the MVO deletion rule when the last connector is disconnected.
    /// Following industry-standard identity management practices, this method NEVER deletes the MVO directly.
    /// Instead, it sets LastConnectorDisconnectedDate and lets housekeeping handle actual deletion.
    /// </summary>
    private async Task ProcessMvoDeletionRuleAsync(MetaverseObject mvo, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        if (mvo.Type == null)
        {
            Log.Warning($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} has no Type set. Cannot determine deletion rule.");
            return;
        }

        switch (mvo.Type.DeletionRule)
        {
            case MetaverseObjectDeletionRule.Manual:
                // No automatic deletion - MVO remains intact
                Log.Verbose($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} has DeletionRule=Manual. No automatic deletion.");
                break;

            case MetaverseObjectDeletionRule.WhenLastConnectorDisconnected:
                // Only apply to Projected MVOs (Internal MVOs like admin accounts are protected)
                if (mvo.Origin == MetaverseObjectOrigin.Internal)
                {
                    Log.Verbose($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} has Origin=Internal. Protected from automatic deletion.");
                    break;
                }

                // Record when the last connector was disconnected
                // This timestamp is used by housekeeping to determine when the grace period expires
                mvo.LastConnectorDisconnectedDate = DateTime.UtcNow;

                if (mvo.Type.DeletionGracePeriodDays.HasValue && mvo.Type.DeletionGracePeriodDays.Value > 0)
                {
                    Log.Information($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} marked for deletion (disconnected at {mvo.LastConnectorDisconnectedDate}). Eligible after {mvo.Type.DeletionGracePeriodDays.Value} days.");
                }
                else
                {
                    // No grace period - housekeeping will delete immediately on next run
                    Log.Information($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} marked for deletion (disconnected at {mvo.LastConnectorDisconnectedDate}). No grace period configured - will be deleted by housekeeping.");
                }

                // Persist the LastConnectorDisconnectedDate
                await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo);
                break;

            default:
                Log.Warning($"ProcessMvoDeletionRuleAsync: Unknown DeletionRule {mvo.Type.DeletionRule} for MVO {mvo.Id}.");
                break;
        }
    }

    /// <summary>
    /// Checks if the not-Obsolete CSO is joined to a Metaverse Object and updates it per any sync rules,
    /// or checks to see if a Metaverse Object needs creating (projecting the CSO) according to any sync rules.
    /// Changes to Metaverse Objects can have downstream impacts on other Connected System objects.
    /// </summary>
    private async Task ProcessMetaverseObjectChangesAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        Log.Verbose($"ProcessMetaverseObjectChangesAsync: Executing for: {connectedSystemObject}.");
        if (connectedSystemObject.Status == ConnectedSystemObjectStatus.Obsolete)
            return;

        if (activeSyncRules.Count == 0)
            return;

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
                await HandleCsoOutOfScopeAsync(connectedSystemObject, importSyncRules, runProfileExecutionItem);
            }
            return;
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
                await AttemptJoinAsync(scopedSyncRules, connectedSystemObject, runProfileExecutionItem);
            }

            // did we encounter an error whilst attempting a join? stop processing the CSO if so.
            if (runProfileExecutionItem.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
                return;

            // were we able to join to an existing MVO?
            if (connectedSystemObject.MetaverseObject == null)
            {
                // try and project the CSO to the Metaverse.
                // this may cause onward sync operations, so may take time.
                // Only use in-scope sync rules for projection
                using (Diagnostics.Sync.StartSpan("AttemptProjection"))
                {
                    AttemptProjection(scopedSyncRules, connectedSystemObject);
                }
            }
        }

        // are we joined yet?
        if (connectedSystemObject.MetaverseObject != null)
        {
            // process sync rules to see if we need to flow any attribute updates from the CSO to the MVO.
            using (Diagnostics.Sync.StartSpan("ProcessInboundAttributeFlow"))
            {
                foreach (var inboundSyncRule in activeSyncRules.Where(sr => sr.Direction == SyncRuleDirection.Import && sr.ConnectedSystemObjectTypeId == connectedSystemObject.TypeId))
                {
                    // evaluate inbound attribute flow rules
                    ProcessInboundAttributeFlow(connectedSystemObject, inboundSyncRule);
                }
            }

            // Collect changed attributes BEFORE applying pending changes (we need them for export evaluation)
            var changedAttributes = connectedSystemObject.MetaverseObject.PendingAttributeValueAdditions
                .Concat(connectedSystemObject.MetaverseObject.PendingAttributeValueRemovals)
                .ToList();

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
                _pendingExportEvaluations.Add((connectedSystemObject.MetaverseObject, changedAttributes));
            }
        }
    }

    /// <summary>
    /// Evaluates export rules for an MVO that has changed during inbound sync.
    /// Creates PendingExports for any connected systems that need to be updated.
    /// Also evaluates if MVO has fallen out of scope for any export rules (deprovisioning).
    /// Implements Q1 decision: evaluate exports immediately when MVO changes.
    /// Uses pre-cached export rules and CSO lookups for O(1) access instead of O(N×M) database queries.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed (must have a valid Id assigned).</param>
    /// <param name="changedAttributes">The list of attribute values that changed.</param>
    private async Task EvaluateOutboundExportsAsync(MetaverseObject mvo, List<MetaverseObjectAttributeValue> changedAttributes)
    {
        if (_exportEvaluationCache == null)
        {
            Log.Warning("EvaluateOutboundExportsAsync: Export evaluation cache not initialised, skipping export evaluation for MVO {MvoId}", mvo.Id);
            return;
        }

        // Evaluate export rules for MVOs that are IN scope, using cached data for O(1) lookups
        List<JIM.Models.Transactional.PendingExport> pendingExports;
        using (Diagnostics.Sync.StartSpan("EvaluateExportRules"))
        {
            pendingExports = await _jim.ExportEvaluation.EvaluateExportRulesAsync(
                mvo,
                changedAttributes,
                _connectedSystem,
                _exportEvaluationCache);
        }

        if (pendingExports.Count > 0)
        {
            Log.Information("EvaluateOutboundExportsAsync: Created {Count} pending exports for MVO {MvoId}",
                pendingExports.Count, mvo.Id);
        }

        // Evaluate if MVO has fallen OUT of scope for any export rules (deprovisioning), using cached data
        List<JIM.Models.Transactional.PendingExport> deprovisioningExports;
        using (Diagnostics.Sync.StartSpan("EvaluateOutOfScopeExports"))
        {
            deprovisioningExports = await _jim.ExportEvaluation.EvaluateOutOfScopeExportsAsync(
                mvo,
                _connectedSystem,
                _exportEvaluationCache);
        }

        if (deprovisioningExports.Count > 0)
        {
            Log.Information("EvaluateOutboundExportsAsync: Created {Count} deprovisioning exports for MVO {MvoId}",
                deprovisioningExports.Count, mvo.Id);
        }
    }

    /// <summary>
    /// Batch persists all pending MVO creates and updates collected during the current page.
    /// This reduces database round trips from n writes to 2 writes (one for creates, one for updates).
    /// After creates are persisted, CSO foreign keys are updated with the newly assigned MVO IDs.
    /// </summary>
    private async Task PersistPendingMetaverseObjectsAsync()
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

            // After batch save, EF has assigned IDs - update the CSO foreign keys
            foreach (var mvo in _pendingMvoCreates)
            {
                foreach (var cso in mvo.ConnectedSystemObjects)
                {
                    cso.MetaverseObjectId = mvo.Id;
                }
            }

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
    /// Batch evaluates export rules for all MVOs that changed during the current page.
    /// Must be called after PersistPendingMetaverseObjectsAsync so MVOs have valid IDs for pending export FKs.
    /// </summary>
    private async Task EvaluatePendingExportsAsync()
    {
        if (_pendingExportEvaluations.Count == 0)
            return;

        using var span = Diagnostics.Sync.StartSpan("EvaluatePendingExports");
        span.SetTag("count", _pendingExportEvaluations.Count);

        foreach (var (mvo, changedAttributes) in _pendingExportEvaluations)
        {
            await EvaluateOutboundExportsAsync(mvo, changedAttributes);
        }

        Log.Verbose("EvaluatePendingExportsAsync: Evaluated exports for {Count} MVOs", _pendingExportEvaluations.Count);
        _pendingExportEvaluations.Clear();

        span.SetSuccess();
    }

    /// <summary>
    /// Attempts to find a Metaverse Object that matches the CSO using Object Matching Rules on any applicable Sync Rules for this system and object type.
    /// </summary>
    /// <param name="activeSyncRules">The active sync rules that contain all possible join rules to be evaluated.</param>
    /// <param name="connectedSystemObject">The Connected System Object to try and find a matching Metaverse Object for.</param>
    /// <param name="runProfileExecutionItem">The Run Profile Execution Item we're tracking changes/errors against for the CSO.</param>
    /// <returns>True if a join was established, otherwise False.</returns>
    /// <exception cref="InvalidDataException">Will be thrown if an unsupported join state is found preventing processing.</exception>
    private async Task AttemptJoinAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
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
                runProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.AmbiguousMatch;
                runProfileExecutionItem.ErrorMessage = $"Multiple Metaverse Objects ({ex.Matches.Count}) match this Connected System Object. " +
                    $"An MVO can only be joined to a single CSO per Connected System. " +
                    $"Check your Object Matching Rules to ensure unique matches. Matching MVO IDs: {string.Join(", ", ex.Matches)}";
                _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);
                return;
            }

            if (mvo == null)
                continue;

            // MVO must not already be joined to a connected system object in this connected system. Joins are 1:1.
            var existingCsoJoins = mvo.ConnectedSystemObjects.Where(q => q.ConnectedSystemId == _connectedSystem.Id).ToList();

            if (existingCsoJoins.Count > 1)
                throw new InvalidDataException($"More than one CSO is already joined to the MVO {mvo} we found that matches the matching rules. This is not good!");

            if (existingCsoJoins.Count == 1)
            {
                runProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.CouldNotJoinDueToExistingJoin;
                runProfileExecutionItem.ErrorMessage = $"Would have joined this Connector Space Object to a Metaverse Object ({mvo}), but that already has a join to CSO " +
                                                       $"{existingCsoJoins[0]}. Check the attributes on this object are not duplicated, and/or check your " +
                                                       $"Object Matching Rules for uniqueness.";
                _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);
                return;
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
            return;
        }

        // No join could be established.
    }

    /// <summary>
    /// Attempts to create a Metaverse Object from the Connected System Object using the first Sync Rule for the object type that has Projection enabled.
    /// </summary>
    /// <param name="activeSyncRules">The active sync rules that contain projection and attribute flow information.</param>
    /// <param name="connectedSystemObject">The Connected System Object to attempt to project to the Metaverse.</param>
    /// <exception cref="InvalidDataException">Will be thrown if not all required properties are populated on the Sync Rule.</exception>
    /// <exception cref="NotImplementedException">Will be thrown if a Sync Rule attempts to use a Function as a source.</exception>
    private static void AttemptProjection(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        // see if there are any sync rules for this object type where projection is enabled. first to project, wins.
        var projectionSyncRule = activeSyncRules?.FirstOrDefault(sr =>
            sr.ProjectToMetaverse.HasValue && sr.ProjectToMetaverse.Value &&
            sr.ConnectedSystemObjectType.Id == connectedSystemObject.TypeId);

        if (projectionSyncRule == null)
            return;

        // create the MVO using type from the Sync Rule.
        // Note: Do NOT assign Id here - let it remain Guid.Empty so that
        // ProcessMetaverseObjectChangesAsync knows to call CreateMetaverseObjectAsync.
        // The Id will be assigned by EF when the MVO is saved to the database.
        var mvo = new MetaverseObject();
        mvo.ConnectedSystemObjects.Add(connectedSystemObject);
        mvo.Type = projectionSyncRule.MetaverseObjectType;
        connectedSystemObject.MetaverseObject = mvo;
        // Don't set MetaverseObjectId yet - it will be set after the MVO is persisted
        connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.Projected;
        connectedSystemObject.DateJoined = DateTime.UtcNow;

        // do not flow attributes at this point. let that happen separately, so we don't re-process sync rules later.
    }

    /// <summary>
    /// Assigns values to a Metaverse Object, from a Connected System Object using a Sync Rule.
    /// Does not perform any delta processing. This is for MVO create scenarios where there are not MVO attribute values already.
    /// </summary>
    /// <param name="connectedSystemObject">The source Connected System Object to map values from.</param>
    /// <param name="syncRule">The Sync Rule to use to determine which attributes, and how should be assigned to the Metaverse Object.</param>
    /// <exception cref="InvalidDataException">Can be thrown if a Sync Rule Mapping Source is not properly formed.</exception>
    /// <exception cref="NotImplementedException">Will be thrown whilst Functions have not been implemented, but are being used in the Sync Rule.</exception>
    private void ProcessInboundAttributeFlow(ConnectedSystemObject connectedSystemObject, SyncRule syncRule)
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

            SyncRuleMappingProcessor.Process(connectedSystemObject, syncRuleMapping, _objectTypes);
        }
    }

    /// <summary>
    /// Builds a Metaverse Object Attribute Value using values from a Connected System Object Attribute Value and assigns it to a Metaverse Object.
    /// </summary>
    /// <param name="metaverseObject">The Metaverse Object to add the Attribute Value to.</param>
    /// <param name="metaverseAttribute">The Metaverse Attribute the Attribute Value will be for.</param>
    /// <param name="connectedSystemObjectAttributeValue">The source for the values on the Metaverse Object Attribute Value.</param>
    private void SetMetaverseObjectAttributeValue(
        MetaverseObject metaverseObject, MetaverseAttribute metaverseAttribute, ConnectedSystemObjectAttributeValue connectedSystemObjectAttributeValue)
    {
        // TODO: review for evolution to handle update/delete scenarios

        metaverseObject.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            MetaverseObject = metaverseObject,
            Attribute = metaverseAttribute,
            ContributedBySystem = _connectedSystem,
            StringValue = connectedSystemObjectAttributeValue.StringValue,
            BoolValue = connectedSystemObjectAttributeValue.BoolValue,
            ByteValue = connectedSystemObjectAttributeValue.ByteValue,
            GuidValue = connectedSystemObjectAttributeValue.GuidValue,
            IntValue = connectedSystemObjectAttributeValue.IntValue,
            DateTimeValue = connectedSystemObjectAttributeValue.DateTimeValue,
            UnresolvedReferenceValue = connectedSystemObjectAttributeValue.ConnectedSystemObject,
            UnresolvedReferenceValueId = connectedSystemObjectAttributeValue.ConnectedSystemObject.Id
        });
    }

    /// <summary>
    /// Applies pending attribute value changes to a Metaverse Object.
    /// This moves values from PendingAttributeValueAdditions to AttributeValues
    /// and removes values listed in PendingAttributeValueRemovals.
    /// </summary>
    /// <param name="mvo">The Metaverse Object to apply pending changes to.</param>
    private static void ApplyPendingMetaverseObjectAttributeChanges(MetaverseObject mvo)
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
    private Task ResolveReferencesAsync()
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
    private async Task<List<SyncRule>> GetInScopeImportRulesAsync(ConnectedSystemObject connectedSystemObject, List<SyncRule> importSyncRules)
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
    private async Task HandleCsoOutOfScopeAsync(
        ConnectedSystemObject connectedSystemObject,
        List<SyncRule> importSyncRules,
        ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        // If not joined, nothing special to do - just skip processing
        if (connectedSystemObject.MetaverseObject == null)
        {
            Log.Verbose("HandleCsoOutOfScopeAsync: CSO {CsoId} is not joined, skipping out-of-scope processing", connectedSystemObject.Id);
            return;
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
                break;

            case InboundOutOfScopeAction.Disconnect:
            default:
                // Break the join between CSO and MVO
                Log.Information("HandleCsoOutOfScopeAsync: CSO {CsoId} is out of scope. InboundOutOfScopeAction=Disconnect. Breaking join.",
                    connectedSystemObject.Id);

                var mvo = connectedSystemObject.MetaverseObject;
                var mvoId = mvo.Id;

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

                // Check if this was the last connector
                var remainingCsoCount = await _jim.ConnectedSystems.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(mvoId);
                if (remainingCsoCount == 0)
                {
                    await ProcessMvoDeletionRuleAsync(mvo, runProfileExecutionItem);
                }
                break;
        }
    }
}

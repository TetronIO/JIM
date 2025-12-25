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

/// <summary>
/// Processes delta synchronisation for a Connected System.
/// Unlike full sync which processes ALL CSOs, delta sync only processes CSOs
/// that have been modified since the last sync completed (based on LastUpdated timestamp).
/// This provides significant performance improvements when only a small subset of objects changed.
/// </summary>
public class SyncDeltaSyncTaskProcessor
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

    public SyncDeltaSyncTaskProcessor(
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

    public async Task PerformDeltaSyncAsync()
    {
        using var syncSpan = Diagnostics.Sync.StartSpan("DeltaSync");
        syncSpan.SetTag("connectedSystemId", _connectedSystem.Id);
        syncSpan.SetTag("connectedSystemName", _connectedSystem.Name);

        Log.Verbose("PerformDeltaSyncAsync: Starting");

        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Preparing delta sync");

        // Determine the watermark - when was the last successful sync?
        var lastSyncTimestamp = _connectedSystem.LastDeltaSyncCompletedAt ?? DateTime.MinValue;
        syncSpan.SetTag("lastSyncTimestamp", lastSyncTimestamp.ToString("O"));

        // How many CSOs have been modified since the last sync?
        int totalCsosToProcess;
        using (Diagnostics.Sync.StartSpan("CountModifiedCsos"))
        {
            totalCsosToProcess = await _jim.ConnectedSystems.GetConnectedSystemObjectModifiedSinceCountAsync(
                _connectedSystem.Id,
                lastSyncTimestamp);
        }

        syncSpan.SetTag("modifiedCsoCount", totalCsosToProcess);
        Log.Information("PerformDeltaSyncAsync: Found {Count} CSOs modified since {Timestamp}",
            totalCsosToProcess, lastSyncTimestamp);

        // If no CSOs have changed, we can complete quickly
        if (totalCsosToProcess == 0)
        {
            Log.Information("PerformDeltaSyncAsync: No CSOs modified since last sync. Completing immediately.");
            await _jim.Activities.UpdateActivityMessageAsync(_activity, "No changes to process");

            // Update the watermark even when there are no changes
            await UpdateDeltaSyncWatermarkAsync();
            return;
        }

        // Count pending exports (still need to process these)
        var totalPendingExportObjectsToProcess = await _jim.ConnectedSystems.GetPendingExportsCountAsync(_connectedSystem.Id);
        var totalObjectsToProcess = totalCsosToProcess + totalPendingExportObjectsToProcess;
        _activity.ObjectsToProcess = totalObjectsToProcess;
        _activity.ObjectsProcessed = 0;
        await _jim.Activities.UpdateActivityAsync(_activity);

        // Get all the active sync rules for this system
        List<SyncRule> activeSyncRules;
        using (Diagnostics.Sync.StartSpan("LoadSyncRules"))
        {
            activeSyncRules = await _jim.ConnectedSystems.GetSyncRulesAsync(_connectedSystem.Id, false);
        }

        // Get the schema for all object types upfront
        using (Diagnostics.Sync.StartSpan("LoadObjectTypes"))
        {
            _objectTypes = await _jim.ConnectedSystems.GetObjectTypesAsync(_connectedSystem.Id);
        }

        // Load all pending exports once upfront and index by CSO ID for O(1) lookup
        using (Diagnostics.Sync.StartSpan("LoadPendingExports"))
        {
            var allPendingExports = await _jim.ConnectedSystems.GetPendingExportsAsync(_connectedSystem.Id);
            _pendingExportsByCsoId = allPendingExports
                .Where(pe => pe.ConnectedSystemObject?.Id != null)
                .GroupBy(pe => pe.ConnectedSystemObject!.Id)
                .ToDictionary(g => g.Key, g => g.ToList());
            Log.Verbose("PerformDeltaSyncAsync: Loaded {Count} pending exports into lookup dictionary", allPendingExports.Count);
        }

        // Pre-load export evaluation cache
        using (Diagnostics.Sync.StartSpan("LoadExportEvaluationCache"))
        {
            _exportEvaluationCache = await _jim.ExportEvaluation.BuildExportEvaluationCacheAsync(_connectedSystem.Id);
        }

        // Process only the modified CSOs in batches
        const int pageSize = 200;
        var totalCsoPages = Convert.ToInt16(Math.Ceiling((double)totalCsosToProcess / pageSize));
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Processing modified Connected System Objects");

        using var processCsosSpan = Diagnostics.Sync.StartSpan("ProcessModifiedConnectedSystemObjects");
        processCsosSpan.SetTag("totalObjects", totalCsosToProcess);
        processCsosSpan.SetTag("pageSize", pageSize);
        processCsosSpan.SetTag("totalPages", totalCsoPages);

        for (var page = 1; page <= totalCsoPages; page++)
        {
            PagedResultSet<ConnectedSystemObject> csoPagedResult;
            using (Diagnostics.Sync.StartSpan("LoadModifiedCsoPage"))
            {
                // Use the delta-specific query that filters by LastUpdated > watermark
                // Note: Page is 1-indexed to match the repository's paging convention
                csoPagedResult = await _jim.ConnectedSystems.GetConnectedSystemObjectsModifiedSinceAsync(
                    _connectedSystem.Id,
                    lastSyncTimestamp,
                    page,
                    pageSize);
            }

            using (Diagnostics.Sync.StartSpan("ProcessCsoLoop").SetTag("csoCount", csoPagedResult.Results.Count))
            {
                foreach (var connectedSystemObject in csoPagedResult.Results)
                {
                    // Check for cancellation request
                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        Log.Information("PerformDeltaSyncAsync: Cancellation requested. Stopping CSO enumeration.");
                        return;
                    }

                    await ProcessConnectedSystemObjectAsync(activeSyncRules, connectedSystemObject);
                    _activity.ObjectsProcessed++;
                }
            }

            // Batch persist all MVOs collected during this page
            await PersistPendingMetaverseObjectsAsync();

            // Batch evaluate exports for all MVOs that changed during this page
            await EvaluatePendingExportsAsync();

            // Update activity progress once per page
            using (Diagnostics.Sync.StartSpan("UpdateActivityProgress"))
            {
                await _jim.Activities.UpdateActivityAsync(_activity);
            }
        }

        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Resolving references");

        using (Diagnostics.Sync.StartSpan("ResolveReferences"))
        {
            await ResolveReferencesAsync();
        }

        // Update the watermark to mark this sync as complete
        await UpdateDeltaSyncWatermarkAsync();

        syncSpan.SetSuccess();
    }

    /// <summary>
    /// Updates the Connected System's LastDeltaSyncCompletedAt timestamp to mark the sync as complete.
    /// This becomes the watermark for the next delta sync.
    /// </summary>
    private async Task UpdateDeltaSyncWatermarkAsync()
    {
        using var span = Diagnostics.Sync.StartSpan("UpdateDeltaSyncWatermark");

        _connectedSystem.LastDeltaSyncCompletedAt = DateTime.UtcNow;
        await _jim.ConnectedSystems.UpdateConnectedSystemAsync(_connectedSystem, null, _activity);

        Log.Information("PerformDeltaSyncAsync: Updated delta sync watermark to {Timestamp}",
            _connectedSystem.LastDeltaSyncCompletedAt);

        span.SetSuccess();
    }

    // ============================================================================
    // The following methods are identical to SyncFullSyncTaskProcessor.
    // In future, consider extracting to a base class or shared helper to reduce duplication.
    // ============================================================================

    /// <summary>
    /// Attempts to join/project/delete/flow attributes to the Metaverse for a single Connected System Object.
    /// </summary>
    private async Task ProcessConnectedSystemObjectAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        Log.Verbose($"ProcessConnectedSystemObjectAsync: Performing a delta sync on Connected System Object: {connectedSystemObject}.");

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
            runProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError;
            runProfileExecutionItem.ErrorMessage = e.Message;
            runProfileExecutionItem.ErrorStackTrace = e.StackTrace;
            _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);

            Log.Error(e, $"ProcessConnectedSystemObjectAsync: Unhandled {_connectedSystemRunProfile} sync error whilst processing {connectedSystemObject}.");
        }
    }

    private async Task ProcessPendingExportAsync(ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        Log.Verbose($"ProcessPendingExportAsync: Executing for: {connectedSystemObject}.");

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
            var successfulChanges = new List<JIM.Models.Transactional.PendingExportAttributeValueChange>();
            var failedChanges = new List<JIM.Models.Transactional.PendingExportAttributeValueChange>();

            foreach (var attributeChange in pendingExport.AttributeValueChanges)
            {
                var csoAttributeValue = connectedSystemObject.AttributeValues
                    .FirstOrDefault(av => av.AttributeId == attributeChange.AttributeId);

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

            if (failedChanges.Count == 0)
            {
                Log.Information($"ProcessPendingExportAsync: All changes confirmed for pending export {pendingExport.Id}. Deleting.");
                await _jim.ConnectedSystems.DeletePendingExportAsync(pendingExport);
                pendingExportsForThisCso.Remove(pendingExport);
            }
            else if (successfulChanges.Count > 0)
            {
                Log.Information($"ProcessPendingExportAsync: Partial success for pending export {pendingExport.Id}. " +
                    $"{successfulChanges.Count} succeeded, {failedChanges.Count} failed. Updating pending export.");

                foreach (var successfulChange in successfulChanges)
                {
                    pendingExport.AttributeValueChanges.Remove(successfulChange);
                }

                pendingExport.ErrorCount++;
                pendingExport.Status = JIM.Models.Transactional.PendingExportStatus.ExportNotImported;

                await _jim.ConnectedSystems.UpdatePendingExportAsync(pendingExport);
            }
            else
            {
                Log.Warning($"ProcessPendingExportAsync: Complete failure for pending export {pendingExport.Id}. " +
                    $"All {failedChanges.Count} attribute changes failed. Incrementing error count.");

                pendingExport.ErrorCount++;
                pendingExport.Status = JIM.Models.Transactional.PendingExportStatus.ExportNotImported;

                await _jim.ConnectedSystems.UpdatePendingExportAsync(pendingExport);
            }
        }
    }

    private bool AttributeValuesMatch(ConnectedSystemObjectAttributeValue csoValue, JIM.Models.Transactional.PendingExportAttributeValueChange pendingChange)
    {
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

    private async Task ProcessObsoleteConnectedSystemObjectAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        if (connectedSystemObject.Status != ConnectedSystemObjectStatus.Obsolete)
            return;

        if (connectedSystemObject.MetaverseObject == null)
        {
            await _jim.ConnectedSystems.DeleteConnectedSystemObjectAsync(connectedSystemObject, runProfileExecutionItem);
            return;
        }

        var inboundOutOfScopeAction = DetermineInboundOutOfScopeAction(activeSyncRules, connectedSystemObject);

        if (inboundOutOfScopeAction == InboundOutOfScopeAction.RemainJoined)
        {
            Log.Information($"ProcessObsoleteConnectedSystemObjectAsync: InboundOutOfScopeAction=RemainJoined for CSO {connectedSystemObject.Id}. " +
                "CSO will be deleted but MVO join state preserved (object considered 'always managed').");

            await _jim.ConnectedSystems.DeleteConnectedSystemObjectAsync(connectedSystemObject, runProfileExecutionItem);
            return;
        }

        var mvo = connectedSystemObject.MetaverseObject;
        var connectedSystemId = connectedSystemObject.ConnectedSystemId;

        if (connectedSystemObject.Type.RemoveContributedAttributesOnObsoletion)
        {
            var contributedAttributes = mvo.AttributeValues
                .Where(av => av.ContributedBySystem?.Id == connectedSystemId)
                .ToList();

            foreach (var attributeValue in contributedAttributes)
            {
                mvo.PendingAttributeValueRemovals.Add(attributeValue);
                Log.Verbose($"ProcessObsoleteConnectedSystemObjectAsync: Marking attribute '{attributeValue.Attribute?.Name}' for removal from MVO {mvo.Id}.");
            }
        }

        var mvoId = mvo.Id;
        mvo.ConnectedSystemObjects.Remove(connectedSystemObject);
        connectedSystemObject.MetaverseObject = null;
        connectedSystemObject.MetaverseObjectId = null;
        connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.NotJoined;
        connectedSystemObject.DateJoined = null;
        Log.Verbose($"ProcessObsoleteConnectedSystemObjectAsync: Broke join between CSO {connectedSystemObject.Id} and MVO {mvoId}.");

        await _jim.ConnectedSystems.DeleteConnectedSystemObjectAsync(connectedSystemObject, runProfileExecutionItem);

        var remainingCsoCount = await _jim.ConnectedSystems.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(mvoId);
        if (remainingCsoCount == 0)
        {
            await ProcessMvoDeletionRuleAsync(mvo, runProfileExecutionItem);
        }
    }

    private static InboundOutOfScopeAction DetermineInboundOutOfScopeAction(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        var importSyncRule = activeSyncRules.FirstOrDefault(sr =>
            sr.Direction == SyncRuleDirection.Import &&
            sr.Enabled &&
            sr.ConnectedSystemObjectTypeId == connectedSystemObject.TypeId);

        if (importSyncRule == null)
        {
            return InboundOutOfScopeAction.Disconnect;
        }

        return importSyncRule.InboundOutOfScopeAction;
    }

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
                Log.Verbose($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} has DeletionRule=Manual. No automatic deletion.");
                break;

            case MetaverseObjectDeletionRule.WhenLastConnectorDisconnected:
                if (mvo.Origin == MetaverseObjectOrigin.Internal)
                {
                    Log.Verbose($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} has Origin=Internal. Protected from automatic deletion.");
                    break;
                }

                mvo.LastConnectorDisconnectedDate = DateTime.UtcNow;

                if (mvo.Type.DeletionGracePeriodDays.HasValue && mvo.Type.DeletionGracePeriodDays.Value > 0)
                {
                    Log.Information($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} marked for deletion (disconnected at {mvo.LastConnectorDisconnectedDate}). Eligible after {mvo.Type.DeletionGracePeriodDays.Value} days.");
                }
                else
                {
                    Log.Information($"ProcessMvoDeletionRuleAsync: MVO {mvo.Id} marked for deletion (disconnected at {mvo.LastConnectorDisconnectedDate}). No grace period configured - will be deleted by housekeeping.");
                }

                await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo);
                break;

            default:
                Log.Warning($"ProcessMvoDeletionRuleAsync: Unknown DeletionRule {mvo.Type.DeletionRule} for MVO {mvo.Id}.");
                break;
        }
    }

    private async Task ProcessMetaverseObjectChangesAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        Log.Verbose($"ProcessMetaverseObjectChangesAsync: Executing for: {connectedSystemObject}.");
        if (connectedSystemObject.Status == ConnectedSystemObjectStatus.Obsolete)
            return;

        if (activeSyncRules.Count == 0)
            return;

        var importSyncRules = activeSyncRules
            .Where(sr => sr.Direction == SyncRuleDirection.Import && sr.ConnectedSystemObjectTypeId == connectedSystemObject.TypeId)
            .ToList();

        List<SyncRule> inScopeImportRules;
        using (Diagnostics.Sync.StartSpan("GetInScopeImportRules"))
        {
            inScopeImportRules = await GetInScopeImportRulesAsync(connectedSystemObject, importSyncRules);
        }

        if (inScopeImportRules.Count == 0 && importSyncRules.Any(sr => sr.ObjectScopingCriteriaGroups.Count > 0))
        {
            Log.Debug("ProcessMetaverseObjectChangesAsync: CSO {CsoId} is out of scope for all import sync rules", connectedSystemObject.Id);

            using (Diagnostics.Sync.StartSpan("HandleCsoOutOfScope"))
            {
                await HandleCsoOutOfScopeAsync(connectedSystemObject, importSyncRules, runProfileExecutionItem);
            }
            return;
        }

        if (connectedSystemObject.MetaverseObject == null)
        {
            var scopedSyncRules = inScopeImportRules.Count > 0 ? inScopeImportRules : activeSyncRules;

            using (Diagnostics.Sync.StartSpan("AttemptJoin"))
            {
                await AttemptJoinAsync(scopedSyncRules, connectedSystemObject, runProfileExecutionItem);
            }

            if (runProfileExecutionItem.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
                return;

            if (connectedSystemObject.MetaverseObject == null)
            {
                using (Diagnostics.Sync.StartSpan("AttemptProjection"))
                {
                    AttemptProjection(scopedSyncRules, connectedSystemObject);
                }
            }
        }

        if (connectedSystemObject.MetaverseObject != null)
        {
            using (Diagnostics.Sync.StartSpan("ProcessInboundAttributeFlow"))
            {
                foreach (var inboundSyncRule in activeSyncRules.Where(sr => sr.Direction == SyncRuleDirection.Import && sr.ConnectedSystemObjectTypeId == connectedSystemObject.TypeId))
                {
                    ProcessInboundAttributeFlow(connectedSystemObject, inboundSyncRule);
                }
            }

            var changedAttributes = connectedSystemObject.MetaverseObject.PendingAttributeValueAdditions
                .Concat(connectedSystemObject.MetaverseObject.PendingAttributeValueRemovals)
                .ToList();

            ApplyPendingMetaverseObjectAttributeChanges(connectedSystemObject.MetaverseObject);

            if (connectedSystemObject.MetaverseObject.Id == Guid.Empty)
            {
                _pendingMvoCreates.Add(connectedSystemObject.MetaverseObject);
            }
            else
            {
                _pendingMvoUpdates.Add(connectedSystemObject.MetaverseObject);
            }

            if (changedAttributes.Count > 0)
            {
                _pendingExportEvaluations.Add((connectedSystemObject.MetaverseObject, changedAttributes));
            }
        }
    }

    private async Task EvaluateOutboundExportsAsync(MetaverseObject mvo, List<MetaverseObjectAttributeValue> changedAttributes)
    {
        if (_exportEvaluationCache == null)
        {
            Log.Warning("EvaluateOutboundExportsAsync: Export evaluation cache not initialised, skipping export evaluation for MVO {MvoId}", mvo.Id);
            return;
        }

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

    private async Task PersistPendingMetaverseObjectsAsync()
    {
        if (_pendingMvoCreates.Count == 0 && _pendingMvoUpdates.Count == 0)
            return;

        using var span = Diagnostics.Sync.StartSpan("PersistPendingMetaverseObjects");
        span.SetTag("createCount", _pendingMvoCreates.Count);
        span.SetTag("updateCount", _pendingMvoUpdates.Count);

        if (_pendingMvoCreates.Count > 0)
        {
            await _jim.Metaverse.CreateMetaverseObjectsAsync(_pendingMvoCreates);

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

        if (_pendingMvoUpdates.Count > 0)
        {
            await _jim.Metaverse.UpdateMetaverseObjectsAsync(_pendingMvoUpdates);
            Log.Verbose("PersistPendingMetaverseObjectsAsync: Updated {Count} MVOs in batch", _pendingMvoUpdates.Count);
            _pendingMvoUpdates.Clear();
        }

        span.SetSuccess();
    }

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

    private async Task AttemptJoinAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject, ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        foreach (var importSyncRule in activeSyncRules.Where(sr => sr.Direction == SyncRuleDirection.Import && sr.ConnectedSystemObjectTypeId == connectedSystemObject.TypeId))
        {
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

            connectedSystemObject.MetaverseObject = mvo;
            connectedSystemObject.MetaverseObjectId = mvo.Id;
            connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.Joined;
            connectedSystemObject.DateJoined = DateTime.UtcNow;
            mvo.ConnectedSystemObjects.Add(connectedSystemObject);

            if (mvo.LastConnectorDisconnectedDate.HasValue)
            {
                Log.Information($"AttemptJoinAsync: Clearing LastConnectorDisconnectedDate for MVO {mvo.Id} as connector has reconnected.");
                mvo.LastConnectorDisconnectedDate = null;
            }

            Log.Information("AttemptJoinAsync: Established join between CSO {CsoId} and MVO {MvoId}", connectedSystemObject.Id, mvo.Id);
            return;
        }
    }

    private static void AttemptProjection(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        var projectionSyncRule = activeSyncRules?.FirstOrDefault(sr =>
            sr.ProjectToMetaverse.HasValue && sr.ProjectToMetaverse.Value &&
            sr.ConnectedSystemObjectType.Id == connectedSystemObject.TypeId);

        if (projectionSyncRule == null)
            return;

        var mvo = new MetaverseObject();
        mvo.ConnectedSystemObjects.Add(connectedSystemObject);
        mvo.Type = projectionSyncRule.MetaverseObjectType;
        connectedSystemObject.MetaverseObject = mvo;
        connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.Projected;
        connectedSystemObject.DateJoined = DateTime.UtcNow;
    }

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

    private static void ApplyPendingMetaverseObjectAttributeChanges(MetaverseObject mvo)
    {
        var addCount = mvo.PendingAttributeValueAdditions.Count;
        var removeCount = mvo.PendingAttributeValueRemovals.Count;

        if (addCount == 0 && removeCount == 0)
            return;

        foreach (var removal in mvo.PendingAttributeValueRemovals)
        {
            mvo.AttributeValues.Remove(removal);
        }

        foreach (var addition in mvo.PendingAttributeValueAdditions)
        {
            mvo.AttributeValues.Add(addition);
        }

        mvo.PendingAttributeValueRemovals.Clear();
        mvo.PendingAttributeValueAdditions.Clear();

        Log.Verbose("ApplyPendingMetaverseObjectAttributeChanges: Applied {AddCount} additions and {RemoveCount} removals to MVO {MvoId}",
            addCount, removeCount, mvo.Id);
    }

    private Task ResolveReferencesAsync()
    {
        return Task.CompletedTask;
    }

    private async Task<List<SyncRule>> GetInScopeImportRulesAsync(ConnectedSystemObject connectedSystemObject, List<SyncRule> importSyncRules)
    {
        var inScopeRules = new List<SyncRule>();
        var csoWithAttributes = connectedSystemObject;

        foreach (var syncRule in importSyncRules)
        {
            if (syncRule.ObjectScopingCriteriaGroups.Count == 0)
            {
                inScopeRules.Add(syncRule);
                continue;
            }

            if (csoWithAttributes.AttributeValues.Count == 0)
            {
                var fullCso = await _jim.ConnectedSystems.GetConnectedSystemObjectAsync(_connectedSystem.Id, connectedSystemObject.Id);
                if (fullCso != null)
                {
                    csoWithAttributes = fullCso;
                    connectedSystemObject.AttributeValues = fullCso.AttributeValues;
                }
            }

            if (_jim.ScopingEvaluation.IsCsoInScopeForImportRule(csoWithAttributes, syncRule))
            {
                inScopeRules.Add(syncRule);
            }
        }

        return inScopeRules;
    }

    private async Task HandleCsoOutOfScopeAsync(
        ConnectedSystemObject connectedSystemObject,
        List<SyncRule> importSyncRules,
        ActivityRunProfileExecutionItem runProfileExecutionItem)
    {
        if (connectedSystemObject.MetaverseObject == null)
        {
            Log.Verbose("HandleCsoOutOfScopeAsync: CSO {CsoId} is not joined, skipping out-of-scope processing", connectedSystemObject.Id);
            return;
        }

        var inboundOutOfScopeAction = importSyncRules
            .Where(sr => sr.ObjectScopingCriteriaGroups.Count > 0)
            .Select(sr => sr.InboundOutOfScopeAction)
            .FirstOrDefault();

        switch (inboundOutOfScopeAction)
        {
            case InboundOutOfScopeAction.RemainJoined:
                Log.Information("HandleCsoOutOfScopeAsync: CSO {CsoId} is out of scope but InboundOutOfScopeAction=RemainJoined. " +
                    "Join preserved, no attribute flow.", connectedSystemObject.Id);
                break;

            case InboundOutOfScopeAction.Disconnect:
            default:
                Log.Information("HandleCsoOutOfScopeAsync: CSO {CsoId} is out of scope. InboundOutOfScopeAction=Disconnect. Breaking join.",
                    connectedSystemObject.Id);

                var mvo = connectedSystemObject.MetaverseObject;
                var mvoId = mvo.Id;

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

                mvo.ConnectedSystemObjects.Remove(connectedSystemObject);
                connectedSystemObject.MetaverseObject = null;
                connectedSystemObject.MetaverseObjectId = null;
                connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.NotJoined;
                connectedSystemObject.DateJoined = null;
                Log.Verbose("HandleCsoOutOfScopeAsync: Broke join between CSO {CsoId} and MVO {MvoId}", connectedSystemObject.Id, mvoId);

                ApplyPendingMetaverseObjectAttributeChanges(mvo);
                await _jim.Metaverse.UpdateMetaverseObjectAsync(mvo);

                var remainingCsoCount = await _jim.ConnectedSystems.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(mvoId);
                if (remainingCsoCount == 0)
                {
                    await ProcessMvoDeletionRuleAsync(mvo, runProfileExecutionItem);
                }
                break;
        }
    }
}

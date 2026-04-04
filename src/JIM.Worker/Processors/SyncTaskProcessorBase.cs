using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Utilities;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Application.Servers;
using JIM.Application.Services;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using JIM.Models.Sync;
using JIM.Utilities;
using JIM.Data.Repositories;
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
    protected readonly ISyncEngine _syncEngine;
    protected readonly ISyncServer _syncServer;
    protected readonly ISyncRepository _syncRepo;
    protected readonly ConnectedSystem _connectedSystem;
    protected readonly ConnectedSystemRunProfile _connectedSystemRunProfile;
    protected readonly Activity _activity;
    protected readonly CancellationTokenSource _cancellationTokenSource;
    protected List<ConnectedSystemObjectType>? _objectTypes;
    protected Dictionary<Guid, List<JIM.Models.Transactional.PendingExport>>? _pendingExportsByCsoId;
    protected ExportEvaluationCache? _exportEvaluationCache;

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

    // Batch collection for deferred CSO join/projection updates (JoinType, DateJoined, MetaverseObjectId).
    // CSO scalar property changes are NOT detected by EF during sync because AutoDetectChangesEnabled is
    // disabled at page boundaries for performance. Without explicit persistence, JoinType stays as NotJoined
    // and DateJoined stays null in the database after projection or join.
    protected readonly List<ConnectedSystemObject> _pendingCsoJoinUpdates = [];

    // Batch collection for deferred provisioning CSO creation (avoid per-CSO database calls)
    protected readonly List<ConnectedSystemObject> _provisioningCsosToCreate = [];

    // Batch collection for deferred CSO deletions (avoid per-CSO database calls)
    protected readonly List<(ConnectedSystemObject Cso, ActivityRunProfileExecutionItem ExecutionItem)> _obsoleteCsosToDelete = [];

    // Tracks MVO IDs that have had CSOs disconnected in-memory during this batch but not yet flushed to the database.
    // Used by AttemptJoinAsync to avoid false CouldNotJoinDueToExistingJoin errors when an obsolete CSO
    // is disconnected and a new CSO tries to join the same MVO within the same page.
    protected readonly List<Guid> _pendingDisconnectedMvoIds = [];

    // Batch collection for quiet CSO deletions (pre-disconnected CSOs that don't need RPEIs)
    // These are CSOs that were already disconnected during synchronous MVO deletion and just need cleanup
    protected readonly List<ConnectedSystemObject> _quietCsosToDelete = [];

    // Batch collection for deferred MVO deletions (for immediate 0-grace-period deletions)
    // Stores: (MVO, FinalAttributeValues) - attribute values are snapshotted before attribute recall
    // because recall removes them from the MVO before FlushPendingMvoDeletionsAsync runs.
    protected readonly List<(MetaverseObject Mvo, List<MetaverseObjectAttributeValue> FinalAttributeValues)> _pendingMvoDeletions = [];

    // Pre-recall attribute value snapshots for MVOs that may be deleted.
    // Captured in ProcessObsoleteConnectedSystemObjectAsync before attribute recall runs,
    // then consumed by MarkMvoForDeletionAsync when the MVO is queued for deletion.
    // This is needed because recall removes attributes from the MVO before the deletion
    // rule evaluation determines whether the MVO should actually be deleted.
    private readonly Dictionary<Guid, List<MetaverseObjectAttributeValue>> _preRecallAttributeSnapshots = new();

    // Batch collection for MVO change object creation (deferred to page boundary for performance)
    // Stores: (MVO, Additions, Removals, ChangeType, RPEI) - captured BEFORE applying pending changes
    // ChangeType indicates how the MVO was created/modified (Projected, Joined, AttributeFlow, Updated)
    // RPEI links MVO changes to the Activity for initiator context (User, ApiKey, etc.)
    protected readonly List<(MetaverseObject Mvo, List<MetaverseObjectAttributeValue> Additions, List<MetaverseObjectAttributeValue> Removals, ObjectChangeType ChangeType, ActivityRunProfileExecutionItem Rpei)> _pendingMvoChanges = [];

    // Batch collection for deferred reference attribute processing.
    // Reference attributes must be processed AFTER all CSOs in the page have been processed (joined/projected)
    // because group member references may point to user CSOs that come later in the processing order.
    // By deferring reference attributes, we ensure all MVOs exist before resolving references.
    protected readonly List<(ConnectedSystemObject Cso, List<SyncRule> SyncRules)> _pendingReferenceAttributeProcessing = [];

    // Tracks CSOs with unresolved cross-page reference attributes.
    // During page processing, if ProcessDeferredReferenceAttributes finds references where
    // ReferenceValue.MetaverseObject is null (the referenced CSO is on a different page and hasn't
    // been joined/projected yet), the CSO ID and applicable sync rule IDs are recorded here.
    // After all pages, these CSOs are reloaded from the DB (where all MVOs now exist)
    // and reference attributes are re-processed in ResolveCrossPageReferencesAsync.
    protected readonly List<(Guid CsoId, List<int> SyncRuleIds)> _unresolvedCrossPageReferences = [];

    // Summary stats are accumulated incrementally during each FlushRpeisAsync call via
    // Worker.AccumulateActivitySummaryStats, so RPEIs can be released immediately without
    // keeping them in memory for a final calculation.

    // Set to true when raw SQL is available (production with real database).
    // Used to conditionally apply ClearChangeTracker after MVO deletions — only needed in
    // production where raw SQL operations create stale change tracker entries.
    protected bool _hasRawSqlSupport;

    // Controls how much detail is recorded for sync outcome graphs on each RPEI.
    // Loaded once at sync start by each processor subclass. Default: None (no overhead).
    protected ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel _syncOutcomeTrackingLevel =
        ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None;

    // Controls whether CSO change history records are created for PendingExportCreated outcomes.
    // Loaded once at sync start. When enabled, snapshots pending export attribute data so the
    // Causality Tree can render attribute detail even after the PendingExport is deleted.
    protected bool _csoChangeTrackingEnabled;

    // MVO ID → RPEI lookup for linking export evaluation results back to the originating RPEI.
    // Populated when RPEIs are created in ProcessMetaverseObjectChangesAsync.
    // Looked up in EvaluateOutboundExportsAsync to attach export outcomes as children.
    // Cleared per page alongside other batch collections.
    protected readonly Dictionary<Guid, ActivityRunProfileExecutionItem> _mvoIdToRpei = [];

    // Deferred MVO→RPEI mappings for newly projected MVOs whose ID is Guid.Empty at registration time.
    // After PersistPendingMetaverseObjectsAsync assigns real IDs, these are re-keyed into _mvoIdToRpei.
    private readonly List<(MetaverseObject Mvo, ActivityRunProfileExecutionItem Rpei)> _deferredMvoRpeiMappings = [];

    // Expression evaluator for expression-based sync rule mappings
    protected readonly IExpressionEvaluator _expressionEvaluator = new DynamicExpressoEvaluator();

    protected SyncTaskProcessorBase(
        ISyncEngine syncEngine,
        ISyncServer syncServer,
        ISyncRepository syncRepository,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile connectedSystemRunProfile,
        Activity activity,
        CancellationTokenSource cancellationTokenSource)
    {
        _syncEngine = syncEngine;
        _syncServer = syncServer;
        _syncRepo = syncRepository;
        _connectedSystem = connectedSystem;
        _connectedSystemRunProfile = connectedSystemRunProfile;
        _activity = activity;
        _cancellationTokenSource = cancellationTokenSource;
    }

    /// <summary>
    /// Logs memory diagnostics at page boundaries to track memory usage across the sync run.
    /// Helps diagnose memory accumulation issues and verify bounded memory behaviour.
    /// </summary>
    protected static void LogPageMemoryDiagnostics(int pageNumber, int totalPages)
    {
        var memoryBytes = GC.GetTotalMemory(forceFullCollection: false);
        var memoryMb = memoryBytes / (1024.0 * 1024.0);
        Log.Information("Page {Page}/{TotalPages} complete. Memory: {MemoryMb:F1} MB, Gen0: {Gen0}, Gen1: {Gen1}, Gen2: {Gen2}",
            pageNumber, totalPages, memoryMb,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    /// <summary>
    /// Flushes the current page's RPEIs to the database via raw SQL bulk insert,
    /// accumulates summary stats incrementally, then releases the RPEIs from memory.
    ///
    /// IMPORTANT: This method MUST be called BEFORE any SaveChangesAsync that might trigger
    /// DetectChanges() while RPEIs are in Activity.RunProfileExecutionItems. DetectChanges()
    /// scans the tracked Activity's collection, discovers RPEIs as new items, marks them as
    /// Added, and inserts them prematurely. If this method later attempts raw SQL insert of
    /// the same RPEIs, it causes duplicate key violations.
    ///
    /// In production (raw SQL): clears RPEIs from Activity.RunProfileExecutionItems and detaches
    /// from change tracker to prevent re-insertion by subsequent SaveChangesAsync calls.
    ///
    /// In tests (EF fallback): leaves RPEIs in Activity.RunProfileExecutionItems for test
    /// assertions. They're tracked by EF (via AddRange) and persisted by next SaveChangesAsync.
    /// </summary>
    protected async Task FlushRpeisAsync()
    {
        var pageRpeis = _activity.RunProfileExecutionItems.ToList();
        if (pageRpeis.Count == 0)
            return;

        // Ensure all RPEIs have ActivityId set, IDs pre-generated, and CSO display snapshots populated
        foreach (var rpei in pageRpeis)
        {
            rpei.ActivityId = _activity.Id;
            if (rpei.Id == Guid.Empty)
                rpei.Id = Guid.NewGuid();

            // Snapshot CSO display fields (ExternalId, DisplayName, ObjectType) for historical preservation.
            // This centralised call ensures every sync RPEI gets snapshots regardless of creation path.
            if (rpei.ConnectedSystemObject != null)
                rpei.SnapshotCsoDisplayFields(rpei.ConnectedSystemObject);
        }

        // Retroactively update outcome descriptions and target entity IDs.
        // Sync outcome nodes are created before ApplyPendingMetaverseObjectAttributeChanges (which
        // sets DisplayName) and before PersistPendingMetaverseObjectsAsync (which assigns real IDs).
        // By this point both have run, so we can fill in any blanks.
        if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
        {
            foreach (var rpei in pageRpeis)
            {
                var mvo = rpei.ConnectedSystemObject?.MetaverseObject;
                if (mvo == null) continue;

                foreach (var outcome in rpei.SyncOutcomes)
                {
                    if (outcome.OutcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.Projected
                            or ActivityRunProfileExecutionItemSyncOutcomeType.Joined
                            or ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow)
                    {
                        // Fill in MVO display name if it was null at creation time
                        if (string.IsNullOrEmpty(outcome.TargetEntityDescription)
                            && !string.IsNullOrEmpty(mvo.DisplayName))
                        {
                            outcome.TargetEntityDescription = mvo.DisplayName;
                        }

                        // Fill in MVO ID if it was null at creation time (newly projected MVOs)
                        if (!outcome.TargetEntityId.HasValue && mvo.Id != Guid.Empty)
                        {
                            outcome.TargetEntityId = mvo.Id;
                        }
                    }
                }
            }
        }

        // Build OutcomeSummary strings from outcome trees before bulk insert.
        // This runs after all outcome nodes have been attached (including export evaluation).
        if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
        {
            foreach (var rpei in pageRpeis)
                SyncOutcomeBuilder.BuildOutcomeSummary(rpei);
        }

        // Bulk insert this page's RPEIs via raw SQL (or EF fallback for tests).
        // Returns true if raw SQL was used, false if EF fallback was used.
        var usedRawSql = await _syncRepo.BulkInsertRpeisAsync(pageRpeis);

        if (usedRawSql)
        {
            // Production: accumulate summary stats from this batch before clearing RPEIs.
            // Stats are computed incrementally per-flush, eliminating the need to keep all RPEIs
            // in memory for a final calculation (which caused OOM at 100K+ objects).
            Worker.AccumulateActivitySummaryStats(_activity, pageRpeis);

            // Clear from Activity's collection and detach from change tracker so no
            // subsequent SaveChangesAsync re-inserts them.
            _activity.RunProfileExecutionItems.Clear();
            _syncRepo.DetachRpeisFromChangeTracker(pageRpeis);
            _hasRawSqlSupport = true;
        }
        // Tests (EF fallback): RPEIs stay in Activity.RunProfileExecutionItems for test
        // assertions. Stats are computed at activity completion by CalculateActivitySummaryStats
        // in CompleteActivityBasedOnExecutionResultsAsync (assignment, not accumulation).

        // Clear per-page MVO→RPEI lookup (only used within a single page's processing)
        _mvoIdToRpei.Clear();
        _deferredMvoRpeiMappings.Clear();
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
        await _syncRepo.UpdateConnectedSystemAsync(_connectedSystem);

        Log.Information("UpdateDeltaSyncWatermarkAsync: Updated delta sync watermark to {Timestamp}",
            _connectedSystem.LastDeltaSyncCompletedAt);

        span.SetSuccess();
    }

    /// <summary>
    /// Pass 1: Processes pending export confirmations and obsolete CSO teardown for a single Connected System Object.
    /// This must run for ALL CSOs in the page BEFORE Pass 2 (ProcessActiveConnectedSystemObjectAsync) runs,
    /// so that all disconnections are recorded in _pendingDisconnectedMvoIds before any join attempts.
    /// Without this ordering guarantee, a new CSO processed before an obsolete CSO (due to GUID ordering)
    /// would see a stale join count and incorrectly throw CouldNotJoinDueToExistingJoin.
    /// </summary>
    protected async Task ProcessObsoleteAndExportConfirmationAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        Log.Verbose($"ProcessObsoleteAndExportConfirmationAsync: Pass 1 for CSO: {connectedSystemObject}.");

        try
        {
            using (Diagnostics.Sync.StartSpan("ProcessPendingExport"))
            {
                // Note: ProcessPendingExport handles pending export confirmation, not CSO/MVO changes
                // Queues operations for batch processing at end of page (avoids per-CSO database calls)
                ProcessPendingExport(connectedSystemObject);
            }

            List<ActivityRunProfileExecutionItem> obsoleteExecutionItems;
            using (Diagnostics.Sync.StartSpan("ProcessObsoleteConnectedSystemObject"))
            {
                obsoleteExecutionItems = await ProcessObsoleteConnectedSystemObjectAsync(activeSyncRules, connectedSystemObject);
            }

            // Add execution items for obsolete CSO (created by ProcessObsoleteConnectedSystemObjectAsync)
            // Returns a single RPEI per CSO with Disconnected + CsoDeleted outcomes when a joined CSO is obsoleted
            if (obsoleteExecutionItems.Count > 0)
            {
                foreach (var item in obsoleteExecutionItems)
                    _activity.RunProfileExecutionItems.Add(item);
            }
        }
        catch (Exception e)
        {
            // Create execution item for unhandled error tracking
            var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
            runProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
            runProfileExecutionItem.ConnectedSystemObjectId = connectedSystemObject.Id;
            runProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError;
            runProfileExecutionItem.ErrorMessage = e.Message;
            runProfileExecutionItem.ErrorStackTrace = e.StackTrace;
            _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);

            Log.Error(e, "ProcessObsoleteAndExportConfirmationAsync: Unhandled error during pass 1 for {Cso}.",
                connectedSystemObject);
        }
    }

    /// <summary>
    /// Pass 2: Processes joins, projections, and attribute flow for a single non-obsolete Connected System Object.
    /// This must run AFTER Pass 1 (ProcessObsoleteAndExportConfirmationAsync) has completed for ALL CSOs in the page,
    /// ensuring _pendingDisconnectedMvoIds is fully populated before any join attempts.
    /// Skips obsolete CSOs (already handled in Pass 1).
    /// </summary>
    protected async Task ProcessActiveConnectedSystemObjectAsync(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        // Skip obsolete CSOs - already fully handled in Pass 1
        if (connectedSystemObject.Status == ConnectedSystemObjectStatus.Obsolete)
            return;

        // Skip if no sync rules defined AND not in simple mode — nothing to join/project/flow.
        // In simple mode, matching rules on the object type can drive joining even without sync rules.
        if (activeSyncRules.Count == 0 && _connectedSystem.ObjectMatchingRuleMode != ObjectMatchingRuleMode.ConnectedSystem)
            return;

        Log.Verbose($"ProcessActiveConnectedSystemObjectAsync: Pass 2 for CSO: {connectedSystemObject}.");

        try
        {
            MetaverseObjectChangeResult changeResult;
            using (Diagnostics.Sync.StartSpan("ProcessMetaverseObjectChanges"))
            {
                changeResult = await ProcessMetaverseObjectChangesAsync(activeSyncRules, connectedSystemObject);
            }

            // Handle execution item for successful changes (join, projection, attribute flow)
            if (changeResult.HasChanges)
            {
                // Check if an RPEI was already created for this CSO (in ProcessMetaverseObjectChangesAsync when MVO changes were captured)
                var existingRpei = _activity.RunProfileExecutionItems.FirstOrDefault(r => r.ConnectedSystemObject?.Id == connectedSystemObject.Id);
                if (existingRpei != null)
                {
                    // Update the existing RPEI with the ObjectChangeType
                    existingRpei.ObjectChangeType = changeResult.ChangeType;

                    // Propagate attribute flow count from change result (e.g., DisconnectedOutOfScope with attribute removals)
                    if (changeResult.AttributeFlowCount.HasValue)
                    {
                        existingRpei.AttributeFlowCount = changeResult.AttributeFlowCount;
                    }

                    // Capture recalled attribute values for MVO change tracking (enables attribute change table in RPEI detail)
                    if (changeResult.RecalledAttributeValues != null && changeResult.DisconnectedMvo != null)
                    {
                        _pendingMvoChanges.Add((changeResult.DisconnectedMvo, new List<MetaverseObjectAttributeValue>(),
                            changeResult.RecalledAttributeValues, ObjectChangeType.DisconnectedOutOfScope, existingRpei));
                    }
                }
                else
                {
                    // Create a new RPEI (for cases like join/project without attribute changes)
                    var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
                    runProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
                    runProfileExecutionItem.ConnectedSystemObjectId = connectedSystemObject.Id;
                    runProfileExecutionItem.ObjectChangeType = changeResult.ChangeType;

                    // Propagate attribute flow count from change result (e.g., DisconnectedOutOfScope with attribute removals)
                    if (changeResult.AttributeFlowCount.HasValue)
                    {
                        runProfileExecutionItem.AttributeFlowCount = changeResult.AttributeFlowCount;
                    }

                    // Capture recalled attribute values for MVO change tracking (enables attribute change table in RPEI detail)
                    if (changeResult.RecalledAttributeValues != null && changeResult.DisconnectedMvo != null)
                    {
                        _pendingMvoChanges.Add((changeResult.DisconnectedMvo, new List<MetaverseObjectAttributeValue>(),
                            changeResult.RecalledAttributeValues, ObjectChangeType.DisconnectedOutOfScope, runProfileExecutionItem));
                    }

                    // Build sync outcome for RPEIs not already covered by ProcessMetaverseObjectChangesAsync
                    if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                    {
                        var outcomeType = changeResult.ChangeType switch
                        {
                            ObjectChangeType.Projected => ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
                            ObjectChangeType.Joined => ActivityRunProfileExecutionItemSyncOutcomeType.Joined,
                            ObjectChangeType.DisconnectedOutOfScope => ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
                            _ => ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow
                        };
                        // Include MVO info for outcomes (only store ID if already persisted)
                        var mvoRef = connectedSystemObject.MetaverseObject;
                        Guid? mvoId = mvoRef != null && mvoRef.Id != Guid.Empty ? mvoRef.Id : null;
                        string? mvoDescription = mvoRef?.DisplayName;

                        // Only put detailCount on AttributeFlow/DisconnectedOutOfScope root outcomes, not on Joined/Projected
                        int? rootDetailCount = outcomeType is ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow
                            or ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope
                            ? changeResult.AttributeFlowCount : null;

                        var rootOutcome = SyncOutcomeBuilder.AddRootOutcome(runProfileExecutionItem, outcomeType,
                            targetEntityId: mvoId,
                            targetEntityDescription: mvoDescription,
                            detailCount: rootDetailCount);

                        // In Detailed mode, add AttributeFlow child under DisconnectedOutOfScope when attributes were recalled.
                        if (_syncOutcomeTrackingLevel == ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed
                            && changeResult.ChangeType == ObjectChangeType.DisconnectedOutOfScope
                            && changeResult.AttributeFlowCount is > 0)
                        {
                            SyncOutcomeBuilder.AddChildOutcome(runProfileExecutionItem, rootOutcome,
                                ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                                detailCount: changeResult.AttributeFlowCount);
                        }

                        // Add MVO deletion fate outcome for DisconnectedOutOfScope when the deletion rule was triggered
                        if (changeResult.ChangeType == ObjectChangeType.DisconnectedOutOfScope
                            && changeResult.MvoDeletionFate != MvoDeletionFate.NotDeleted)
                        {
                            var deletionOutcomeType = changeResult.MvoDeletionFate == MvoDeletionFate.DeletedImmediately
                                ? ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted
                                : ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled;

                            SyncOutcomeBuilder.AddChildOutcome(runProfileExecutionItem, rootOutcome,
                                deletionOutcomeType,
                                targetEntityId: mvoId,
                                targetEntityDescription: mvoDescription);
                        }
                    }

                    _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);
                }
            }
        }
        catch (SyncJoinException joinEx)
        {
            // Create execution item for join-specific errors with proper error type
            var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
            runProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
            runProfileExecutionItem.ConnectedSystemObjectId = connectedSystemObject.Id;
            runProfileExecutionItem.ErrorType = joinEx.ErrorType;
            runProfileExecutionItem.ErrorMessage = joinEx.Message;
            _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);

            Log.Warning(joinEx, "ProcessActiveConnectedSystemObjectAsync: Join error for {Cso}: {Message}",
                connectedSystemObject, joinEx.Message);
        }
        catch (Exception e)
        {
            // Create execution item for unhandled error tracking
            var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
            runProfileExecutionItem.ConnectedSystemObject = connectedSystemObject;
            runProfileExecutionItem.ConnectedSystemObjectId = connectedSystemObject.Id;
            runProfileExecutionItem.ErrorType = ActivityRunProfileExecutionItemErrorType.UnhandledError;
            runProfileExecutionItem.ErrorMessage = e.Message;
            runProfileExecutionItem.ErrorStackTrace = e.StackTrace;
            _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);

            Log.Error(e, "ProcessActiveConnectedSystemObjectAsync: Unhandled error during pass 2 for {Cso}.",
                connectedSystemObject);
        }
    }

    /// <summary>
    /// See if a Pending Export Object for a Connected System Object can be invalidated and deleted.
    /// This would occur when the Pending Export changes are visible on the Connected System Object after a confirming import.
    /// Queues pending export operations for batch processing at the end of page processing (avoids per-CSO database calls).
    /// </summary>
    protected void ProcessPendingExport(ConnectedSystemObject connectedSystemObject)
    {
        var result = _syncEngine.EvaluatePendingExportConfirmation(connectedSystemObject, _pendingExportsByCsoId);
        if (!result.HasResults)
            return;

        // Apply the engine's decisions to the batch collections
        _pendingExportsToDelete.AddRange(result.ToDelete);
        _pendingExportsToUpdate.AddRange(result.ToUpdate);
    }

    /// <summary>
    /// Check if a CSO has been obsoleted and delete it, applying any joined Metaverse Object changes as necessary.
    /// Respects the InboundOutOfScopeAction setting on import sync rules to determine whether to disconnect.
    /// Deleting a Metaverse Object can have downstream impacts on other Connected System objects.
    /// CSO deletions are batched for performance - call FlushObsoleteCsoOperationsAsync() at page boundaries.
    /// When a joined CSO is obsoleted with Disconnect action, two RPEIs are produced:
    /// 1. Disconnected - records the CSO-MVO join being broken (with any attribute removals)
    /// 2. Deleted - records the CSO being removed from staging
    /// </summary>
    /// <returns>The execution items if CSO was obsoleted (for the caller to add to the activity), empty list otherwise.</returns>
    protected async Task<List<ActivityRunProfileExecutionItem>> ProcessObsoleteConnectedSystemObjectAsync(
        List<SyncRule> activeSyncRules,
        ConnectedSystemObject connectedSystemObject)
    {
        if (connectedSystemObject.Status != ConnectedSystemObjectStatus.Obsolete)
            return [];

        // Create the execution item for the CSO deletion
        // Note: RPEI uses Delete (user-facing), CSO status uses Obsolete (internal state)
        var deletionExecutionItem = _activity.PrepareRunProfileExecutionItem();
        deletionExecutionItem.ConnectedSystemObject = connectedSystemObject;
        deletionExecutionItem.ConnectedSystemObjectId = connectedSystemObject.Id;
        deletionExecutionItem.ObjectChangeType = ObjectChangeType.Deleted;
        // Snapshot CSO display fields eagerly — FlushObsoleteCsoOperationsAsync() will null the CSO
        // reference before FlushRpeisAsync() runs, so the centralised snapshot would find nothing.
        deletionExecutionItem.SnapshotCsoDisplayFields(connectedSystemObject);

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
                return [];
            }

            // Not joined but has a different JoinType (e.g., Explicit) - this is a regular orphan deletion
            if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                SyncOutcomeBuilder.AddRootOutcome(deletionExecutionItem, ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted);

            _obsoleteCsosToDelete.Add((connectedSystemObject, deletionExecutionItem));
            return [deletionExecutionItem];
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
            if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                SyncOutcomeBuilder.AddRootOutcome(deletionExecutionItem, ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted);

            _obsoleteCsosToDelete.Add((connectedSystemObject, deletionExecutionItem));
            return [deletionExecutionItem];
        }

        // InboundOutOfScopeAction = Disconnect (default) - break the join and handle MVO deletion rules
        var mvo = connectedSystemObject.MetaverseObject;
        var connectedSystemId = connectedSystemObject.ConnectedSystemId;
        var mvoId = mvo.Id;
        var mvoDisplayName = mvo.DisplayName;

        // Single RPEI for both disconnection and deletion (one-RPEI-per-CSO rule).
        // The ObjectChangeType is Disconnected (the meaningful event); CsoDeleted is recorded
        // as an outcome on the same RPEI since the deletion is a consequence of the disconnection.
        // Reuse deletionExecutionItem (already created above) and change its type to Disconnected.
        deletionExecutionItem.ObjectChangeType = ObjectChangeType.Disconnected;

        // Query remaining CSO count BEFORE breaking the join so the count includes all current connectors.
        // Then subtract 1 to exclude this CSO which is about to be disconnected.
        var totalCsoCount = await _syncRepo.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(mvoId);
        var remainingCsoCount = Math.Max(0, totalCsoCount - 1);

        // Snapshot the MVO's current attribute values before recall removes them.
        // This snapshot is used by MarkMvoForDeletionAsync to capture the final state
        // on the deletion change record for audit purposes.
        if (!_preRecallAttributeSnapshots.ContainsKey(mvoId))
        {
            _preRecallAttributeSnapshots[mvoId] = mvo.AttributeValues.ToList();
        }

        // Evaluate the MVO deletion rule BEFORE attribute recall (#390 optimisation).
        // If the MVO will be deleted immediately, attribute recall is nugatory work —
        // the attributes, MVO update, and export evaluations would all be discarded
        // when the MVO is deleted moments later in FlushPendingMvoDeletionsAsync.
        var mvoDeletionFate = await ProcessMvoDeletionRuleAsync(mvo, connectedSystemId, remainingCsoCount);

        // Check if we should remove contributed attributes based on the object type setting.
        // When a grace period is configured, skip attribute recall to preserve identity-critical
        // attribute values (e.g., display name, department) that feed expression-based exports
        // (e.g., LDAP Distinguished Name). Recalling these attributes during the grace period
        // would produce invalid export values. The attributes will be cleaned up when the MVO
        // is deleted after the grace period expires, or preserved if the object reappears.
        // Also skip recall when the MVO will be deleted immediately — the recall work (MVO update,
        // export evaluation queueing) would be discarded when the MVO is deleted (#390).
        var hasGracePeriod = mvo.Type?.DeletionGracePeriod is { } gp && gp > TimeSpan.Zero;
        var skipRecallForImmediateDeletion = mvoDeletionFate == MvoDeletionFate.DeletedImmediately;
        if (connectedSystemObject.Type.RemoveContributedAttributesOnObsoletion && !hasGracePeriod && !skipRecallForImmediateDeletion)
        {
            // Find all MVO attribute values contributed by this connected system and mark them for removal
            var contributedAttributes = mvo.AttributeValues
                .Where(av => av.ContributedBySystemId == connectedSystemId)
                .ToList();

            foreach (var attributeValue in contributedAttributes)
            {
                mvo.PendingAttributeValueRemovals.Add(attributeValue);
                Log.Verbose($"ProcessObsoleteConnectedSystemObjectAsync: Marking attribute '{attributeValue.Attribute?.Name}' for removal from MVO {mvo.Id}.");
            }

            // Apply attribute removals and queue the MVO for export evaluation and persistence.
            // ProcessMetaverseObjectChangesAsync is skipped for obsolete CSOs (it's guarded by
            // Status != Obsolete), so we must handle this here to ensure target systems are
            // notified of the recalled attributes via pending exports.
            if (mvo.PendingAttributeValueRemovals.Count > 0)
            {
                var changedAttributes = mvo.PendingAttributeValueRemovals.ToList();
                var removedAttributes = mvo.PendingAttributeValueRemovals.ToHashSet();

                // Track attribute removals on the RPEI (these are part of the disconnection)
                deletionExecutionItem.AttributeFlowCount = mvo.PendingAttributeValueRemovals.Count;

                // Capture MVO changes for change tracking BEFORE applying (which clears the pending lists).
                // This enables the RPEI detail page to show the recalled attribute values in the causality tree.
                var removals = mvo.PendingAttributeValueRemovals.ToList();
                _pendingMvoChanges.Add((mvo, new List<MetaverseObjectAttributeValue>(), removals, ObjectChangeType.Disconnected, deletionExecutionItem));

                Log.Information("ProcessObsoleteConnectedSystemObjectAsync: Applying {Count} attribute removals to MVO {MvoId} and queueing for export evaluation",
                    changedAttributes.Count, mvo.Id);

                ApplyPendingMetaverseObjectAttributeChanges(mvo);

                // Queue for batch persistence (MVO attributes have changed)
                _pendingMvoUpdates.Add(mvo);

                // Queue for export evaluation so target systems receive pending exports
                // for the recalled attribute values
                _pendingExportEvaluations.Add((mvo, changedAttributes, removedAttributes));
            }
        }
        else if (skipRecallForImmediateDeletion)
        {
            Log.Debug("ProcessObsoleteConnectedSystemObjectAsync: Skipping attribute recall for CSO {CsoId} " +
                "because MVO {MvoId} will be deleted immediately (#390 optimisation).",
                connectedSystemObject.Id, mvo.Id);
        }
        else if (hasGracePeriod)
        {
            Log.Debug("ProcessObsoleteConnectedSystemObjectAsync: Skipping attribute recall for CSO {CsoId} " +
                "because MVO {MvoId} has a grace period of {GracePeriod}. Attributes will be preserved until " +
                "grace period expires.", connectedSystemObject.Id, mvo.Id, mvo.Type!.DeletionGracePeriod);
        }

        // Break the CSO-MVO join
        mvo.ConnectedSystemObjects.Remove(connectedSystemObject);
        connectedSystemObject.MetaverseObject = null;
        connectedSystemObject.MetaverseObjectId = null;
        connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.NotJoined;
        connectedSystemObject.DateJoined = null;
        Log.Verbose($"ProcessObsoleteConnectedSystemObjectAsync: Broke join between CSO {connectedSystemObject.Id} and MVO {mvoId}.");

        // Track this disconnection so AttemptJoinAsync can account for it when checking existing joins.
        // The database still shows this CSO as joined until FlushObsoleteCsoOperationsAsync() runs.
        _pendingDisconnectedMvoIds.Add(mvoId);

        // Queue the CSO for batch deletion (deletion will happen at end of page processing).
        // The same RPEI is used for both the disconnection record and the deletion tracking.
        _obsoleteCsosToDelete.Add((connectedSystemObject, deletionExecutionItem));

        // Build sync outcomes: Disconnected as root, CsoDeleted as child (causal chain).
        // The disconnection is the primary event; CSO deletion is a consequential outcome.
        if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
        {
            var disconnectedRoot = SyncOutcomeBuilder.AddRootOutcome(deletionExecutionItem,
                ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected,
                targetEntityId: mvoId,
                targetEntityDescription: mvoDisplayName,
                detailCount: deletionExecutionItem.AttributeFlowCount);

            // In Detailed mode, add AttributeFlow child under Disconnected when attributes were recalled.
            if (_syncOutcomeTrackingLevel == ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed
                && deletionExecutionItem.AttributeFlowCount is > 0)
            {
                SyncOutcomeBuilder.AddChildOutcome(deletionExecutionItem, disconnectedRoot,
                    ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                    detailCount: deletionExecutionItem.AttributeFlowCount);
            }

            SyncOutcomeBuilder.AddChildOutcome(deletionExecutionItem, disconnectedRoot,
                ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted);

            // Add MVO deletion fate outcome when the deletion rule was triggered
            if (mvoDeletionFate == MvoDeletionFate.DeletedImmediately)
            {
                SyncOutcomeBuilder.AddChildOutcome(deletionExecutionItem, disconnectedRoot,
                    ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
                    targetEntityId: mvoId,
                    targetEntityDescription: mvoDisplayName);
            }
            else if (mvoDeletionFate == MvoDeletionFate.DeletionScheduled)
            {
                var gracePeriod = mvo.Type?.DeletionGracePeriod;
                var graceMessage = gracePeriod.HasValue
                    ? $"Grace period: {FormatGracePeriod(gracePeriod.Value)}"
                    : null;

                SyncOutcomeBuilder.AddChildOutcome(deletionExecutionItem, disconnectedRoot,
                    ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled,
                    targetEntityId: mvoId,
                    targetEntityDescription: mvoDisplayName,
                    detailMessage: graceMessage);
            }
        }

        // Return single RPEI with both Disconnected and CsoDeleted outcomes
        return [deletionExecutionItem];
    }

    /// <summary>
    /// Determines the InboundOutOfScopeAction to use for a CSO by finding the applicable import sync rule.
    /// If multiple import sync rules exist for this CSO type, the first one's setting is used.
    /// Uses pre-loaded sync rules to avoid database round trips.
    /// </summary>
    protected InboundOutOfScopeAction DetermineInboundOutOfScopeAction(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
        => _syncEngine.DetermineOutOfScopeAction(connectedSystemObject, activeSyncRules);

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
    protected async Task<MvoDeletionFate> ProcessMvoDeletionRuleAsync(MetaverseObject mvo, int disconnectingSystemId, int remainingCsoCount)
    {
        var decision = _syncEngine.EvaluateMvoDeletionRule(mvo, disconnectingSystemId, remainingCsoCount);
        return await ApplyMvoDeletionDecisionAsync(mvo, decision);
    }

    /// <summary>
    /// Applies an MVO deletion decision from the engine — handles I/O (queuing immediate deletion or persisting grace period).
    /// </summary>
    private async Task<MvoDeletionFate> ApplyMvoDeletionDecisionAsync(MetaverseObject mvo, MvoDeletionDecision decision)
    {
        switch (decision.Fate)
        {
            case MvoDeletionFate.NotDeleted:
                return MvoDeletionFate.NotDeleted;

            case MvoDeletionFate.DeletedImmediately:
                return await MarkMvoForDeletionAsync(mvo, decision.Reason ?? "deletion rule triggered");

            case MvoDeletionFate.DeletionScheduled:
                return await MarkMvoForDeletionAsync(mvo, decision.Reason ?? "deletion rule triggered");

            default:
                return MvoDeletionFate.NotDeleted;
        }
    }

    /// <summary>
    /// Processes MVO deletion based on grace period configuration.
    /// For 0-grace-period: queues for immediate synchronous deletion at page flush.
    /// For grace period > 0: marks for deferred deletion by housekeeping.
    /// </summary>
    /// <param name="mvo">The Metaverse Object to process for deletion.</param>
    /// <param name="reason">A description of why the MVO is being deleted (for logging).</param>
    private async Task<MvoDeletionFate> MarkMvoForDeletionAsync(MetaverseObject mvo, string reason)
    {
        var gracePeriod = mvo.Type!.DeletionGracePeriod;

        if (!gracePeriod.HasValue || gracePeriod.Value == TimeSpan.Zero)
        {
            // No grace period - delete synchronously during this sync page flush
            // Check if already queued (multiple CSOs from same MVO may disconnect in same page)
            if (!_pendingMvoDeletions.Any(m => m.Mvo.Id == mvo.Id))
            {
                Log.Information(
                    "MarkMvoForDeletionAsync: MVO {MvoId} queued for immediate deletion ({Reason}). No grace period configured.",
                    mvo.Id, reason);
                // Use the pre-recall attribute snapshot if available (captured before attribute
                // recall removed them), otherwise fall back to the MVO's current values.
                var finalAttributeValues = _preRecallAttributeSnapshots.TryGetValue(mvo.Id, out var snapshot)
                    ? snapshot
                    : mvo.AttributeValues.ToList();
                _preRecallAttributeSnapshots.Remove(mvo.Id);
                _pendingMvoDeletions.Add((mvo, finalAttributeValues));
            }
            return MvoDeletionFate.DeletedImmediately;
        }
        else
        {
            // Grace period configured - mark for deferred deletion by housekeeping
            // Capture initiator info NOW so housekeeping can preserve the audit trail
            mvo.LastConnectorDisconnectedDate = DateTime.UtcNow;
            mvo.DeletionInitiatedByType = _activity.InitiatedByType;
            mvo.DeletionInitiatedById = _activity.InitiatedById;
            mvo.DeletionInitiatedByName = _activity.InitiatedByName;
            Log.Information(
                "MarkMvoForDeletionAsync: MVO {MvoId} marked for deletion ({Reason}). Eligible after {GracePeriod}. Initiator: {Initiator}",
                mvo.Id, reason, gracePeriod.Value, _activity.InitiatedByName ?? "Unknown");

            // Persist the LastConnectorDisconnectedDate and initiator info
            await _syncRepo.UpdateMetaverseObjectAsync(mvo);
            return MvoDeletionFate.DeletionScheduled;
        }
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

        // Skip if no sync rules AND not in simple mode — nothing to join/project/flow.
        // In simple mode, matching rules on the object type can drive joining even without sync rules.
        if (activeSyncRules.Count == 0 && _connectedSystem.ObjectMatchingRuleMode != ObjectMatchingRuleMode.ConnectedSystem)
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
                    if (wasProjected)
                        _pendingCsoJoinUpdates.Add(connectedSystemObject);
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
            var attributeFlowWarnings = new List<AttributeFlowWarning>();
            using (Diagnostics.Sync.StartSpan("ProcessInboundAttributeFlow"))
            {
                foreach (var inboundSyncRule in inboundSyncRules)
                {
                    // evaluate inbound attribute flow rules, skipping reference attributes
                    attributeFlowWarnings.AddRange(
                        ProcessInboundAttributeFlow(connectedSystemObject, inboundSyncRule, skipReferenceAttributes: true));
                }
            }

            // Create warning RPEIs for MVA->SVA truncations (#435)
            foreach (var warning in attributeFlowWarnings)
            {
                var warningRpei = _activity.PrepareRunProfileExecutionItem();
                warningRpei.ConnectedSystemObject = connectedSystemObject;
                warningRpei.ConnectedSystemObjectId = connectedSystemObject.Id;
                warningRpei.ErrorType = ActivityRunProfileExecutionItemErrorType.MultiValuedAttributeTruncated;
                warningRpei.ErrorMessage = $"Multi-valued source attribute '{warning.SourceAttributeName}' has {warning.ValueCount} values " +
                    $"but target attribute '{warning.TargetAttributeName}' is single-valued. First value used: '{warning.SelectedValue}'.";
                _activity.RunProfileExecutionItems.Add(warningRpei);
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
                // Determine the correct ObjectChangeType based on how the MVO was created/modified
                var changeType = wasProjected ? ObjectChangeType.Projected
                    : wasJoined ? ObjectChangeType.Joined
                    : ObjectChangeType.AttributeFlow;
                // Create RPEI for this CSO change - will be used to link MVO change to Activity for initiator context
                var rpei = _activity.PrepareRunProfileExecutionItem();
                rpei.ConnectedSystemObject = connectedSystemObject;
                rpei.ConnectedSystemObjectId = connectedSystemObject.Id;
                _activity.RunProfileExecutionItems.Add(rpei);

                // Track attribute flow count when the primary change type is Join or Projection
                // This prevents attribute flows from being "absorbed" into joins/projections
                if (changeType is ObjectChangeType.Joined or ObjectChangeType.Projected)
                {
                    rpei.AttributeFlowCount = attributesAdded + attributesRemoved;
                }

                _pendingMvoChanges.Add((connectedSystemObject.MetaverseObject, additions, removals, changeType, rpei));

                // Build sync outcome tree on the RPEI
                if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                {
                    var outcomeType = changeType switch
                    {
                        ObjectChangeType.Projected => ActivityRunProfileExecutionItemSyncOutcomeType.Projected,
                        ObjectChangeType.Joined => ActivityRunProfileExecutionItemSyncOutcomeType.Joined,
                        _ => ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow
                    };
                    var mvo = connectedSystemObject.MetaverseObject;
                    // Only store the MVO ID if it's already persisted (non-empty).
                    // For newly projected MVOs, the ID is Guid.Empty until batch persistence.
                    var mvoId = mvo.Id != Guid.Empty ? mvo.Id : (Guid?)null;
                    var mvoDescription = mvo.DisplayName;

                    var rootOutcome = SyncOutcomeBuilder.AddRootOutcome(rpei, outcomeType,
                        targetEntityId: mvoId,
                        targetEntityDescription: mvoDescription);

                    // In Detailed mode, add a separate AttributeFlow child under Projected/Joined
                    if (_syncOutcomeTrackingLevel == ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed
                        && changeType is ObjectChangeType.Projected or ObjectChangeType.Joined
                        && (attributesAdded + attributesRemoved) > 0)
                    {
                        SyncOutcomeBuilder.AddChildOutcome(rpei, rootOutcome,
                            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                            targetEntityDescription: mvoDescription,
                            detailCount: attributesAdded + attributesRemoved);
                    }

                    // Register MVO→RPEI mapping for export evaluation outcome linking.
                    // Newly projected MVOs have Guid.Empty as their ID until PersistPendingMetaverseObjectsAsync
                    // assigns real IDs. Defer these mappings and re-key after persistence.
                    if (connectedSystemObject.MetaverseObject.Id != Guid.Empty)
                        _mvoIdToRpei[connectedSystemObject.MetaverseObject.Id] = rpei;
                    else
                        _deferredMvoRpeiMappings.Add((connectedSystemObject.MetaverseObject, rpei));
                }
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
            // Merge with existing entry if the same MVO is already queued (e.g., multiple CSOs joined
            // to the same MVO, or scalar + reference changes processed in separate passes).
            if (changedAttributes.Count > 0)
            {
                MergeOrAddPendingExportEvaluation(connectedSystemObject.MetaverseObject, changedAttributes, removedAttributes);
            }

            // Evaluate drift detection: check if the CSO has drifted from expected state
            // This detects unauthorised changes made directly in the target system
            // and stages corrective pending exports (added to _pendingExportsToCreate for batch save)
            using (Diagnostics.Sync.StartSpan("EvaluateDrift"))
            {
                EvaluateDriftAndEnforceState(connectedSystemObject, connectedSystemObject.MetaverseObject);
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
            var result = await _syncServer.EvaluateExportRulesWithNoNetChangeDetectionAsync(
                mvo,
                changedAttributes,
                _connectedSystem,
                _exportEvaluationCache,
                deferSave: true,
                removedAttributes: removedAttributes,
                existingPendingExports: _pendingExportsToCreate);

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

            // Attach export evaluation outcomes to the originating RPEI (Detailed mode only).
            // Causal tree structure:
            //   Root (Projected/Joined/AttributeFlow)
            //     └── AttributeFlow (if present — MVO fully formed)
            //           ├── Provisioned → CS A (new CSO created)
            //           │     └── Pending Export → CS A
            //           ├── Provisioned → CS B (new CSO created)
            //           │     └── Pending Export → CS B
            //           └── Pending Export → CS C (update to existing CSO, no Provisioned parent)
            if (_syncOutcomeTrackingLevel == ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed
                && _mvoIdToRpei.TryGetValue(mvo.Id, out var originatingRpei))
            {
                // Prefer the AttributeFlow child as parent (MVO is fully formed after attribute flow),
                // fall back to root outcome, fall back to creating outcomes at root level.
                var rootOutcome = originatingRpei.SyncOutcomes.FirstOrDefault(o =>
                    o.ParentSyncOutcome == null && o.ParentSyncOutcomeId == null);
                var attributeFlowChild = originatingRpei.SyncOutcomes.FirstOrDefault(o =>
                    o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow
                    && o.ParentSyncOutcome != null);
                var exportParent = attributeFlowChild ?? rootOutcome;

                // Track Provisioned outcomes by CS ID so we can nest Pending Exports under them
                var provisionedByCs = new Dictionary<int, ActivityRunProfileExecutionItemSyncOutcome>();

                // Build CS ID → name lookup from the export evaluation cache (the provisioning CSO
                // intentionally does not have the ConnectedSystem nav property loaded).
                var csNameLookup = _exportEvaluationCache?.ExportRulesByMvoTypeId.Values
                    .SelectMany(rules => rules)
                    .Where(sr => sr.ConnectedSystem != null)
                    .GroupBy(sr => sr.ConnectedSystemId)
                    .ToDictionary(g => g.Key, g => g.First().ConnectedSystem.Name)
                    ?? new Dictionary<int, string>();

                foreach (var provisioningCso in result.ProvisioningCsosToCreate)
                {
                    // Store CS integer ID in DetailMessage for hyperlinking in the UI
                    var csIdString = provisioningCso.ConnectedSystemId > 0
                        ? provisioningCso.ConnectedSystemId.ToString()
                        : null;

                    csNameLookup.TryGetValue(provisioningCso.ConnectedSystemId, out var csName);

                    ActivityRunProfileExecutionItemSyncOutcome provisionedOutcome;
                    if (exportParent != null)
                    {
                        provisionedOutcome = SyncOutcomeBuilder.AddChildOutcome(originatingRpei, exportParent,
                            ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
                            targetEntityDescription: csName,
                            detailMessage: csIdString);
                    }
                    else
                    {
                        provisionedOutcome = SyncOutcomeBuilder.AddRootOutcome(originatingRpei,
                            ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
                            targetEntityDescription: csName,
                            detailMessage: csIdString);
                    }

                    provisionedByCs[provisioningCso.ConnectedSystemId] = provisionedOutcome;
                }

                foreach (var pendingExport in result.PendingExports)
                {
                    // Use ConnectedSystemId directly (nav property may not be set for deferred provisioning CSOs)
                    var peCsId = pendingExport.ConnectedSystemId;

                    ActivityRunProfileExecutionItemSyncOutcome peOutcome;

                    // If this PE matches a Provisioned CSO, nest it under the Provisioned node
                    if (peCsId > 0 && provisionedByCs.TryGetValue(peCsId, out var provisionedParent))
                    {
                        peOutcome = SyncOutcomeBuilder.AddChildOutcome(originatingRpei, provisionedParent,
                            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
                            targetEntityId: pendingExport.Id,
                            targetEntityDescription: provisionedParent.TargetEntityDescription,
                            detailCount: pendingExport.AttributeValueChanges.Count,
                            detailMessage: peCsId.ToString());
                    }
                    else if (exportParent != null)
                    {
                        // Update to existing CSO — attach under AttributeFlow or root
                        csNameLookup.TryGetValue(peCsId, out var peCsName);
                        peCsName ??= pendingExport.ConnectedSystemObject?.ConnectedSystem?.Name;
                        peOutcome = SyncOutcomeBuilder.AddChildOutcome(originatingRpei, exportParent,
                            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
                            targetEntityId: pendingExport.Id,
                            targetEntityDescription: peCsName,
                            detailCount: pendingExport.AttributeValueChanges.Count,
                            detailMessage: peCsId.ToString());
                    }
                    else
                    {
                        csNameLookup.TryGetValue(peCsId, out var peCsName);
                        peCsName ??= pendingExport.ConnectedSystemObject?.ConnectedSystem?.Name;
                        peOutcome = SyncOutcomeBuilder.AddRootOutcome(originatingRpei,
                            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
                            targetEntityId: pendingExport.Id,
                            targetEntityDescription: peCsName,
                            detailCount: pendingExport.AttributeValueChanges.Count,
                            detailMessage: peCsId.ToString());
                    }

                    // Snapshot PE attribute changes so the Causality Tree can render detail
                    // even after the PendingExport is deleted during export confirmation
                    await SnapshotPendingExportChangesAsync(peOutcome, pendingExport);
                }
            }
            else if (_syncOutcomeTrackingLevel == ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Standard
                && _mvoIdToRpei.TryGetValue(mvo.Id, out var standardRpei))
            {
                // Standard mode: add root-level outcomes only (no children)
                foreach (var _ in result.ProvisioningCsosToCreate)
                {
                    SyncOutcomeBuilder.AddRootOutcome(standardRpei,
                        ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned);
                }

                foreach (var pe in result.PendingExports)
                {
                    var peOutcome = SyncOutcomeBuilder.AddRootOutcome(standardRpei,
                        ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
                        targetEntityId: pe.Id,
                        targetEntityDescription: pe.ConnectedSystemObject?.ConnectedSystem?.Name,
                        detailCount: pe.AttributeValueChanges.Count,
                        detailMessage: pe.ConnectedSystemId.ToString());

                    await SnapshotPendingExportChangesAsync(peOutcome, pe);
                }
            }
        }

        // Evaluate if MVO has fallen OUT of scope for any export rules (deprovisioning), using cached data
        using (Diagnostics.Sync.StartSpan("EvaluateOutOfScopeExports"))
        {
            await _syncServer.EvaluateOutOfScopeExportsAsync(
                mvo,
                _connectedSystem,
                _exportEvaluationCache!);
        }
    }

    /// <summary>
    /// Batch persists all pending MVO creates and updates collected during the current page,
    /// then persists CSO join/projection updates (JoinType, DateJoined, MetaverseObjectId).
    /// CSO updates are flushed AFTER MVO creates so that projected CSOs can pick up the
    /// newly assigned MVO IDs. This is necessary because AutoDetectChangesEnabled is disabled
    /// during page flush, so EF does not detect CSO scalar property changes automatically.
    /// </summary>
    protected async Task PersistPendingMetaverseObjectsAsync()
    {
        if (_pendingMvoCreates.Count == 0 && _pendingMvoUpdates.Count == 0 && _pendingCsoJoinUpdates.Count == 0)
            return;

        using var span = Diagnostics.Sync.StartSpan("PersistPendingMetaverseObjects");
        span.SetTag("createCount", _pendingMvoCreates.Count);
        span.SetTag("updateCount", _pendingMvoUpdates.Count);
        span.SetTag("csoJoinUpdateCount", _pendingCsoJoinUpdates.Count);

        // Batch create new MVOs
        if (_pendingMvoCreates.Count > 0)
        {
            await _syncRepo.CreateMetaverseObjectsAsync(_pendingMvoCreates);
            Log.Verbose("PersistPendingMetaverseObjectsAsync: Created {Count} MVOs in batch", _pendingMvoCreates.Count);
            _pendingMvoCreates.Clear();
        }

        // Batch update existing MVOs
        if (_pendingMvoUpdates.Count > 0)
        {
            // Tactical fixup: capture reference FK data from in-memory navigations BEFORE
            // persistence, because EF clears navigations during SaveChangesAsync when using
            // explicit Entry().State management. After persist, issue a targeted SQL UPDATE
            // for any attribute values where EF failed to infer the FK.
            // We capture (MvoId, AttributeId, TargetMvoId) since av.Id may be Guid.Empty
            // (assigned by EF during SaveChangesAsync).
            // RETIRE: when MVO persistence is converted to direct SQL.
            var refFixups = new List<(Guid MvoId, int AttributeId, Guid TargetMvoId)>();
            foreach (var mvo in _pendingMvoUpdates)
            {
                foreach (var av in mvo.AttributeValues)
                {
                    if (!av.ReferenceValueId.HasValue && av.ReferenceValue != null && av.ReferenceValue.Id != Guid.Empty)
                        refFixups.Add((mvo.Id, av.AttributeId, av.ReferenceValue.Id));
                }
            }

            await _syncRepo.UpdateMetaverseObjectsAsync(_pendingMvoUpdates);
            Log.Verbose("PersistPendingMetaverseObjectsAsync: Updated {Count} MVOs in batch", _pendingMvoUpdates.Count);

            if (refFixups.Count > 0)
                await _syncRepo.FixupMvoReferenceValueIdsAsync(refFixups);

            _pendingMvoUpdates.Clear();
        }

        // Batch update CSOs that had JoinType/DateJoined/MetaverseObjectId changed during
        // projection or join. This must run AFTER MVO creates so that projected CSOs can
        // pick up the newly assigned MVO IDs (set via EF relationship fixup during AddRange).
        if (_pendingCsoJoinUpdates.Count > 0)
        {
            // For projected CSOs, MetaverseObjectId was not set at projection time (MVO had
            // no ID yet). Now that CreateMetaverseObjectsAsync has persisted the MVOs, EF has
            // assigned IDs and relationship fixup has set MetaverseObjectId on the CSOs.
            // Ensure the FK is populated for any CSO where it's still null.
            foreach (var cso in _pendingCsoJoinUpdates)
            {
                if (cso.MetaverseObjectId == null && cso.MetaverseObject != null)
                    cso.MetaverseObjectId = cso.MetaverseObject.Id;
            }

            await _syncRepo.UpdateConnectedSystemObjectJoinStatesAsync(_pendingCsoJoinUpdates);
            Log.Verbose("PersistPendingMetaverseObjectsAsync: Updated {Count} CSO join states in batch", _pendingCsoJoinUpdates.Count);
            _pendingCsoJoinUpdates.Clear();
        }

        // Re-key deferred MVO→RPEI mappings now that newly projected MVOs have real IDs.
        // This enables EvaluateOutboundExportsAsync to find the originating RPEI for outcome linking.
        if (_deferredMvoRpeiMappings.Count > 0)
        {
            foreach (var (mvo, rpei) in _deferredMvoRpeiMappings)
            {
                if (mvo.Id != Guid.Empty)
                    _mvoIdToRpei[mvo.Id] = rpei;
            }
            _deferredMvoRpeiMappings.Clear();
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
            // Note: reference attributes are inherently multi-valued so MVA->SVA warnings are unlikely here
            foreach (var syncRule in syncRules)
            {
                // Warnings already captured in the first pass; reference-only pass does not repeat them
                ProcessInboundAttributeFlow(cso, syncRule, skipReferenceAttributes: false, onlyReferenceAttributes: true);
            }

            // Check for unresolved cross-page references after processing this CSO.
            // Only check CSO attribute values that are actually mapped by reference-type sync
            // rule mappings. Checking ALL CSO attributes would incorrectly flag users with
            // unmapped reference attributes (e.g. managedBy), causing thousands of unnecessary
            // CSOs to be re-processed in the cross-page resolution pass.
            var mappedRefAttributeIds = syncRules
                .SelectMany(sr => sr.AttributeFlowRules)
                .Where(afr => afr.TargetMetaverseAttribute?.Type == AttributeDataType.Reference)
                .SelectMany(afr => afr.Sources)
                .Where(s => s.ConnectedSystemAttributeId.HasValue)
                .Select(s => s.ConnectedSystemAttributeId!.Value)
                .ToHashSet();

            // A cross-page reference is unresolved if the referenced CSO has no MetaverseObjectId
            // (it hasn't been joined/projected yet). ResolvedReferenceMetaverseObjectId (direct SQL)
            // is the primary source; fall back to navigation for in-memory test compatibility.
            var hasUnresolvedCrossPageRefs = mappedRefAttributeIds.Count > 0 &&
                cso.AttributeValues.Any(av =>
                    mappedRefAttributeIds.Contains(av.AttributeId) &&
                    av.ReferenceValueId.HasValue &&
                    !av.ResolvedReferenceMetaverseObjectId.HasValue &&
                    (av.ReferenceValue == null || av.ReferenceValue.MetaverseObject == null));

            if (hasUnresolvedCrossPageRefs)
            {
                var syncRuleIds = syncRules.Select(sr => sr.Id).ToList();
                _unresolvedCrossPageReferences.Add((cso.Id, syncRuleIds));
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

                // Capture additions and removals BEFORE applying changes (they get cleared by ApplyPendingMetaverseObjectAttributeChanges)
                // This is needed for both export (Remove changes for MVAs) and MVO change tracking.
                // Check both ReferenceValue (navigation, set for in-memory resolved refs) and
                // ReferenceValueId (scalar FK, always set for persisted refs). The navigation may
                // be null when the ReferenceValue ThenInclude is omitted from MVO loading queries.
                var refAddedAttributes = mvo.PendingAttributeValueAdditions
                    .Where(av => av.ReferenceValue != null || av.ReferenceValueId.HasValue)
                    .ToList();
                var refRemovedAttributesList = mvo.PendingAttributeValueRemovals
                    .Where(av => av.ReferenceValue != null || av.ReferenceValueId.HasValue)
                    .ToList();
                // Also keep as HashSet for export evaluation (existing code expects HashSet)
                var refRemovedAttributes = refRemovedAttributesList.Count > 0
                    ? refRemovedAttributesList.ToHashSet()
                    : null;

                // Ensure we have an RPEI for this CSO (needed for MVO change tracking and activity visibility)
                var rpei = existingRpei;
                if (rpei == null)
                {
                    // No RPEI exists for this CSO - create one for the reference attribute flow
                    rpei = _activity.PrepareRunProfileExecutionItem();
                    rpei.ConnectedSystemObject = cso;
                    rpei.ConnectedSystemObjectId = cso.Id;
                    rpei.ObjectChangeType = ObjectChangeType.AttributeFlow;
                    _activity.RunProfileExecutionItems.Add(rpei);
                    Log.Debug("ProcessDeferredReferenceAttributes: Created RPEI for CSO {CsoId} with reference-only changes",
                        cso.Id);

                    // Build outcome for the new reference-only RPEI
                    if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                    {
                        SyncOutcomeBuilder.AddRootOutcome(rpei,
                            ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                            detailCount: additionsFromReferences + removalsFromReferences);

                        // Register in MVO→RPEI lookup for export evaluation
                        _mvoIdToRpei[mvo.Id] = rpei;
                    }
                }

                // Capture MVO changes for change tracking (reference attributes processed separately from scalar attributes)
                // Try to find an existing pending change entry for this MVO (from scalar attribute processing)
                // and merge reference changes into it, rather than creating duplicate entries
                if (refAddedAttributes.Count > 0 || refRemovedAttributesList.Count > 0)
                {
                    var existingChangeEntry = _pendingMvoChanges.FirstOrDefault(p => p.Mvo == mvo);
                    if (existingChangeEntry.Mvo != null)
                    {
                        // Merge reference changes into existing entry (keeps existing ChangeType)
                        existingChangeEntry.Additions.AddRange(refAddedAttributes);
                        existingChangeEntry.Removals.AddRange(refRemovedAttributesList);

                        // If the existing entry is a Join or Projection, increment AttributeFlowCount
                        // so reference attribute flows aren't absorbed into the primary change type
                        if (existingChangeEntry.ChangeType is ObjectChangeType.Joined or ObjectChangeType.Projected
                            && existingChangeEntry.Rpei != null)
                        {
                            existingChangeEntry.Rpei.AttributeFlowCount =
                                (existingChangeEntry.Rpei.AttributeFlowCount ?? 0)
                                + refAddedAttributes.Count + refRemovedAttributesList.Count;

                            // Update the existing outcome tree to reflect merged reference attribute changes
                            if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                            {
                                // Note: Do NOT set DetailCount on the root Projected/Joined outcome — the attribute
                                // count is only meaningful on the AttributeFlow child node (Detailed mode below).

                                // In Detailed mode, also update the AttributeFlow child node
                                if (_syncOutcomeTrackingLevel == ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed)
                                {
                                    var attrFlowChild = existingChangeEntry.Rpei.SyncOutcomes.FirstOrDefault(o =>
                                        o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow
                                        && o.ParentSyncOutcome != null);
                                    if (attrFlowChild != null)
                                        attrFlowChild.DetailCount = existingChangeEntry.Rpei.AttributeFlowCount;
                                }
                            }
                        }
                    }
                    else
                    {
                        // No existing entry - create new one (reference-only change scenario)
                        // Use AttributeFlow since this is just attribute changes on an existing MVO
                        _pendingMvoChanges.Add((mvo, refAddedAttributes, refRemovedAttributesList, ObjectChangeType.AttributeFlow, rpei));
                    }
                }

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
                if (changedRefAttributes.Count > 0)
                {
                    MergeOrAddPendingExportEvaluation(mvo, changedRefAttributes, refRemovedAttributes);
                }
            }
        }

        Log.Verbose("ProcessDeferredReferenceAttributes: Processed {Count} CSOs, {Changes} total reference changes, " +
            "{UnresolvedCount} CSOs have unresolved cross-page references",
            _pendingReferenceAttributeProcessing.Count, totalChanges, _unresolvedCrossPageReferences.Count);

        _pendingReferenceAttributeProcessing.Clear();
        span.SetSuccess();

        return totalChanges;
    }

    /// <summary>
    /// Resolves cross-page reference attributes after all pages have been processed.
    /// During page processing, some CSO reference attributes cannot be resolved because the
    /// referenced CSO is on a different page and its MVO either doesn't exist yet (future page)
    /// or has been discarded from the working set (previous page).
    /// After all pages, all MVOs exist in the database. This method reloads the CSOs with
    /// unresolved references and re-processes their reference attributes.
    /// </summary>
    /// <param name="activeSyncRules">The active sync rules for this connected system (already loaded by the caller).</param>
    protected async Task ResolveCrossPageReferencesAsync(List<SyncRule> activeSyncRules)
    {
        if (_unresolvedCrossPageReferences.Count == 0)
            return;

        using var span = Diagnostics.Sync.StartSpan("ResolveCrossPageReferences");
        span.SetTag("csoCount", _unresolvedCrossPageReferences.Count);

        var totalCrossPagesToResolve = _unresolvedCrossPageReferences.Count;
        Log.Information("ResolveCrossPageReferences: Resolving cross-page references for {Count} CSOs",
            totalCrossPagesToResolve);

        await _syncRepo.UpdateActivityMessageAsync(_activity,
            $"Resolving cross-page references (0 / {totalCrossPagesToResolve})");

        // Build a lookup of CSO ID → existing RPEI for CSOs that need cross-page resolution.
        // These RPEIs were created during initial page processing (e.g., Projected, Joined) and have
        // already been persisted to the database. We save references BEFORE clearing the in-memory
        // collection so that cross-page resolution can merge reference attribute flows into the
        // existing RPEIs rather than creating duplicates.
        var unresolvedCsoIds = _unresolvedCrossPageReferences.Select(x => x.CsoId).ToHashSet();
        var existingRpeisByCsoId = new Dictionary<Guid, ActivityRunProfileExecutionItem>();
        foreach (var rpei in _activity.RunProfileExecutionItems)
        {
            if (rpei.ConnectedSystemObjectId.HasValue && unresolvedCsoIds.Contains(rpei.ConnectedSystemObjectId.Value))
                existingRpeisByCsoId[rpei.ConnectedSystemObjectId.Value] = rpei;
        }

        // Clear the change tracker to prevent performance degradation.
        // After processing all pages, the change tracker accumulates thousands of tracked entities
        // (MVOs, CSOs, attribute values, etc.). This causes EF Core's identity resolution to become
        // extremely slow when loading the cross-page reference CSOs with deep Include chains.
        // All previous page data has been fully persisted, so clearing is safe.
        //
        // IMPORTANT: Also clear the Activity's in-memory RPEI collection. RPEIs from all pages have
        // been persisted to the database, but the in-memory list still holds references to detached
        // entities (CSO → MVO → Type → Attributes). If any subsequent EF operation (Update, Remove,
        // SaveChanges) triggers change detection on the Activity, EF will try to re-track those stale
        // entity references, causing identity conflicts with freshly loaded entities from the
        // cross-page resolution query.
        var rpeiCountBeforeClear = _activity.RunProfileExecutionItems.Count;
        _activity.RunProfileExecutionItems.Clear();
        _syncRepo.ClearChangeTracker();
        Log.Debug("ResolveCrossPageReferences: Cleared change tracker and {RpeiCount} persisted RPEIs from activity",
            rpeiCountBeforeClear);

        // Build sync rule lookup (keyed by ID) for O(1) access
        var requiredSyncRuleIds = _unresolvedCrossPageReferences
            .SelectMany(x => x.SyncRuleIds)
            .Distinct()
            .ToHashSet();
        var syncRulesById = activeSyncRules
            .Where(sr => requiredSyncRuleIds.Contains(sr.Id))
            .ToDictionary(sr => sr.Id);

        // Process in batches to avoid loading too many CSOs at once
        var pageSize = await _syncServer.GetSyncPageSizeAsync();
        var totalItems = _unresolvedCrossPageReferences.Count;
        var totalBatches = (int)Math.Ceiling((double)totalItems / pageSize);

        var resolvedCount = 0;
        var updatedExistingRpeis = new List<ActivityRunProfileExecutionItem>();

        // Collect IDs of removed MVO attribute values across all batches for explicit raw SQL deletion.
        // When ClearChangeTracker() runs between batches, EF loses knowledge of attribute values
        // removed from MVO.AttributeValues collections (ApplyPendingAttributeChanges). Without this,
        // the removed rows persist in the database and member removals are never exported.
        var removedMvoAttributeValueIds = new List<Guid>();

        for (var batchIndex = 0; batchIndex < totalBatches; batchIndex++)
        {
            var batch = _unresolvedCrossPageReferences
                .Skip(batchIndex * pageSize)
                .Take(pageSize)
                .ToList();

            await _syncRepo.UpdateActivityMessageAsync(_activity,
                $"Resolving cross-page references ({resolvedCount} / {totalCrossPagesToResolve}) - loading batch {batchIndex + 1} of {totalBatches}");

            // Reload CSOs from DB — now all MVOs exist, so ReferenceValue.MetaverseObject will be populated
            var csoIds = batch.Select(x => x.CsoId).ToList();
            List<ConnectedSystemObject> reloadedCsos;
            using (Diagnostics.Sync.StartSpan("ReloadCsosForCrossPageReferences").SetTag("count", csoIds.Count))
            {
                reloadedCsos = await _syncRepo.GetConnectedSystemObjectsForReferenceResolutionAsync(csoIds);
            }

            // Index reloaded CSOs by ID for O(1) lookup
            var csosById = reloadedCsos.ToDictionary(c => c.Id);

            foreach (var (csoId, syncRuleIds) in batch)
            {
                if (!csosById.TryGetValue(csoId, out var cso) || cso.MetaverseObject == null)
                {
                    Log.Warning("ResolveCrossPageReferences: CSO {CsoId} not found or not joined after reload, skipping",
                        csoId);
                    continue;
                }

                var mvo = cso.MetaverseObject;

                // Resolve the sync rules for this CSO
                var applicableSyncRules = syncRuleIds
                    .Where(id => syncRulesById.ContainsKey(id))
                    .Select(id => syncRulesById[id])
                    .ToList();

                var beforeAdditions = mvo.PendingAttributeValueAdditions.Count;
                var beforeRemovals = mvo.PendingAttributeValueRemovals.Count;

                // Process ONLY reference attributes — now all references should resolve.
                // isFinalReferencePass: true so any still-unresolved references are logged as warnings.
                foreach (var syncRule in applicableSyncRules)
                {
                    ProcessInboundAttributeFlow(cso, syncRule, skipReferenceAttributes: false, onlyReferenceAttributes: true, isFinalReferencePass: true);
                }

                var additionsCount = mvo.PendingAttributeValueAdditions.Count - beforeAdditions;
                var removalsCount = mvo.PendingAttributeValueRemovals.Count - beforeRemovals;

                if (additionsCount > 0 || removalsCount > 0)
                {
                    Log.Debug("ResolveCrossPageReferences: CSO {CsoId} resolved {Adds} reference additions, {Removes} removals",
                        csoId, additionsCount, removalsCount);

                    // Capture changes before applying (they get cleared by ApplyPendingMetaverseObjectAttributeChanges)
                    var refAddedAttributes = mvo.PendingAttributeValueAdditions.ToList();
                    var refRemovedAttributesList = mvo.PendingAttributeValueRemovals.ToList();
                    var refRemovedAttributes = refRemovedAttributesList.Count > 0
                        ? refRemovedAttributesList.ToHashSet()
                        : (HashSet<MetaverseObjectAttributeValue>?)null;

                    // Collect IDs of removed attribute values for explicit deletion after
                    // ClearChangeTracker (which loses EF's collection-removal knowledge).
                    foreach (var removed in refRemovedAttributesList)
                    {
                        if (removed.Id != Guid.Empty)
                            removedMvoAttributeValueIds.Add(removed.Id);
                    }

                    // Merge into existing RPEI if one was created during initial page processing
                    // (e.g., Projected or Joined). This prevents duplicate RPEIs for the same CSO
                    // which would inflate TotalAttributeFlows counts and show duplicate rows in the UI.
                    ActivityRunProfileExecutionItem rpei;
                    if (existingRpeisByCsoId.TryGetValue(csoId, out var existingRpei))
                    {
                        rpei = existingRpei;
                        rpei.AttributeFlowCount = (rpei.AttributeFlowCount ?? 0) + additionsCount + removalsCount;

                        // Update outcome tree: add or update the AttributeFlow child node
                        if (_syncOutcomeTrackingLevel == ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed
                            && rpei.ObjectChangeType is ObjectChangeType.Projected or ObjectChangeType.Joined)
                        {
                            var attrFlowChild = rpei.SyncOutcomes.FirstOrDefault(o =>
                                o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow
                                && o.ParentSyncOutcome != null);
                            if (attrFlowChild != null)
                                attrFlowChild.DetailCount = rpei.AttributeFlowCount;
                            else
                            {
                                // No child yet (e.g., initial projection had no scalar attribute changes).
                                // Add an AttributeFlow child under the root Projected/Joined outcome.
                                var rootOutcome = rpei.SyncOutcomes.FirstOrDefault(o => o.ParentSyncOutcome == null);
                                if (rootOutcome != null)
                                {
                                    SyncOutcomeBuilder.AddChildOutcome(rpei, rootOutcome,
                                        ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                                        targetEntityDescription: mvo.DisplayName,
                                        detailCount: rpei.AttributeFlowCount);
                                }
                            }
                        }

                        // Rebuild OutcomeSummary to reflect the updated attribute flow count
                        if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                        {
                            SyncOutcomeBuilder.BuildOutcomeSummary(rpei);
                            _mvoIdToRpei[mvo.Id] = rpei;
                        }

                        // Track for batch database update (already persisted, needs AttributeFlowCount
                        // and OutcomeSummary updated). Do NOT re-add to _activity.RunProfileExecutionItems
                        // as that would cause FlushRpeisAsync to INSERT a duplicate row.
                        updatedExistingRpeis.Add(rpei);

                        // Re-add to the activity's in-memory collection for test environments where
                        // RPEIs are not persisted to a real database (EF fallback path keeps RPEIs
                        // in memory for assertions). In production, FlushRpeisAsync will skip these
                        // because they are handled by the separate bulk update below.
                        if (!_hasRawSqlSupport)
                            _activity.RunProfileExecutionItems.Add(rpei);

                        Log.Debug("ResolveCrossPageReferences: Merged reference attribute flow into existing {ChangeType} RPEI for CSO {CsoId}",
                            rpei.ObjectChangeType, csoId);
                    }
                    else
                    {
                        // No existing RPEI (uncommon — only if the CSO had no scalar attribute changes
                        // during initial page processing). Create a new reference-only RPEI.
                        rpei = _activity.PrepareRunProfileExecutionItem();
                        rpei.ConnectedSystemObject = cso;
                        rpei.ConnectedSystemObjectId = cso.Id;
                        rpei.ObjectChangeType = ObjectChangeType.AttributeFlow;
                        rpei.AttributeFlowCount = additionsCount + removalsCount;
                        _activity.RunProfileExecutionItems.Add(rpei);

                        // Build outcome for the new reference-only RPEI
                        if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                        {
                            SyncOutcomeBuilder.AddRootOutcome(rpei,
                                ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
                                detailCount: additionsCount + removalsCount);

                            _mvoIdToRpei[mvo.Id] = rpei;
                        }
                    }

                    // Track MVO changes for change tracking
                    _pendingMvoChanges.Add((mvo, refAddedAttributes, refRemovedAttributesList,
                        ObjectChangeType.AttributeFlow, rpei));

                    // Apply changes to MVO
                    ApplyPendingMetaverseObjectAttributeChanges(mvo);

                    // Queue for persistence and export evaluation
                    if (!_pendingMvoUpdates.Contains(mvo))
                        _pendingMvoUpdates.Add(mvo);

                    var changedRefAttributes = mvo.AttributeValues
                        .Where(av => av.ReferenceValue != null || av.ReferenceValueId.HasValue)
                        .Cast<MetaverseObjectAttributeValue>()
                        .ToList();

                    if (refRemovedAttributes != null)
                    {
                        changedRefAttributes.AddRange(refRemovedAttributes);
                    }

                    if (changedRefAttributes.Count > 0)
                    {
                        MergeOrAddPendingExportEvaluation(mvo, changedRefAttributes, refRemovedAttributes);
                    }
                }
            }

            // Batch-delete existing pending exports from DB before export evaluation.
            // During cross-page resolution, PEs from earlier pages have been flushed to DB.
            // Without this, each MVO's export evaluation hits GetPendingExportByConnectedSystemObjectIdAsync
            // individually (N+1 problem, ~1.9s per group PE due to heavy Include chains).
            // By deleting them in one batch query, the DB fallback finds nothing and evaluation
            // creates fresh PEs with the fully-resolved reference attributes.
            if (_pendingExportEvaluations.Count > 0 && _exportEvaluationCache != null)
            {
                // Refresh per-page cache for this batch's MVOs so CsoLookup has the target CSOs
                var mvoIds = _pendingExportEvaluations.Select(e => e.Mvo.Id).ToHashSet();
                await _syncServer.RefreshExportEvaluationCacheForPageAsync(
                    _exportEvaluationCache, mvoIds);

                var targetCsoIds = _exportEvaluationCache.CsoLookup
                    .Where(kvp => mvoIds.Contains(kvp.Key.MvoId))
                    .Select(kvp => kvp.Value.Id)
                    .Distinct()
                    .ToList();

                if (targetCsoIds.Count > 0)
                {
                    // Use raw SQL delete by CSO IDs instead of loading PE entities into the change
                    // tracker. After ClearChangeTracker(), loading PEs with Include chains would create
                    // MetaverseAttribute instances that conflict with instances already tracked by the
                    // cross-page CSO query, causing identity resolution failures.
                    var deletedCount = await _syncRepo.DeletePendingExportsByConnectedSystemObjectIdsAsync(targetCsoIds);
                    if (deletedCount > 0)
                    {
                        Log.Information("ResolveCrossPageReferences: Batch-deleted {Count} existing pending exports " +
                            "from earlier pages before re-evaluation with resolved references", deletedCount);
                    }
                }
            }

            resolvedCount += batch.Count;

            // Disable AutoDetectChangesEnabled for the flush sequence (including the status update).
            // After ClearChangeTracker(), the change tracker contains entities loaded by the cross-page
            // CSO query (with Include chains that bring in MetaverseAttribute, MetaverseObjectType, etc.).
            // Multiple MVOs sharing the same Type/Attributes create separate in-memory instances of these
            // shared entities. When SaveChangesAsync calls DetectChanges(), it walks ALL tracked entities'
            // navigation properties and discovers these conflicting instances, causing identity conflicts.
            //
            // By disabling auto-detect, SaveChangesAsync only persists entities we explicitly mark via
            // Entry().State (in UpdateMetaverseObjectsAsync and UpdateActivityAsync), without scanning
            // navigation properties that lead to shared MetaverseAttribute/MetaverseObjectType entities.
            //
            // IMPORTANT: Must be set BEFORE any SaveChangesAsync call — including the activity message
            // update — because by this point the tracker already contains conflicting instances from
            // the batch processing above.
            _syncRepo.SetAutoDetectChangesEnabled(false);
            try
            {
                await _syncRepo.UpdateActivityMessageAsync(_activity,
                    $"Resolving cross-page references ({resolvedCount} / {totalCrossPagesToResolve}) - saving changes");

                // Flush RPEIs FIRST to prevent DetectChanges() from discovering them during
                // subsequent SaveChangesAsync calls (same pattern as per-page processing).
                // NOTE: This only inserts NEW RPEIs. Existing RPEIs (merged from initial page
                // processing) are updated separately below.
                await FlushRpeisAsync();

                // Batch-update existing RPEIs whose AttributeFlowCount and OutcomeSummary changed
                // due to cross-page reference resolution merging. These RPEIs were already persisted
                // during initial page processing; we update them in-place rather than inserting duplicates.
                if (updatedExistingRpeis.Count > 0)
                {
                    // Collect any new sync outcome nodes added during merge (e.g., AttributeFlow child)
                    var newOutcomes = updatedExistingRpeis
                        .SelectMany(r => r.SyncOutcomes)
                        .Where(o => o.Id == Guid.Empty)
                        .ToList();
                    foreach (var outcome in newOutcomes)
                    {
                        if (outcome.Id == Guid.Empty)
                            outcome.Id = Guid.NewGuid();
                    }

                    await _syncRepo.BulkUpdateRpeiOutcomesAsync(updatedExistingRpeis, newOutcomes);

                    // Accumulate stats ONLY for newly added outcome nodes (not the full RPEIs, which
                    // were already counted during initial page flush). New outcomes can occur when the
                    // initial RPEI had no AttributeFlow child (Detailed mode) and cross-page resolution
                    // adds one. In the common case, no new outcomes are added (just DetailCount updated).
                    if (_hasRawSqlSupport && newOutcomes.Count > 0)
                    {
                        _activity.TotalAttributeFlows += newOutcomes.Count(o =>
                            o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
                    }

                    Log.Debug("ResolveCrossPageReferences: Updated {Count} existing RPEIs with merged reference attribute flows",
                        updatedExistingRpeis.Count);
                    updatedExistingRpeis.Clear();
                }

                // Flush this batch (same sequence as per-page processing)
                await PersistPendingMetaverseObjectsAsync();
                await CreatePendingMvoChangeObjectsAsync();
                await EvaluatePendingExportsAsync();
                await FlushPendingExportOperationsAsync();

                // Flush any RPEIs generated during the flush sequence
                await FlushRpeisAsync();

                // Update activity progress
                await _syncRepo.UpdateActivityAsync(_activity);
            }
            finally
            {
                _syncRepo.SetAutoDetectChangesEnabled(true);
            }

            // Clear the change tracker between batches to prevent entity tracking conflicts.
            // Each batch loads CSOs, MVOs, MetaverseObjectType, MetaverseAttribute, and other
            // entities into the tracker. Without clearing, subsequent batches encounter identity
            // conflicts when the same shared entities (e.g., MetaverseObjectType) are loaded again.
            // All batch data has been fully persisted above, so clearing is safe.
            // The Activity entity will be re-attached by UpdateActivityAsync's detached handling.
            if (batchIndex < totalBatches - 1)
                _syncRepo.ClearChangeTracker();
        }

        // Explicitly delete removed MVO attribute values via raw SQL.
        // ClearChangeTracker between batches erases EF's knowledge of attribute values removed
        // from MVO.AttributeValues collections (by ApplyPendingAttributeChanges). Without this
        // explicit deletion, the removed rows persist in the database — member removals would
        // never be exported to target systems.
        if (removedMvoAttributeValueIds.Count > 0)
        {
            await _syncRepo.DeleteMetaverseObjectAttributeValuesByIdsAsync(removedMvoAttributeValueIds);
            Log.Information("ResolveCrossPageReferences: Explicitly deleted {Count} removed MVO attribute values via raw SQL",
                removedMvoAttributeValueIds.Count);
        }

        Log.Information("ResolveCrossPageReferences: Completed cross-page reference resolution for {Count} CSOs",
            totalCrossPagesToResolve);

        _unresolvedCrossPageReferences.Clear();

        // Clear the change tracker after cross-page resolution completes.
        // The tracker contains entities loaded by the cross-page CSO query with deep Include chains
        // that bring in MetaverseAttribute and MetaverseObjectType instances. Multiple MVOs sharing
        // the same type/attributes create separate in-memory instances that conflict when any
        // subsequent SaveChangesAsync triggers DetectChanges. Clearing removes all tracked entities
        // so the caller's next SaveChangesAsync starts with a clean tracker.
        // The Activity entity will be re-attached by UpdateActivityAsync's detached entity handling.
        _syncRepo.ClearChangeTracker();

        span.SetSuccess();
    }

    /// <summary>
    /// Batch evaluates export rules for all MVOs that changed during the current page.
    /// Must be called after PersistPendingMetaverseObjectsAsync so MVOs have valid IDs for pending export FKs.
    /// Skips MVOs that are queued for immediate deletion (0-grace-period) because
    /// FlushPendingMvoDeletionsAsync will create the correct Delete exports via EvaluateMvoDeletionAsync.
    /// Creating Update exports for recalled attributes on a doomed MVO is spurious and can produce
    /// invalid attribute values (e.g., empty DN from expression evaluation against recalled attributes).
    /// </summary>
    protected async Task EvaluatePendingExportsAsync()
    {
        if (_pendingExportEvaluations.Count == 0)
            return;

        using var span = Diagnostics.Sync.StartSpan("EvaluatePendingExports");
        span.SetTag("count", _pendingExportEvaluations.Count);

        // Refresh the per-page portion of the export evaluation cache for only this page's MVOs.
        // This loads target CSOs and their attribute values for no-net-change detection, scoped to
        // just the MVOs being evaluated — avoiding the previous approach of loading ALL target CSOs
        // upfront which consumed multiple GB at 100K+ objects.
        if (_exportEvaluationCache != null)
        {
            var mvoIdsForExport = _pendingExportEvaluations
                .Select(x => x.Mvo.Id)
                .Where(id => id != Guid.Empty)
                .Distinct();
            await _syncServer.RefreshExportEvaluationCacheForPageAsync(_exportEvaluationCache, mvoIdsForExport);
        }

        // Build a set of MVO IDs pending immediate deletion.
        // These MVOs will get Delete exports in FlushPendingMvoDeletionsAsync,
        // so creating Update exports here would be spurious.
        var pendingDeletionMvoIds = _pendingMvoDeletions.Count > 0
            ? _pendingMvoDeletions.Select(m => m.Mvo.Id).ToHashSet()
            : null;

        var skippedCount = 0;

        foreach (var (mvo, changedAttributes, removedAttributes) in _pendingExportEvaluations)
        {
            if (pendingDeletionMvoIds != null && pendingDeletionMvoIds.Contains(mvo.Id))
            {
                Log.Debug("EvaluatePendingExportsAsync: Skipping export evaluation for MVO {MvoId} — " +
                    "queued for immediate deletion, Delete exports will be created by FlushPendingMvoDeletionsAsync",
                    mvo.Id);
                skippedCount++;
                continue;
            }

            using (Diagnostics.Sync.StartSpan("EvaluateSingleMvoExports")
                .SetTag("mvoId", mvo.Id)
                .SetTag("changedAttributeCount", changedAttributes.Count))
            {
                await EvaluateOutboundExportsAsync(mvo, changedAttributes, removedAttributes);
            }
        }

        if (skippedCount > 0)
        {
            Log.Information("EvaluatePendingExportsAsync: Skipped {SkippedCount} MVO(s) pending immediate deletion", skippedCount);
        }

        Log.Verbose("EvaluatePendingExportsAsync: Evaluated exports for {Count} MVOs ({SkippedCount} skipped due to pending deletion)",
            _pendingExportEvaluations.Count, skippedCount);
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
            await _syncRepo.CreateConnectedSystemObjectsAsync(_provisioningCsosToCreate);

            // Add provisioned CSOs to the lookup cache so confirming imports can find them.
            // Provisioned CSOs don't have a primary external ID yet (it's system-assigned during export),
            // so we cache by secondary external ID (e.g., distinguishedName) instead.
            foreach (var cso in _provisioningCsosToCreate)
            {
                if (cso.SecondaryExternalIdAttributeId.HasValue)
                {
                    var secondaryIdValue = cso.AttributeValues?.FirstOrDefault(av => av.AttributeId == cso.SecondaryExternalIdAttributeId);
                    if (secondaryIdValue?.StringValue != null)
                        _syncServer.AddCsoToCache(cso.ConnectedSystemId, cso.SecondaryExternalIdAttributeId.Value, secondaryIdValue.StringValue, cso.Id);
                }
            }

            Log.Verbose("FlushPendingExportOperationsAsync: Created {Count} provisioning CSOs in batch", _provisioningCsosToCreate.Count);
            _provisioningCsosToCreate.Clear();
        }

        // Pre-export reconciliation: detect deferred CREATE/UPDATE PEs whose CSOs already have
        // a DELETE PE persisted to the DB (created immediately by HandleOutboundDeprovisioningAsync
        // or EvaluateMvoDeletionAsync during the same page). Cancel contradictory pairs to avoid
        // exporting objects that would be immediately deleted.
        if (_pendingExportsToCreate.Count > 0)
        {
            await ReconcileDeferredExportsAgainstPersistedDeletesAsync();
        }

        // Batch create new pending exports (evaluated during export evaluation phase)
        if (_pendingExportsToCreate.Count > 0)
        {
            // Delete any existing PEs for the same CSOs to prevent unique constraint violations.
            // This can happen when drift detection creates a corrective PE for a CSO that already
            // has a PE from a previous sync step (e.g., forward sync PE not yet exported).
            var csoIdsWithNewPes = _pendingExportsToCreate
                .Where(pe => pe.ConnectedSystemObjectId.HasValue)
                .Select(pe => pe.ConnectedSystemObjectId!.Value)
                .Distinct()
                .ToList();
            if (csoIdsWithNewPes.Count > 0)
            {
                var deletedCount = await _syncRepo.DeletePendingExportsByConnectedSystemObjectIdsAsync(csoIdsWithNewPes);
                if (deletedCount > 0)
                {
                    Log.Information("FlushPendingExportOperationsAsync: Deleted {Count} existing pending exports to prevent duplicates before batch create",
                        deletedCount);
                }
            }

            await _syncRepo.CreatePendingExportsAsync(_pendingExportsToCreate);
            Log.Verbose("FlushPendingExportOperationsAsync: Created {Count} pending exports in batch", _pendingExportsToCreate.Count);
            _pendingExportsToCreate.Clear();
        }

        // Batch delete confirmed pending exports
        if (_pendingExportsToDelete.Count > 0)
        {
            await _syncRepo.DeletePendingExportsAsync(_pendingExportsToDelete);
            Log.Verbose("FlushPendingExportOperationsAsync: Deleted {Count} confirmed pending exports in batch", _pendingExportsToDelete.Count);
            _pendingExportsToDelete.Clear();
        }

        // Batch update pending exports that need error tracking
        if (_pendingExportsToUpdate.Count > 0)
        {
            await _syncRepo.UpdatePendingExportsAsync(_pendingExportsToUpdate);
            Log.Verbose("FlushPendingExportOperationsAsync: Updated {Count} pending exports in batch", _pendingExportsToUpdate.Count);
            _pendingExportsToUpdate.Clear();
        }

        span.SetSuccess();
    }

    /// <summary>
    /// Checks deferred CREATE/UPDATE pending exports (in-memory) against DELETE pending exports
    /// already persisted to the DB during this page. Removes contradictory pairs:
    /// - CREATE(Pending) + DELETE(Pending) → cancel both (no net change)
    /// - UPDATE(Pending) + DELETE(Pending) → remove UPDATE (DELETE still needed)
    /// </summary>
    private async Task ReconcileDeferredExportsAgainstPersistedDeletesAsync()
    {
        // Collect CSO IDs from deferred exports that have a CSO reference
        var csoIds = _pendingExportsToCreate
            .Where(pe => pe.ConnectedSystemObjectId.HasValue)
            .Select(pe => pe.ConnectedSystemObjectId!.Value)
            .Distinct()
            .ToList();

        if (csoIds.Count == 0)
            return;

        // Batch lookup: find DELETE PEs already in the DB for these CSO IDs.
        // Use the lightweight (AsNoTracking, no CSO Include) variant to avoid loading
        // ConnectedSystemObject entities into the change tracker — tracked CSOs from this
        // query would conflict with CSOs loaded during cross-page reference resolution batches.
        var persistedPesByCsoId = await _syncRepo.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(csoIds);

        if (persistedPesByCsoId.Count == 0)
            return;

        var reconciledCount = 0;

        foreach (var (csoId, persistedPe) in persistedPesByCsoId)
        {
            if (persistedPe.ChangeType != PendingExportChangeType.Delete ||
                persistedPe.Status != PendingExportStatus.Pending)
                continue;

            // Find the matching deferred CREATE or UPDATE
            var deferredPe = _pendingExportsToCreate.FirstOrDefault(pe =>
                pe.ConnectedSystemObjectId == csoId &&
                pe.Status == PendingExportStatus.Pending);

            if (deferredPe == null)
                continue;

            if (deferredPe.ChangeType == PendingExportChangeType.Create)
            {
                // CREATE + DELETE → cancel both. Remove deferred CREATE and queue persisted DELETE for deletion.
                _pendingExportsToCreate.Remove(deferredPe);
                _pendingExportsToDelete.Add(persistedPe);

                // Also remove the provisioning CSO — it was never exported, so no need to create it
                var provisioningCso = _provisioningCsosToCreate.FirstOrDefault(c => c.Id == csoId);
                if (provisioningCso != null)
                    _provisioningCsosToCreate.Remove(provisioningCso);

                Log.Information("ReconcileDeferredExportsAgainstPersistedDeletesAsync: Cancelled CREATE PE {CreateId} and DELETE PE {DeleteId} for CSO {CsoId} — no net change, object was never exported",
                    deferredPe.Id, persistedPe.Id, csoId);
                reconciledCount++;
            }
            else if (deferredPe.ChangeType == PendingExportChangeType.Update)
            {
                // UPDATE + DELETE → remove UPDATE only, DELETE still needed
                _pendingExportsToCreate.Remove(deferredPe);

                Log.Information("ReconcileDeferredExportsAgainstPersistedDeletesAsync: Removed redundant UPDATE PE {UpdateId} for CSO {CsoId} — DELETE PE {DeleteId} will proceed",
                    deferredPe.Id, csoId, persistedPe.Id);
                reconciledCount++;
            }
        }

        if (reconciledCount > 0)
        {
            Log.Information("ReconcileDeferredExportsAgainstPersistedDeletesAsync: Reconciled {Count} CREATE/UPDATE+DELETE pairs in this page",
                reconciledCount);
        }
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
            await _syncRepo.DeleteConnectedSystemObjectsAsync(_quietCsosToDelete);
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

        await _syncServer.DeleteConnectedSystemObjectsAsync(csosToDelete, executionItems);
        Log.Verbose("FlushObsoleteCsoOperationsAsync: Deleted {Count} obsolete CSOs in batch", _obsoleteCsosToDelete.Count);

        _obsoleteCsosToDelete.Clear();
        _pendingDisconnectedMvoIds.Clear();
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

        foreach (var (mvo, finalAttributeValues) in _pendingMvoDeletions)
        {
            try
            {
                // Safeguard: If this MVO was re-joined by a new CSO during Pass 2 of the same page
                // (e.g., an obsolete CSO disconnected in Pass 1 triggered 0-grace-period deletion,
                // then a different CSO joined the same MVO in Pass 2), skip the deletion.
                // Check specifically for CSOs joined during THIS sync page — identified by JoinType=Joined
                // which is set by AttemptJoinAsync. Other join types (Provisioned, Projected) predate this page.
                // We must also verify the CSO belongs to the current connected system being synced,
                // because CSOs from other systems (e.g., a Provisioned AD CSO) were not processed
                // in this sync run and should not prevent intentional deletion rules.
                if (mvo.ConnectedSystemObjects.Any(cso =>
                    cso.JoinType == ConnectedSystemObjectJoinType.Joined &&
                    cso.ConnectedSystemId == _connectedSystem.Id &&
                    cso.Status != ConnectedSystemObjectStatus.Obsolete))
                {
                    Log.Information(
                        "FlushPendingMvoDeletionsAsync: Skipping deletion of MVO {MvoId} — it was reconnected during this page " +
                        "({ConnectorCount} active connector(s) from this system). Clearing deletion markers.",
                        mvo.Id, mvo.ConnectedSystemObjects.Count(cso =>
                            cso.JoinType == ConnectedSystemObjectJoinType.Joined &&
                            cso.ConnectedSystemId == _connectedSystem.Id));

                    // Clear any deletion markers set during Pass 1
                    mvo.LastConnectorDisconnectedDate = null;
                    mvo.DeletionInitiatedByType = ActivityInitiatorType.NotSet;
                    mvo.DeletionInitiatedById = null;
                    mvo.DeletionInitiatedByName = null;
                    continue;
                }

                // Create delete pending exports for any remaining Provisioned CSOs
                // This handles WhenAuthoritativeSourceDisconnected where target CSOs still exist
                var deleteExports = await _syncServer.EvaluateMvoDeletionAsync(mvo);
                if (deleteExports.Count > 0)
                {
                    Log.Information(
                        "FlushPendingMvoDeletionsAsync: Created {Count} delete pending exports for MVO {MvoId}",
                        deleteExports.Count, mvo.Id);
                }

                // Delete the MVO, passing initiator info and the snapshotted final attribute values
                // (captured before attribute recall removed them from the MVO)
                await _syncServer.DeleteMetaverseObjectAsync(
                    mvo,
                    _activity.InitiatedByType,
                    _activity.InitiatedById,
                    _activity.InitiatedByName,
                    finalAttributeValues);
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
                await _syncRepo.UpdateMetaverseObjectAsync(mvo);
            }
        }

        _pendingMvoDeletions.Clear();
        _preRecallAttributeSnapshots.Clear();
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
        var changeTrackingEnabled = await _syncServer.GetMvoChangeTrackingEnabledAsync();
        if (!changeTrackingEnabled)
        {
            _pendingMvoChanges.Clear();
            return;
        }

        using var span = Diagnostics.Sync.StartSpan("CreatePendingMvoChangeObjects");
        span.SetTag("changeCount", _pendingMvoChanges.Count);

        foreach (var (mvo, additions, removals, changeType, rpei) in _pendingMvoChanges)
        {
            // Create MVO change object with the specific ChangeType (Projected, Joined, AttributeFlow, etc.)
            // Initiator info copied directly from Activity for self-contained audit trail
            var change = new MetaverseObjectChange
            {
                MetaverseObject = mvo,
                ChangeType = changeType,
                ChangeTime = DateTime.UtcNow,
                ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule,
                // Copy initiator info directly from Activity for self-contained audit trail
                InitiatedByType = _activity.InitiatedByType,
                InitiatedById = _activity.InitiatedById,
                InitiatedByName = _activity.InitiatedByName,
                // Link to RPEI for additional context (optional - RPEI may be cleaned up)
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
        var attributeChange = metaverseObjectChange.AttributeChanges.SingleOrDefault(ac => ac.Attribute!.Id == metaverseObjectAttributeValue.Attribute.Id);
        if (attributeChange == null)
        {
            // Create the attribute change object that provides an audit trail of changes to an MVO's attributes
            attributeChange = new MetaverseObjectChangeAttribute
            {
                Attribute = metaverseObjectAttributeValue.Attribute,
                AttributeName = metaverseObjectAttributeValue.Attribute.Name,
                AttributeType = metaverseObjectAttributeValue.Attribute.Type,
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
            case AttributeDataType.Reference when metaverseObjectAttributeValue.ReferenceValueId.HasValue:
                // Navigation property not loaded but FK is set — record the referenced MVO ID as a GUID.
                // This happens when ReferenceValue navigations are not loaded via EF Include
                // (replaced by direct SQL PopulateReferenceValuesAsync on the CSO side).
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(attributeChange, valueChangeType, metaverseObjectAttributeValue.ReferenceValueId.Value));
                break;
            case AttributeDataType.Reference when metaverseObjectAttributeValue.UnresolvedReferenceValue != null:
                // We do not log changes for unresolved references. Only resolved references get change tracked.
                break;
            case AttributeDataType.Reference:
                // Reference attribute with no resolved or unresolved value — nothing to track
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
        var isSimpleMode = _connectedSystem.ObjectMatchingRuleMode == ObjectMatchingRuleMode.ConnectedSystem;
        var attemptedMatching = false;

        // Enumerate import sync rules for this CSO type to attempt matching.
        foreach (var importSyncRule in activeSyncRules.Where(sr => sr.Direction == SyncRuleDirection.Import && sr.ConnectedSystemObjectTypeId == connectedSystemObject.TypeId))
        {
            attemptedMatching = true;

            // Resolve matching rules based on mode
            var matchingRules = isSimpleMode
                ? GetSimpleModeMatchingRules(connectedSystemObject.TypeId)
                : importSyncRule.ObjectMatchingRules.ToList();

            if (matchingRules.Count == 0)
                continue;

            // For advanced mode, set MetaverseObjectType on each rule from the sync rule
            if (!isSimpleMode)
            {
                foreach (var rule in matchingRules)
                    rule.MetaverseObjectType = importSyncRule.MetaverseObjectType;
            }

            var mvo = await FindMatchingMvoForJoinAsync(connectedSystemObject, matchingRules);
            if (mvo == null)
                continue;

            return await EstablishJoinAsync(connectedSystemObject, mvo);
        }

        // Simple mode fallback: if no import sync rules were evaluated, try matching directly from object type rules.
        // This enables simple mode joining without requiring empty import sync rules.
        if (!attemptedMatching && isSimpleMode)
        {
            var matchingRules = GetSimpleModeMatchingRules(connectedSystemObject.TypeId);
            if (matchingRules.Count > 0)
            {
                var mvo = await FindMatchingMvoForJoinAsync(connectedSystemObject, matchingRules);
                if (mvo != null)
                    return await EstablishJoinAsync(connectedSystemObject, mvo);
            }
        }

        // No join could be established.
        return false;
    }

    /// <summary>
    /// Gets simple mode matching rules from the object type loaded in _objectTypes.
    /// </summary>
    private List<ObjectMatchingRule> GetSimpleModeMatchingRules(int? csoTypeId)
    {
        if (csoTypeId == null || _objectTypes == null)
            return new List<ObjectMatchingRule>();

        var objectType = _objectTypes.FirstOrDefault(ot => ot.Id == csoTypeId);
        return objectType?.ObjectMatchingRules?.ToList() ?? new List<ObjectMatchingRule>();
    }

    /// <summary>
    /// Calls the matching engine and translates MultipleMatchesException into SyncJoinException.
    /// </summary>
    private async Task<MetaverseObject?> FindMatchingMvoForJoinAsync(ConnectedSystemObject connectedSystemObject, List<ObjectMatchingRule> matchingRules)
    {
        try
        {
            return await _syncServer.FindMatchingMetaverseObjectAsync(connectedSystemObject, matchingRules);
        }
        catch (JIM.Models.Exceptions.MultipleMatchesException ex)
        {
            throw new SyncJoinException(
                ActivityRunProfileExecutionItemErrorType.AmbiguousMatch,
                $"Multiple Metaverse Objects ({ex.Matches.Count}) match this Connected System Object. " +
                $"An MVO can only be joined to a single CSO per Connected System. " +
                $"Check your Object Matching Rules to ensure unique matches. Matching MVO IDs: {string.Join(", ", ex.Matches)}");
        }
    }

    /// <summary>
    /// Validates join constraints and establishes the join between a CSO and MVO.
    /// </summary>
    private async Task<bool> EstablishJoinAsync(ConnectedSystemObject connectedSystemObject, MetaverseObject mvo)
    {
        // MVO must not already be joined to a connected system object in this connected system. Joins are 1:1.
        var existingCsoJoinCount = await _syncRepo.GetConnectedSystemObjectCountByMvoAsync(
            _connectedSystem.Id, mvo.Id);

        // Account for CSOs that have been disconnected in-memory but not yet flushed to the database.
        if (existingCsoJoinCount > 0 && _pendingDisconnectedMvoIds.Contains(mvo.Id))
        {
            var pendingDisconnectCount = _pendingDisconnectedMvoIds.Count(id => id == mvo.Id);
            existingCsoJoinCount -= pendingDisconnectCount;
            Log.Debug("EstablishJoinAsync: Adjusted existing join count for MVO {MvoId} from {DbCount} to {AdjustedCount} " +
                "(accounting for {PendingCount} pending disconnection(s) not yet flushed to database)",
                mvo.Id, existingCsoJoinCount + pendingDisconnectCount, existingCsoJoinCount, pendingDisconnectCount);
        }

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
        _pendingCsoJoinUpdates.Add(connectedSystemObject);
        mvo.ConnectedSystemObjects.Add(connectedSystemObject);

        // If the MVO was marked for deletion (reconnection scenario), clear the disconnection date
        if (mvo.LastConnectorDisconnectedDate.HasValue)
        {
            Log.Information($"EstablishJoinAsync: Clearing LastConnectorDisconnectedDate for MVO {mvo.Id} as connector has reconnected.");
            mvo.LastConnectorDisconnectedDate = null;
        }

        Log.Information("EstablishJoinAsync: Established join between CSO {CsoId} and MVO {MvoId}", connectedSystemObject.Id, mvo.Id);
        return true;
    }

    /// <summary>
    /// Attempts to create a Metaverse Object from the Connected System Object using the first Sync Rule for the object type that has Projection enabled.
    /// </summary>
    /// <param name="activeSyncRules">The active sync rules that contain projection and attribute flow information.</param>
    /// <param name="connectedSystemObject">The Connected System Object to attempt to project to the Metaverse.</param>
    /// <returns>True if projection occurred, false otherwise.</returns>
    /// <exception cref="InvalidDataException">Will be thrown if not all required properties are populated on the Sync Rule.</exception>
    /// <exception cref="NotImplementedException">Will be thrown if a Sync Rule attempts to use a Function as a source.</exception>
    protected bool AttemptProjection(List<SyncRule> activeSyncRules, ConnectedSystemObject connectedSystemObject)
    {
        var decision = _syncEngine.EvaluateProjection(connectedSystemObject, activeSyncRules);
        if (!decision.ShouldProject)
            return false;

        // Apply the projection decision: create MVO and link to CSO.
        // Note: Do NOT assign Id here - let it remain Guid.Empty so that
        // ProcessMetaverseObjectChangesAsync knows to call CreateMetaverseObjectAsync.
        var mvo = new MetaverseObject();
        mvo.Type = decision.MetaverseObjectType!;
        connectedSystemObject.MetaverseObject = mvo;
        mvo.ConnectedSystemObjects.Add(connectedSystemObject);
        connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.Projected;
        connectedSystemObject.DateJoined = DateTime.UtcNow;

        return true;
    }

    /// <summary>
    /// Assigns values to a Metaverse Object, from a Connected System Object using a Sync Rule.
    /// Merges changed attributes and removals into an existing pending export evaluation entry for the given MVO,
    /// or adds a new entry if none exists. This prevents silently dropping reference attribute changes when
    /// scalar changes have already been queued for the same MVO.
    /// </summary>
    protected void MergeOrAddPendingExportEvaluation(
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> changedAttributes,
        HashSet<MetaverseObjectAttributeValue>? removedAttributes)
    {
        var existingIndex = _pendingExportEvaluations.FindIndex(e => e.Mvo == mvo);
        if (existingIndex >= 0)
        {
            // Merge: ChangedAttributes is a List (reference type) so AddRange mutates the existing list
            _pendingExportEvaluations[existingIndex].ChangedAttributes.AddRange(changedAttributes);

            if (removedAttributes != null)
            {
                var existing = _pendingExportEvaluations[existingIndex];
                if (existing.RemovedAttributes != null)
                {
                    foreach (var removed in removedAttributes)
                        existing.RemovedAttributes.Add(removed);
                }
                else
                {
                    // Replace tuple entry to add the removedAttributes set
                    _pendingExportEvaluations[existingIndex] = (existing.Mvo, existing.ChangedAttributes, removedAttributes);
                }
            }
        }
        else
        {
            _pendingExportEvaluations.Add((mvo, changedAttributes, removedAttributes));
        }
    }

    /// <summary>
    /// Snapshots the pending export's attribute changes onto the outcome node as a
    /// <see cref="ConnectedSystemObjectChange"/> record. This enables the Causality Tree to
    /// render attribute detail for PendingExportCreated outcomes even after the PendingExport
    /// is deleted during export confirmation.
    /// </summary>
    /// <remarks>
    /// For reference attributes, resolves MVO GUIDs stored in <see cref="PendingExportAttributeValueChange.UnresolvedReferenceValue"/>
    /// to their corresponding stub CSOs in the target connected system. This allows the Causality Tree
    /// to render meaningful identifiers (External ID / Secondary External ID / CSO ID) instead of
    /// raw MVO GUIDs with a misleading "unresolved reference" icon.
    /// </remarks>
    private async Task SnapshotPendingExportChangesAsync(
        ActivityRunProfileExecutionItemSyncOutcome outcome,
        PendingExport pendingExport)
    {
        if (!_csoChangeTrackingEnabled || pendingExport.AttributeValueChanges.Count == 0)
            return;

        // Collect MVO GUIDs from reference attribute changes so we can resolve them
        // to stub CSOs in the target connected system
        Dictionary<Guid, ConnectedSystemObject>? resolvedReferences = null;
        var mvoGuids = pendingExport.AttributeValueChanges
            .Where(avc => !string.IsNullOrEmpty(avc.UnresolvedReferenceValue)
                       && avc.Attribute?.Type == AttributeDataType.Reference)
            .Select(avc => Guid.TryParse(avc.UnresolvedReferenceValue, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .Distinct()
            .ToList();

        if (mvoGuids.Count > 0)
        {
            // First, check the in-memory batch of provisioning CSOs that haven't been persisted yet.
            // During sync, provisioning CSOs are collected for batch creation (FlushPendingExportOperationsAsync)
            // and won't be in the database when this snapshot runs.
            // IMPORTANT: We create detached copies to avoid EF change tracker issues — the original
            // CSOs are tracked entities that will be inserted by FlushPendingExportOperationsAsync.
            // If we reference them directly via the ReferenceValue navigation property, EF's
            // SaveChangesAsync would attempt to insert them again, causing duplicate key violations.
            resolvedReferences = new Dictionary<Guid, ConnectedSystemObject>();
            foreach (var cso in _provisioningCsosToCreate)
            {
                if (cso.MetaverseObjectId.HasValue
                    && cso.ConnectedSystemId == pendingExport.ConnectedSystemId
                    && mvoGuids.Contains(cso.MetaverseObjectId.Value))
                {
                    var detached = new ConnectedSystemObject
                    {
                        Id = cso.Id,
                        MetaverseObjectId = cso.MetaverseObjectId,
                        ConnectedSystemId = cso.ConnectedSystemId,
                        Status = cso.Status,
                        Type = cso.Type,
                        TypeId = cso.TypeId,
                        ExternalIdAttributeId = cso.ExternalIdAttributeId,
                        SecondaryExternalIdAttributeId = cso.SecondaryExternalIdAttributeId,
                        AttributeValues = cso.AttributeValues.ToList()
                    };
                    resolvedReferences.TryAdd(cso.MetaverseObjectId.Value, detached);
                }
            }

            // For any MVO GUIDs not found in the in-memory batch, query the database
            // (these may be CSOs created in a previous page/batch or a prior sync run)
            var unresolvedMvoGuids = mvoGuids
                .Where(g => !resolvedReferences.ContainsKey(g))
                .ToList();

            if (unresolvedMvoGuids.Count > 0)
            {
                var dbResolved = await _syncRepo.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(unresolvedMvoGuids, pendingExport.ConnectedSystemId);
                foreach (var kvp in dbResolved)
                {
                    resolvedReferences.TryAdd(kvp.Key, kvp.Value);
                }

                // Some references may remain unresolved if the referenced objects haven't been
                // processed yet on this page (e.g. a group is synced before its member users).
                // These will appear as raw MVO GUIDs in the Causality Tree — a cosmetic limitation
                // of snapshot-time resolution. The references themselves are correct and will be
                // resolved during export execution.
            }
        }

        var change = ExportChangeHistoryBuilder.BuildFromPendingExport(
            pendingExport,
            _activity.InitiatedByType,
            _activity.InitiatedById,
            _activity.InitiatedByName,
            resolvedReferences);

        outcome.ConnectedSystemObjectChange = change;
    }

    /// <summary>
    /// Post-page resolution pass for pending export reference snapshots.
    /// Called after <see cref="FlushPendingExportOperationsAsync"/> has persisted all provisioning CSOs
    /// but before <see cref="FlushRpeisAsync"/> persists RPEIs with their CSO change snapshots.
    /// </summary>
    /// <remarks>
    /// During per-object processing, <see cref="SnapshotPendingExportChangesAsync"/> may not be able to resolve
    /// all MVO GUID references because referenced objects haven't been processed yet on the page
    /// (e.g. a group is synced before its member users). At this point, all provisioning CSOs for the page
    /// have been created (either in-memory or persisted to the database), so we can resolve the remaining
    /// references by querying the database.
    /// </remarks>
    protected async Task ResolvePendingExportReferenceSnapshotsAsync()
    {
        if (!_csoChangeTrackingEnabled)
            return;

        // Collect all unresolved MVO GUIDs from pending export snapshots across all RPEIs on this page
        var unresolvedByCs = new Dictionary<int, HashSet<Guid>>();
        var unresolvedValueChanges = new List<(ConnectedSystemObjectChangeAttributeValue ValueChange, int ConnectedSystemId, Guid MvoGuid)>();

        foreach (var rpei in _activity.RunProfileExecutionItems)
        {
            foreach (var outcome in rpei.SyncOutcomes)
            {
                if (outcome.ConnectedSystemObjectChange == null)
                    continue;

                var change = outcome.ConnectedSystemObjectChange;
                foreach (var attrChange in change.AttributeChanges)
                {
                    if (attrChange.Attribute?.Type != AttributeDataType.Reference)
                        continue;

                    foreach (var valueChange in attrChange.ValueChanges)
                    {
                        // Only process string values that look like MVO GUIDs and aren't already resolved
                        if (valueChange.IsPendingExportStub || valueChange.ReferenceValue != null)
                            continue;

                        if (!string.IsNullOrEmpty(valueChange.StringValue)
                            && Guid.TryParse(valueChange.StringValue, out var mvoGuid))
                        {
                            if (!unresolvedByCs.TryGetValue(change.ConnectedSystemId, out var guidSet))
                            {
                                guidSet = new HashSet<Guid>();
                                unresolvedByCs[change.ConnectedSystemId] = guidSet;
                            }
                            guidSet.Add(mvoGuid);
                            unresolvedValueChanges.Add((valueChange, change.ConnectedSystemId, mvoGuid));
                        }
                    }
                }
            }
        }

        if (unresolvedValueChanges.Count == 0)
            return;

        // Batch-resolve all MVO GUIDs per connected system
        var resolvedLookup = new Dictionary<(int CsId, Guid MvoGuid), ConnectedSystemObject>();
        foreach (var (csId, mvoGuids) in unresolvedByCs)
        {
            var resolved = await _syncRepo.GetConnectedSystemObjectsByMetaverseObjectIdsAsync(mvoGuids, csId);
            foreach (var (mvoGuid, cso) in resolved)
            {
                resolvedLookup[(csId, mvoGuid)] = cso;
            }
        }

        if (resolvedLookup.Count == 0)
            return;

        // Update the unresolved value changes with resolved display identifiers
        var resolvedCount = 0;
        foreach (var (valueChange, csId, mvoGuid) in unresolvedValueChanges)
        {
            if (resolvedLookup.TryGetValue((csId, mvoGuid), out var cso))
            {
                valueChange.StringValue = ExportChangeHistoryBuilder.GetCsoDisplayIdentifier(cso);
                valueChange.IsPendingExportStub = true;
                resolvedCount++;
            }
        }

        Log.Debug("ResolvePendingExportReferenceSnapshotsAsync: Resolved {ResolvedCount} of {TotalCount} " +
            "previously unresolved pending export reference snapshots across {CsCount} connected system(s)",
            resolvedCount, unresolvedValueChanges.Count, unresolvedByCs.Count);
    }

    /// <summary>
    /// Does not perform any delta processing. This is for MVO create scenarios where there are not MVO attribute values already.
    /// </summary>
    /// <param name="connectedSystemObject">The source Connected System Object to map values from.</param>
    /// <param name="syncRule">The Sync Rule to use to determine which attributes, and how should be assigned to the Metaverse Object.</param>
    /// <param name="skipReferenceAttributes">If true, skip reference attributes (they will be processed in a second pass after all MVOs exist).</param>
    /// <param name="onlyReferenceAttributes">If true, process ONLY reference attributes (for deferred second pass). Takes precedence over skipReferenceAttributes.</param>
    /// <exception cref="InvalidDataException">Can be thrown if a Sync Rule Mapping Source is not properly formed.</exception>
    /// <exception cref="NotImplementedException">Will be thrown whilst Functions have not been implemented, but are being used in the Sync Rule.</exception>
    protected List<AttributeFlowWarning> ProcessInboundAttributeFlow(ConnectedSystemObject connectedSystemObject, SyncRule syncRule, bool skipReferenceAttributes = false, bool onlyReferenceAttributes = false, bool isFinalReferencePass = false)
    {
        if (_objectTypes == null)
            throw new MissingMemberException("_objectTypes is null!");

        return _syncEngine.FlowInboundAttributes(connectedSystemObject, syncRule, _objectTypes, _expressionEvaluator, skipReferenceAttributes, onlyReferenceAttributes, isFinalReferencePass);
    }

    /// <summary>
    /// Applies pending attribute value changes to a Metaverse Object.
    /// This moves values from PendingAttributeValueAdditions to AttributeValues
    /// and removes values listed in PendingAttributeValueRemovals.
    /// </summary>
    /// <param name="mvo">The Metaverse Object to apply pending changes to.</param>
    protected void ApplyPendingMetaverseObjectAttributeChanges(MetaverseObject mvo)
        => _syncEngine.ApplyPendingAttributeChanges(mvo);

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
                var fullCso = await _syncRepo.GetConnectedSystemObjectAsync(_connectedSystem.Id, connectedSystemObject.Id);
                if (fullCso != null)
                {
                    csoWithAttributes = fullCso;
                    // Copy attributes to original CSO so we have them for later
                    connectedSystemObject.AttributeValues = fullCso.AttributeValues;
                }
            }

            // Evaluate scoping criteria
            if (_syncServer.IsCsoInScopeForImportRule(csoWithAttributes, syncRule))
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
                var totalCsoCount = await _syncRepo.GetConnectedSystemObjectCountByMetaverseObjectIdAsync(mvoId);
                var remainingCsoCount = Math.Max(0, totalCsoCount - 1);

                // Evaluate the MVO deletion rule BEFORE attribute recall (#390 optimisation).
                // If the MVO will be deleted immediately, attribute recall is nugatory work —
                // the attributes, MVO update, and export evaluations would all be discarded
                // when the MVO is deleted moments later in FlushPendingMvoDeletionsAsync.
                var mvoDeletionFate = await ProcessMvoDeletionRuleAsync(mvo, _connectedSystem.Id, remainingCsoCount);

                // Check if we should remove contributed attributes based on the object type setting.
                // Skip recall when a grace period is configured (see ProcessObsoleteConnectedSystemObjectAsync).
                // Also skip recall when the MVO will be deleted immediately — the recall work (MVO update,
                // export evaluation queueing) would be discarded when the MVO is deleted (#390).
                int attributeRemovalCount = 0;
                List<MetaverseObjectAttributeValue>? recalledAttributeValues = null;
                var hasGracePeriod = mvo.Type?.DeletionGracePeriod is { } gracePeriod && gracePeriod > TimeSpan.Zero;
                var skipRecallForImmediateDeletion = mvoDeletionFate == MvoDeletionFate.DeletedImmediately;
                if (connectedSystemObject.Type.RemoveContributedAttributesOnObsoletion && !hasGracePeriod && !skipRecallForImmediateDeletion)
                {
                    var contributedAttributes = mvo.AttributeValues
                        .Where(av => av.ContributedBySystemId == _connectedSystem.Id)
                        .ToList();

                    foreach (var attributeValue in contributedAttributes)
                    {
                        mvo.PendingAttributeValueRemovals.Add(attributeValue);
                        Log.Verbose("HandleCsoOutOfScopeAsync: Marking attribute '{AttrName}' for removal from MVO {MvoId}",
                            attributeValue.Attribute?.Name, mvo.Id);
                    }

                    attributeRemovalCount = contributedAttributes.Count;

                    // Capture recalled attribute values BEFORE applying (which clears the pending lists).
                    // These are passed back in the result so the caller can add them to _pendingMvoChanges
                    // for MVO change tracking, enabling the RPEI detail page to show recalled attribute values.
                    if (mvo.PendingAttributeValueRemovals.Count > 0)
                    {
                        recalledAttributeValues = mvo.PendingAttributeValueRemovals.ToList();
                    }
                }
                else if (skipRecallForImmediateDeletion)
                {
                    Log.Debug("HandleCsoOutOfScopeAsync: Skipping attribute recall for CSO {CsoId} " +
                        "because MVO {MvoId} will be deleted immediately (#390 optimisation).",
                        connectedSystemObject.Id, mvo.Id);
                }

                // Break the CSO-MVO join
                mvo.ConnectedSystemObjects.Remove(connectedSystemObject);
                connectedSystemObject.MetaverseObject = null;
                connectedSystemObject.MetaverseObjectId = null;
                connectedSystemObject.JoinType = ConnectedSystemObjectJoinType.NotJoined;
                connectedSystemObject.DateJoined = null;
                Log.Verbose("HandleCsoOutOfScopeAsync: Broke join between CSO {CsoId} and MVO {MvoId}", connectedSystemObject.Id, mvoId);

                // Apply pending attribute changes and update MVO (skip when MVO is about to be
                // deleted immediately — the update would be a wasted database round trip).
                if (!skipRecallForImmediateDeletion)
                {
                    ApplyPendingMetaverseObjectAttributeChanges(mvo);
                    await _syncRepo.UpdateMetaverseObjectAsync(mvo);
                }

                return MetaverseObjectChangeResult.DisconnectedOutOfScope(
                    attributeFlowCount: attributeRemovalCount > 0 ? attributeRemovalCount : null,
                    mvoDeletionFate: mvoDeletionFate,
                    recalledAttributeValues: recalledAttributeValues,
                    disconnectedMvo: recalledAttributeValues != null ? mvo : null);
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
    protected void EvaluateDriftAndEnforceState(ConnectedSystemObject cso, MetaverseObject? mvo)
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

        var result = _syncServer.EvaluateDrift(
            cso,
            targetMvo,
            _driftDetectionExportRules,
            _importMappingCache);

        if (result.HasDrift)
        {
            Log.Information("EvaluateDriftAndEnforceState: Detected {DriftCount} drifted attributes on CSO {CsoId}, " +
                "staged {ExportCount} corrective pending exports for batch save",
                result.DriftedAttributes.Count, cso.Id, result.CorrectiveExports.Count);

            span.SetTag("driftedAttributeCount", result.DriftedAttributes.Count);
            span.SetTag("correctiveExportCount", result.CorrectiveExports.Count);

            // Add corrective pending exports to the batch list for persistence during FlushPendingExportOperationsAsync.
            // These will be merged with any export evaluation PEs for the same CSO during export evaluation.
            _pendingExportsToCreate.AddRange(result.CorrectiveExports);

            // Create RPEI for drift correction to provide visibility in Activity UI
            // This shows that the delta sync detected unauthorised changes and staged corrective exports
            var runProfileExecutionItem = _activity.PrepareRunProfileExecutionItem();
            runProfileExecutionItem.ConnectedSystemObject = cso;
            runProfileExecutionItem.ConnectedSystemObjectId = cso.Id;
            runProfileExecutionItem.ObjectChangeType = ObjectChangeType.DriftCorrection;
            _activity.RunProfileExecutionItems.Add(runProfileExecutionItem);

            if (_syncOutcomeTrackingLevel != ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None)
                SyncOutcomeBuilder.AddRootOutcome(runProfileExecutionItem,
                    ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection,
                    detailCount: result.DriftedAttributes.Count);
        }

        span.SetSuccess();
    }
}

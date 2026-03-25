using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Application.Interfaces;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using Serilog;

namespace JIM.Worker.Processors;

/// <summary>
/// Processes full synchronisation for a Connected System.
/// Full sync processes ALL CSOs in the Connected System, regardless of when they were last modified.
/// Use this for initial sync or when you need to ensure complete synchronisation.
/// </summary>
public class SyncFullSyncTaskProcessor : SyncTaskProcessorBase
{
    public SyncFullSyncTaskProcessor(
        ISyncEngine syncEngine,
        ISyncServer syncServer,
        ISyncRepository syncRepository,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile connectedSystemRunProfile,
        Activity activity,
        CancellationTokenSource cancellationTokenSource)
        : base(syncEngine, syncServer, syncRepository, connectedSystem, connectedSystemRunProfile, activity, cancellationTokenSource)
    {
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

        await _syncRepo.UpdateActivityMessageAsync(_activity, "Preparing");

        // how many objects are we processing? that = CSO count + Pending Export Object count.
        // update the activity with this info so a progress bar can be shown.
        var totalCsosToProcess = await _syncRepo.GetConnectedSystemObjectCountAsync(_connectedSystem.Id);
        var totalPendingExportObjectsToProcess = await _syncRepo.GetPendingExportsCountAsync(_connectedSystem.Id);
        var totalObjectsToProcess = totalCsosToProcess + totalPendingExportObjectsToProcess;
        _activity.ObjectsToProcess = totalObjectsToProcess;
        _activity.ObjectsProcessed = 0;
        await _syncRepo.UpdateActivityAsync(_activity);

        // get all the active sync rules for this system
        List<SyncRule> activeSyncRules;
        using (Diagnostics.Sync.StartSpan("LoadSyncRules"))
        {
            activeSyncRules = await _syncRepo.GetSyncRulesAsync(_connectedSystem.Id, false);
        }

        // Load ALL sync rules from ALL systems for drift detection import mapping cache.
        // This is needed because drift detection must know which systems contribute to which MVO attributes
        // to avoid false positives on export-only systems.
        List<SyncRule> allSyncRules;
        using (Diagnostics.Sync.StartSpan("LoadAllSyncRulesForDriftDetection"))
        {
            allSyncRules = await _syncRepo.GetAllSyncRulesAsync();
        }

        // Build drift detection cache (import mapping cache + export rules with EnforceState=true)
        // This enables efficient drift detection during CSO processing
        BuildDriftDetectionCache(allSyncRules, activeSyncRules);

        // get the schema for all object types upfront in this Connected System, so we can retrieve lightweight CSOs without this data.
        using (Diagnostics.Sync.StartSpan("LoadObjectTypes"))
        {
            _objectTypes = await _syncRepo.GetObjectTypesAsync(_connectedSystem.Id);
        }

        // load all pending exports once upfront and index by CSO ID for O(1) lookup
        // this avoids O(n²) behaviour from loading all pending exports for every CSO
        using (Diagnostics.Sync.StartSpan("LoadPendingExports"))
        {
            var allPendingExports = await _syncRepo.GetPendingExportsAsync(_connectedSystem.Id);
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
            _exportEvaluationCache = await _syncServer.BuildExportEvaluationCacheAsync(_connectedSystem.Id);
        }

        // Load settings once at start of sync
        _syncOutcomeTrackingLevel = await _syncServer.GetSyncOutcomeTrackingLevelAsync();
        _csoChangeTrackingEnabled = await _syncServer.GetCsoChangeTrackingEnabledAsync();

        // Process CSOs in batches. This enables us to respond to cancellation requests in a reasonable timeframe.
        // Page size is configurable via service settings for performance tuning.
        var pageSize = await _syncServer.GetSyncPageSizeAsync();
        var totalCsoPages = Convert.ToInt16(Math.Ceiling((double)totalCsosToProcess / pageSize));
        await _syncRepo.UpdateActivityMessageAsync(_activity, "Processing Connected System Objects");

        using var processCsosSpan = Diagnostics.Sync.StartSpan("ProcessConnectedSystemObjects");
        processCsosSpan.SetTag("totalObjects", totalCsosToProcess);
        processCsosSpan.SetTag("pageSize", pageSize);
        processCsosSpan.SetTag("totalPages", totalCsoPages);

        // Set the message once for the entire phase (no page details for users)
        await _syncRepo.UpdateActivityMessageAsync(_activity, "Processing Connected System Objects");

        for (var i = 1; i <= totalCsoPages; i++)
        {

            PagedResultSet<ConnectedSystemObject> csoPagedResult;
            using (Diagnostics.Sync.StartSpan("LoadCsoPage"))
            {
                csoPagedResult = await _syncRepo.GetConnectedSystemObjectsAsync(_connectedSystem.Id, i, pageSize);
            }

            // Note: Target CSO attribute values for no-net-change detection are pre-loaded in ExportEvaluationCache
            // (built at sync start) rather than per-page, since we need target system CSO attributes not source CSO attributes.

            int processedInPage = 0;
            using (Diagnostics.Sync.StartSpan("ProcessCsoLoop").SetTag("csoCount", csoPagedResult.Results.Count))
            {
                // Two-pass processing ensures all CSO disconnections are recorded before any join attempts.
                // Without this, GUID-based page ordering could cause a new CSO to be processed before an
                // obsolete CSO, seeing a stale join count and incorrectly throwing CouldNotJoinDueToExistingJoin.

                // Pass 1: Process pending export confirmations and obsolete CSO teardown.
                // This populates _pendingDisconnectedMvoIds so Pass 2 join checks account for disconnections.
                foreach (var connectedSystemObject in csoPagedResult.Results)
                {
                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        Log.Information("PerformFullSyncAsync: Cancellation requested. Stopping CSO enumeration.");
                        return;
                    }

                    await ProcessObsoleteAndExportConfirmationAsync(activeSyncRules, connectedSystemObject);
                }

                // Pass 2: Process joins, projections, and attribute flow for non-obsolete CSOs.
                // All disconnections from Pass 1 are now visible via _pendingDisconnectedMvoIds.
                foreach (var connectedSystemObject in csoPagedResult.Results)
                {
                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        Log.Information("PerformFullSyncAsync: Cancellation requested. Stopping CSO enumeration.");
                        return;
                    }

                    await ProcessActiveConnectedSystemObjectAsync(activeSyncRules, connectedSystemObject);

                    _activity.ObjectsProcessed++;
                    processedInPage++;
                }
            }

            // Process deferred reference attributes after all CSOs in this page have been processed.
            // Reference attributes (e.g., group members) may point to CSOs that are processed later in the same page.
            // By deferring reference attributes, we ensure all MVOs exist before resolving references.
            // This enables a single sync run to fully reconcile all objects including references.
            ProcessDeferredReferenceAttributes();

            // Disable AutoDetectChangesEnabled for the page flush sequence.
            // Without this, every SaveChangesAsync call below triggers DetectChanges() which
            // scans ALL tracked entities' navigation properties. The tracked Activity entity's
            // RunProfileExecutionItems collection contains RPEIs accumulated during CSO processing.
            // DetectChanges() discovers these as new items, marks them as Added, and inserts them
            // during SaveChangesAsync. Later, FlushRpeisAsync attempts raw SQL bulk insert of the
            // same RPEIs, causing duplicate key violations.
            //
            // With AutoDetectChanges disabled, SaveChangesAsync only persists entities we explicitly
            // mark (AddRange for creates, Entry().State = Modified for updates) without scanning
            // navigation property collections.
            _syncRepo.SetAutoDetectChangesEnabled(false);
            try
            {
                // Batch persist all MVOs collected during this page.
                //
                // IMPORTANT DESIGN NOTE (Jan 2026):
                // We previously attempted to decouple progress updates from batch size by calling
                // UpdateActivityAsync mid-loop (every N objects). This DOES NOT WORK because:
                //
                // 1. When we set cso.MetaverseObject = mvo, EF's "relationship fixup" automatically
                //    adds the CSO to mvo.ConnectedSystemObjects (bidirectional nav property sync).
                //
                // 2. During SaveChangesAsync (triggered by activity updates), EF calls DetectChanges()
                //    which traverses all navigation properties to discover changes.
                //
                // 3. If CSOs were loaded with AsNoTracking() to improve query performance, EF will
                //    try to attach them when it discovers them via the MVO navigation, causing
                //    "another instance with same key is already tracked" errors.
                //
                // 4. Attempts to work around this (clearing collections, removing CSOs from collections,
                //    deferring FK updates) all failed because EF's relationship fixup is automatic
                //    and cannot be prevented without breaking the navigation property assignments.
                //
                // The solution is to use AsSplitQuery() instead of AsNoTracking() so all entities
                // are properly tracked, and only persist at page boundaries to batch database writes.
                // Progress updates at finer granularity would require a separate DbContext instance.
                await PersistPendingMetaverseObjectsAsync();

                // create MVO change objects for change tracking (after MVOs persisted so IDs available)
                await CreatePendingMvoChangeObjectsAsync();

                // batch evaluate exports for all MVOs that changed during this page
                await EvaluatePendingExportsAsync();

                // batch process pending export confirmations (deletes and updates)
                await FlushPendingExportOperationsAsync();

                // Resolve any pending export reference snapshots that couldn't be resolved during
                // per-object processing (e.g. groups processed before their member users on this page).
                // Must run after FlushPendingExportOperationsAsync (CSOs now in DB) and before
                // FlushRpeisAsync (RPEIs with snapshots persisted).
                await ResolvePendingExportReferenceSnapshotsAsync();

                // batch delete obsolete CSOs
                await FlushObsoleteCsoOperationsAsync();

                // batch delete MVOs marked for immediate deletion (0-grace-period)
                var hadMvoDeletions = _pendingMvoDeletions.Count > 0;
                await FlushPendingMvoDeletionsAsync();

                // Flush this page's RPEIs via bulk insert before updating progress
                await FlushRpeisAsync();

                // Clear the change tracker after MVO deletions to prevent stale entity conflicts.
                // MVO deletion involves raw SQL (nulling FK references in Activities and
                // MetaverseObjectChanges tables) followed by EF Remove + SaveChanges. The raw SQL
                // modifies rows that may still be tracked in memory, and cascade deletes at the
                // database level remove rows that are still in the tracker as Modified.
                // Without clearing, the next SaveChangesAsync tries to UPDATE these ghost rows
                // and fails with DbUpdateConcurrencyException.
                // UpdateDetachedSafe will re-attach the Activity in Modified state.
                if (hadMvoDeletions && _hasRawSqlSupport)
                    _syncRepo.ClearChangeTracker();

                // Update progress with page completion - this persists ObjectsProcessed to database (including MVO changes)
                using (Diagnostics.Sync.StartSpan("UpdateActivityProgress"))
                {
                    await _syncRepo.UpdateActivityAsync(_activity);
                }
            }
            finally
            {
                _syncRepo.SetAutoDetectChangesEnabled(true);
            }
        }

        // Resolve cross-page reference attributes.
        // During page processing, some CSO reference attributes could not be resolved because
        // the referenced CSO was on a different page. Now that all pages have been processed
        // and all MVOs exist in the database, reload those CSOs and resolve their references.
        await ResolveCrossPageReferencesAsync(activeSyncRules);

        // Flush any RPEIs from cross-page resolution
        await FlushRpeisAsync();

        // Ensure the activity and any pending db updates are applied after all pages are processed
        await _syncRepo.UpdateActivityAsync(_activity);

        // Update the delta sync watermark to establish baseline for future delta syncs
        await UpdateDeltaSyncWatermarkAsync();

        // Summary stats were accumulated incrementally during each FlushRpeisAsync call (production).
        // In tests (EF fallback), RPEIs remain in Activity.RunProfileExecutionItems — compute stats
        // from them now so tests that check stats before CompleteActivityBasedOnExecutionResultsAsync work.
        if (!_hasRawSqlSupport && _activity.RunProfileExecutionItems.Count > 0)
            Worker.CalculateActivitySummaryStats(_activity);

        syncSpan.SetSuccess();
    }
}

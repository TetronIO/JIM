// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Application.Interfaces;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
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
        syncSpan.SetTag("connectorType", _connectedSystem.ConnectorDefinition.Name);

        Log.Verbose("PerformFullSyncAsync: Starting");

        // what needs to happen:
        // - confirm Pending Exports
        // - establish new joins to existing Metaverse Objects
        // - project CSO to the MV if there are no join matches and if a Synchronisation Rule for this CS has Projection enabled.
        // - work out if we CAN update any Metaverse Objects (where there's Attribute Flow) and whether we SHOULD (where there's Attribute Flow priority).
        // - update the Metaverse Objects accordingly.
        // - work out if this requires other Connected System to be updated by way of creating new Pending Export Objects.

        await _syncRepo.UpdateActivityMessageAsync(_activity, "Preparing");

        // how many CSOs are we processing? update the activity so a progress bar can be shown.
        // Pending Exports are processed as a side-effect of CSO evaluation, not as separate objects.
        var totalCsosToProcess = await _syncRepo.GetConnectedSystemObjectCountAsync(_connectedSystem.Id);
        var totalObjectsToProcess = totalCsosToProcess;
        _activity.ObjectsToProcess = totalObjectsToProcess;
        _activity.ObjectsProcessed = 0;
        await _syncRepo.UpdateActivityAsync(_activity);

        // get all the active Synchronisation Rules for this system
        List<SyncRule> activeSyncRules;
        using (Diagnostics.Sync.StartSpan("LoadSyncRules"))
        {
            activeSyncRules = await _syncRepo.GetSyncRulesAsync(_connectedSystem.Id, false, withChangeTracking: true);
        }

        // Load ALL Synchronisation Rules from ALL systems for drift detection import mapping cache.
        // This is needed because drift detection must know which systems contribute to which MVO attributes
        // to avoid false positives on export-only systems.
        List<SyncRule> allSyncRules;
        using (Diagnostics.Sync.StartSpan("LoadAllSyncRulesForDriftDetection"))
        {
            allSyncRules = await _syncRepo.GetAllSyncRulesAsync(withChangeTracking: true);
        }

        // Build drift detection cache (import mapping cache + export rules with EnforceState=true)
        // This enables efficient drift detection during CSO processing
        BuildDriftDetectionCache(allSyncRules, activeSyncRules);

        // Build reference object type cache for selective attribute loading optimisation.
        // Object types with reference attribute rules need full attribute loading even when unchanged.
        BuildReferenceObjectTypeCache(activeSyncRules);

        // Use object types already loaded on the Connected System (with matching rules and attributes)
        // to avoid creating duplicate entity instances that conflict with EF Core's change tracker.
        _objectTypes = _connectedSystem.ObjectTypes!;

        // load all Pending Exports once upfront and index by CSO ID for O(1) lookup
        // this avoids O(n²) behaviour from loading all Pending Exports for every CSO
        using (Diagnostics.Sync.StartSpan("LoadPendingExports"))
        {
            var allPendingExports = await _syncRepo.GetPendingExportsAsync(_connectedSystem.Id);
            _pendingExportsByCsoId = allPendingExports
                .Where(pe => pe.ConnectedSystemObject?.Id != null)
                .GroupBy(pe => pe.ConnectedSystemObject!.Id)
                .ToDictionary(g => g.Key, g => g.ToList());
            Log.Verbose("PerformFullSyncAsync: Loaded {Count} Pending Exports into lookup dictionary", allPendingExports.Count);
        }

        // Pre-load export evaluation cache (export rules + CSO lookups) for O(1) access
        // This eliminates O(N×M) database queries during export evaluation
        using (Diagnostics.Sync.StartSpan("LoadExportEvaluationCache"))
        {
            _exportEvaluationCache = await _syncServer.BuildExportEvaluationCacheAsync(_connectedSystem.Id);

            // Separate run-scoped cache for reference recall staging (#1003): recall must not
            // exclude the source system (Q3 does not apply to deletions), so the stable tier is
            // built with sourceConnectedSystemId 0, reusing the already-loaded rules (no new query).
            _recallExportEvaluationCache = await _syncServer.BuildExportEvaluationCacheAsync(
                sourceConnectedSystemId: 0, preloadedSyncRules: allSyncRules);
        }

        // Load settings once at start of sync
        _syncOutcomeTrackingLevel = await _syncServer.GetSyncOutcomeTrackingLevelAsync();
        _csoChangeTrackingEnabled = await _syncServer.GetCsoChangeTrackingEnabledAsync();

        // A Full Synchronisation is the documented way to apply configuration changes (attribute priority
        // reordering, "Null is a value", rule enable/disable, scoping) to every object. The unchanged-object
        // optimisation skips Attribute Flow for objects whose SOURCE DATA has not changed since the last
        // completed sync, which is only safe while the CONFIGURATION has not changed either; otherwise a pure
        // configuration change would never re-resolve anything until the object's data happened to change.
        // The comparison baseline is ConfigurationLastFullyAppliedAt (the start of the last completed Full
        // Synchronisation), NOT LastSyncCompletedAt: a no-change Delta Synchronisation advances the latter
        // without applying configuration, and must not hide a configuration change from the next full run.
        // When any Synchronisation Rule or mapping changed after that baseline (across all Connected Systems,
        // because another system's priority affects this system's resolution), disable the optimisation for
        // this run by loading without a watermark, so every object is fully evaluated against the new
        // configuration.
        var fullSyncStartedAt = DateTime.UtcNow;
        var csoLoadWatermark = _connectedSystem.LastSyncCompletedAt;
        if (csoLoadWatermark.HasValue)
        {
            var configurationBaseline = _connectedSystem.ConfigurationLastFullyAppliedAt;
            var configChangedAt = await _syncRepo.GetLatestSyncRuleConfigurationChangeAsync();
            if (configurationBaseline == null ||
                (configChangedAt.HasValue && configChangedAt.Value > configurationBaseline.Value))
            {
                Log.Information("PerformFullSyncAsync: Synchronisation Rule configuration changed at {ConfigChangedAt:O}, " +
                    "after it was last fully applied at {Baseline:O}. Disabling the unchanged-object optimisation for " +
                    "this run so the new configuration is applied to every object.",
                    configChangedAt, configurationBaseline);
                csoLoadWatermark = null;
            }
        }

        // Process CSOs in batches. This enables us to respond to cancellation requests in a reasonable timeframe.
        // Page size is configurable via service settings for performance tuning.
        var pageSize = await _syncServer.GetSyncPageSizeAsync();
        var totalCsoPages = Convert.ToInt16(Math.Ceiling((double)totalCsosToProcess / pageSize));
        await _syncRepo.UpdateActivityMessageAsync(_activity, "Processing Connected System Objects");

        using var processCsosSpan = Diagnostics.Sync.StartSpan("ProcessConnectedSystemObjects");
        processCsosSpan.SetTag("totalObjects", totalCsosToProcess);
        processCsosSpan.SetTag("pageSize", pageSize);
        processCsosSpan.SetTag("totalPages", totalCsoPages);

        var throughput = new ThroughputTracker();
        var csoPhaseStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _syncRepo.UpdateActivityMessageAsync(_activity, "Processing Connected System Objects");

        // Keyset cursor for CSO page loads. Starting at Guid.Empty (below every generated uuid in
        // both PostgreSQL's bytewise and .NET's component-wise ordering) keeps every page on the
        // keyset path, so per-page cost stays O(pageSize) instead of degrading with OFFSET depth
        // (measured at Scale500k25kGroups: 977s across 1,050 page loads). The cursor must advance
        // to the last row of each page exactly as the repository returned it; a client-side max
        // would diverge from PostgreSQL's uuid ordering and skip rows.
        var csoPageCursor = Guid.Empty;

        for (var i = 1; i <= totalCsoPages; i++)
        {

            PagedResultSet<ConnectedSystemObject> csoPagedResult;
            using (Diagnostics.Sync.StartSpan("LoadCsoPage"))
            {
                csoPagedResult = await _syncRepo.GetConnectedSystemObjectsAsync(
                    _connectedSystem.Id, i, pageSize, totalCsosToProcess,
                    csoLoadWatermark, csoPageCursor);
            }

            if (csoPagedResult.Results.Count > 0)
                csoPageCursor = csoPagedResult.Results[^1].Id;

            // Note: Target CSO attribute values for no-net-change detection are pre-loaded in ExportEvaluationCache
            // (built at sync start) rather than per-page, since we need target system CSO attributes not source CSO attributes.

            int processedInPage = 0;
            using (Diagnostics.Sync.StartSpan("ProcessCsoLoop")
                .SetTag("connectedSystemId", _connectedSystem.Id)
                .SetTag("csoCount", csoPagedResult.Results.Count)
                .SetTag("cumulativeObjectCount", _activity.ObjectsProcessed + csoPagedResult.Results.Count)
                .SetTag("wallClockOffsetMs", csoPhaseStopwatch.Elapsed.TotalMilliseconds))
            {
                // Two-pass processing ensures all CSO disconnections are recorded before any join attempts.
                // Without this, GUID-based page ordering could cause a new CSO to be processed before an
                // obsolete CSO, seeing a stale join count and incorrectly throwing CouldNotJoinDueToExistingJoin.

                // Pass 1: Process Pending Export confirmations and obsolete CSO teardown.
                // This populates _pendingDisconnectedMvoIds so Pass 2 join checks account for disconnections.
                foreach (var connectedSystemObject in csoPagedResult.Results)
                {
                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        Log.Information("PerformFullSyncAsync: Cancellation requested during Pass 1. Will flush already-processed objects before exiting.");
                        break;
                    }

                    await ProcessObsoleteAndExportConfirmationAsync(activeSyncRules, connectedSystemObject);
                }

                // If cancelled during Pass 1, skip Pass 2 entirely — no objects have been
                // joined/projected yet, so Pass 2 batch collections are empty.
                if (!_cancellationTokenSource.IsCancellationRequested)
                {
                    // Pass 2: Process joins, projections, and Attribute Flow for non-obsolete CSOs.
                    // All disconnections from Pass 1 are now visible via _pendingDisconnectedMvoIds.
                    foreach (var connectedSystemObject in csoPagedResult.Results)
                    {
                        if (_cancellationTokenSource.IsCancellationRequested)
                        {
                            Log.Information("PerformFullSyncAsync: Cancellation requested during Pass 2. Will flush already-processed objects before exiting.");
                            break;
                        }

                        await ProcessActiveConnectedSystemObjectAsync(activeSyncRules, connectedSystemObject);

                        _activity.ObjectsProcessed++;
                        processedInPage++;
                        OnCsoProcessedInPass2?.Invoke();
                    }
                }
            }

            Log.Information("MetricsCheckpoint: FullSync processed={ObjectsProcessed} elapsed={ElapsedMs}ms total={TotalObjects} cs={ConnectedSystemName}",
                _activity.ObjectsProcessed, (long)csoPhaseStopwatch.Elapsed.TotalMilliseconds, totalCsosToProcess, _connectedSystem.Name);

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

                // evaluate queued drift detection (after MVOs persisted so corrective Pending Exports
                // capture real Metaverse Object ids, before the export flush persists them)
                EvaluateQueuedDrift();

                // batch evaluate exports for all MVOs that changed during this page
                await EvaluatePendingExportsAsync();

                // batch process Pending Export confirmations (deletes and updates)
                await FlushPendingExportOperationsAsync();

                // Resolve any Pending Export reference snapshots that couldn't be resolved during
                // per-object processing (e.g. groups processed before their member users on this page).
                // Must run after FlushPendingExportOperationsAsync (CSOs now in DB) and before
                // FlushRpeisAsync (RPEIs with snapshots persisted).
                await ResolvePendingExportReferenceSnapshotsAsync();

                // batch delete obsolete CSOs
                await FlushObsoleteCsoOperationsAsync();

                // batch delete MVOs marked for immediate deletion (0-grace-period)
                await FlushPendingMvoDeletionsAsync();

                // Flush this page's RPEIs via bulk insert before updating progress
                await FlushRpeisAsync();

                // Persist MVO change records via raw SQL before clearing the change tracker
                await FlushPendingMvoChangesAsync();

                // Clear the change tracker unconditionally at every page boundary to prevent
                // memory accumulation from tracked entities across pages. Without this, the tracker
                // grows linearly with total object count (500K+ entries at 100K objects), causing OOM.
                // All page data has been flushed to the database above. The Activity entity becomes
                // detached but UpdateDetachedSafe re-attaches it on the next UpdateActivityAsync call.
                // Cross-page state (_unresolvedCrossPageReferences, _exportEvaluationCache, etc.) is
                // held in CLR fields — detaching does not null their populated navigation properties.
                _syncRepo.ClearChangeTracker();

                // Update progress with page completion
                using (Diagnostics.Sync.StartSpan("UpdateActivityProgress"))
                {
                    var message = $"Syncing — {_activity.ObjectsProcessed:N0} of {totalObjectsToProcess:N0}" +
                        throughput.FormatThroughput(_activity.ObjectsProcessed, totalObjectsToProcess);
                    await _syncRepo.UpdateActivityMessageAsync(_activity, message);
                }

                LogPageMemoryDiagnostics(i, totalCsoPages);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not SyncPersistenceException)
            {
                // Attribute the persistence failure (page, Connected System, affected object ids) before it
                // propagates to the worker's activity-failure handler, which otherwise records only a generic
                // "unhandled exception". This is a hard failure: we rethrow so the run stops rather than
                // continuing with a page that did not fully persist (Synchronisation Integrity).
                throw CreatePagePersistenceException(i, totalCsoPages, ex);
            }
            finally
            {
                _syncRepo.SetAutoDetectChangesEnabled(true);
            }

            // After flushing the current page, exit the page loop if cancellation was requested.
            // The flush has completed for all objects processed so far — no data is orphaned.
            if (_cancellationTokenSource.IsCancellationRequested)
            {
                Log.Information("PerformFullSyncAsync: Cancellation completed. Page {Page} flushed, exiting page loop.", i);
                break;
            }
        }

        // Skip post-processing on cancellation: cross-page references target unprocessed pages,
        // and the watermark must NOT advance so the next sync re-processes everything.
        if (!_cancellationTokenSource.IsCancellationRequested)
        {
            // Resolve cross-page reference attributes.
            // During page processing, some CSO reference attributes could not be resolved because
            // the referenced CSO was on a different page. Now that all pages have been processed
            // and all MVOs exist in the database, reload those CSOs and resolve their references.
            await ResolveCrossPageReferencesAsync(activeSyncRules);

            // Flush any RPEIs from cross-page resolution
            await FlushRpeisAsync();

            // Emit the deduplicated reference-recall Pending Export RPEIs accumulated across all page
            // deletions (one per referencing group CSO, not one per page-flush that touched it).
            await FlushDeferredRecallRpeisAsync();

            // Outbound Temporal Scope Reconciler apply step (#892): re-evaluate export scope for Metaverse
            // Objects the reconciler flagged, whose export-rule scope drifted with the clock without a data
            // change (the change-driven export path never revisits them). Runs after all inbound-driven changes
            // are persisted so provisioning/deprovisioning reflects the fully reconciled Metaverse state.
            await ProcessScopeReviewPendingMetaverseObjectsAsync();

            // Ensure the activity and any pending db updates are applied after all pages are processed
            await _syncRepo.UpdateActivityAsync(_activity);

            // Record that this run applied the configuration as of its start to every object; a configuration
            // change made mid-run carries a later timestamp and is picked up by the next Full Synchronisation.
            _connectedSystem.ConfigurationLastFullyAppliedAt = fullSyncStartedAt;

            // Update the delta sync watermark to establish baseline for future delta syncs
            await UpdateDeltaSyncWatermarkAsync();

            // Update activity message with throughput summary
            var syncCompleteMessage = $"Sync complete: {_activity.ObjectsProcessed:N0} objects" +
                throughput.FormatCompletion(_activity.ObjectsProcessed);
            await _syncRepo.UpdateActivityMessageAsync(_activity, syncCompleteMessage);

            // Summary stats were accumulated incrementally during each FlushRpeisAsync call (production).
            // In tests (EF fallback), RPEIs remain in Activity.RunProfileExecutionItems — compute stats
            // from them now so tests that check stats before CompleteActivityBasedOnExecutionResultsAsync work.
            if (!_hasRawSqlSupport && _activity.RunProfileExecutionItems.Count > 0)
                Worker.CalculateActivitySummaryStats(_activity);
        }

        syncSpan.SetSuccess();
    }
}

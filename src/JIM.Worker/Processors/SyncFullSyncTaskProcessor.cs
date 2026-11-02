// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Application.UniqueValues;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;
using JIM.Worker.UniqueValues;
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
        CancellationTokenSource cancellationTokenSource,
        ActivityPhaseReporter? phaseReporter = null,
        UniqueValueReservationSet? uniqueValueReservations = null,
        IUniquenessProbeSessionHost? uniquenessProbeSessionHost = null,
        IReadOnlyCollection<long>? configurationChangeIdsEligibleToClaim = null)
        : base(syncEngine, syncServer, syncRepository, connectedSystem, connectedSystemRunProfile, activity, cancellationTokenSource, phaseReporter, uniqueValueReservations, uniquenessProbeSessionHost, configurationChangeIdsEligibleToClaim)
    {
    }

    public async Task PerformFullSyncAsync()
    {
        try
        {
            await PerformFullSyncCoreAsync();
        }
        finally
        {
            // Unique Value Generation (#242, Phase 2 work package G): release this run's claims on the
            // process-wide reservation set whatever happened (success, failure or cancellation), so a value
            // this run proposed but never committed is not held against every other run for ever.
            _uniqueValueReservations.ReleaseAll(_activity.Id);

            // Unique Value Generation (#242, release 3): report each Connected System that could not be probed for a
            // value this run issued, as an Activity warning, and close every probe connection the run opened.
            ReportGeneratedValuesWaiting();
            await CloseUniquenessProbeSessionAsync();
        }
    }

    private async Task PerformFullSyncCoreAsync()
    {
        using var syncSpan = Diagnostics.Sync.StartSpan("FullSync");
        syncSpan.SetTag("connectedSystemId", _connectedSystem.Id);
        syncSpan.SetTag("connectedSystemName", _connectedSystem.Name);
        syncSpan.SetTag("connectorType", _connectedSystem.ConnectorDefinition.Name);

        Log.Verbose("PerformFullSyncAsync: Starting");

        // Claim the configuration changes awaiting synchronisation before anything else: before the count and before any
        // Synchronisation Rule is loaded, which is what makes the claim race-free (#2615). A Full Synchronisation
        // processes every object of every type anyway, so there is nothing further to select; the claim is what lets it
        // mark the changes applied on completion. A change saved after this point is not claimed and waits for the next
        // run.
        await ClaimConfigurationChangesAwaitingSynchronisationAsync("Full Synchronisation");
        syncSpan.SetTag("typesWithConfigurationChangesClaimed", _objectTypeIdsToProcessInFull.Count);

        // what needs to happen:
        // - confirm Pending Exports
        // - establish new joins to existing Metaverse Objects
        // - project CSO to the MV if there are no join matches and if a Synchronisation Rule for this CS has Projection enabled.
        // - work out if we CAN update any Metaverse Objects (where there's Attribute Flow) and whether we SHOULD (where there's Attribute Flow priority).
        // - update the Metaverse Objects accordingly.
        // - work out if this requires other Connected System to be updated by way of creating new Pending Export Objects.

        await _phases.EnterAsync(RunPhaseKeys.SyncPrepare);

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

        // Metaverse-Derived Attribute Flows (#1750): build the run's dependency graph from the same all-systems rule
        // set, once for the run. A cycle among the enabled derived flows throws DerivedFlowCycleException here, before
        // any object is processed, failing the run hard with the cycle named on the Activity (plan decision 11).
        DerivedFlowGraph derivedFlowGraph;
        using (Diagnostics.Sync.StartSpan("BuildDerivedFlowGraph"))
        {
            derivedFlowGraph = _syncServer.CreateDerivedFlowGraph(allSyncRules);
        }

        // Build drift detection cache (import mapping cache + export rules with EnforceState=true), and the attribute
        // priority context carrying the derived flow graph.
        // This enables efficient drift detection during CSO processing
        BuildDriftDetectionCache(allSyncRules, activeSyncRules, derivedFlowGraph);

        // Use object types already loaded on the Connected System (with matching rules and attributes)
        // to avoid creating duplicate entity instances that conflict with EF Core's change tracker.
        _objectTypes = _connectedSystem.ObjectTypes!;

        // Pre-load export evaluation cache (export rules + CSO lookups) for O(1) access
        // This eliminates O(N×M) database queries during export evaluation
        using (Diagnostics.Sync.StartSpan("LoadExportEvaluationCache"))
        {
            _exportEvaluationCache = await _syncServer.BuildExportEvaluationCacheAsync(preloadedSyncRules: allSyncRules);

            // Separate run-scoped cache instance for reference recall staging (#1003). The two now
            // build identically (no system is excluded from export evaluation any more, #1284), but
            // they stay separate instances because each is refreshed per page with different Metaverse
            // Object sets, and sharing one would let the recall refresh clobber the main flow's lookups.
            _recallExportEvaluationCache = await _syncServer.BuildExportEvaluationCacheAsync(preloadedSyncRules: allSyncRules);
        }

        // Unique Value Generation (#242, Phase 2 work package G) per-run setup: after the export evaluation
        // cache (its participating-targets precompute reads ExportRulesByMvoTypeId), before the page loop.
        // A no-op, costing nothing further this run, when activeSyncRules carries no generated import mapping.
        await PrepareUniqueValueGenerationAsync(activeSyncRules);

        // Load settings once at start of sync
        _syncOutcomeTrackingLevel = await _syncServer.GetSyncOutcomeTrackingLevelAsync();
        _csoChangeTrackingEnabled = await _syncServer.GetCsoChangeTrackingEnabledAsync();

        // A Full Synchronisation evaluates every object, every run, with no regard to when it last changed (#891). It is
        // the administrator's remediation tool: it applies configuration and fixed engine logic to existing data, and
        // repairs whatever a Delta Synchronisation missed, so it must not trust the timestamps and marks a Delta
        // Synchronisation relies on to pick its objects. Delta Synchronisation is the fast path.
        var fullSyncStartedAt = DateTime.UtcNow;

        // Process CSOs in batches. This enables us to respond to cancellation requests in a reasonable timeframe.
        // Page size is configurable via service settings for performance tuning.
        var pageSize = await _syncServer.GetSyncPageSizeAsync();
        var totalCsoPages = Convert.ToInt16(Math.Ceiling((double)totalCsosToProcess / pageSize));
        await _phases.EnterAsync(RunPhaseKeys.SyncProcessObjects);

        using var processCsosSpan = Diagnostics.Sync.StartSpan("ProcessConnectedSystemObjects");
        processCsosSpan.SetTag("totalObjects", totalCsosToProcess);
        processCsosSpan.SetTag("pageSize", pageSize);
        processCsosSpan.SetTag("totalPages", totalCsoPages);

        var throughput = new ThroughputTracker();
        var csoPhaseStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Page through the Object Types in dependency order (#2492), referenced types first, so a group's members have
        // been projected by the time its page is processed and its member references resolve in this loop rather than
        // being deferred to the cross-page reference step, which would reload the group and redo its work. Each type
        // is walked with its own keyset cursor starting at Guid.Empty (below every generated uuid in both PostgreSQL's
        // bytewise and .NET's component-wise ordering), which keeps every page O(pageSize) instead of degrading with
        // OFFSET depth (measured at Scale500k25kGroups: 977s across 1,050 page loads); the cursor advances to the last
        // row of each page exactly as the repository returned it, since a client-side max would diverge from
        // PostgreSQL's uuid ordering and skip rows.
        //
        // Each type pages until a page comes back empty, not to the count taken at the start (#2092): objects created
        // while the run is in flight would otherwise push the original tail past a page budget fixed before they
        // arrived. A closing sweep then picks up objects created mid-run behind a type that had already been walked.
        // The count is the progress estimate only, and ObjectsProcessed may exceed it.
        var csoPager = await CreateTypeOrderedPagerAsync(activeSyncRules, fullSyncStartedAt,
            (afterId, objectTypeId, createdOnOrAfter) => _syncRepo.GetConnectedSystemObjectsAsync(
                _connectedSystem.Id, 1, pageSize, totalCsosToProcess, afterId, objectTypeId, createdOnOrAfter),
            "Full Synchronisation", observeUndeclaredReferenceTargets: true);

        try
        {
            for (var i = 1; ; i++)
            {
                // Change detection stays off from the page's load to its tracker clear (#2598). The page's Metaverse
                // Objects are loaded untracked and hung off its tracked Connected System Objects, so a detection pass
                // anywhere in the page would discover them through cso.MetaverseObject and attach them, every value
                // included, as Modified: a later EF save would then issue xmin-guarded updates that match no rows. With
                // detection off, an EF save writes only what is explicitly marked (AddRange for creates,
                // Entry().State = Modified for updates). The flush relies on that too: detection would otherwise discover
                // the tracked Activity's RunProfileExecutionItems and insert them ahead of the raw RPEI bulk insert.
                _syncRepo.SetAutoDetectChangesEnabled(false);

                PagedResultSet<ConnectedSystemObject>? csoPagedResult;
                using (Diagnostics.Sync.StartSpan("LoadCsoPage"))
                {
                    csoPagedResult = await csoPager.NextPageAsync();
                }

                if (csoPagedResult == null)
                    break;

                // Seed the page identity map (#1612) with every already-joined MVO this page's CSO load
                // brought in, so Pass 1's obsoletion handling and Pass 2's matching-rule join both resolve
                // onto the same canonical instance as this navigation, rather than two distinct loads of the
                // same row.
                _mvoIdentityMap.Seed(csoPagedResult.Results);

                // The row version of every marked object (derived-input, #1750; synchronisation-pending, #2092) as this load
                // read it, before this run writes anything; it guards the marks' clear at page flush (see the method for why).
                CaptureMarkedRowVersions(csoPagedResult.Results);

                // Unique Value Generation (#242, Phase 2 work package G) page-start prefetch: a no-op when this
                // run has no generated mappings.
                await PrefetchGeneratedValueAssignmentsForPageAsync(csoPagedResult.Results);

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

                        using (Diagnostics.Sync.StartSpan("ProcessObsoleteCsoTeardown"))
                            await ProcessObsoleteConnectedSystemObjectTeardownAsync(activeSyncRules, connectedSystemObject);
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

                            using (Diagnostics.Sync.StartSpan("ProcessActiveCso"))
                                await ProcessActiveConnectedSystemObjectAsync(activeSyncRules, connectedSystemObject);

                            _activity.ObjectsProcessed++;
                            processedInPage++;
                            OnCsoProcessedInPass2?.Invoke();
                            OnConnectedSystemObjectProcessedInPass2?.Invoke(connectedSystemObject);
                        }
                    }
                }

                Log.Information("MetricsCheckpoint: FullSync processed={ObjectsProcessed} elapsed={ElapsedMs}ms total={TotalObjects} cs={ConnectedSystemName}",
                    _activity.ObjectsProcessed, (long)csoPhaseStopwatch.Elapsed.TotalMilliseconds, totalCsosToProcess, _connectedSystem.Name);

                // Process deferred reference attributes after all CSOs in this page have been processed.
                // Reference attributes (e.g., group members) may point to CSOs that are processed later in the same page.
                // By deferring reference attributes, we ensure all MVOs exist before resolving references.
                // This enables a single sync run to fully reconcile all objects including references.
                using (Diagnostics.Sync.StartSpan("ProcessDeferredReferenceAttributes"))
                    ProcessDeferredReferenceAttributes();

                // Change detection has been off since the page loaded (see the top of the loop); it is restored once the
                // page's tracker is cleared, below.
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

                    // Unique Value Generation (#242, Phase 2 work package G): delete whatever the page's lifecycle
                    // reconciliation (or a stale Sticky match, #242 Scenario 023 bug fix) decided no longer belongs,
                    // THEN commit this page's generated assignments now the objects have real ids - deletion
                    // must run first because a stale assignment being removed can share its (object, attribute) key
                    // with the fresh one about to be inserted for the same request; see FlushGeneratedValueAssignmentDeletionsAsync's
                    // doc comment. Both are no-ops for a run with no generated mappings, or a page with nothing to
                    // commit/delete.
                    using (Diagnostics.Sync.StartSpan("FlushGeneratedValueAssignmentDeletions"))
                        await FlushGeneratedValueAssignmentDeletionsAsync();
                    using (Diagnostics.Sync.StartSpan("CommitGeneratedValueAssignments"))
                        await CommitGeneratedValueAssignmentsAsync();

                    // create MVO change objects for change tracking (after MVOs persisted so IDs available)
                    await CreatePendingMvoChangeObjectsAsync(activeSyncRules);

                    // evaluate queued drift detection (after MVOs persisted so corrective Pending Exports
                    // capture real Metaverse Object ids, before the export flush persists them)
                    await EvaluateQueuedDriftAsync();

                    // batch evaluate exports for all MVOs that changed during this page
                    await EvaluatePendingExportsAsync();

                    // batch process Pending Export confirmations (deletes and updates)
                    await FlushPendingExportOperationsAsync();

                    // Resolve any Pending Export reference snapshots that couldn't be resolved during
                    // per-object processing (e.g. groups processed before their member users on this page).
                    // Must run after FlushPendingExportOperationsAsync (CSOs now in DB) and before
                    // FlushRpeisAsync (RPEIs with snapshots persisted).
                    using (Diagnostics.Sync.StartSpan("ResolvePendingExportReferenceSnapshots"))
                        await ResolvePendingExportReferenceSnapshotsAsync();

                    // batch delete obsolete CSOs
                    using (Diagnostics.Sync.StartSpan("FlushObsoleteCsoOperations"))
                        await FlushObsoleteCsoOperationsAsync();

                    // batch delete MVOs marked for immediate deletion (0-grace-period)
                    await FlushPendingMvoDeletionsAsync();

                    // Flush this page's RPEIs via bulk insert before updating progress
                    using (Diagnostics.Sync.StartSpan("FlushRpeis"))
                        await FlushRpeisAsync();

                    // Persist MVO change records via raw SQL before clearing the change tracker
                    using (Diagnostics.Sync.StartSpan("FlushPendingMvoChanges"))
                        await FlushPendingMvoChangesAsync();

                    // Clear the change tracker (and the page identity map, #1612) unconditionally at every page
                    // boundary to prevent memory accumulation from tracked entities across pages. Without this,
                    // the tracker grows linearly with total object count (500K+ entries at 100K objects),
                    // causing OOM. All page data has been flushed to the database above. The Activity entity
                    // becomes detached but UpdateDetachedSafe re-attaches it on the next UpdateActivityAsync
                    // call. Cross-page state (_unresolvedCrossPageReferences, _exportEvaluationCache, etc.) is
                    // held in CLR fields — detaching does not null their populated navigation properties.
                    // The tracker's size before the clear is what the page accumulated, logged with the page below (#2669).
                    var trackerCountBeforeClear = _syncRepo.GetChangeTrackerEntityCount();
                    ClearPageTrackingState();

                    // Update progress with page completion. The call persists the Activity's counters,
                    // which is what the portal renders the count, rate and time remaining from; the
                    // message carries no numbers of its own, and the running step's name already says
                    // what is happening, so there is nothing left for it to add here.
                    using (Diagnostics.Sync.StartSpan("UpdateActivityProgress"))
                    {
                        await _syncRepo.UpdateActivityMessageAsync(_activity, string.Empty);
                    }

                    LogPageMemoryDiagnostics(i, totalCsoPages, trackerCountBeforeClear);
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
        }
        finally
        {
            // Not every load reaches a page's own tracker clear (#2598): the pager's closing sweep can load pages it hands
            // nothing out of, and an exception in Pass 1 or 2 leaves the page before its flush. What they loaded holds
            // Connected System Objects tracked over untracked Metaverse Objects, so it goes before detection is restored.
            ClearPageTrackingState();
            _syncRepo.SetAutoDetectChangesEnabled(true);
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
            using (Diagnostics.Sync.StartSpan("FlushRpeis"))
                await FlushRpeisAsync();

            // Emit the deduplicated reference-recall Pending Export RPEIs accumulated across all page
            // deletions (one per referencing group CSO, not one per page-flush that touched it).
            await FlushDeferredRecallRpeisAsync();

            // Outbound Temporal Scope Reconciler apply step (#892): re-evaluate export scope for Metaverse
            // Objects the reconciler flagged, whose export-rule scope drifted with the clock without a data
            // change (the change-driven export path never revisits them). Runs after all inbound-driven changes
            // are persisted so provisioning/deprovisioning reflects the fully reconciled Metaverse state.
            await ProcessScopeReviewPendingMetaverseObjectsAsync();

            // Collision Remediation (#242, release 4): carry generated values an export run corrected to the queued
            // exports, the rejecting system's included, now that this run's own changes are persisted.
            await ProcessGeneratedValueRevisionsPendingAsync();

            // Unique Value Generation (#2044): the run has drawn every sequence number it will, so hand the undrawn
            // rest of its reserved blocks back, and the next run carries on from the next number rather than a block on.
            await ReturnUnusedSequenceNumbersAsync();

            // Ensure the activity and any pending db updates are applied after all pages are processed
            await _syncRepo.UpdateActivityAsync(_activity);

            // Update the delta sync watermark to establish baseline for future delta syncs
            await UpdateDeltaSyncWatermarkAsync();

            // The configuration changes this run claimed are applied: every object of every type has been processed.
            // Reached only on completion, never on cancellation or failure, so those leave the changes awaiting
            // synchronisation for the next run to claim again.
            await ApplyClaimedConfigurationChangesAsync("Full Synchronisation");

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

        LogMarkSummary();
        syncSpan.SetSuccess();
    }
}

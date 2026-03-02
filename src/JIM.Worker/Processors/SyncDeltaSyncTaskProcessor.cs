using JIM.Application;
using JIM.Application.Diagnostics;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Utility;
using Serilog;

namespace JIM.Worker.Processors;

/// <summary>
/// Processes delta synchronisation for a Connected System.
/// Unlike full sync which processes ALL CSOs, delta sync only processes CSOs
/// that have been modified since the last sync completed (based on LastUpdated timestamp).
/// This provides significant performance improvements when only a small subset of objects changed.
/// </summary>
public class SyncDeltaSyncTaskProcessor : SyncTaskProcessorBase
{
    public SyncDeltaSyncTaskProcessor(
        JimApplication jimApplication,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile connectedSystemRunProfile,
        Activity activity,
        CancellationTokenSource cancellationTokenSource)
        : base(jimApplication, connectedSystem, connectedSystemRunProfile, activity, cancellationTokenSource)
    {
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

        // Load ALL sync rules from ALL systems for drift detection import mapping cache.
        // This is needed because drift detection must know which systems contribute to which MVO attributes
        // to avoid false positives on export-only systems.
        List<SyncRule> allSyncRules;
        using (Diagnostics.Sync.StartSpan("LoadAllSyncRulesForDriftDetection"))
        {
            allSyncRules = await _jim.ConnectedSystems.GetSyncRulesAsync();
        }

        // Build drift detection cache (import mapping cache + export rules with EnforceState=true)
        // This enables efficient drift detection during CSO processing
        BuildDriftDetectionCache(allSyncRules, activeSyncRules);

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

        // Load sync outcome tracking level (None/Standard/Detailed) for building outcome trees on RPEIs
        _syncOutcomeTrackingLevel = await _jim.ServiceSettings.GetSyncOutcomeTrackingLevelAsync();

        // Process only the modified CSOs in batches. This enables us to respond to cancellation requests in a reasonable timeframe.
        // Page size is configurable via service settings for performance tuning.
        var pageSize = await _jim.ServiceSettings.GetSyncPageSizeAsync();
        var totalCsoPages = Convert.ToInt16(Math.Ceiling((double)totalCsosToProcess / pageSize));
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Processing modified Connected System Objects");

        using var processCsosSpan = Diagnostics.Sync.StartSpan("ProcessModifiedConnectedSystemObjects");
        processCsosSpan.SetTag("totalObjects", totalCsosToProcess);
        processCsosSpan.SetTag("pageSize", pageSize);
        processCsosSpan.SetTag("totalPages", totalCsoPages);

        // Set the message once for the entire phase (no page details for users)
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Processing modified Connected System Objects");

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

            // Note: Target CSO attribute values for no-net-change detection are pre-loaded in ExportEvaluationCache
            // (built at sync start) rather than per-page, since we need target system CSO attributes not source CSO attributes.

            int processedInPage = 0;
            using (Diagnostics.Sync.StartSpan("ProcessCsoLoop").SetTag("csoCount", csoPagedResult.Results.Count))
            {
                // Two-pass processing ensures all CSO disconnections are recorded before any join attempts.
                // See SyncFullSyncTaskProcessor for detailed rationale.

                // Pass 1: Process pending export confirmations and obsolete CSO teardown.
                foreach (var connectedSystemObject in csoPagedResult.Results)
                {
                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        Log.Information("PerformDeltaSyncAsync: Cancellation requested. Stopping CSO enumeration.");
                        return;
                    }

                    await ProcessObsoleteAndExportConfirmationAsync(activeSyncRules, connectedSystemObject);
                }

                // Pass 2: Process joins, projections, and attribute flow for non-obsolete CSOs.
                foreach (var connectedSystemObject in csoPagedResult.Results)
                {
                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        Log.Information("PerformDeltaSyncAsync: Cancellation requested. Stopping CSO enumeration.");
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
            // See SyncFullSyncTaskProcessor for detailed explanation of why this is necessary
            // to prevent DetectChanges() from discovering RPEIs and causing duplicate key violations.
            _jim.Repository.SetAutoDetectChangesEnabled(false);
            try
            {
                // Batch persist all MVOs collected during this page.
                // See SyncFullSyncTaskProcessor for design notes on why progress updates
                // cannot be decoupled from batch persistence boundaries.
                await PersistPendingMetaverseObjectsAsync();

                // create MVO change objects for change tracking (after MVOs persisted so IDs available)
                await CreatePendingMvoChangeObjectsAsync();

                // Batch evaluate exports for all MVOs that changed during this page
                await EvaluatePendingExportsAsync();

                // batch process pending export confirmations (deletes and updates)
                await FlushPendingExportOperationsAsync();

                // batch delete obsolete CSOs
                await FlushObsoleteCsoOperationsAsync();

                // batch delete MVOs marked for immediate deletion (0-grace-period)
                var hadMvoDeletions = _pendingMvoDeletions.Count > 0;
                await FlushPendingMvoDeletionsAsync();

                // Flush this page's RPEIs via bulk insert before updating progress
                await FlushRpeisAsync();

                // Clear the change tracker after MVO deletions to prevent stale entity conflicts.
                // See SyncFullSyncTaskProcessor for detailed explanation.
                if (hadMvoDeletions && _hasRawSqlSupport)
                    _jim.Repository.ClearChangeTracker();

                // Update progress with page completion - this persists ObjectsProcessed to database (including MVO changes)
                using (Diagnostics.Sync.StartSpan("UpdateActivityProgress"))
                {
                    await _jim.Activities.UpdateActivityAsync(_activity);
                }
            }
            finally
            {
                _jim.Repository.SetAutoDetectChangesEnabled(true);
            }
        }

        // Resolve cross-page reference attributes (same as full sync — see full sync for detailed explanation)
        await ResolveCrossPageReferencesAsync(activeSyncRules);

        // Flush any RPEIs from cross-page resolution
        await FlushRpeisAsync();

        // Ensure the activity and any pending db updates are applied after all pages are processed
        await _jim.Activities.UpdateActivityAsync(_activity);

        // Update the watermark to mark this sync as complete
        await UpdateDeltaSyncWatermarkAsync();

        // Compute summary stats from all RPEIs (flushed + any remaining in Activity).
        // _allPersistedRpeis contains RPEIs from all pages; Activity may have unflushed RPEIs.
        var allRpeis = _allPersistedRpeis.Concat(_activity.RunProfileExecutionItems).ToList();
        Worker.CalculateActivitySummaryStats(_activity, allRpeis);

        syncSpan.SetSuccess();
    }
}

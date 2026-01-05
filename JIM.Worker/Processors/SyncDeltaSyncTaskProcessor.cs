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

            // Load CSO attribute values for this page (for no-net-change detection during export evaluation)
            await LoadPageCsoAttributeCacheAsync(csoPagedResult.Results.Select(cso => cso.Id));

            int processedInPage = 0;
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
                    processedInPage++;
                }
            }

            // Batch persist all MVOs collected during this page.
            // See SyncFullSyncTaskProcessor for design notes on why progress updates
            // cannot be decoupled from batch persistence boundaries.
            await PersistPendingMetaverseObjectsAsync();

            // Batch evaluate exports for all MVOs that changed during this page
            await EvaluatePendingExportsAsync();

            // batch process pending export confirmations (deletes and updates)
            await FlushPendingExportOperationsAsync();

            // batch delete obsolete CSOs
            await FlushObsoleteCsoOperationsAsync();

            // Clear per-page CSO attribute cache to free memory
            ClearPageCsoAttributeCache();

            // Update progress with page completion - this persists ObjectsProcessed to database
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
}

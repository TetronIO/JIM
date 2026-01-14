using JIM.Application;
using JIM.Application.Diagnostics;
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
        JimApplication jimApplication,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile connectedSystemRunProfile,
        Activity activity,
        CancellationTokenSource cancellationTokenSource)
        : base(jimApplication, connectedSystem, connectedSystemRunProfile, activity, cancellationTokenSource)
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

            // Surface pending exports awaiting confirmation as RPEIs for operator visibility.
            // This gives operators insight into what changes will be made on the next export run.
            SurfacePendingExportsAsExecutionItems(allPendingExports);
        }

        // Pre-load export evaluation cache (export rules + CSO lookups) for O(1) access
        // This eliminates O(N×M) database queries during export evaluation
        using (Diagnostics.Sync.StartSpan("LoadExportEvaluationCache"))
        {
            _exportEvaluationCache = await _jim.ExportEvaluation.BuildExportEvaluationCacheAsync(_connectedSystem.Id);
        }

        // Process CSOs in batches. This enables us to respond to cancellation requests in a reasonable timeframe.
        // Page size is configurable via service settings for performance tuning.
        var pageSize = await _jim.ServiceSettings.GetSyncPageSizeAsync();
        var totalCsoPages = Convert.ToInt16(Math.Ceiling((double)totalCsosToProcess / pageSize));
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Processing Connected System Objects");

        using var processCsosSpan = Diagnostics.Sync.StartSpan("ProcessConnectedSystemObjects");
        processCsosSpan.SetTag("totalObjects", totalCsosToProcess);
        processCsosSpan.SetTag("pageSize", pageSize);
        processCsosSpan.SetTag("totalPages", totalCsoPages);

        // Set the message once for the entire phase (no page details for users)
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Processing Connected System Objects");

        for (var i = 1; i <= totalCsoPages; i++)
        {

            PagedResultSet<ConnectedSystemObject> csoPagedResult;
            using (Diagnostics.Sync.StartSpan("LoadCsoPage"))
            {
                csoPagedResult = await _jim.ConnectedSystems.GetConnectedSystemObjectsAsync(_connectedSystem.Id, i, pageSize, returnAttributes: false);
            }

            // Load CSO attribute values for this page (for no-net-change detection during export evaluation)
            await LoadPageCsoAttributeCacheAsync(csoPagedResult.Results.Select(cso => cso.Id));

            int processedInPage = 0;
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

                    await ProcessConnectedSystemObjectAsync(activeSyncRules, connectedSystemObject);

                    _activity.ObjectsProcessed++;
                    processedInPage++;
                }
            }

            // Process deferred reference attributes after all CSOs in this page have been processed.
            // Reference attributes (e.g., group members) may point to CSOs that are processed later in the same page.
            // By deferring reference attributes, we ensure all MVOs exist before resolving references.
            // This enables a single sync run to fully reconcile all objects including references.
            ProcessDeferredReferenceAttributes();

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

            // batch evaluate exports for all MVOs that changed during this page
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

        // TODO: work out if CSO changes have been persisted. Is a dedicated db update call needed?
        // ensure the activity and any pending db updates are applied.
        await _jim.Activities.UpdateActivityMessageAsync(_activity, "Resolving references");

        using (Diagnostics.Sync.StartSpan("ResolveReferences"))
        {
            await ResolveReferencesAsync();
        }

        // Update the delta sync watermark to establish baseline for future delta syncs
        await UpdateDeltaSyncWatermarkAsync();

        syncSpan.SetSuccess();
    }

    /// <summary>
    /// Creates ActivityRunProfileExecutionItems for pending exports that are awaiting confirmation.
    /// This surfaces unconfirmed exports (ExportNotImported status) to the Activity so operators
    /// can see what changes will be made to connected systems on the next export run.
    /// </summary>
    /// <param name="allPendingExports">All pending exports for this connected system.</param>
    private void SurfacePendingExportsAsExecutionItems(List<PendingExport> allPendingExports)
    {
        // Filter to only pending exports that are awaiting confirmation (ExportNotImported)
        // or are pending execution. These represent staged changes the operator should know about.
        var pendingExportsToSurface = allPendingExports
            .Where(pe => pe.Status == PendingExportStatus.ExportNotImported ||
                         pe.Status == PendingExportStatus.Pending)
            .ToList();

        if (pendingExportsToSurface.Count == 0)
        {
            Log.Verbose("SurfacePendingExportsAsExecutionItems: No pending exports to surface.");
            return;
        }

        Log.Information("SurfacePendingExportsAsExecutionItems: Surfacing {Count} pending exports as execution items for operator visibility.",
            pendingExportsToSurface.Count);

        foreach (var pendingExport in pendingExportsToSurface)
        {
            var executionItem = _activity.PrepareRunProfileExecutionItem();
            executionItem.ObjectChangeType = ObjectChangeType.PendingExport;
            executionItem.ConnectedSystemObject = pendingExport.ConnectedSystemObject;
            executionItem.ConnectedSystemObjectId = pendingExport.ConnectedSystemObjectId;

            // Capture the external ID snapshot for historical reference
            if (pendingExport.ConnectedSystemObject != null)
            {
                executionItem.ExternalIdSnapshot = pendingExport.ConnectedSystemObject.ExternalIdAttributeValue?.StringValue;
            }

            _activity.RunProfileExecutionItems.Add(executionItem);
        }

        Log.Debug("SurfacePendingExportsAsExecutionItems: Created {Count} execution items for pending exports.",
            pendingExportsToSurface.Count);
    }
}

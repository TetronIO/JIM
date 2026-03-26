using JIM.Application.Interfaces;
using JIM.Data.Repositories;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Thin orchestration wrapper for pending export reconciliation.
/// Loads data from the database, delegates to <see cref="ISyncEngine"/> for pure reconciliation logic,
/// and persists the results. All decision-making logic lives in SyncEngine.
/// </summary>
public class PendingExportReconciliationService
{
    private readonly ISyncRepository _syncRepo;
    private readonly ISyncEngine _syncEngine;

    public PendingExportReconciliationService(ISyncRepository syncRepo, ISyncEngine syncEngine)
    {
        _syncRepo = syncRepo;
        _syncEngine = syncEngine;
    }

    /// <summary>
    /// Reconciles a Connected System Object's imported attribute values against any pending exports.
    /// Loads the pending export from the database, delegates reconciliation to SyncEngine,
    /// and persists the outcome. For bulk operations, use SyncEngine.ReconcileCsoAgainstPendingExport directly
    /// with pre-loaded data and batch persistence.
    /// </summary>
    /// <param name="connectedSystemObject">The CSO that was just imported/updated.</param>
    /// <returns>A result indicating what reconciliation actions were taken.</returns>
    public async Task<PendingExportReconciliationResult> ReconcileAsync(ConnectedSystemObject connectedSystemObject)
    {
        var result = new PendingExportReconciliationResult();

        var pendingExport = await _syncRepo.GetPendingExportByConnectedSystemObjectIdAsync(connectedSystemObject.Id);

        if (pendingExport == null)
        {
            Log.Debug("ReconcileAsync: No pending export found for CSO {CsoId}", connectedSystemObject.Id);
            return result;
        }

        // Delegate to SyncEngine for pure in-memory reconciliation
        _syncEngine.ReconcileCsoAgainstPendingExport(connectedSystemObject, pendingExport, result);

        // Persist changes
        if (result.PendingExportDeleted)
        {
            await _syncRepo.DeletePendingExportAsync(pendingExport);
            Log.Information("ReconcileAsync: All attribute changes confirmed. Deleted pending export {ExportId}", pendingExport.Id);
        }
        else if (result.HasChanges)
        {
            await _syncRepo.UpdatePendingExportAsync(pendingExport);
            Log.Debug("ReconcileAsync: Updated pending export {ExportId} with {Confirmed} confirmed, {Retry} for retry, {Failed} failed",
                pendingExport.Id, result.ConfirmedChanges.Count, result.RetryChanges.Count, result.FailedChanges.Count);
        }

        return result;
    }
}

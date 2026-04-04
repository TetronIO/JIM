using JIM.Models.Transactional;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Pre-export reconciliation logic — identifies CREATE+DELETE and UPDATE+DELETE
/// pending export pairs targeting the same CSO that cancel each other out.
/// This prevents unnecessary round-trips to connected systems.
/// </summary>
public partial class SyncEngine
{
    /// <inheritdoc />
    public PreExportReconciliationResult ReconcileCreateDeletePairs(IReadOnlyList<PendingExportSummary> pendingExports)
    {
        var result = new PreExportReconciliationResult();

        if (pendingExports.Count == 0)
            return result;

        // Group by ConnectedSystemObjectId — only exports with a CSO ID can be matched
        var groupsByCso = pendingExports
            .Where(pe => pe.ConnectedSystemObjectId.HasValue)
            .GroupBy(pe => pe.ConnectedSystemObjectId!.Value);

        foreach (var group in groupsByCso)
        {
            var deletePe = group.FirstOrDefault(pe =>
                pe.ChangeType == PendingExportChangeType.Delete &&
                pe.Status == PendingExportStatus.Pending);

            if (deletePe == null)
                continue;

            // Look for a CREATE or UPDATE that is still Pending (not yet exported)
            var createOrUpdatePe = group.FirstOrDefault(pe =>
                pe.Id != deletePe.Id &&
                pe.Status == PendingExportStatus.Pending &&
                (pe.ChangeType == PendingExportChangeType.Create ||
                 pe.ChangeType == PendingExportChangeType.Update));

            if (createOrUpdatePe == null)
                continue;

            var pair = new ReconciledExportPair
            {
                ConnectedSystemObjectId = group.Key,
                SourceMetaverseObjectId = createOrUpdatePe.SourceMetaverseObjectId ?? deletePe.SourceMetaverseObjectId,
            };

            if (createOrUpdatePe.ChangeType == PendingExportChangeType.Create)
            {
                // CREATE + DELETE → cancel both (no net change — object never existed in target)
                pair.CancelledExportIds.Add(createOrUpdatePe.Id);
                pair.CancelledExportIds.Add(deletePe.Id);
                pair.Reason = "Pending CREATE followed by pending DELETE — no net change, object was never exported to target system";

                Log.Information("ReconcileCreateDeletePairs: CREATE PE {CreateId} and DELETE PE {DeleteId} for CSO {CsoId} cancel each other out — neither will be exported",
                    createOrUpdatePe.Id, deletePe.Id, group.Key);
            }
            else
            {
                // UPDATE + DELETE → remove UPDATE only (object exists in target, DELETE still needed)
                pair.CancelledExportIds.Add(createOrUpdatePe.Id);
                pair.Reason = "Pending UPDATE followed by pending DELETE — UPDATE is redundant since object will be deleted";

                Log.Information("ReconcileCreateDeletePairs: UPDATE PE {UpdateId} for CSO {CsoId} is redundant — DELETE PE {DeleteId} will proceed",
                    createOrUpdatePe.Id, group.Key, deletePe.Id);
            }

            result.ReconciledPairs.Add(pair);
        }

        return result;
    }
}

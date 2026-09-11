// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Utilities;

/// <summary>
/// Derives a Connected System Object's <see cref="ConnectedSystemObjectConnectionState"/> (D-S7) from
/// its <see cref="ConnectedSystemObjectStatus"/> and its Pending Export, if any. Pure and side-effect
/// free so it is reusable by both the Identity Connections tab and the Connector Space list, and unit
/// testable one state at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a Create Pending Export never survives to reach the priority checks against a Normal
/// status:</b> a Create Pending Export exists only on a CSO created by provisioning, which is created
/// with <see cref="ConnectedSystemObjectStatus.PendingProvisioning"/>
/// (<c>ExportEvaluationServer.CreatePendingProvisioningCsoAsync</c>). The transition back to
/// <see cref="ConnectedSystemObjectStatus.Normal"/> happens in exactly one place,
/// <c>SyncImportTaskProcessor.ProcessImportObjectsAsync</c>, at the moment a confirming import matches
/// the object by its (now exported) secondary external id. That same confirming import pass is what
/// reconciles the Pending Export
/// (<c>SyncEngine.Reconciliation.ReconcileCsoAgainstPendingExport</c> ->
/// <c>TransitionCreateToUpdateIfSecondaryExternalIdConfirmed</c>), flipping its
/// <see cref="PendingExport.ChangeType"/> from <see cref="PendingExportChangeType.Create"/> to
/// <see cref="PendingExportChangeType.Update"/> in the same pass. The two flips are not
/// transactionally linked by a shared write, but they are driven by the same confirming import
/// finding the same object, so a Create Pending Export paired with a Normal status, or an Update
/// paired with PendingProvisioning, are not combinations the running system produces. This resolver
/// still answers something reasonable if one is ever observed (see the PendingProvisioning-with-no-
/// Pending-Export fallback below), rather than assuming the impossible case cannot reach it.
/// </para>
/// <para>
/// <b>Priority when several facts could apply:</b> a Failed Pending Export is the single most
/// actionable fact or reads first (<see cref="ConnectedSystemObjectConnectionState.ExportFailed"/>);
/// Obsolete is checked next, because the object's own status says it is gone from the Connected
/// System, ahead of a Pending Export that will not export anywhere. The rest follow the export's
/// change type.
/// </para>
/// </remarks>
public static class ConnectedSystemObjectConnectionStateResolver
{
    /// <summary>
    /// Resolves the connection state for one Connected System Object.
    /// </summary>
    /// <param name="status">The object's current status.</param>
    /// <param name="pendingExport">
    /// The object's Pending Export, or null when none is queued. Only <see cref="PendingExport.ChangeType"/>
    /// and <see cref="PendingExport.Status"/> are read; callers may pass a lightweight instance.
    /// </param>
    public static ConnectedSystemObjectConnectionState Resolve(ConnectedSystemObjectStatus status, PendingExport? pendingExport)
    {
        if (pendingExport == null)
        {
            return status switch
            {
                ConnectedSystemObjectStatus.Obsolete => ConnectedSystemObjectConnectionState.Obsolete,
                // Defensive: a PendingProvisioning object should always carry a Create Pending Export
                // (see remarks) until a confirming import flips both together. If one is ever observed
                // without a Pending Export, the most honest reading is "not yet exported" rather than a
                // silent InSync, which would understate that the object does not exist in the target yet.
                ConnectedSystemObjectStatus.PendingProvisioning => ConnectedSystemObjectConnectionState.ProvisioningExportPending,
                _ => ConnectedSystemObjectConnectionState.InSync
            };
        }

        // A Failed Pending Export needs manual intervention regardless of anything else about the
        // object; that fact must never be masked by a status check further down.
        if (pendingExport.Status == PendingExportStatus.Failed)
            return ConnectedSystemObjectConnectionState.ExportFailed;

        // The object's own status says it is gone from the Connected System; a leftover Pending Export
        // (e.g. an Update that raced a deletion detected out of band) cannot change that.
        if (status == ConnectedSystemObjectStatus.Obsolete)
            return ConnectedSystemObjectConnectionState.Obsolete;

        // A confirming import ran but did not confirm one or more values; it will retry on the next
        // export run. Applies to both Create and Update Pending Exports (see remarks: a Create reaching
        // this status has already been reconciled to Update by the same confirming import pass, but the
        // check is written against the status rather than assuming that ordering).
        if (pendingExport.Status == PendingExportStatus.ExportNotConfirmed)
            return ConnectedSystemObjectConnectionState.ExportNotConfirmed;

        return pendingExport.ChangeType switch
        {
            PendingExportChangeType.Create => pendingExport.Status == PendingExportStatus.Exported
                ? ConnectedSystemObjectConnectionState.ProvisioningAwaitingConfirmation
                : ConnectedSystemObjectConnectionState.ProvisioningExportPending,
            // Pending and Executing are collapsed to one state: from an administrator's perspective the
            // outcome is unknown either way until the delete is confirmed or fails.
            PendingExportChangeType.Delete => ConnectedSystemObjectConnectionState.DeletePending,
            _ => ConnectedSystemObjectConnectionState.UpdatePending
        };
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Worker.Processors;

/// <summary>
/// Records why an export happened, on the Run Profile Execution Item that carried it out (#1223).
/// </summary>
/// <remarks>
/// An export run knows only that it had a queue of changes to make. The decision that put a change in that
/// queue was taken by a synchronisation, in a different Activity, minutes or days earlier, and nothing on the
/// executing item points back at it: the Causality panel rendered no "Caused by" at all for an export, which is
/// exactly the "this change has no cause whatsoever" case the feature exists to remove.
///
/// The PRD expected this hop to be free via <see cref="ActivityRunProfileExecutionItem.PendingExportId"/>. It is
/// not: that column is only populated on a <c>PendingExport</c>-type item (a provisioning export with no
/// Connected System Object yet), never on an ordinary <c>Exported</c> one, so there is nothing to walk back
/// along. Nor could the link be derived later, because the Pending Export row is deleted the moment the export
/// succeeds. It is recorded here or never, which is what earns this seam an edge.
/// </remarks>
public static class ExportCausalEdgeBuilder
{
    /// <summary>
    /// Records the synchronisation that staged this export's Pending Export as the cause of the export.
    /// </summary>
    /// <param name="executionItem">The export's own Run Profile Execution Item: the effect.</param>
    /// <param name="exportItem">The processed export, carrying the cause identifiers copied off the Pending
    /// Export before it was deleted.</param>
    /// <param name="effectOutcome">The Exported / Deprovisioned outcome the cause explains, where outcome
    /// tracking recorded one. Null leaves the edge attached to the item as a whole.</param>
    /// <param name="connectedSystem">The system exported to, snapshotted onto the edge so the chain still
    /// reads after a rename or a deletion.</param>
    public static void RecordQueueingCause(
        ActivityRunProfileExecutionItem executionItem,
        ProcessedExportItem exportItem,
        ActivityRunProfileExecutionItemSyncOutcome? effectOutcome,
        ConnectedSystem connectedSystem)
    {
        ArgumentNullException.ThrowIfNull(executionItem);
        ArgumentNullException.ThrowIfNull(exportItem);
        ArgumentNullException.ThrowIfNull(connectedSystem);

        // A failed export changed nothing on the Connected System, so there is no effect for a cause to
        // explain; the item's story is its error message.
        if (!exportItem.Succeeded)
            return;

        // Neither the queueing run nor the identity behind it is known, which happens for an export staged
        // before this seam existed against an object that has since lost its Metaverse link. An edge here would
        // name nothing, and a "Caused by" whose one entry is unidentifiable is worse than none at all.
        if (!exportItem.QueuedByRunProfileExecutionItemId.HasValue && !exportItem.SourceMetaverseObjectId.HasValue)
            return;

        executionItem.CausalEdges.Add(new CausalCause
        {
            // Known by id rather than by reference: the queueing item belongs to an earlier Activity and is
            // long persisted, which is the opposite of every same-run seam.
            RunProfileExecutionItemId = exportItem.QueuedByRunProfileExecutionItemId,
            MetaverseObjectId = exportItem.SourceMetaverseObjectId,
            PendingExportId = exportItem.PendingExportId,
            // Named from the exported object, because the Pending Export is deleted moments from now and the
            // Metaverse Object may be too (this seam carries deprovisions as well as updates).
            DisplayName = executionItem.DisplayNameSnapshot,
            ConnectedSystemId = connectedSystem.Id,
            ConnectedSystemName = connectedSystem.Name
            // No reason code: the effect outcome already distinguishes an export from a deprovision, and
            // cohorts are computed per effect, so a code here would add nothing to group on.
        }.ToEdge(CausalEdgeType.PendingExportQueueingCausedExportExecution, effectOutcome));
    }
}

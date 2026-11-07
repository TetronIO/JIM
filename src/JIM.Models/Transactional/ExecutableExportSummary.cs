// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A Pending Export as export execution selects it into a batch: what the selection decides on, and the keyset cursor
/// it pages by. Summary-tier (#2813): choosing a batch needs none of the export's object, so the selection reads these
/// alone and the batch's exports are loaded by id once chosen, by whichever context executes them.
/// </summary>
/// <param name="Id">The Pending Export.</param>
/// <param name="ChangeType">What the export does to its object; the Run Profile Safeguards reserve against it.</param>
/// <param name="Status">Where the export stands.</param>
/// <param name="HasUnresolvedReferences">Whether the export waits on references, and so belongs to the deferred pass.</param>
/// <param name="CreatedAt">When the export was created; with <paramref name="Id"/>, the keyset cursor batches page by.</param>
public sealed record ExecutableExportSummary(
    Guid Id,
    PendingExportChangeType ChangeType,
    PendingExportStatus Status,
    bool HasUnresolvedReferences,
    DateTime CreatedAt)
{
    /// <summary>
    /// The summary of a Pending Export already in hand.
    /// </summary>
    public static ExecutableExportSummary Of(PendingExport pendingExport)
    {
        ArgumentNullException.ThrowIfNull(pendingExport);
        return new ExecutableExportSummary(pendingExport.Id, pendingExport.ChangeType, pendingExport.Status,
            pendingExport.HasUnresolvedReferences, pendingExport.CreatedAt);
    }
}

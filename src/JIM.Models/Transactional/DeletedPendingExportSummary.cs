// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A Pending Export as it stood when it was deleted along with its object's others: enough to tell what deleting it meant.
/// Summary-tier: the export's changes are deleted with it and never read.
/// </summary>
/// <param name="Id">The Pending Export.</param>
/// <param name="ConnectedSystemObjectId">The object it was for.</param>
/// <param name="ChangeType">What it did to its object.</param>
/// <param name="Status">Where it stood when deleted.</param>
public sealed record DeletedPendingExportSummary(
    Guid Id,
    Guid ConnectedSystemObjectId,
    PendingExportChangeType ChangeType,
    PendingExportStatus Status)
{
    /// <summary>
    /// Whether the object's deletion in the target confirms this export: a Delete JIM sent, awaiting confirmation (#3087).
    /// </summary>
    /// <remarks>
    /// A Delete not yet sent confirms nothing, since the target deleted the object without it; any other change to a deleted
    /// object is moot rather than confirmed, since the values it carried are nowhere in the target. The same rule as for
    /// removals of references to a deleted object (#3053): what JIM sent is confirmed, what it did not send is not.
    /// </remarks>
    public bool IsConfirmedByObjectDeletion =>
        ChangeType == PendingExportChangeType.Delete &&
        Status is PendingExportStatus.Exported or PendingExportStatus.ExportNotConfirmed;
}

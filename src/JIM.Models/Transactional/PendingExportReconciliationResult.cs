// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// Result of a Pending Export reconciliation operation.
/// </summary>
public class PendingExportReconciliationResult
{
    /// <summary>
    /// Attribute changes that were confirmed (value matched imported value).
    /// These have been removed from the PendingExport.
    /// </summary>
    public List<PendingExportAttributeValueChange> ConfirmedChanges { get; } = new();

    /// <summary>
    /// Attribute changes that were not confirmed and will be sent again at the next export run, however many times
    /// they have been sent before (#2119).
    /// </summary>
    public List<PendingExportAttributeValueChange> RetryChanges { get; } = new();

    /// <summary>
    /// Attribute changes written with an interim value until an Initial Password is delivered
    /// (<see cref="PendingExportAttributeValueChange.AwaitsInitialPassword"/>, #2039) that the import found still
    /// holding the interim value while a password is owed to the account. Left exactly as they were: not retried,
    /// not reported as unconfirmed, and nothing to persist. Counted in the reconciliation summary.
    /// </summary>
    public List<PendingExportAttributeValueChange> HeldChanges { get; } = new();

    /// <summary>
    /// Attribute changes written with an interim value until an Initial Password is delivered that are still
    /// unconfirmed when no password is owed any more (it expired, was withdrawn, or was never staged): found holding
    /// the interim value by the import, or judged on an object the import did not return. Put back to
    /// <see cref="PendingExportAttributeChangeStatus.Pending"/> with the flag cleared, so the next export writes the
    /// asserted value as an ordinary Update. Deliberately not reported as unconfirmed: the interim value was written
    /// on purpose.
    /// </summary>
    public List<PendingExportAttributeValueChange> ReleasedChanges { get; } = new();

    /// <summary>
    /// Whether the PendingExport was deleted (all changes confirmed).
    /// </summary>
    public bool PendingExportDeleted { get; set; }

    /// <summary>
    /// The Pending Export to delete (for batched operations).
    /// Only set when PendingExportDeleted is true.
    /// </summary>
    public PendingExport? PendingExportToDelete { get; set; }

    /// <summary>
    /// The Pending Export to update (for batched operations).
    /// Only set when there are changes but the export should not be deleted.
    /// </summary>
    public PendingExport? PendingExportToUpdate { get; set; }

    /// <summary>
    /// True if reconciliation changed anything that must be persisted. A hold changes nothing, so
    /// <see cref="HeldChanges"/> alone does not count; a release does.
    /// </summary>
    public bool HasChanges => ConfirmedChanges.Count > 0 || RetryChanges.Count > 0 || ReleasedChanges.Count > 0;
}

using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// Result of a pending export reconciliation operation.
/// </summary>
public class PendingExportReconciliationResult
{
    /// <summary>
    /// Attribute changes that were confirmed (value matched imported value).
    /// These have been removed from the PendingExport.
    /// </summary>
    public List<PendingExportAttributeValueChange> ConfirmedChanges { get; } = new();

    /// <summary>
    /// Attribute changes that were not confirmed and will be retried.
    /// </summary>
    public List<PendingExportAttributeValueChange> RetryChanges { get; } = new();

    /// <summary>
    /// Attribute changes that exceeded max retries and are now Failed.
    /// </summary>
    public List<PendingExportAttributeValueChange> FailedChanges { get; } = new();

    /// <summary>
    /// Whether the PendingExport was deleted (all changes confirmed).
    /// </summary>
    public bool PendingExportDeleted { get; set; }

    /// <summary>
    /// The pending export to delete (for batched operations).
    /// Only set when PendingExportDeleted is true.
    /// </summary>
    public PendingExport? PendingExportToDelete { get; set; }

    /// <summary>
    /// The pending export to update (for batched operations).
    /// Only set when there are changes but the export should not be deleted.
    /// </summary>
    public PendingExport? PendingExportToUpdate { get; set; }

    /// <summary>
    /// True if any reconciliation was performed.
    /// </summary>
    public bool HasChanges => ConfirmedChanges.Count > 0 || RetryChanges.Count > 0 || FailedChanges.Count > 0;
}

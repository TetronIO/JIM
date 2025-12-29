using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// Result of an export execution run.
/// </summary>
public class ExportExecutionResult
{
    /// <summary>
    /// The Connected System ID this export was for.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The mode this export ran in (Preview Only or Preview and Sync).
    /// </summary>
    public SyncRunMode RunMode { get; set; }

    /// <summary>
    /// When the export execution started.
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When the export execution completed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Total number of pending exports that were processed.
    /// </summary>
    public int TotalPendingExports { get; set; }

    /// <summary>
    /// Number of exports that succeeded.
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of exports that failed.
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Number of exports that were deferred due to unresolved references.
    /// </summary>
    public int DeferredCount { get; set; }

    /// <summary>
    /// IDs of the pending exports that were processed.
    /// Use these IDs to fetch the actual PendingExport records for detailed information.
    /// Note: These records may be deleted after successful export, use ProcessedExportItems instead.
    /// </summary>
    public List<Guid> ProcessedPendingExportIds { get; set; } = [];

    /// <summary>
    /// Information about each processed export for activity tracking.
    /// This is captured before pending exports are deleted, allowing execution item creation.
    /// </summary>
    public List<ProcessedExportItem> ProcessedExportItems { get; set; } = [];

    /// <summary>
    /// DNs of containers (OUs) that were created during this export session.
    /// Used by JIM to auto-select newly created containers in the hierarchy.
    /// </summary>
    public List<string> CreatedContainerDns { get; set; } = [];
}

/// <summary>
/// Information about a processed export, captured before deletion for activity tracking.
/// </summary>
public class ProcessedExportItem
{
    /// <summary>
    /// The change type that was exported (Create, Update, Delete).
    /// </summary>
    public PendingExportChangeType ChangeType { get; set; }

    /// <summary>
    /// The Connected System Object that was exported (if available).
    /// </summary>
    public ConnectedSystemObject? ConnectedSystemObject { get; set; }

    /// <summary>
    /// Number of attribute value changes in this export.
    /// </summary>
    public int AttributeChangeCount { get; set; }

    /// <summary>
    /// Whether the export succeeded.
    /// </summary>
    public bool Succeeded { get; set; }

    /// <summary>
    /// Error message if the export failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Number of retry attempts if the export failed.
    /// </summary>
    public int ErrorCount { get; set; }
}

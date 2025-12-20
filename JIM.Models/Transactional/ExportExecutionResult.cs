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
    /// </summary>
    public List<Guid> ProcessedPendingExportIds { get; set; } = [];
}

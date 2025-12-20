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
    /// Preview information for each pending export.
    /// Always populated regardless of run mode.
    /// </summary>
    public List<ExportPreviewResult> Previews { get; set; } = new();
}

namespace JIM.Models.Transactional;

/// <summary>
/// Progress information for export execution.
/// </summary>
public class ExportProgressInfo
{
    /// <summary>
    /// Current phase of export execution.
    /// </summary>
    public ExportPhase Phase { get; set; }

    /// <summary>
    /// Total number of exports to process.
    /// </summary>
    public int TotalExports { get; set; }

    /// <summary>
    /// Number of exports processed so far.
    /// </summary>
    public int ProcessedExports { get; set; }

    /// <summary>
    /// Size of the current batch being processed.
    /// </summary>
    public int CurrentBatchSize { get; set; }

    /// <summary>
    /// Number of successful exports (only populated in Completed phase).
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of failed exports (only populated in Completed phase).
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Number of deferred exports (only populated in Completed phase).
    /// </summary>
    public int DeferredCount { get; set; }

    /// <summary>
    /// Human-readable progress message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public int ProgressPercentage => TotalExports > 0
        ? (int)((double)ProcessedExports / TotalExports * 100)
        : 0;
}

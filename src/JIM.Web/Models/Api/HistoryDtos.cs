namespace JIM.Web.Models.Api;

/// <summary>
/// Response for manual history cleanup operation.
/// </summary>
public class HistoryCleanupResponse
{
    /// <summary>
    /// Number of CSO change records deleted.
    /// </summary>
    public int CsoChangesDeleted { get; set; }

    /// <summary>
    /// Number of MVO change records deleted.
    /// </summary>
    public int MvoChangesDeleted { get; set; }

    /// <summary>
    /// Number of Activity records deleted.
    /// </summary>
    public int ActivitiesDeleted { get; set; }

    /// <summary>
    /// Oldest record timestamp that was deleted (if any records were deleted).
    /// </summary>
    public DateTime? OldestRecordDeleted { get; set; }

    /// <summary>
    /// Newest record timestamp that was deleted (if any records were deleted).
    /// </summary>
    public DateTime? NewestRecordDeleted { get; set; }

    /// <summary>
    /// Cutoff date used for this cleanup operation (records older than this were deleted).
    /// </summary>
    public DateTime CutoffDate { get; set; }

    /// <summary>
    /// Configured retention period in days.
    /// </summary>
    public int RetentionPeriodDays { get; set; }

    /// <summary>
    /// Maximum records deleted per type in this batch.
    /// </summary>
    public int BatchSize { get; set; }
}

/// <summary>
/// Response for connected system change history count query.
/// </summary>
public class HistoryCountResponse
{
    /// <summary>
    /// Connected system ID.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// Connected system name.
    /// </summary>
    public required string ConnectedSystemName { get; set; }

    /// <summary>
    /// Count of CSO change records for this connected system.
    /// </summary>
    public int ChangeRecordCount { get; set; }
}

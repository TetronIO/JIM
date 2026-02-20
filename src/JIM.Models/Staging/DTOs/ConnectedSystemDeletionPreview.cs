namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Preview of the impact of deleting a Connected System.
/// Generated before deletion to inform the administrator of what will be affected.
/// </summary>
public class ConnectedSystemDeletionPreview
{
    public int ConnectedSystemId { get; set; }
    public string ConnectedSystemName { get; set; } = null!;

    // Object counts
    public int ConnectedSystemObjectCount { get; set; }
    public int SyncRuleCount { get; set; }
    public int RunProfileCount { get; set; }
    public int PartitionCount { get; set; }
    public int ContainerCount { get; set; }
    public int PendingExportCount { get; set; }
    public int ActivityCount { get; set; }

    // MVO Impact
    public int JoinedMvoCount { get; set; }

    /// <summary>
    /// MVOs that have other CSO connections and won't be affected by deletion.
    /// </summary>
    public int MvosWithOtherConnectorsCount { get; set; }

    /// <summary>
    /// MVOs that may be deleted due to WhenLastConnectorDisconnected rule (no grace period).
    /// </summary>
    public int MvosWithDeletionRuleCount { get; set; }

    /// <summary>
    /// MVOs that will be scheduled for deletion with a grace period.
    /// </summary>
    public int MvosWithGracePeriodCount { get; set; }

    // Warnings for the administrator
    public List<string> Warnings { get; set; } = new();

    // Execution details
    public TimeSpan EstimatedDeletionTime { get; set; }

    /// <summary>
    /// True if the deletion will be queued as a background job (large system).
    /// False if it will execute synchronously.
    /// </summary>
    public bool WillRunAsBackgroundJob { get; set; }

    /// <summary>
    /// True if a sync operation is currently running for this system.
    /// If so, deletion will be queued to run after the sync completes.
    /// </summary>
    public bool HasRunningSyncOperation { get; set; }
}

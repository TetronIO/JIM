// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Preview of the impact of deleting a Connected System.
/// Generated before deletion to inform the administrator of what will be affected.
/// </summary>
public class ConnectedSystemDeletionPreview
{
    /// <summary>
    /// The ID of the Connected System.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The name of the Connected System.
    /// </summary>
    public string ConnectedSystemName { get; set; } = null!;

    // Object counts

    /// <summary>
    /// How many Connected System Objects the deletion removes.
    /// </summary>
    public int ConnectedSystemObjectCount { get; set; }

    /// <summary>
    /// How many Synchronisation Rules the deletion removes.
    /// </summary>
    public int SyncRuleCount { get; set; }

    /// <summary>
    /// How many Run Profiles the deletion removes.
    /// </summary>
    public int RunProfileCount { get; set; }

    /// <summary>
    /// How many partitions the deletion removes.
    /// </summary>
    public int PartitionCount { get; set; }

    /// <summary>
    /// How many containers the deletion removes.
    /// </summary>
    public int ContainerCount { get; set; }

    /// <summary>
    /// How many Pending Exports the deletion discards.
    /// </summary>
    public int PendingExportCount { get; set; }

    /// <summary>
    /// How many Activities name the Connected System. They are kept, as history.
    /// </summary>
    public int ActivityCount { get; set; }

    // MVO Impact

    /// <summary>
    /// How many Metaverse Objects are joined to one of its Connected System Objects, and so are disconnected by the
    /// deletion.
    /// </summary>
    public int JoinedMvoCount { get; set; }

    /// <summary>
    /// Of those, how many are also joined in another Connected System, so stay joined there. They can still lose
    /// the values this one contributed, and some can still be marked for deletion; MvosWithDeletionRuleCount counts
    /// those.
    /// </summary>
    public int MvosWithOtherConnectorsCount { get; set; }

    /// <summary>
    /// How many Metaverse Objects their Metaverse Object Type's deletion rules will mark for deletion when the
    /// Connected System is deleted.
    /// </summary>
    /// <remarks>
    /// Computed with the same mode-aware predicate execution uses (WhenLastConnectorDisconnected, plus
    /// WhenAuthoritativeSourceDisconnected under its configured trigger mode, #119), so the preview always agrees
    /// with what deletion marks.
    /// </remarks>
    public int MvosWithDeletionRuleCount { get; set; }

    /// <summary>
    /// Of the objects MvosWithDeletionRuleCount counts, how many have a grace period, so are deleted when it ends
    /// rather than at once.
    /// </summary>
    public int MvosWithGracePeriodCount { get; set; }

    /// <summary>
    /// How many Metaverse attribute values the Connected System's Synchronisation Rules contributed: the values the
    /// deletion recalls, or hands to another contributor.
    /// </summary>
    /// <remarks>
    /// Counted by provenance (#809).
    /// </remarks>
    public int ContributedValueCount { get; set; }

    /// <summary>
    /// How many Metaverse Objects hold at least one of those values. Not the same as JoinedMvoCount: an object can
    /// be joined without holding a contributed value.
    /// </summary>
    public int ContributedValueObjectCount { get; set; }

    // Warnings for the administrator

    /// <summary>
    /// Anything that changes how the deletion runs, such as a synchronisation already running, which delays it
    /// until that finishes.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    // Execution details

    /// <summary>
    /// Roughly how long the deletion takes.
    /// </summary>
    public TimeSpan EstimatedDeletionTime { get; set; }

    /// <summary>
    /// True if the deletion will be queued as a background job (large system).
    /// False if it will execute synchronously.
    /// </summary>
    public bool WillRunAsBackgroundJob { get; set; }

    /// <summary>
    /// Whether a synchronisation is running on the Connected System, in which case the deletion waits for it to
    /// finish.
    /// </summary>
    public bool HasRunningSyncOperation { get; set; }
}

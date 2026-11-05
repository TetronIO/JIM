// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Result of clearing all objects from a Connected System, including counts of removed items.
/// </summary>
public class ClearConnectedSystemResult
{
    /// <summary>
    /// Count of Pending Exports removed during the clear operation.
    /// </summary>
    public int PendingExportsRemoved { get; set; }

    /// <summary>
    /// Count of Connected System Objects removed during the clear operation.
    /// </summary>
    public int ConnectedSystemObjectsRemoved { get; set; }

    /// <summary>
    /// Count of <see cref="ConnectorSpaceClearJoinRecord"/> rows written for this clear (#1605): one per
    /// Connected System Object that was joined to a Metaverse Object at the moment of the clear, so the
    /// post-clear reconciliation sweep has durable evidence of who to expect back. Zero when the clear
    /// removed no joined objects, and always zero when called from Connected System deletion rather than
    /// a Connector Space clear (recording joins is pointless there; the system is going away regardless).
    /// </summary>
    public int JoinRecordsWritten { get; set; }

    /// <summary>
    /// Whether the clear removed the Delta Import watermark the Connector keeps in the Connected System's persisted
    /// connector data (#2724), so the next Delta Import reads every object rather than only the changes after it.
    /// False when there was none to remove, and always false when called from Connected System deletion.
    /// </summary>
    public bool ImportWatermarkRemoved { get; set; }

    /// <summary>
    /// Count of Partition Delta Import watermarks the clear removed (#2715), so each Partition's next Delta Import reads
    /// every object in it.
    /// </summary>
    public int PartitionWatermarksRemoved { get; set; }

    /// <summary>
    /// Describes the Delta Import starting points the clear removed, for the clear's Activity, so the reset is visible to
    /// whoever reads it. Null when it removed none.
    /// </summary>
    public string? DescribeDeltaImportReset()
    {
        var partitions = PartitionWatermarksRemoved == 1 ? "1 partition" : $"{PartitionWatermarksRemoved:N0} partitions";
        return (ImportWatermarkRemoved, PartitionWatermarksRemoved > 0) switch
        {
            (true, true) => $"Removed the Connected System's Delta Import watermark and those of {partitions}, so the next Delta Import reads every object, as a Full Import does.",
            (true, false) => "Removed the Connected System's Delta Import watermark, so the next Delta Import reads every object, as a Full Import does.",
            (false, true) => $"Removed the Delta Import watermarks of {partitions}, so the next Delta Import reads every object in them, as a Full Import does.",
            _ => null
        };
    }
}

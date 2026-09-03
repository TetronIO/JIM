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
}

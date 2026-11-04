// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The Delta Import watermark JIM holds for one Partition of a Connected System: the point the Partition's next Delta
/// Import reads changes after. A Partition with no watermark has no row, and its next Delta Import reads every object
/// in it.
/// </summary>
/// <remarks>
/// #2715: each Partition keeps its own watermark, so a Run Profile that reads one Partition moves only that Partition's
/// starting point. JIM never interprets <see cref="Value"/>; it hands the value back to the Connector that wrote it.
/// <para>
/// Deliberately without navigation properties: that keeps the rows out of configuration snapshots, out of the graph a
/// Connected System save marks for update, and out of the OpenAPI property walk. The rows are runtime state written by
/// imports, not configuration.
/// </para>
/// </remarks>
public class ConnectedSystemPartitionWatermark
{
    /// <summary>
    /// The Partition this watermark belongs to. A Partition has at most one watermark.
    /// </summary>
    public int PartitionId { get; set; }

    /// <summary>
    /// The Connected System the Partition belongs to.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The watermark, in the form the Connected System's Connector keeps it.
    /// </summary>
    public string Value { get; set; } = null!;

    /// <summary>
    /// When an import last recorded this watermark (UTC), whether or not the value changed: the last time an import
    /// that read this Partition completed.
    /// </summary>
    public DateTime RecordedAt { get; set; }
}

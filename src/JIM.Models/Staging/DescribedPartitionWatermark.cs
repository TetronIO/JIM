// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The Delta Import watermark JIM holds for one Partition of a Connected System, with what its Connector reads it as:
/// where the Partition's next Delta Import reads changes after, and when an import last recorded it.
/// </summary>
/// <remarks>
/// #2715, Decision 3: read-only, for the portal, the REST API and PowerShell. JIM never parses <see cref="Value"/>;
/// <see cref="Description"/> is the Connector's own reading of it.
/// </remarks>
public class DescribedPartitionWatermark
{
    /// <summary>
    /// The Partition the watermark belongs to.
    /// </summary>
    public int PartitionId { get; init; }

    /// <summary>
    /// The watermark, in the form the Connected System's Connector keeps it.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// When an import last recorded the watermark (UTC), whether or not the value changed.
    /// </summary>
    public DateTime RecordedAt { get; init; }

    /// <summary>
    /// The Connector's reading of <see cref="Value"/>, or null when it finds no watermark in it (a value it cannot
    /// parse); only the raw value is known then.
    /// </summary>
    public ImportWatermarkDescription? Description { get; init; }
}

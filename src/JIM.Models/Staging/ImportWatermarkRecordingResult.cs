// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What recording an import's watermarks changed, for the import's end-of-run summary.
/// </summary>
public class ImportWatermarkRecordingResult
{
    /// <summary>
    /// The Partition watermarks written, new or replaced. A Partition removed from the Connected System while the
    /// import ran is not written, and is not counted.
    /// </summary>
    public int PartitionWatermarksRecorded { get; init; }

    /// <summary>
    /// The Partition watermarks removed because the import cleared them.
    /// </summary>
    public int PartitionWatermarksCleared { get; init; }

    /// <summary>
    /// The watermarks removed because their Partition is no longer selected.
    /// </summary>
    public int DeselectedPartitionWatermarksRemoved { get; init; }
}

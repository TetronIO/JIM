// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// What an import's Activity says about one Partition's Delta Import watermark on one side of the run (#2715).
/// </summary>
public enum ImportPartitionWatermarkState
{
    /// <summary>A value is recorded, and is shown.</summary>
    Recorded,

    /// <summary>Before the run: the Partition had no watermark, so the run read every object in it.</summary>
    None,

    /// <summary>After the run: it recorded the value it started from again, or left the Partition out, which keeps it.</summary>
    Unchanged,

    /// <summary>After the run: it cleared the Partition's watermark, so the next Delta Import reads every object in it.</summary>
    Cleared,

    /// <summary>After the run: it recorded no Partition's watermark at all, so every one stays where the run found it.</summary>
    NotRecorded
}

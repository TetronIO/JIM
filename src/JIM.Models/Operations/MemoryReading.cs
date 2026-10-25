// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Operations;

/// <summary>
/// One reading of a JIM process's memory and of the memory it can draw on: what the process holds, and how much of
/// the total its container (or, without a container limit, its host) still has available.
/// </summary>
public sealed class MemoryReading
{
    /// <summary>The memory the process holds (its working set), in bytes.</summary>
    public required long ProcessBytes { get; init; }

    /// <summary>
    /// The memory the process can draw on in all, in bytes: its container's limit when one is set and the host can
    /// honour it, otherwise the host's physical memory. Null when it could not be read.
    /// </summary>
    public long? TotalBytes { get; init; }

    /// <summary>How much of <see cref="TotalBytes"/> is still available, in bytes, counting reclaimable file cache. Null when it could not be read.</summary>
    public long? AvailableBytes { get; init; }

    /// <summary>How much of <see cref="TotalBytes"/> is in use, in bytes, by this process and everything else; null when either figure is unknown.</summary>
    public long? UsedBytes => TotalBytes is { } total && AvailableBytes is { } available ? total - available : null;

    /// <summary>The share of <see cref="TotalBytes"/> in use, from 0 to 1; null when unknown.</summary>
    public double? UsedFraction => UsedBytes is { } used && TotalBytes is > 0 and var total ? (double)used / total : null;
}

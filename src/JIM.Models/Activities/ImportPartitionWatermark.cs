// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// One Partition's Delta Import watermark as an import read or recorded it.
/// </summary>
/// <remarks>
/// #2715: an import Activity records these per Partition. The Partition's name is captured when the import runs, so the
/// record still reads correctly after the Partition is renamed, or removed from its Connected System; an Activity is
/// an audit record and outlives both.
/// </remarks>
public class ImportPartitionWatermark
{
    /// <summary>
    /// The Partition's identifier, as it was when the import ran.
    /// </summary>
    public int PartitionId { get; init; }

    /// <summary>
    /// The Partition's name, as it was when the import ran.
    /// </summary>
    public string PartitionName { get; init; } = null!;

    /// <summary>
    /// The watermark, in the form the Connected System's Connector keeps it. Null where the Partition had none: before
    /// an import, it read every object in the Partition; after one, the import cleared it.
    /// </summary>
    public string? Value { get; init; }
}

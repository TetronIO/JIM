// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Web.Models.Api;

/// <summary>
/// One partition's Delta Import watermark, as an import started from it or recorded it.
/// </summary>
public class ImportPartitionWatermarkDto
{
    /// <summary>
    /// The ID of the partition, as it was when the import ran.
    /// </summary>
    public int PartitionId { get; set; }

    /// <summary>
    /// The name of the partition, as it was when the import ran, so the record still reads correctly after the
    /// partition is renamed or removed.
    /// </summary>
    public string PartitionName { get; set; } = null!;

    /// <summary>
    /// The watermark, exactly as the Connector keeps it. Empty where the partition had none: where the import started,
    /// it read every object in the partition; where it recorded, it cleared the partition's watermark.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Creates the DTO from one recorded Partition watermark.
    /// </summary>
    public static ImportPartitionWatermarkDto FromEntity(ImportPartitionWatermark watermark) => new()
    {
        PartitionId = watermark.PartitionId,
        PartitionName = watermark.PartitionName,
        Value = watermark.Value
    };
}

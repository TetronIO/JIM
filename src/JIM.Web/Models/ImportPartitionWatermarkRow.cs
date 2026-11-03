// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// One Partition's Delta Import watermark before and after an import, as the Activity's Connector state shows it.
/// </summary>
/// <param name="PartitionId">The Partition's id, as the Activity recorded it.</param>
/// <param name="PartitionName">The Partition's name, as the Activity recorded it when the import ran.</param>
/// <param name="BeforeState">What the Partition's watermark was when the import started.</param>
/// <param name="Before">The watermark the import started from, when <paramref name="BeforeState"/> is Recorded.</param>
/// <param name="AfterState">What the import did with the Partition's watermark.</param>
/// <param name="After">The watermark the import recorded, when <paramref name="AfterState"/> is Recorded.</param>
public sealed record ImportPartitionWatermarkRow(
    int PartitionId,
    string PartitionName,
    ImportPartitionWatermarkState BeforeState,
    string? Before,
    ImportPartitionWatermarkState AfterState,
    string? After);

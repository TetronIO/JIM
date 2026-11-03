// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column lists used by the raw-SQL upsert that records Partition watermarks
/// (ConnectedSystemRepository.RecordImportWatermarksAsync, #2715). The writer MUST write values in exactly list order.
/// BulkInsertColumnCompletenessTests asserts the insert list matches the EF model's mapped columns exactly, and that
/// every column has a conscious home in the update list or the documented exclusion list, so a migration cannot
/// silently leave the writer behind.
/// </summary>
internal static class PartitionWatermarkBulkColumns
{
    /// <summary>
    /// Insert columns for the ConnectedSystemPartitionWatermarks table.
    /// </summary>
    internal static readonly string[] ConnectedSystemPartitionWatermarks =
    [
        "PartitionId", "ConnectedSystemId", "Value", "RecordedAt"
    ];

    /// <summary>
    /// Update columns for the upsert: what a later import of the same Partition changes. RecordedAt is rewritten even
    /// when the value is unchanged, so it reads as when an import last read the Partition.
    /// </summary>
    internal static readonly string[] ConnectedSystemPartitionWatermarksUpdate =
    [
        "Value", "RecordedAt"
    ];

    /// <summary>
    /// Columns deliberately excluded from the update list: PartitionId is the key the upsert conflicts on, and a
    /// Partition never changes Connected System, so ConnectedSystemId never needs rewriting.
    /// </summary>
    internal static readonly string[] ConnectedSystemPartitionWatermarksUpdateExclusions =
    [
        "PartitionId", "ConnectedSystemId"
    ];
}

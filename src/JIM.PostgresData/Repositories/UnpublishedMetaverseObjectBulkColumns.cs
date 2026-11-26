// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column list of the COPY that records a synchronisation page's projected Metaverse
/// Objects as unpublished (#2848), written with the objects by <see cref="SyncRepository.CreateMetaverseObjectsBulkAsync"/>.
/// The writer MUST write values in exactly list order. BulkInsertColumnCompletenessTests asserts the list matches the EF
/// model's mapped columns exactly. Insert only: a record is deleted when its object is published, never updated.
/// </summary>
internal static class UnpublishedMetaverseObjectBulkColumns
{
    /// <summary>
    /// Insert columns for the UnpublishedMetaverseObjects table.
    /// </summary>
    internal static readonly string[] UnpublishedMetaverseObjects =
    [
        "MetaverseObjectId", "ActivityId", "Created"
    ];
}

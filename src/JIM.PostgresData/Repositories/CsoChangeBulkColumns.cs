// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column lists used by the raw-SQL COPY writers that persist
/// Connected System Object change history (SyncRepository.RpeiOperations.cs). Every writer for a
/// table MUST write values in exactly this order. BulkInsertColumnCompletenessTests asserts each
/// list matches the EF model's mapped columns exactly, so adding a column without extending the
/// writers fails the build's test run rather than silently defaulting it for every bulk-written
/// audit row.
/// </summary>
internal static class CsoChangeBulkColumns
{
    /// <summary>
    /// Insert columns for the ConnectedSystemObjectChanges table.
    /// </summary>
    internal static readonly string[] ConnectedSystemObjectChanges =
    [
        "Id", "ActivityRunProfileExecutionItemId", "ConnectedSystemId", "ConnectedSystemObjectId",
        "ChangeTime", "ChangeType", "InitiatedByType", "InitiatedById", "InitiatedByName",
        "DeletedObjectTypeId", "DeletedObjectExternalIdAttributeValueId", "DeletedObjectExternalId", "DeletedObjectDisplayName"
    ];

    /// <summary>
    /// Insert columns for the ConnectedSystemObjectChangeAttributes table.
    /// </summary>
    internal static readonly string[] ConnectedSystemObjectChangeAttributes =
    [
        "Id", "ConnectedSystemChangeId", "AttributeId", "AttributeName", "AttributeType"
    ];

    /// <summary>
    /// Insert columns for the ConnectedSystemObjectChangeAttributeValues table.
    /// </summary>
    internal static readonly string[] ConnectedSystemObjectChangeAttributeValues =
    [
        "Id", "ConnectedSystemObjectChangeAttributeId", "ValueChangeType",
        "StringValue", "DateTimeValue", "IntValue", "LongValue",
        "DecimalValue", "ByteValueLength", "GuidValue", "BoolValue", "ReferenceValueId",
        "IsPendingExportStub"
    ];
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column lists used by the raw-SQL write paths that persist
/// Metaverse Object change history: the bulk COPY writers in SyncRepository.MvoChangeOperations.cs
/// and the direct (change-tracker-bypassing) INSERTs in MetaverseRepository. Every writer for a
/// table MUST write values in exactly this order.
///
/// These lists exist because raw SQL bypasses the EF Core model: when a migration adds a column,
/// EF-tracked writes pick it up automatically but these statements silently drop it, defaulting
/// the column for every row (this is how Decimal audit values were lost from the bulk change path,
/// and how deletion records lost Long Number, Decimal and FK-only reference values, #1046/#871).
/// BulkInsertColumnCompletenessTests asserts each list matches the EF model's mapped columns
/// exactly, so adding a column without extending every writer fails the build's test run rather
/// than corrupting audit records at customer sites.
/// </summary>
internal static class MvoChangeBulkColumns
{
    /// <summary>
    /// Insert columns for the MetaverseObjectChanges table.
    /// </summary>
    internal static readonly string[] MetaverseObjectChanges =
    [
        "Id", "MetaverseObjectId", "ActivityRunProfileExecutionItemId",
        "ChangeTime", "ChangeType", "ChangeInitiatorType",
        "InitiatedByType", "InitiatedById", "InitiatedByName",
        "SyncRuleId", "SyncRuleName",
        "DeletedObjectTypeId", "DeletedMetaverseObjectId", "DeletedObjectDisplayName"
    ];

    /// <summary>
    /// Insert columns for the MetaverseObjectChangeAttributes table.
    /// </summary>
    internal static readonly string[] MetaverseObjectChangeAttributes =
    [
        "Id", "MetaverseObjectChangeId", "AttributeId", "AttributeName", "AttributeType"
    ];

    /// <summary>
    /// Insert columns for the MetaverseObjectChangeAttributeValues table.
    /// </summary>
    internal static readonly string[] MetaverseObjectChangeAttributeValues =
    [
        "Id", "MetaverseObjectChangeAttributeId", "ValueChangeType",
        "StringValue", "DateTimeValue", "IntValue", "LongValue", "DecimalValue",
        "ByteValueLength", "GuidValue", "BoolValue", "ReferenceValueId"
    ];
}

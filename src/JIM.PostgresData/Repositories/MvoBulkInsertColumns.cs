// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column lists used by the raw-SQL bulk insert paths
/// (COPY binary import and chunked parameterised INSERT) that persist newly projected
/// Metaverse Objects and their attribute values. Both writers for a table MUST write values
/// in exactly this order.
///
/// These lists exist because raw SQL bypasses the EF Core model: when a migration adds a column,
/// EF-tracked writes pick it up automatically but these inserts silently drop it, defaulting the
/// column for every bulk-written row (this is how newly projected Metaverse Object attribute
/// values lost their ContributedBySyncRuleId provenance, breaking Attribute Priority resolution
/// against them, #91). BulkInsertColumnCompletenessTests asserts each list matches the EF model's
/// mapped columns exactly, so adding a column without extending the bulk writers fails the build's
/// test run rather than corrupting data at customer sites.
/// </summary>
internal static class MvoBulkInsertColumns
{
    /// <summary>
    /// Insert columns for the MetaverseObjects table. Excludes the store-generated xmin
    /// concurrency token, which PostgreSQL assigns automatically.
    /// </summary>
    internal static readonly string[] MetaverseObjects =
    [
        "Id", "Created", "LastUpdated", "TypeId", "Status", "Origin",
        "LastConnectorDisconnectedDate", "DeletionInitiatedByType",
        "DeletionInitiatedById", "DeletionInitiatedByName", "CachedDisplayName",
        "ScopeReviewPending", "LastScopeEvaluatedAt"
    ];

    /// <summary>
    /// Update columns for the MetaverseObjects table: the mutable subset written by the raw-SQL
    /// synchronisation update path (<see cref="SyncRepository.UpdateMetaverseObjectsBulkAsync"/>).
    /// This is <see cref="MetaverseObjects"/> minus the immutable primary key (Id) and the create-only
    /// Created timestamp (and, as with the insert list, the store-generated xmin concurrency token).
    /// BulkInsertColumnCompletenessTests keeps it in lockstep with <see cref="MetaverseObjects"/> so a
    /// migration that adds a mutable column fails the build's test run rather than silently dropping it
    /// from every bulk update.
    /// </summary>
    internal static readonly string[] MetaverseObjectsUpdate =
    [
        "LastUpdated", "TypeId", "Status", "Origin",
        "LastConnectorDisconnectedDate", "DeletionInitiatedByType",
        "DeletionInitiatedById", "DeletionInitiatedByName", "CachedDisplayName",
        "ScopeReviewPending", "LastScopeEvaluatedAt"
    ];

    /// <summary>
    /// Insert columns for the MetaverseObjectAttributeValues table.
    /// </summary>
    internal static readonly string[] MetaverseObjectAttributeValues =
    [
        "Id", "MetaverseObjectId", "AttributeId", "StringValue",
        "DateTimeValue", "IntValue", "LongValue", "ByteValue",
        "GuidValue", "BoolValue", "ReferenceValueId",
        "UnresolvedReferenceValueId", "ContributedBySystemId",
        "ContributedBySyncRuleId", "NullValue"
    ];

    /// <summary>
    /// Renders a column list as quoted, comma-separated SQL identifiers, e.g. "Id", "Created".
    /// </summary>
    internal static string ToQuotedList(string[] columns) =>
        string.Join(", ", columns.Select(c => $"\"{c}\""));
}

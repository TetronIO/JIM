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
        "DeletionInitiatedById", "DeletionInitiatedByName",
        "DeletionTriggeredBySystemId", "DeletionTriggeredBySystemName",
        "DeletionPolicySnapshotJson", "CachedDisplayName",
        "ScopeReviewPending", "LastScopeEvaluatedAt"
    ];

    /// <summary>
    /// Update columns for the MetaverseObjects table: the mutable subset written by the raw-SQL
    /// synchronisation update path (<see cref="SyncRepository.UpdateMetaverseObjectsBulkAsync"/>).
    /// This is <see cref="MetaverseObjects"/> minus <see cref="MetaverseObjectsUpdateExclusions"/> (and, as
    /// with the insert list, the store-generated xmin concurrency token). Excluded beyond the immutable
    /// primary key (Id) and the create-only Created timestamp are ScopeReviewPending and
    /// LastScopeEvaluatedAt: other writers set them through dedicated statements while a synchronisation
    /// holds the object as it loaded it (an export Synchronisation Rule's configuration change flags every
    /// object of its type, #1925; the Temporal Scope Reconciler flags and stamps on its own schedule, #892),
    /// and the synchronisation's own drain clears the flag through a dedicated statement too. Writing them
    /// here from the loaded entity would put back the value it was loaded with, silently losing a review the
    /// change asked for. The Connected System Object update list excludes its scope columns for the same reason.
    /// BulkInsertColumnCompletenessTests keeps the two lists in lockstep with <see cref="MetaverseObjects"/>
    /// so a migration that adds a mutable column must be placed in one of them consciously, rather than
    /// being silently dropped from every bulk update.
    /// </summary>
    internal static readonly string[] MetaverseObjectsUpdate =
    [
        "LastUpdated", "TypeId", "Status", "Origin",
        "LastConnectorDisconnectedDate", "DeletionInitiatedByType",
        "DeletionInitiatedById", "DeletionInitiatedByName",
        "DeletionTriggeredBySystemId", "DeletionTriggeredBySystemName",
        "DeletionPolicySnapshotJson", "CachedDisplayName"
    ];

    /// <summary>
    /// Columns deliberately excluded from <see cref="MetaverseObjectsUpdate"/>; see its documentation for the
    /// rationale per column.
    /// </summary>
    internal static readonly string[] MetaverseObjectsUpdateExclusions =
    [
        "Id", "Created", "ScopeReviewPending", "LastScopeEvaluatedAt"
    ];

    /// <summary>
    /// Insert columns for the MetaverseObjectAttributeValues table.
    /// </summary>
    internal static readonly string[] MetaverseObjectAttributeValues =
    [
        "Id", "MetaverseObjectId", "AttributeId", "StringValue",
        "DateTimeValue", "IntValue", "LongValue", "DecimalValue", "ByteValue",
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

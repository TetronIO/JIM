// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column lists used by the raw-SQL bulk paths that persist
/// Connected System Objects and their attribute values (the COPY and parameterised INSERT writers
/// in SyncRepository.CsOperations.cs and ConnectedSystemRepository, and the bulk UPDATE in
/// ConnectedSystemRepository). Every writer for a table MUST write values in exactly this order.
///
/// These lists exist because raw SQL bypasses the EF Core model: when a migration adds a column,
/// EF-tracked writes pick it up automatically but these statements silently drop it (this is how
/// PartitionId, assigned by the import processor for partition-scoped deletion detection, was
/// silently discarded by both the insert and update paths, leaving objects invisible to their
/// partition's obsoletion sweep). BulkInsertColumnCompletenessTests asserts each list matches the
/// EF model's mapped columns exactly, so adding a column without extending every writer fails the
/// build's test run rather than corrupting data at customer sites.
/// </summary>
internal static class CsoBulkColumns
{
    /// <summary>
    /// Insert columns for the ConnectedSystemObjects table.
    /// </summary>
    internal static readonly string[] ConnectedSystemObjects =
    [
        "Id", "ConnectedSystemId", "Created", "LastUpdated", "TypeId",
        "ExternalIdAttributeId", "SecondaryExternalIdAttributeId",
        "Status", "MetaverseObjectId", "JoinType", "DateJoined",
        "PartitionId", "ScopeReviewPending", "LastScopeEvaluatedAt"
    ];

    /// <summary>
    /// Update columns for the ConnectedSystemObjects table: the mutable subset written by the raw
    /// bulk update path (<see cref="ConnectedSystemRepository"/>). Excluded beyond the immutable
    /// identity/creation columns (Id, ConnectedSystemId, Created, TypeId) are ScopeReviewPending and
    /// LastScopeEvaluatedAt: both have dedicated persistence statements on the scope-evaluation path,
    /// and writing them from entities held across a long import flush could overwrite a concurrent
    /// scope evaluation's newer values with stale in-memory state.
    /// BulkInsertColumnCompletenessTests keeps this list in lockstep with
    /// <see cref="ConnectedSystemObjects"/> so a migration that adds a mutable column is a conscious
    /// decision here, not a silent omission.
    /// </summary>
    internal static readonly string[] ConnectedSystemObjectsUpdate =
    [
        "LastUpdated", "Status", "MetaverseObjectId", "JoinType", "DateJoined",
        "ExternalIdAttributeId", "SecondaryExternalIdAttributeId", "PartitionId"
    ];

    /// <summary>
    /// Columns deliberately excluded from <see cref="ConnectedSystemObjectsUpdate"/>; see its
    /// documentation for the rationale per column.
    /// </summary>
    internal static readonly string[] ConnectedSystemObjectsUpdateExclusions =
    [
        "Id", "ConnectedSystemId", "Created", "TypeId", "ScopeReviewPending", "LastScopeEvaluatedAt"
    ];

    /// <summary>
    /// Insert columns for the ConnectedSystemObjectAttributeValues table.
    /// </summary>
    internal static readonly string[] ConnectedSystemObjectAttributeValues =
    [
        "Id", "ConnectedSystemObjectId", "AttributeId", "StringValue",
        "DateTimeValue", "IntValue", "LongValue", "DecimalValue", "ByteValue",
        "GuidValue", "BoolValue", "ReferenceValueId", "UnresolvedReferenceValue"
    ];
}

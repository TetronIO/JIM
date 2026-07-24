// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column lists used by the raw-SQL paths that persist Activity
/// Run Profile Execution Items and their sync outcomes (SyncRepository.RpeiOperations.cs). Every
/// writer for a table MUST write values in exactly this order. BulkInsertColumnCompletenessTests
/// asserts the insert lists match the EF model's mapped columns exactly, and that every column has
/// a conscious home in either the update list or the documented exclusion list, so a migration
/// cannot silently leave a raw writer behind (this is how ErrorStackTrace was dropped from the
/// bulk field update while its two sibling error columns were written).
/// </summary>
internal static class RpeiBulkColumns
{
    /// <summary>
    /// Insert columns for the ActivityRunProfileExecutionItems table.
    /// </summary>
    internal static readonly string[] ActivityRunProfileExecutionItems =
    [
        "Id", "ActivityId", "ObjectChangeType", "NoChangeReason",
        "ConnectedSystemObjectId", "ExternalIdSnapshot", "DisplayNameSnapshot",
        "ObjectTypeSnapshot", "ErrorType", "ErrorMessage", "ErrorStackTrace",
        "AttributeFlowCount", "OutcomeSummary", "PendingExportId"
    ];

    /// <summary>
    /// Update columns for the bulk field update (BulkUpdateRpeiFieldsRawAsync): the outcome and
    /// error fields mutated on an already-persisted RPEI. The three error columns are co-mutated
    /// at every worker error site and must always travel together.
    /// </summary>
    internal static readonly string[] ActivityRunProfileExecutionItemsUpdate =
    [
        "OutcomeSummary", "ErrorType", "ErrorMessage", "ErrorStackTrace", "AttributeFlowCount"
    ];

    /// <summary>
    /// Columns deliberately excluded from <see cref="ActivityRunProfileExecutionItemsUpdate"/>:
    /// the identity, snapshots and change classification are immutable once the RPEI is inserted.
    /// </summary>
    internal static readonly string[] ActivityRunProfileExecutionItemsUpdateExclusions =
    [
        "Id", "ActivityId", "ObjectChangeType", "NoChangeReason", "ConnectedSystemObjectId",
        "ExternalIdSnapshot", "DisplayNameSnapshot", "ObjectTypeSnapshot", "PendingExportId"
    ];

    /// <summary>
    /// Insert columns for the ActivityRunProfileExecutionItemSyncOutcomes table.
    /// </summary>
    internal static readonly string[] ActivityRunProfileExecutionItemSyncOutcomes =
    [
        "Id", "ActivityRunProfileExecutionItemId", "ParentSyncOutcomeId",
        "OutcomeType", "TargetEntityId", "TargetEntityDescription",
        "DetailCount", "DetailMessage", "Ordinal", "ConnectedSystemObjectChangeId",
        "SyncRuleId", "SyncRuleName"
    ];
}

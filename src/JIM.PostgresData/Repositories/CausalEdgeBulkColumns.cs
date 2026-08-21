// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column list used by the raw-SQL path that persists causal
/// edges (#1223). The writer MUST write values in exactly this order.
/// BulkInsertColumnCompletenessTests asserts the list matches the EF model's mapped columns exactly,
/// so a migration cannot silently leave the writer behind.
///
/// There is no update list: causal edges are append-only. An edge records that one thing caused
/// another at a moment in time, which is not a fact that can later change; if a cascade is
/// re-evaluated it produces new edges rather than amending old ones.
/// </summary>
internal static class CausalEdgeBulkColumns
{
    /// <summary>
    /// Insert columns for the CausalEdges table.
    /// </summary>
    internal static readonly string[] CausalEdges =
    [
        "Id",
        "EffectRunProfileExecutionItemId", "EffectSyncOutcomeId",
        "CauseRunProfileExecutionItemId", "CauseSyncOutcomeId",
        "CauseMetaverseObjectId", "CauseConnectedSystemObjectId", "CausePendingExportId", "CauseDisplayName",
        "CauseObjectTypeName", "CauseObjectTypePluralName", "EffectAttributeName",
        "EdgeType", "ReasonCode",
        "ConnectedSystemId", "ConnectedSystemName",
        "SyncRuleId", "SyncRuleName",
        "Created"
    ];
}

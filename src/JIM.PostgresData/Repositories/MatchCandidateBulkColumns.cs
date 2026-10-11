// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column list used by the raw-SQL path that persists ambiguous match candidates
/// (#2052). The writer MUST write values in exactly this order. BulkInsertColumnCompletenessTests asserts the list
/// matches the EF model's mapped columns exactly, so a migration cannot silently leave the writer behind.
///
/// There is no update list: a candidate records what one ambiguous match found at one moment, which never changes.
/// </summary>
internal static class MatchCandidateBulkColumns
{
    /// <summary>
    /// Insert columns for the ActivityRunProfileExecutionItemMatchCandidates table.
    /// </summary>
    internal static readonly string[] MatchCandidates =
    [
        "Id", "ActivityRunProfileExecutionItemId",
        "CandidateKind", "CandidateId", "DisplayNameSnapshot", "TypeNameSnapshot",
        "ConnectedSystemId", "ConnectedSystemNameSnapshot",
        "MatchedValue", "ObjectMatchingRuleId", "ObjectMatchingRuleDescriptionSnapshot",
        "Ordinal", "CandidateTotal"
    ];
}

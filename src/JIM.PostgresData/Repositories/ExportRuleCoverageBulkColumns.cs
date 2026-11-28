// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column list of the raw-SQL write to the export rule coverage record (#2826): the
/// <c>INSERT ... SELECT ... FROM unnest(...)</c> in <c>SyncRepository.AddExportRuleCoveragesAsync</c>, whose <c>SELECT</c>
/// list MUST follow this order. <c>BulkInsertColumnCompletenessTests</c> asserts the list matches the EF model's mapped
/// columns, so a migration that adds a column fails the unit pass until the writer is extended.
/// </summary>
internal static class ExportRuleCoverageBulkColumns
{
    /// <summary>
    /// Insert columns for the ExportRuleCoverages table. There is no update list: a row is written once and deleted when
    /// the coverage ends.
    /// </summary>
    internal static readonly string[] ExportRuleCoverages =
    [
        "ConnectedSystemObjectId", "SyncRuleId", "Recorded"
    ];
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Utility;

/// <summary>
/// What one change record's value rows amount to, read as <see cref="ChangeValueRowQuery"/> with
/// <see cref="ChangeValueRowQuery.Normalise"/> reads them (#2498): how many are Sets, Adds and Removes, and the one
/// Synchronisation Rule every row that names a rule names, if there is exactly one.
/// </summary>
public sealed record ChangeValueSummary(Guid ChangeId, int SetCount, int AddCount, int RemoveCount, int? AgreedSyncRuleId, string? AgreedSyncRuleName)
{
    /// <summary>How many value rows the change has in all.</summary>
    public int RowCount => SetCount + AddCount + RemoveCount;
}

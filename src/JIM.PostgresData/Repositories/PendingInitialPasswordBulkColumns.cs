// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column list used by the raw-SQL path that stages initial passwords
/// (SyncRepository.CsOperations.cs). The writer beside it MUST write values in exactly list order.
/// BulkInsertColumnCompletenessTests asserts the insert list matches the EF model's mapped columns exactly, and
/// that every column has a conscious home in the update list or the documented exclusion list, so a migration
/// cannot silently leave the writer behind.
/// </summary>
internal static class PendingInitialPasswordBulkColumns
{
    /// <summary>
    /// Insert columns for the PendingInitialPasswords table.
    /// </summary>
    internal static readonly string[] PendingInitialPasswords =
    [
        "Id", "ConnectedSystemObjectId", "ConnectedSystemId", "SyncRuleId", "Status",
        "FailureReason", "TargetMessage", "AttemptCount", "CreatedAt", "LastAttemptedAt", "ExpiresAt"
    ];

    /// <summary>
    /// Update columns for recording the outcome of a delivery attempt: everything a try can change.
    /// </summary>
    internal static readonly string[] PendingInitialPasswordsAttemptUpdate =
    [
        "Status", "FailureReason", "TargetMessage", "AttemptCount", "LastAttemptedAt", "ExpiresAt"
    ];

    /// <summary>
    /// Columns deliberately excluded from the update list. The identity, the account the password is owed to,
    /// the Connected System it lives in, the rule that asked for it, and when the work was staged are all facts
    /// about how the record came to exist, and none of them changes once it has.
    /// </summary>
    internal static readonly string[] PendingInitialPasswordsUpdateExclusions =
    [
        "Id", "ConnectedSystemObjectId", "ConnectedSystemId", "SyncRuleId", "CreatedAt"
    ];
}

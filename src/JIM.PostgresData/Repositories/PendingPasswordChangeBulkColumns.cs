// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column lists used by the raw-SQL paths that write the Password
/// Synchronisation queue (SyncRepository.PasswordOperations.cs). Every writer MUST write values in exactly list
/// order. BulkInsertColumnCompletenessTests asserts the insert list matches the EF model's mapped columns
/// exactly, and that every column has a conscious home in an update list or the documented exclusion list, so a
/// migration cannot silently leave a writer behind.
/// </summary>
internal static class PendingPasswordChangeBulkColumns
{
    /// <summary>
    /// Insert columns for the PendingPasswordChanges table.
    /// </summary>
    internal static readonly string[] PendingPasswordChanges =
    [
        "Id", "MetaverseObjectId", "ConnectedSystemId", "ConnectedSystemObjectId", "EncryptedPassword",
        "ExpiryBehaviour", "Status", "FailureReason", "TargetMessage", "AttemptCount", "NextRetryAt",
        "CreatedAt", "LastAttemptedAt", "ExpiresAt", "ActivityId", "CancelledAt", "CancelledById",
        "CancelledByName"
    ];

    /// <summary>
    /// Update columns for the coalescing UPSERT: what a newer password change replaces on an existing row.
    /// <para>
    /// Everything describing the superseded password goes, which is why the failure fields and the attempt count
    /// are here rather than in the exclusions. Carrying them forward would let a newer password inherit an
    /// exhausted retry budget, or a park earned by a password nobody is trying to deliver any more.
    /// </para>
    /// <para>
    /// The cancellation stamp goes with them, and for the same reason: it cancelled a password that no longer
    /// exists on this row. Leaving it would produce a pending row claiming to have been cancelled.
    /// </para>
    /// </summary>
    internal static readonly string[] PendingPasswordChangesSupersedeUpdate =
    [
        "ConnectedSystemObjectId", "EncryptedPassword", "ExpiryBehaviour", "Status", "FailureReason",
        "TargetMessage", "AttemptCount", "NextRetryAt", "CreatedAt", "LastAttemptedAt", "ExpiresAt", "ActivityId",
        "CancelledAt", "CancelledById", "CancelledByName"
    ];

    /// <summary>
    /// Update columns for recording the outcome of a delivery attempt: everything one try can change.
    /// <para>
    /// ConnectedSystemObjectId is included because delivery re-resolves the account on each attempt, so a change
    /// queued before its account existed gains one the moment provisioning catches up.
    /// </para>
    /// </summary>
    internal static readonly string[] PendingPasswordChangesAttemptUpdate =
    [
        "ConnectedSystemObjectId", "Status", "FailureReason", "TargetMessage", "AttemptCount", "NextRetryAt",
        "LastAttemptedAt"
    ];

    /// <summary>
    /// Columns deliberately excluded from every update list. The identity of the row, and the pair of keys it
    /// coalesces on, are facts about which work this row represents; a write that changed any of them would be
    /// creating different work rather than updating this.
    /// </summary>
    internal static readonly string[] PendingPasswordChangesUpdateExclusions =
    [
        "Id", "MetaverseObjectId", "ConnectedSystemId"
    ];
}

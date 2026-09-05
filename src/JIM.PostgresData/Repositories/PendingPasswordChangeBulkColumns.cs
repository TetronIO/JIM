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
        "CancelledByName", "ClaimedAt", "ClaimedBy", "Origin", "EnableAccount"
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
    /// <para>
    /// So does the claim (#1635): a deliverer holding the superseded password has nothing to deliver any more, and
    /// its outcome write is guarded on the row still being Delivering, so clearing the claim here is what makes
    /// that write land nowhere. The new password is delivered on a claim of its own.
    /// </para>
    /// <para>
    /// The origin and the enable decision describe the password the row is carrying now (#1635), so the newer
    /// change's values replace the older's: an administrator's reset replaces a held propagated change and is
    /// delivered as a reset; a later propagated change replaces the reset and carries no enable decision.
    /// </para>
    /// </summary>
    internal static readonly string[] PendingPasswordChangesSupersedeUpdate =
    [
        "ConnectedSystemObjectId", "EncryptedPassword", "ExpiryBehaviour", "Status", "FailureReason",
        "TargetMessage", "AttemptCount", "NextRetryAt", "CreatedAt", "LastAttemptedAt", "ExpiresAt", "ActivityId",
        "CancelledAt", "CancelledById", "CancelledByName", "ClaimedAt", "ClaimedBy", "Origin", "EnableAccount"
    ];

    /// <summary>
    /// Update columns for recording the outcome of a delivery attempt: everything one try can change.
    /// <para>
    /// ConnectedSystemObjectId is included because delivery re-resolves the account on each attempt, so a change
    /// queued before its account existed gains one the moment provisioning catches up. The claim columns are
    /// included because an attempt ends the claim (#1635): the writer sets them to null.
    /// </para>
    /// </summary>
    internal static readonly string[] PendingPasswordChangesAttemptUpdate =
    [
        "ConnectedSystemObjectId", "Status", "FailureReason", "TargetMessage", "AttemptCount", "NextRetryAt",
        "LastAttemptedAt", "ClaimedAt", "ClaimedBy"
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

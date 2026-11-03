// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The single source of truth for the column lists used by the raw-SQL paths that write the Exchange queue
/// (SyncRepository.ExchangeOperations.cs). Every writer MUST write values in exactly list order.
/// BulkInsertColumnCompletenessTests asserts the insert list matches the EF model's mapped columns exactly, and that
/// every column has a conscious home in an update list or the documented exclusion list, so a migration cannot
/// silently leave a writer behind.
/// </summary>
internal static class PendingExchangeChangeBulkColumns
{
    /// <summary>
    /// Insert columns for the PendingExchangeChanges table.
    /// </summary>
    internal static readonly string[] PendingExchangeChanges =
    [
        "Id", "ConnectedSystemId", "ConnectedSystemObjectId", "MetaverseObjectId", "SyncRuleId", "RecipientType",
        "Alias", "Database", "PrimarySmtpAddress", "ExternalAddress", "DomainController", "Status", "FailureKind",
        "FailureMessage", "AttemptCount", "CreatedAt", "LastAttemptedAt", "FirstFailedAt", "NextRetryAt", "ClaimedAt",
        "ClaimedBy", "ActivityId"
    ];

    /// <summary>
    /// Update columns for the staging UPSERT: what a newer declared state replaces on the row already held for the
    /// account.
    /// <para>
    /// The declared state goes, and so does everything saying where and why it was declared: the domain controller the
    /// export wrote to, the identity, the Synchronisation Rule, the Activity and the creation time. They describe the
    /// state the row is carrying now.
    /// </para>
    /// <para>
    /// So does every trace of the earlier attempts (the status, the failure, the attempt count and the failure clock),
    /// which is why they are here rather than in the exclusions: carrying them forward would let a newer declared state
    /// inherit a park, or a 24-hour failure window, earned by a request nobody is making any more. The staging
    /// statement sets the attempt, failure-clock and claim columns to their initial values rather than copying the
    /// offered row's, so a deliverable row is due at once whatever the caller passed; the status and failure fields
    /// take the offered row's own, so a refusal superseding a queued change records itself and its reason.
    /// </para>
    /// <para>
    /// So does the claim: a deliverer applying the superseded state has nothing to apply any more, and its outcome write
    /// is guarded on the row still being Delivering, so clearing the claim here is what makes that write land nowhere.
    /// </para>
    /// </summary>
    internal static readonly string[] PendingExchangeChangesSupersedeUpdate =
    [
        "MetaverseObjectId", "SyncRuleId", "RecipientType", "Alias", "Database", "PrimarySmtpAddress", "ExternalAddress",
        "DomainController", "Status", "FailureKind", "FailureMessage", "AttemptCount", "CreatedAt", "LastAttemptedAt",
        "FirstFailedAt", "NextRetryAt", "ClaimedAt", "ClaimedBy", "ActivityId"
    ];

    /// <summary>
    /// Update columns for recording a failed delivery attempt (#2021, Phase 5): how it failed, the attempt count and
    /// failure clock, when the next attempt falls due, and the status it leaves the change in (Pending to retry, Parked
    /// to wait for a person). The claim columns are included because an attempt ends the claim: the writer sets them to
    /// null. The declared state, the account and its Activity are untouched: an attempt changes how the change stands,
    /// never what it is.
    /// </summary>
    internal static readonly string[] PendingExchangeChangesAttemptUpdate =
    [
        "Status", "FailureKind", "FailureMessage", "AttemptCount", "LastAttemptedAt", "FirstFailedAt", "NextRetryAt",
        "ClaimedAt", "ClaimedBy"
    ];

    /// <summary>
    /// Columns deliberately excluded from every update list. The row's identity, and the account it coalesces on, are
    /// facts about which work this row represents; a write that changed either would be creating different work rather
    /// than updating this. The Connected System follows from the account, so a supersede cannot change it either.
    /// </summary>
    internal static readonly string[] PendingExchangeChangesUpdateExclusions =
    [
        "Id", "ConnectedSystemId", "ConnectedSystemObjectId"
    ];
}

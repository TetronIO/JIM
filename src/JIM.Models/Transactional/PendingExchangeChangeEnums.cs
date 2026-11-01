// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// Where a queued Exchange change stands in reaching Exchange (#2021).
/// <para>
/// There is no "delivered" state, for the reason the Password Synchronisation queue has none: a successful delivery
/// removes the row, because the queue is work outstanding and the change's Activity is the history.
/// </para>
/// </summary>
public enum PendingExchangeChangeStatus
{
    /// <summary>
    /// The change is owed to Exchange and JIM will try it. Covers a first attempt not yet made and one waiting out a
    /// backoff after a failure that may clear by itself.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The Exchange delivery lane has claimed the change and is applying it now.
    /// <para>
    /// Held under a lease, as a password delivery is: a deliverer that dies mid-flight leaves the row here, and once
    /// the lease has run out the row is claimable again. A newer declared state staged while the row is here
    /// supersedes it in place and clears the claim, so the in-flight attempt's outcome write, guarded on the row still
    /// being Delivering, lands nowhere.
    /// </para>
    /// </summary>
    Delivering = 1,

    /// <summary>
    /// Nothing will change until a person acts, so JIM has stopped trying.
    /// <para>
    /// Reached by a failure no retry can cure (Exchange refused the change, or JIM lacks the permissions to make it),
    /// or by failures that have gone on for 24 hours (<see cref="PendingExchangeChange.FirstFailedAt"/>). The ways out
    /// are a manual retry once the cause is fixed, or a newer declared state superseding the row.
    /// </para>
    /// </summary>
    Parked = 2,

    /// <summary>
    /// An administrator decided the change should not be applied, so JIM stopped trying.
    /// <para>
    /// Recorded rather than deleted, and it holds the cancelled declared state: an export re-evaluated as unconfirmed
    /// re-stages the same values, and that must leave the cancellation in place rather than quietly undo it
    /// (<see cref="ExchangeChangeStagingDisposition.HeldCancelled"/>). Only a different declared state replaces it.
    /// </para>
    /// </summary>
    Cancelled = 3
}

/// <summary>
/// How the last attempt at applying a queued Exchange change failed (#2021): the one classification that drives
/// retry, parking and what an administrator is told.
/// </summary>
public enum ExchangeChangeFailureKind
{
    /// <summary>
    /// Exchange could not find the directory object the change is for: typically replication from the domain
    /// controller the account was written to has not reached the server Exchange read from. Retried, because it
    /// usually clears by itself.
    /// </summary>
    ObjectNotFound = 0,

    /// <summary>
    /// Exchange refused the change itself (an alias already in use, an unknown database, a recipient type the object
    /// cannot take). Parked, because repeating the same request will be refused the same way.
    /// </summary>
    Refused = 1,

    /// <summary>
    /// The account JIM signs in to Exchange with lacks the role needed for the change. Parked, because the remedy is a
    /// permission an administrator grants.
    /// </summary>
    InsufficientPermissions = 2,

    /// <summary>
    /// Exchange did not accept JIM's sign-in. Retried within the failure window, then parked: an expired credential
    /// will not renew itself.
    /// </summary>
    AuthenticationFailed = 3,

    /// <summary>
    /// JIM could not reach Exchange at all (a network failure, a timeout, a server unavailable). Retried, because it
    /// usually clears by itself.
    /// </summary>
    Unreachable = 4
}

/// <summary>
/// What staging an Exchange change did, decided in one statement against the row the queue already holds for the
/// account, if any (<c>SyncRepository.ExchangeOperations.cs</c>'s <c>StageExchangeChangesAsync</c>).
/// </summary>
public enum ExchangeChangeStagingDisposition
{
    /// <summary>
    /// The queue held no row for the account, so the change was inserted as a new row.
    /// </summary>
    Inserted = 0,

    /// <summary>
    /// A row existed for the account and the change replaced it in place: its declared state, its domain controller, its
    /// Activity and its creation time, with every trace of the earlier attempt (failure, attempts, claim, cancellation)
    /// cleared and the row due at once. The row keeps its own id, so a caller must adopt the row's id for anything it
    /// does with the change afterwards. A row being delivered is superseded too, which is what makes the in-flight
    /// attempt's result land nowhere.
    /// </summary>
    Superseded = 1,

    /// <summary>
    /// A row existed that an administrator had cancelled, declaring exactly this state, so it was left untouched: a
    /// re-stage of what was cancelled must not undo the cancellation.
    /// </summary>
    HeldCancelled = 2
}

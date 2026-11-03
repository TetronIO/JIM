// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Where one queued Exchange change stands, in the terms an administrator following it is shown (#2021).
/// <para>
/// A projection over <see cref="PendingExchangeChangeStatus"/> and the Activities recorded under the change rather
/// than a copy of either: the queue has no state for a delivered, withdrawn or replaced change (the row is gone or
/// re-pointed), and no state that tells a change still waiting its first attempt from one waiting out a backoff.
/// </para>
/// </summary>
public enum ExchangeChangeState
{
    /// <summary>
    /// Queued and not yet attempted. The Exchange delivery lane picks it up within a few seconds.
    /// </summary>
    Queued = 0,

    /// <summary>
    /// The Exchange delivery lane has claimed it and is applying it now.
    /// </summary>
    Delivering = 1,

    /// <summary>
    /// An attempt failed in a way that may clear by itself, and JIM will try again at the next attempt time.
    /// </summary>
    Retrying = 2,

    /// <summary>
    /// JIM has stopped trying: Exchange refused the change, JIM's account lacks the permission, or failures went on
    /// for 24 hours. It waits for a person to retry it, or for a different declared state.
    /// </summary>
    Parked = 3,

    /// <summary>
    /// Exchange made the account the declared recipient, or already had it as one.
    /// </summary>
    Delivered = 4,

    /// <summary>
    /// The change was no longer owed when its turn came (the account or its Synchronisation Rule was removed, or the
    /// declaration moved on), so Exchange was not asked.
    /// </summary>
    Withdrawn = 5,

    /// <summary>
    /// A newer declared state for the same account replaced the change before it was delivered. The newer state is
    /// followed under its own Activity.
    /// </summary>
    Replaced = 6,

    /// <summary>
    /// JIM refused the declared state before asking Exchange, for example a recipient type that does not fit the
    /// object. It holds until the declared state changes.
    /// </summary>
    Refused = 7,

    /// <summary>
    /// The declared state would change an existing recipient's type, which this release does not make. It is
    /// reported and holds until the declared state changes.
    /// </summary>
    NotMade = 8
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Where one password change stands at one Connected System, in the terms a person waiting on it is shown (#1635).
/// <para>
/// A projection over <see cref="PendingPasswordChangeStatus"/> and the delivery Activities rather than a copy of
/// either: the queue has no state for a delivered change (the row is gone) and no state for a change held by a
/// switched-off system (it is simply Pending), and both are things a caller needs to be told apart.
/// </para>
/// </summary>
public enum PasswordChangeTargetState
{
    /// <summary>
    /// Queued and not yet attempted. The Password Delivery Service will pick it up within a second or so.
    /// </summary>
    Queued = 0,

    /// <summary>
    /// The Password Delivery Service has claimed it and is delivering it now.
    /// </summary>
    Delivering = 1,

    /// <summary>
    /// The Connected System took the password. The queue row is gone; the child Activity says so.
    /// </summary>
    Set = 2,

    /// <summary>
    /// An attempt failed in a way another may resolve, and the next one is scheduled; see
    /// <see cref="PasswordChangeTargetOutcome.NextAttemptAt"/>.
    /// </summary>
    Retrying = 3,

    /// <summary>
    /// JIM has stopped trying: the target refused the password, the operation is unsupported, or the configured
    /// attempts ran out. Waits on a person.
    /// </summary>
    Parked = 4,

    /// <summary>
    /// Queued for a Connected System whose Password Synchronisation is switched off. Held until somebody switches
    /// it on, or the change expires first.
    /// </summary>
    Held = 5,

    /// <summary>
    /// Outlived its time to live before it could be delivered. Nothing can deliver it now.
    /// </summary>
    Expired = 6,

    /// <summary>
    /// An administrator stopped it being delivered.
    /// </summary>
    Cancelled = 7
}

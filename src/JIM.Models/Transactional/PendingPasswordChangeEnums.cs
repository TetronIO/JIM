// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// Where a queued password change stands in reaching its Connected System (#1119).
/// <para>
/// Deliberately the same three states as <see cref="PendingInitialPasswordStatus"/>, and for the same reasons.
/// There is no "delivered" state: a successful delivery removes the row, because this is a list of work
/// outstanding rather than a history of work done, and the Activity is the history. Keeping delivered rows would
/// grow a table by one row per password change per system, to answer a question Activities already answer, while
/// holding an encrypted password long after anything needed it.
/// </para>
/// </summary>
public enum PendingPasswordChangeStatus
{
    /// <summary>
    /// The change is owed to the system and JIM will try again. Covers a first attempt not yet made, one waiting
    /// out a backoff, and one held because the Connected System is configured but disabled.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Nothing will change until a person acts, so JIM has stopped trying.
    /// <para>
    /// Reached by a policy rejection, an operation the target does not support, or exhausting the configured
    /// retries. The policy rejection is where this feature diverges from initial passwords: there, a refused
    /// password can be regenerated under corrected settings, so parking waits on a configuration change. Here the
    /// password is the person's own and JIM has no other to send, so the remedy is outside JIM entirely, and the
    /// only ways out are a manual retry once the cause is fixed, or a newer password change superseding this one.
    /// </para>
    /// </summary>
    Parked = 1,

    /// <summary>
    /// The change sat unsent for longer than the Connected System's time to live, so JIM stopped rather than
    /// deliver a password that has since been superseded elsewhere.
    /// <para>
    /// Recorded rather than removed. A password change that quietly stopped being owed, with nothing to say so,
    /// would leave an administrator believing an identity's password is consistent across their systems when it
    /// is not; that is the silent divergence this whole feature exists to prevent.
    /// </para>
    /// </summary>
    Expired = 2
}

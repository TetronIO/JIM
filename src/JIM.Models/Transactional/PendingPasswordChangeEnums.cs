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
    Expired = 2,

    /// <summary>
    /// An administrator decided the change should not be delivered, so JIM stopped trying.
    /// <para>
    /// Recorded rather than deleted, for the reason <see cref="Expired"/> is: the identity's password stays
    /// divergent in that system whether or not the row survives, and a row that vanishes says the opposite. This
    /// is the one thing that separates a cancellation from a delivery, which does delete its row: a delivery
    /// leaves nothing divergent to report.
    /// </para>
    /// <para>
    /// Unlike an expiry it is not final. The password is still held, so an administrator who cancelled by mistake
    /// can retry the row back into the queue until its time to live runs out.
    /// </para>
    /// </summary>
    Cancelled = 3,

    /// <summary>
    /// The Password Delivery Service has claimed the change and is delivering it now.
    /// <para>
    /// A claim is what stops two deliverers (two Worker replicas, or one lane overlapping a safety poll) sending
    /// the same password twice, and what lets the person who asked for the change be shown "delivering" rather
    /// than "waiting". It is held under a lease: a deliverer that dies mid-flight leaves the row here, and once
    /// the lease has run out the row is claimable again, which is the only way out of this state that does not
    /// pass through the deliverer's own outcome write.
    /// </para>
    /// <para>
    /// Not a terminal state and not a waiting one. The queue page groups it with Pending as "Waiting"; the
    /// retention cleanup never removes it; expiry never touches it.
    /// </para>
    /// </summary>
    Delivering = 4
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// One password change owed to one Connected System (#1119): the Password Synchronisation queue.
/// <para>
/// Three invariants shape everything here, and each has a failure mode worth naming:
/// </para>
/// <para>
/// <b>The row holds the latest intended password for its target, never a replayable sequence.</b> A person who
/// changes their password twice in an hour must not have the older one delivered to a system that was briefly
/// unavailable; that would leave them with a password they have already replaced. A unique index on
/// (Metaverse Object, Connected System) makes that a database guarantee rather than a convention, and
/// <see cref="Supersede"/> is what a newer change does to an older one.
/// </para>
/// <para>
/// <b>Success deletes the row.</b> The queue is work outstanding; the Activity is the history. A delivered row
/// would hold an encrypted password long after anything needed it, for no gain.
/// </para>
/// <para>
/// <b>This is the only place JIM holds a synchronised password</b>, and it holds it encrypted under a purpose of
/// its own. Nothing reads it back to a surface: no DTO carries it, no log records it, and no preview shows it.
/// </para>
/// </summary>
public class PendingPasswordChange
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The identity whose password changed. Half of the coalescing key, and what the Metaverse Object's password
    /// panel reads to show an identity's outstanding work.
    /// </summary>
    public Guid MetaverseObjectId { get; set; }

    /// <summary>
    /// The Connected System this change is owed to. The other half of the coalescing key.
    /// <para>
    /// Denormalised rather than reached through the Connected System Object, deliberately and for the same reason
    /// <see cref="PendingInitialPassword.ConnectedSystemId"/> is: "what is outstanding on this system?" is asked
    /// on every delivery pass and on every list page, and it must not need a join. It is also the only way to
    /// hold a change for a system where the account does not exist yet.
    /// </para>
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The account to set the password on, or null where the identity has no account in this system yet.
    /// <para>
    /// Null is an ordinary state rather than an error: a password change can reach JIM before the account it
    /// belongs to has been provisioned. The change waits, bounded by <see cref="ExpiresAt"/>, and delivery
    /// re-resolves the account on each attempt, so the provisioning race resolves itself.
    /// </para>
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The password, encrypted under the Password Synchronisation protection purpose.
    /// <para>
    /// Decrypted only in the delivery processor, only for the duration of one attempt. It is never returned by
    /// any surface, and no DTO built from this row carries it.
    /// </para>
    /// </summary>
    public string EncryptedPassword { get; set; } = null!;

    /// <summary>
    /// What should happen to the password once set, carried from the change rather than from the Connected
    /// System's configuration.
    /// <para>
    /// Per-change because it belongs to the circumstance rather than to the system: an administrator setting a
    /// password on somebody's behalf may reasonably require a change at next sign-in, whereas a password the
    /// person chose themselves must not, and both go to the same targets.
    /// </para>
    /// </summary>
    public PasswordExpiryBehaviour ExpiryBehaviour { get; set; } = PasswordExpiryBehaviour.ExpiresAccordingToTargetPolicy;

    public PendingPasswordChangeStatus Status { get; set; } = PendingPasswordChangeStatus.Pending;

    /// <summary>
    /// How the last attempt failed, or null before one has been made. Reused from the synchronous set-password
    /// path rather than defined again, so one classification drives retry, parking and what an administrator is
    /// told, whichever route the password took.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; set; }

    /// <summary>
    /// The target's own words on the last failure, or null. The Connector has already stripped anything
    /// password-like from it; why a directory refused is a fact about that directory and the most useful thing an
    /// administrator can be shown.
    /// </summary>
    public string? TargetMessage { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>
    /// When the next delivery attempt falls due, or null for a change that is due now.
    /// <para>
    /// The genuine addition over <see cref="PendingInitialPassword"/>, which has no such field because its
    /// retries ride the next export run. A synchronised password is not tied to a run, so it needs a clock, and
    /// the backoff between attempts is what stops a refusing target being hammered.
    /// </para>
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastAttemptedAt { get; set; }

    /// <summary>
    /// When this change stops being worth delivering, from the Connected System's time to live.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// The Activity recording the password change that produced this row (requirement 27).
    /// <para>
    /// The queue holds operational state; the Activity is the durable audit record and outlives the row, which is
    /// deleted the moment delivery succeeds. Re-pointed by <see cref="Supersede"/>, so the row always names the
    /// Activity for the password it is actually carrying.
    /// </para>
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// When an administrator cancelled this change, or null if nobody has.
    /// </summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>
    /// Who cancelled it, mirroring the Activity initiator fields (<see cref="Activities.Activity.InitiatedById"/>
    /// and <see cref="Activities.Activity.InitiatedByName"/>) rather than inventing a second shape.
    /// <para>
    /// Both are nullable because the API-key path has no person behind it. A cancellation with no name is still
    /// worth recording: what matters most is that the change stopped being delivered on purpose.
    /// </para>
    /// </summary>
    public Guid? CancelledById { get; set; }

    /// <inheritdoc cref="CancelledById"/>
    public string? CancelledByName { get; set; }

    /// <summary>
    /// When the Password Delivery Service claimed this change for delivery, or null while nobody holds it.
    /// <para>
    /// A claim is a lease, not a lock: it is honoured for <see cref="ClaimLease"/> and then ignored, so a
    /// deliverer that died mid-flight cannot strand the row in <see cref="PendingPasswordChangeStatus.Delivering"/>.
    /// Set and cleared only in the database, by the claim statement and by whichever transition ends the claim.
    /// </para>
    /// </summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>
    /// The service instance holding the claim (host name plus a per-process id), or null while nobody does.
    /// Recorded so an administrator reading a row stuck in Delivering can tell which process to look at.
    /// </summary>
    [MaxLength(200)]
    public string? ClaimedBy { get; set; }

    /// <summary>
    /// How long a claim is honoured before the change is claimable again.
    /// <para>
    /// A minute comfortably outlives one attempt against one directory, and is short enough that a deliverer
    /// that dies leaves the person waiting about a minute rather than until somebody notices. A lane claims in
    /// small batches so a long queue never holds a claim for much longer than the attempts it covers.
    /// </para>
    /// </summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether a delivery pass at <paramref name="asOf"/> should attempt this change: it is still pending, and
    /// either has no retry scheduled or has reached it. A claimed change is not due; it is being delivered.
    /// </summary>
    public bool IsDue(DateTime asOf) =>
        Status == PendingPasswordChangeStatus.Pending && (NextRetryAt == null || NextRetryAt <= asOf);

    /// <summary>
    /// Whether a claim on this change has outlived <paramref name="lease"/> and the change is claimable again:
    /// it is <see cref="PendingPasswordChangeStatus.Delivering"/> and was claimed at least a lease ago. False for
    /// a change nobody holds, whatever its status.
    /// </summary>
    public bool IsClaimExpired(DateTime asOf, TimeSpan lease) =>
        Status == PendingPasswordChangeStatus.Delivering && ClaimedAt != null && ClaimedAt + lease <= asOf;

    /// <summary>
    /// Takes the change for delivery: the in-memory twin of the claim statement, for the in-memory repository
    /// and for reasoning about the transition in tests. The status, and only the status, changes with the
    /// stamp; everything describing the password and its attempts is left alone, because a claim is a promise
    /// to attempt rather than an attempt.
    /// </summary>
    public void Claim(string claimedBy, DateTime asOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);

        Status = PendingPasswordChangeStatus.Delivering;
        ClaimedAt = asOf;
        ClaimedBy = claimedBy;
    }

    /// <summary>
    /// Gives a claimed change back unattempted, for a lane that claimed and then could not deliver at all: the
    /// Connector has no password capability, the channel could not be opened or was refused as insecure, or the
    /// lane was cancelled before reaching the change. Nothing is counted against the change, because nothing was
    /// tried; it goes back to Pending exactly as it was, still due.
    /// </summary>
    public void ReleaseClaim()
    {
        if (Status != PendingPasswordChangeStatus.Delivering)
            return;

        Status = PendingPasswordChangeStatus.Pending;
        ClearClaim();
    }

    /// <summary>
    /// Whether this change has outlived its time to live and should be expired rather than attempted.
    /// <para>
    /// Only a pending change expires. An expired one stays as it is rather than being re-expired on every pass,
    /// and a parked one is waiting on a person: expiring it under them would remove the very thing they were
    /// asked to look at.
    /// </para>
    /// </summary>
    public bool HasExpired(DateTime asOf) =>
        Status == PendingPasswordChangeStatus.Pending && ExpiresAt <= asOf;

    /// <summary>
    /// Records a failed delivery attempt, and decides from its classification whether to retry or to park.
    /// </summary>
    /// <param name="reason">How the target refused, or how the attempt failed.</param>
    /// <param name="targetMessage">The target's own words, already stripped of anything password-like.</param>
    /// <param name="configuration">The Connected System's Password Synchronisation settings.</param>
    /// <param name="asOf">The instant of the attempt.</param>
    public void RecordAttempt(
        PasswordSetFailureReason reason,
        string? targetMessage,
        ConnectedSystemPasswordSynchronisation configuration,
        DateTime asOf)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        AttemptCount++;
        LastAttemptedAt = asOf;
        FailureReason = reason;
        TargetMessage = targetMessage;

        // An attempt ends the claim whichever way it went: the row goes back to waiting (Pending or Parked), and
        // a stale claim stamp on a waiting row would read as a deliverer that never let go.
        ClearClaim();

        // A policy rejection and an unsupported operation are answers rather than accidents: the same password
        // presented again earns the same reply. Requirement 13 is why the policy case cannot be retried out of,
        // as an initial password can: JIM did not generate this password and has no other to offer.
        var park = reason is PasswordSetFailureReason.PolicyRejection or PasswordSetFailureReason.UnsupportedOperation
                   || AttemptCount > configuration.EffectiveMaxRetries;

        if (park)
        {
            Status = PendingPasswordChangeStatus.Parked;
            NextRetryAt = null;
            return;
        }

        Status = PendingPasswordChangeStatus.Pending;

        var delay = configuration.CalculateRetryDelay(AttemptCount, ExpiresAt - CreatedAt);
        var scheduled = asOf + delay;

        // Never book an attempt for after the change stops being worth making; it would come round to find
        // nothing to attempt, and the row would read as retrying when it was really waiting to expire.
        NextRetryAt = scheduled > ExpiresAt ? ExpiresAt : scheduled;
    }

    /// <summary>
    /// Replaces this change with a newer one for the same identity and Connected System: requirement 8's
    /// coalescing.
    /// <para>
    /// The attempt history is cleared along with the password, because it described the password being replaced.
    /// Carrying it forward would let a newer password inherit an exhausted retry budget, or a park earned by a
    /// password nobody is trying to deliver any more.
    /// </para>
    /// </summary>
    public void Supersede(
        string encryptedPassword,
        PasswordExpiryBehaviour expiryBehaviour,
        Guid activityId,
        TimeSpan timeToLive,
        DateTime asOf)
    {
        EncryptedPassword = encryptedPassword;
        ExpiryBehaviour = expiryBehaviour;
        ActivityId = activityId;
        CreatedAt = asOf;
        ExpiresAt = asOf + timeToLive;

        Status = PendingPasswordChangeStatus.Pending;
        AttemptCount = 0;
        FailureReason = null;
        TargetMessage = null;
        NextRetryAt = null;
        LastAttemptedAt = null;
        ClearCancellation();

        // A newer password replaces whatever the deliverer was holding. Its outcome write is guarded on the row
        // still being Delivering, so it lands nowhere; the new password is delivered on its own claim.
        ClearClaim();
    }

    /// <summary>
    /// Makes this change due immediately, clearing the failure that stopped it: the manual retry from the queue
    /// page (requirement 22).
    /// <para>
    /// The attempt count resets because the retry budget counts attempts against one set of circumstances, and an
    /// administrator retrying has changed them. Without the reset a parked change would exhaust its budget again
    /// on the first attempt and park straight back.
    /// </para>
    /// </summary>
    public void Retry()
    {
        Status = PendingPasswordChangeStatus.Pending;
        AttemptCount = 0;
        NextRetryAt = null;
        FailureReason = null;
        TargetMessage = null;

        // Retrying is also how a cancellation is undone, so the stamp goes with it: a pending row still claiming
        // to have been cancelled would be read as a cancellation that failed to take.
        ClearCancellation();
        ClearClaim();
    }

    /// <summary>
    /// Records that an administrator stopped this change being delivered: the cancel action on the queue page
    /// (requirement 22).
    /// <para>
    /// Deliberately an outcome rather than a deletion. The identity's password stays divergent in that system
    /// either way, and a row that simply disappears reports the opposite; that is the same reasoning
    /// <see cref="Expire"/> follows. The failure that stranded the change is kept, because why it was stuck is
    /// usually why it was cancelled.
    /// </para>
    /// </summary>
    /// <param name="cancelledById">The administrator, or null where an API key acted with no person behind it.</param>
    /// <param name="cancelledByName">Their display name, under the same caveat.</param>
    /// <param name="asOf">The instant of the cancellation.</param>
    public void Cancel(Guid? cancelledById, string? cancelledByName, DateTime asOf)
    {
        Status = PendingPasswordChangeStatus.Cancelled;
        CancelledAt = asOf;
        CancelledById = cancelledById;
        CancelledByName = cancelledByName;
        NextRetryAt = null;

        // Cancelling a change mid-delivery wins: the deliverer's outcome write is guarded on the row still being
        // Delivering, so a cancelled row stays cancelled unless the password actually landed, in which case the
        // row is deleted and there is nothing left to have cancelled.
        ClearClaim();
    }

    /// <summary>
    /// Drops the cancellation stamp, for the two paths that put a cancelled row back to work: a manual retry, and
    /// a newer password change superseding it.
    /// </summary>
    private void ClearCancellation()
    {
        CancelledAt = null;
        CancelledById = null;
        CancelledByName = null;
    }

    /// <summary>
    /// Marks this change expired, keeping why it never landed. Requirement 9: an explicit recorded outcome, not a
    /// silent drop.
    /// </summary>
    public void Expire()
    {
        Status = PendingPasswordChangeStatus.Expired;
        NextRetryAt = null;
        ClearClaim();
    }

    /// <summary>
    /// Drops the claim stamp, for every transition that ends a claim. Kept separate from the status change so a
    /// transition cannot leave a waiting or terminal row still naming a deliverer.
    /// </summary>
    private void ClearClaim()
    {
        ClaimedAt = null;
        ClaimedBy = null;
    }

    public override string ToString()
    {
        return $"{nameof(PendingPasswordChange)}: Metaverse Object {MetaverseObjectId} to Connected System " +
               $"{ConnectedSystemId} ({Status})";
    }
}

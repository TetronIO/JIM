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
    /// <summary>
    /// JIM's own default time to live for a queued password change, applied when nothing more specific
    /// configures one: <see cref="ConnectedSystem.EffectiveInitialPasswordTimeToLive"/> falls back to this when
    /// <see cref="ConnectedSystem.InitialPasswordTimeToLive"/> is unset, and
    /// <see cref="ConnectedSystemPasswordSynchronisation.CalculateRetryDelay"/> caps its retry backoff the same
    /// way.
    /// </summary>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromDays(7);

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
    /// an initial password's own Connected System reference is: "what is outstanding on this system?" is asked
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
    /// The Synchronisation Rule whose Initial Password this row delivers the account's first password under, or null
    /// where the row owes no account a first password. For a <see cref="PendingPasswordChangeOrigin.Provisioned"/> row
    /// the rule also generates the password; for a change carrying the account's first password in place of its
    /// Initial Password (#2160, <see cref="CarriesInitialPassword"/>) it contributes only its promises about the
    /// account (<see cref="InitialPasswordAccountFlags"/>), read fresh at each attempt. Set to null by the database when
    /// the rule is deleted: no navigation property, matching the other foreign keys on this entity.
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// The password, encrypted under the Password Synchronisation protection purpose.
    /// <para>
    /// Decrypted only in the delivery processor, only for the duration of one attempt. It is never returned by
    /// any surface, and no DTO built from this row carries it.
    /// </para>
    /// <para>
    /// Null for a <see cref="PendingPasswordChangeOrigin.Provisioned"/> row, which never carries a value: its
    /// password is resolved from <see cref="SyncRuleId"/>'s initial-password settings at each delivery attempt
    /// instead.
    /// </para>
    /// </summary>
    public string? EncryptedPassword { get; set; }

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

    /// <summary>
    /// Where this change came from (#1635), which is what decides how it is delivered: see
    /// <see cref="PendingPasswordChangeOrigin"/>. Replaced along with the password when a newer change supersedes
    /// this row, because it describes the password the row is carrying now.
    /// </summary>
    public PendingPasswordChangeOrigin Origin { get; set; } = PendingPasswordChangeOrigin.Propagated;

    /// <summary>
    /// Whether to enable the account as the password is set, or null for no choice.
    /// <para>
    /// Carried only by an <see cref="PendingPasswordChangeOrigin.Explicit"/> change, and read only for one: the
    /// administrator who named the account made this decision with it. A propagated change never decides to enable
    /// an account, because it reaches accounts an administrator may have disabled on purpose, and re-enabling one
    /// as a side effect of somebody changing their password elsewhere would undo that silently. Null leaves the
    /// account's enabled state as it is, with two exceptions: an enable JIM itself withheld until a password existed
    /// (#2039) is completed, because that is what the account's Attribute Flow asked for; and an account still owed its
    /// first password follows its provisioning Synchronisation Rule's Enable Account (#2160).
    /// <c>AccountFlagsPolicy</c> holds the whole precedence (#2064).
    /// </para>
    /// </summary>
    public bool? EnableAccount { get; set; }

    /// <summary>
    /// Whether an export to this row's account wrote the attribute holding the account's flags (#2064, #2159):
    /// <c>userAccountControl</c> on Active Directory and Samba AD, which carries both whether the account is enabled and
    /// whether its password never expires. Where it did, the export that writes that attribute owns both flags:
    /// delivering the first password does not apply the provisioning Synchronisation Rule's Enable Account or its
    /// expiry's "Password never expires", and changes the enabled state only to complete an enable JIM withheld until
    /// the password existed (#2039). "Must change at next sign-in" belongs to the password, not to the flags, and is
    /// still applied. The name predates the second flag and is kept because the column is persisted;
    /// <c>AccountFlagsPolicy</c> calls it what it means there, whether the account's flags were written by an export.
    /// <para>
    /// Meaningful only for a row owing an account its first password (<see cref="OwesInitialPassword"/>): an Initial
    /// Password, or a change carrying the account's first password in its place (#2160). Set when the obligation is
    /// staged, from the Create that provisioned the account, and set again by any later export that writes the
    /// attribute while the password is still owed (a leaver disabled, or an account disabled on deprovisioning), so a
    /// late delivery never re-enables it. Recorded rather than inferred at delivery, because nothing else can tell an
    /// account the flow created disabled from one the directory created disabled by default.
    /// </para>
    /// <para>
    /// One fact for both flags holds because Active Directory keeps them in one attribute. A Connector whose targets
    /// keep the enabled state and the expiry in separate attributes would need a fact per attribute.
    /// </para>
    /// </summary>
    public bool EnabledStateSetByAttributeFlow { get; set; }

    /// <summary>
    /// The account this row owes its first password (#2160), or null where it owes none.
    /// <para>
    /// Set on every <see cref="PendingPasswordChangeOrigin.Provisioned"/> row, to the account its export provisioned;
    /// and on a propagated or explicit change that is delivered as that account's first password instead: one already
    /// waiting when the account was provisioned, which staging gives the obligation rather than queue an Initial
    /// Password over the person's own password, or one that replaced the Initial Password before it landed. Such a
    /// change keeps its own password, expiry and Activity, and its delivery carries the Initial Password's promises
    /// about the account, under <see cref="SyncRuleId"/>, provided it reaches this account.
    /// </para>
    /// <para>
    /// Tied to one account so the promises can never enable a different one: a propagated change re-resolves its
    /// account on every attempt. Set to null by the database when the account is deleted, which ends the obligation
    /// with it. It stays when the rule is deleted, and the delivery then says the rule no longer sets one.
    /// </para>
    /// </summary>
    public Guid? ProvisionedConnectedSystemObjectId { get; set; }

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
    /// The genuine addition over an initial password, which has no such field because its retries ride the next
    /// export run. A synchronised password is not tied to a run, so it needs a clock, and the backoff between
    /// attempts is what stops a refusing target being hammered.
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
    /// Whether an administrator named this change's account (<see cref="PendingPasswordChangeOrigin.Explicit"/>),
    /// as opposed to JIM propagating a password to whichever account the system's configuration nominates.
    /// </summary>
    public bool IsExplicit => Origin == PendingPasswordChangeOrigin.Explicit;

    /// <summary>
    /// Whether this is the first password for an account an export has just provisioned
    /// (<see cref="PendingPasswordChangeOrigin.Provisioned"/>).
    /// </summary>
    public bool IsProvisioned => Origin == PendingPasswordChangeOrigin.Provisioned;

    /// <summary>
    /// Whether JIM is carrying this password to every Connected System configured to receive synchronised
    /// passwords (<see cref="PendingPasswordChangeOrigin.Propagated"/>), as opposed to an account named on the row.
    /// </summary>
    public bool IsPropagated => Origin == PendingPasswordChangeOrigin.Propagated;

    /// <summary>
    /// Whether this row owes an account its first password: it is the account's Initial Password, or a change
    /// delivered in its place (#2160). Judged on the origin as well as on
    /// <see cref="ProvisionedConnectedSystemObjectId"/>, so a Provisioned row written without the account (an older
    /// writer, or a fixture) still has its enabled state's mark read before its rule enables anything.
    /// </summary>
    public bool OwesInitialPassword => IsProvisioned || ProvisionedConnectedSystemObjectId.HasValue;

    /// <summary>
    /// Whether this is a propagated or explicit change delivered as an account's first password in place of its
    /// Initial Password (#2160): the change's own password, with the Initial Password's promises about the account.
    /// </summary>
    public bool CarriesInitialPassword => !IsProvisioned && ProvisionedConnectedSystemObjectId.HasValue;

    /// <summary>
    /// Whether a Connected System whose Password Synchronisation is switched off holds this change rather than
    /// delivering it: a propagated change owing no account its first password. An explicit set and an Initial Password
    /// are delivered whatever the system's configuration says (decision D1), because their account is already decided,
    /// and so is a change carrying an account's first password (#2160), like the Initial Password it stands in for.
    /// Staging never gives the obligation to a held change: the Initial Password wins over one instead.
    /// </summary>
    public bool IsHeldWhilePasswordSynchronisationIsOff => IsPropagated && !CarriesInitialPassword;

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
    /// Gives a claimed change back unattempted, to be tried again at <paramref name="until"/> (#2173): delivering it
    /// would enable an account an export is being sent to right now, and that export may be disabling it. Nothing was
    /// sent, so nothing is counted against the change and what earlier attempts recorded stays. Unlike
    /// <see cref="ReleaseClaim"/> the change is not due again at once, so the lane does not claim it straight back
    /// while the export runs; and never later than <see cref="ExpiresAt"/>, so a long export cannot keep a change
    /// waiting past its time to live.
    /// </summary>
    public void Postpone(DateTime until)
    {
        if (Status != PendingPasswordChangeStatus.Delivering)
            return;

        Status = PendingPasswordChangeStatus.Pending;
        NextRetryAt = until > ExpiresAt ? ExpiresAt : until;
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
    /// <para>
    /// Everything the newer change says about the password it carries comes across: its origin, its enable
    /// decision, and the account it names, as well as the value and the expiry behaviour. Coalescing is by identity
    /// and system whatever the origin (#1635); an administrator's reset replaces a propagated change that was still
    /// waiting, and a later propagated change replaces the reset.
    /// </para>
    /// <para>
    /// Passwords replace each other; promises about the account carry forward (#2160). Where this row still owes an
    /// account its first password (it is live: Pending, Delivering or Parked) and the newer change brings no first
    /// password of its own, the obligation stays: the account it is for (<see cref="ProvisionedConnectedSystemObjectId"/>),
    /// the rule whose promises apply (<see cref="SyncRuleId"/>) and whether an export wrote the account's flags
    /// (<see cref="EnabledStateSetByAttributeFlow"/>). The newer change is then delivered as the account's first
    /// password. Otherwise those three come from the newer change: a re-provisioned account's Initial Password brings
    /// its own, and anything replacing an expired or cancelled row brings none.
    /// </para>
    /// <para>
    /// <c>SyncRepository.QueuePasswordChangesAsync</c> applies the same rule in SQL, and also declines to let a
    /// propagated change replace a first password owed on a system whose Password Synchronisation is switched off.
    /// </para>
    /// </summary>
    public void Supersede(PendingPasswordChange newer)
    {
        ArgumentNullException.ThrowIfNull(newer);

        // Judged before anything is replaced: whether this row is live and owes an account its first password.
        var keepsObligation = KeepsInitialPasswordObligationOver(newer);
        var owedAccount = ProvisionedConnectedSystemObjectId ?? (IsProvisioned ? ConnectedSystemObjectId : null);

        EncryptedPassword = newer.EncryptedPassword;
        ExpiryBehaviour = newer.ExpiryBehaviour;
        Origin = newer.Origin;
        EnableAccount = newer.EnableAccount;
        ConnectedSystemObjectId = newer.ConnectedSystemObjectId;

        if (keepsObligation)
        {
            ProvisionedConnectedSystemObjectId = owedAccount;
        }
        else
        {
            ProvisionedConnectedSystemObjectId = newer.ProvisionedConnectedSystemObjectId;
            SyncRuleId = newer.SyncRuleId;
            EnabledStateSetByAttributeFlow = newer.EnabledStateSetByAttributeFlow;
        }

        ActivityId = newer.ActivityId;
        CreatedAt = newer.CreatedAt;
        ExpiresAt = newer.ExpiresAt;

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
    /// Whether this row's obligation to deliver an account's first password survives <paramref name="newer"/> replacing
    /// its password (#2160): it owes one, it is still live, and the newer change brings no first password of its own.
    /// </summary>
    public bool KeepsInitialPasswordObligationOver(PendingPasswordChange newer)
    {
        ArgumentNullException.ThrowIfNull(newer);

        return OwesInitialPassword
               && (ProvisionedConnectedSystemObjectId ?? ConnectedSystemObjectId).HasValue
               && Status is PendingPasswordChangeStatus.Pending or PendingPasswordChangeStatus.Delivering or PendingPasswordChangeStatus.Parked
               && !newer.OwesInitialPassword;
    }

    /// <summary>
    /// Gives this waiting change the obligation to deliver a newly provisioned account's first password (#2160): the
    /// in-memory twin of the attach in <c>SyncRepository.StageProvisionedPasswordChangesAsync</c>, which staging runs
    /// instead of queueing an Initial Password over the person's own password.
    /// <para>
    /// The change keeps its password, origin, expiry, enable choice and Activity: passwords replace each other, and
    /// this one is the newest. It takes the Initial Password's promises about the account: the account, the rule, and
    /// whether the Create wrote the account's flags. A propagated change also takes the account, which now exists.
    /// </para>
    /// <para>
    /// It becomes due at once. A claim is taken back, as <see cref="Supersede"/> takes it back, because the deliverer
    /// holding it decided the account's flags without the promises; its guarded outcome write and delete land nowhere,
    /// and the password is delivered again on a claim of its own. A park the new account cures (the account missing,
    /// or the target unreachable until the retries ran out; the Create has just proved both wrong) is released, and
    /// attempts made against a missing account are reset. The window is extended to the Initial Password's, never
    /// shortened.
    /// </para>
    /// </summary>
    /// <param name="connectedSystemObjectId">The account just provisioned.</param>
    /// <param name="syncRuleId">The provisioning Synchronisation Rule whose Initial Password the change stands in for.</param>
    /// <param name="accountFlagsWrittenByExport">Whether the Create wrote the account's flags (#2064).</param>
    /// <param name="expiresAt">When the Initial Password would have stopped being worth delivering.</param>
    public void CarryInitialPassword(Guid connectedSystemObjectId, int syncRuleId, bool accountFlagsWrittenByExport, DateTime expiresAt)
    {
        EnabledStateSetByAttributeFlow = accountFlagsWrittenByExport
                                         || (ProvisionedConnectedSystemObjectId == connectedSystemObjectId && EnabledStateSetByAttributeFlow);
        ProvisionedConnectedSystemObjectId = connectedSystemObjectId;
        SyncRuleId = syncRuleId;
        ConnectedSystemObjectId = connectedSystemObjectId;
        if (expiresAt > ExpiresAt)
            ExpiresAt = expiresAt;

        if (Status == PendingPasswordChangeStatus.Parked || FailureReason == PasswordSetFailureReason.TargetObjectNotFound)
        {
            AttemptCount = 0;
            FailureReason = null;
            TargetMessage = null;
        }

        Status = PendingPasswordChangeStatus.Pending;
        NextRetryAt = null;
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
               $"{ConnectedSystemId} ({Origin}, {Status})";
    }
}

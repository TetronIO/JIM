// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// Where a queued password change stands in reaching its Connected System (#1119).
/// <para>
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

/// <summary>
/// Where a queued password change came from (#1635): the one fact that decides how it is delivered.
/// <para>
/// All three origins share the queue, the retry policy, the coalescing key, the Activity shape and the person's
/// password history; that is the point of having one pipeline. A propagated change is aimed at whichever account
/// the Connected System's configuration nominates and is held while that system is paused for Password
/// Synchronisation; an explicit set and a provisioned row are both aimed at the account named on the row and are
/// delivered whether or not the system is configured, because that account is already decided (decision D1).
/// </para>
/// </summary>
public enum PendingPasswordChangeOrigin
{
    /// <summary>
    /// The password changed somewhere and JIM is carrying it to every Connected System configured to receive
    /// synchronised passwords. The account is resolved on each attempt from the system's configuration; the
    /// delivery enables the account only to complete an enable JIM withheld until a new account had a password
    /// (#2039, #2064), or where the change is delivered as an account's first password in place of its Initial
    /// Password, whose promises about the account it then carries (#2160); a paused system holds the change until it
    /// is switched back on, unless it carries such a first password.
    /// </summary>
    Propagated = 0,

    /// <summary>
    /// An administrator set a password on an account they named. The row carries that account, and the enable
    /// decision they made with it, and is delivered even where the system has no Password Synchronisation
    /// configuration or has it switched off. Every row queued before origins existed was propagated, which is
    /// why that value is zero and this one is not.
    /// </summary>
    Explicit = 1,

    /// <summary>
    /// The first password for an account an export has just provisioned. Delivered whatever the system's
    /// Password Synchronisation configuration says, exactly like an explicit set: the account already exists and
    /// is already named, so there is no configuration decision left to defer to.
    /// <para>
    /// Carries no password value: the row's <see cref="PendingPasswordChange.EncryptedPassword"/> is always null
    /// for this origin. The password is resolved from the provisioning Synchronisation Rule's initial-password
    /// settings at each delivery attempt, rather than generated once and stored, so a settings change before
    /// delivery succeeds takes effect on the next try.
    /// </para>
    /// <para>
    /// The account is named by <see cref="PendingPasswordChange.ConnectedSystemObjectId"/>, which is never null
    /// for a row of this origin: the export that created the row already knows which account it provisioned.
    /// </para>
    /// </summary>
    Provisioned = 2
}

/// <summary>
/// What staging a <see cref="PendingPasswordChangeOrigin.Provisioned"/> change did, decided in one statement by the
/// conflict clause in <c>SyncRepository.PasswordOperations.cs</c>'s <c>StageProvisionedPasswordChangesAsync</c>
/// (#1697, #2160). Unlike the ordinary coalescing UPSERT behind <see cref="PendingPasswordChangeOrigin"/>'s other two
/// origins, a provisioned row never overwrites a password change that can still reach the new account: that change
/// becomes the account's first password instead, carrying the Initial Password's promises about the account.
/// </summary>
public enum ProvisionedPasswordStagingDisposition
{
    /// <summary>
    /// No row existed for the (Metaverse Object, Connected System) key, so the change was inserted as a new row.
    /// </summary>
    Inserted = 0,

    /// <summary>
    /// A row existed that the account's Initial Password replaces in place: an expired or cancelled change; an Initial
    /// Password for an account since deleted, disjoined or provisioned again; a password change JIM had stopped
    /// trying because no attempt can deliver it (a policy rejection, an unreadable password, an unsupported operation);
    /// an administrator's set whose account is gone; or a propagated change held because Password Synchronisation is
    /// switched off on the system, which has not reached the system and would not, while the account's first password
    /// is delivered whatever the pause (#2160). The row keeps its own id rather than taking the change's, so a caller
    /// must adopt the row's id for anything it does with the change afterwards.
    /// </summary>
    Superseded = 1,

    /// <summary>
    /// A row existed carrying a password change that will reach the new account: a propagated change for the Object
    /// Type the system's Password Synchronisation targets, or an administrator's set naming the account. It keeps its
    /// own password, expiry and Activity, and now carries the Initial Password's obligation (#2160): the account, the
    /// rule, and whether the Create wrote the account's flags. Released to be delivered at once, its claim taken back
    /// where it was being delivered, and a park the account cures released.
    /// </summary>
    Coalesced = 2,

    /// <summary>
    /// A row existed carrying another of this person's Connected System Objects' first password or a change for one,
    /// still live, so nothing was written (#2160). The queue holds one password change per person per Connected System,
    /// and replacing it would lose that account's password; the export reports the Initial Password as not queued.
    /// </summary>
    Conflicted = 3
}

/// <summary>
/// What one attempt at delivering a queued password change did with the row (#1697). Replaces a plain
/// bool return from the delivery lane's per-change step, because a Provisioned row that no longer needs a
/// password is neither a delivery nor a retry: it must be removed from the queue without being counted, or
/// logged, as either.
/// </summary>
public enum PasswordDeliveryDisposition
{
    /// <summary>
    /// The password was set at the target and the row is removed from the queue. The Activity recording the
    /// change is what survives; nothing else needed to know the password ever existed.
    /// </summary>
    Delivered = 0,

    /// <summary>
    /// The row stays in the queue, either waiting out a backoff for the next attempt or parked for a person to
    /// look at. Nothing was delivered, and there is still something to deliver.
    /// </summary>
    Kept = 1,

    /// <summary>
    /// There is nothing left to deliver, so the row is removed from the queue without a password ever being
    /// sent: the account a Provisioned row named no longer exists, or the Synchronisation Rule that provisioned
    /// it no longer sets one. The child Activity records why, completed rather than failed, since nothing went
    /// wrong; the work simply stopped being needed.
    /// </summary>
    Withdrawn = 2,

    /// <summary>
    /// The row goes back to the queue unattempted, to be tried again shortly (#2173): delivering it would have enabled
    /// an account an export is being sent to right now, and that export may be disabling it. Nothing was sent and
    /// nothing failed, so no attempt is counted and no Activity is recorded.
    /// </summary>
    Postponed = 3
}

/// <summary>
/// Why delivering a password wrote one of an account's flags, or left it alone: the row of <c>AccountFlagsPolicy</c>'s
/// ownership ladder that decided it. One ladder decides both flags a delivery can touch, whether the account is
/// enabled (#2064) and whether its password never expires (#2159), each with its own basis. Stated on the delivery's
/// Activity, so an account left disabled or enabled, or with a password that never expires or one that does, is never
/// left unexplained.
/// </summary>
public enum AccountFlagBasis
{
    /// <summary>
    /// The flag was left as the account has it. A propagated password never changes either flag (bar completing a
    /// withheld enable, <c>CompletesWithheldEnable</c>, or carrying an account's first password, <c>SyncRuleSetting</c>),
    /// and an administrator's set with no enable decision leaves the enabled state alone unless the account is still
    /// owed its first password.
    /// </summary>
    LeftAsIs = 0,

    /// <summary>
    /// An administrator named the account and chose what the delivery does: to enable or disable it, or which expiry
    /// the password has, which sets or clears "Password never expires". That choice is applied whatever the Attribute
    /// Flows say; Enforce State puts a flow's value back where it disagrees.
    /// </summary>
    AdministratorChoice = 1,

    /// <summary>
    /// The enabled state only. JIM created the account disabled only because Active Directory refuses an enabled account
    /// with no password, and held back the enabled value the Attribute Flow asked for (#2039). The password now exists,
    /// so the delivery enables the account, which is what the flow asked for.
    /// </summary>
    CompletesWithheldEnable = 2,

    /// <summary>
    /// An export wrote the attribute holding the account's flags (on Active Directory, userAccountControl, which carries
    /// both the enabled state and "Password never expires"), so the Attribute Flow behind that export decides the flag
    /// and the delivery leaves it alone.
    /// </summary>
    LeftToAttributeFlow = 3,

    /// <summary>
    /// Nothing writes the account's flags, so the provisioning Synchronisation Rule's Initial Password decides: Enable
    /// Account on enables the account (off leaves it as it was created), and its expiry sets "Password never expires"
    /// for Never expires and clears it otherwise. Applies to the account's first password whether that is the Initial
    /// Password or a password change delivered in its place (#2160), which keeps its own "must change at next sign-in".
    /// </summary>
    SyncRuleSetting = 4
}

/// <summary>
/// How a password change carrying an account's first password in place of its Initial Password (#2160) relates to the
/// account its delivery reached: whether the Initial Password's promises about the account apply to this delivery.
/// Decided at each attempt, and stated on the delivery's Activity.
/// </summary>
public enum InitialPasswordStandIn
{
    /// <summary>The change carries no account's first password.</summary>
    None = 0,

    /// <summary>
    /// The change reached the account it owes its first password, and the provisioning Synchronisation Rule still sets
    /// an Initial Password, so the rule's promises about the account apply.
    /// </summary>
    Applies = 1,

    /// <summary>
    /// The provisioning Synchronisation Rule no longer sets an Initial Password, or has been deleted. The password is
    /// still delivered, because it is the person's own; the account's flags are left as the change alone decides.
    /// </summary>
    NoLongerSet = 2,

    /// <summary>
    /// The change reached another of this person's Connected System Objects than the one it owes its first password:
    /// the system's Password Synchronisation now targets another Object Type, or an administrator's set named another
    /// account. The promises are not applied to an account they were never made for, and the account they were for is
    /// left without its first password, which the delivery's Activity says.
    /// </summary>
    OtherAccount = 3
}

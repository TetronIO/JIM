// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What one delivery pass over a Connected System's queued password changes achieved (#1119).
/// <para>
/// Reported rather than thrown, for the same reason the initial-password pass reports: a delivery that did not
/// work is an outcome to record against the change it belongs to, not a failure of the pass. A pass that threw
/// would abandon the changes it had not reached and lose the outcomes of the ones it had.
/// </para>
/// </summary>
public class PasswordDeliveryRunResult
{
    /// <summary>Changes delivered and removed from the queue.</summary>
    public int DeliveredCount { get; set; }

    /// <summary>Changes that failed in a way another attempt may resolve, and are scheduled to be retried.</summary>
    public int RetryingCount { get; set; }

    /// <summary>
    /// Changes JIM has stopped trying: the target refused the password, the operation is unsupported, or the
    /// configured attempts ran out. Each stays visible and manually retryable.
    /// </summary>
    public int ParkedCount { get; set; }

    /// <summary>
    /// Changes that outlived their time to live and were expired rather than attempted, before this pass began.
    /// </summary>
    public int ExpiredCount { get; set; }

    /// <summary>
    /// Changes the lane claimed and then gave back unattempted because it was cancelled before reaching them
    /// (#1635). Nothing was counted against them; they are Pending and due again. Not a problem to report, since
    /// the cancellation was asked for, but a number the lane's summary line should carry.
    /// </summary>
    public int ReleasedCount { get; set; }

    /// <summary>
    /// Provisioned rows removed from the queue without a password ever being sent (#1697): the account no
    /// longer exists, or the Synchronisation Rule that provisioned it no longer sets one. Neither a delivery nor
    /// a retry, so it is counted on its own rather than folded into <see cref="DeliveredCount"/> or
    /// <see cref="ParkedCount"/>.
    /// </summary>
    public int WithdrawnCount { get; set; }

    /// <summary>
    /// Passwords delivered to an account whose enabled state an export wrote (#2064), leaving that state to the
    /// Attribute Flow rather than applying the provisioning Synchronisation Rule's Enable Account. Counted so the lane's
    /// summary says how often the flow, not the rule, decided; each delivery's Activity says so too. Included in
    /// <see cref="DeliveredCount"/>.
    /// </summary>
    public int EnabledStateLeftToAttributeFlowCount { get; set; }

    /// <summary>
    /// Passwords delivered to an account whose flags an export wrote (#2159), leaving "Password never expires" to the
    /// Attribute Flow that writes them rather than applying the provisioning Synchronisation Rule's expiry. Counted so
    /// the lane's summary says how often the flow, not the rule, decided; each delivery's Activity says so where it
    /// differs from what the rule asked. Included in <see cref="DeliveredCount"/>.
    /// </summary>
    public int PasswordNeverExpiresLeftToAttributeFlowCount { get; set; }

    /// <summary>
    /// Passwords whose delivery completed an enable JIM withheld until a password existed (#2039, #2064, #2158): the
    /// account was held disabled, at creation or when an Update enabled it, only because Active Directory refuses an
    /// enabled account with no password, and its Attribute Flow still asks for it enabled. Included in
    /// <see cref="DeliveredCount"/>.
    /// </summary>
    public int WithheldEnablesCompletedCount { get; set; }

    /// <summary>
    /// Passwords delivered where the Connected System could not apply everything asked of it, such as an expiry
    /// behaviour it has no equivalent for (#2159): the password is set, and its Activity is completed with a warning
    /// naming what was applied instead. Included in <see cref="DeliveredCount"/>.
    /// </summary>
    public int ExpiryBehaviourNotHonouredCount { get; set; }

    /// <summary>
    /// Password changes delivered as an account's first password in place of its Initial Password (#2160): the
    /// person's own password, with the provisioning Synchronisation Rule's promises about the account (whether it is
    /// enabled, and whether its password never expires). Included in <see cref="DeliveredCount"/>.
    /// </summary>
    public int FirstPasswordsDeliveredInPlaceCount { get; set; }

    /// <summary>
    /// Password changes owing an account its first password that were delivered without the Initial Password's
    /// promises (#2160): the provisioning Synchronisation Rule no longer sets one, or the change reached another of the
    /// person's accounts. Each delivery's Activity says which. Included in <see cref="DeliveredCount"/>.
    /// </summary>
    public int FirstPasswordPromisesNotAppliedCount { get; set; }

    /// <summary>
    /// Changes given back unattempted because an export was being sent to their account (#2173, #2158, #2159): that
    /// export may be disabling it, writing the flags it owns, or holding back an enable the delivery would complete, so
    /// the delivery waits for it and tries again shortly. Nothing is counted against them, and they are neither
    /// retrying nor delivered.
    /// </summary>
    public int PostponedCount { get; set; }

    /// <summary>
    /// True where the Connector could not open its password channel at all, so nothing was attempted. Reported
    /// once for the pass rather than as a failure per change, which would inflate every attempt count for a
    /// problem that belongs to the connection.
    /// </summary>
    public bool CouldNotOpenPasswordConnection { get; set; }

    /// <summary>
    /// True where this Connected System's Connector cannot set passwords at all. The queued changes are left
    /// exactly as they are: the capability may arrive with a Connector upgrade.
    /// </summary>
    public bool ConnectorCannotSetPasswords { get; set; }

    /// <summary>
    /// True where this Connected System requires a secure transport for passwords and the Connector's password
    /// channel is not encrypted. Nothing was sent and no attempt was counted; the queued changes wait for a
    /// secure channel rather than being delivered against the administrator's explicit instruction.
    /// </summary>
    public bool PasswordChannelNotSecure { get; set; }

    /// <summary>
    /// The Connected System names a Connector this build does not have, so nothing could be attempted; the
    /// claimed changes were given back unattempted.
    /// </summary>
    public bool ConnectorCouldNotBeResolved { get; set; }

    /// <summary>
    /// Whether this pass has anything worth telling an administrator about.
    /// </summary>
    public bool HasSomethingToReport =>
        DeliveredCount > 0 || RetryingCount > 0 || ParkedCount > 0 || ExpiredCount > 0 || WithdrawnCount > 0
        || PostponedCount > 0 || CouldNotOpenPasswordConnection || ConnectorCannotSetPasswords || PasswordChannelNotSecure
        || ConnectorCouldNotBeResolved;
}

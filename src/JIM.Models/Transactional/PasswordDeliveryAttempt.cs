// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What one attempt at delivering a queued password change did: what happened to the row (#1697), and for a delivery,
/// which rung of the ownership ladder decided each of the account's flags (#2064, #2159) and whether the Connected
/// System applied everything asked of it (#2159).
/// </summary>
/// <param name="Disposition">What happened to the queue row.</param>
/// <param name="Enablement">For a delivery, which rung decided the account's enabled state; null otherwise.</param>
/// <param name="PasswordNeverExpires">For a delivery, which rung decided "Password never expires"; null otherwise.</param>
/// <param name="ExpiryBehaviourHonoured">
/// For a delivery, false where the Connector set the password but could not apply everything asked of it (the expiry
/// behaviour, or another part of the request such as enabling the account), so the delivery's Activity was completed
/// with a warning. True for every other outcome.
/// </param>
/// <param name="StandIn">
/// For a delivery, whether the password was an account's first password in place of its Initial Password, and how the
/// Initial Password's promises about the account came out (#2160).
/// </param>
public sealed record PasswordDeliveryAttempt(
    PasswordDeliveryDisposition Disposition,
    AccountFlagBasis? Enablement = null,
    AccountFlagBasis? PasswordNeverExpires = null,
    bool ExpiryBehaviourHonoured = true,
    InitialPasswordStandIn StandIn = InitialPasswordStandIn.None)
{
    /// <summary>The row stays in the queue, retrying or parked.</summary>
    public static PasswordDeliveryAttempt Kept { get; } = new(PasswordDeliveryDisposition.Kept);

    /// <summary>The row is removed without a password being sent, because nothing is left to deliver.</summary>
    public static PasswordDeliveryAttempt Withdrawn { get; } = new(PasswordDeliveryDisposition.Withdrawn);

    /// <summary>The row goes back to the queue unattempted, waiting for an export to the account to finish.</summary>
    public static PasswordDeliveryAttempt Postponed { get; } = new(PasswordDeliveryDisposition.Postponed);

    /// <summary>The password was set and the row is removed.</summary>
    public static PasswordDeliveryAttempt Delivered(AccountFlagsDecision flags, bool expiryBehaviourHonoured,
        InitialPasswordStandIn standIn = InitialPasswordStandIn.None) =>
        new(PasswordDeliveryDisposition.Delivered, flags.EnablementBasis, flags.PasswordNeverExpiresBasis, expiryBehaviourHonoured, standIn);
}

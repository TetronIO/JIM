// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What one attempt at delivering a queued password change did: what happened to the row (#1697), and for a delivery,
/// which rule decided the account's enabled state (#2064) and whether the Connected System applied everything asked
/// of it (#2159).
/// </summary>
/// <param name="Disposition">What happened to the queue row.</param>
/// <param name="Enablement">For a delivery, which rule decided the account's enabled state; null otherwise.</param>
/// <param name="ExpiryBehaviourHonoured">
/// For a delivery, false where the Connector set the password but could not apply the requested expiry behaviour (or
/// another part of the request, such as enabling the account), so the delivery's Activity was completed with a
/// warning. True for every other outcome.
/// </param>
public sealed record PasswordDeliveryAttempt(
    PasswordDeliveryDisposition Disposition,
    AccountEnablementBasis? Enablement = null,
    bool ExpiryBehaviourHonoured = true)
{
    /// <summary>The row stays in the queue, retrying or parked.</summary>
    public static PasswordDeliveryAttempt Kept { get; } = new(PasswordDeliveryDisposition.Kept);

    /// <summary>The row is removed without a password being sent, because nothing is left to deliver.</summary>
    public static PasswordDeliveryAttempt Withdrawn { get; } = new(PasswordDeliveryDisposition.Withdrawn);

    /// <summary>The row goes back to the queue unattempted, waiting for an export to the account to finish.</summary>
    public static PasswordDeliveryAttempt Postponed { get; } = new(PasswordDeliveryDisposition.Postponed);

    /// <summary>The password was set and the row is removed.</summary>
    public static PasswordDeliveryAttempt Delivered(AccountEnablementBasis enablement, bool expiryBehaviourHonoured) =>
        new(PasswordDeliveryDisposition.Delivered, enablement, expiryBehaviourHonoured);
}

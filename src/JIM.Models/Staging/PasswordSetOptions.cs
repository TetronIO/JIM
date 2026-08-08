// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// How a Connector should apply a password to a Connected System Object, beyond the password value itself.
/// </summary>
public class PasswordSetOptions
{
    /// <summary>
    /// How the password should behave with respect to expiry once set.
    /// </summary>
    public PasswordExpiryBehaviour ExpiryBehaviour { get; set; } = PasswordExpiryBehaviour.RequireChangeAtNextSignIn;

    /// <summary>
    /// Whether the Connector should enable the account as part of applying the password.
    /// <para>
    /// Null leaves the account's enabled state untouched, which is the right choice for a password reset on an
    /// account that is already in the state the administrator wants.
    /// </para>
    /// <para>
    /// True is for provisioning. Active Directory will not enable an account that does not already hold a
    /// policy-compliant password, so an account has to be created disabled, given its password, and only then
    /// enabled. Folding the enable into the password set keeps that ordering inside the Connector, where the
    /// target's rules are known, rather than leaving callers to rediscover it.
    /// </para>
    /// </summary>
    public bool? EnableAccount { get; set; }
}

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
    /// Whether the Connector should enable the account (true), disable it (false), or leave its enabled state alone
    /// (null) as part of applying the password.
    /// <para>
    /// Null leaves the account's enabled state untouched, which is the right choice for a password reset on an
    /// account that is already in the state the administrator wants, and for an account whose enabled state an
    /// Attribute Flow sets (#2064): the engine decides which, and the Connector applies the answer.
    /// </para>
    /// <para>
    /// True is for provisioning. Active Directory will not enable an account that does not already hold a
    /// policy-compliant password, so an account has to be created disabled, given its password, and only then
    /// enabled. Folding the enable into the password set keeps that ordering inside the Connector, where the
    /// target's rules are known, rather than leaving callers to rediscover it.
    /// </para>
    /// </summary>
    public bool? EnableAccount { get; set; }

    /// <summary>
    /// Whether the Connector should leave the account's "Password never expires" flag exactly as the account has it,
    /// rather than set it for <see cref="PasswordExpiryBehaviour.NeverExpires"/> and clear it for the other two
    /// behaviours. False by default, so a caller that names only an expiry behaviour gets that behaviour applied whole.
    /// <para>
    /// True where something else owns the flag (#2159): an Attribute Flow that writes the attribute holding it (on Active
    /// Directory, userAccountControl), or the account itself for a propagated password, which never changes it. The
    /// engine decides which; the Connector applies the answer. "Must change at next sign-in" belongs to the password
    /// rather than to the flag, so it is still applied for <see cref="PasswordExpiryBehaviour.RequireChangeAtNextSignIn"/>.
    /// Where the flag left set means that cannot take effect, the Connector says so on its result
    /// (<see cref="PasswordSetResult.ExpiryBehaviourWarning"/>). A Connector whose targets have no such flag ignores it.
    /// </para>
    /// </summary>
    public bool LeavePasswordNeverExpiresAsIs { get; set; }
}

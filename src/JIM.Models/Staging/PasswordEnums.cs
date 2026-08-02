// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// How the password JIM sets should behave with respect to expiry.
/// <para>
/// This is deliberately a single tri-state choice rather than two independent switches. Active Directory treats
/// "must change at next sign-in" (pwdLastSet = 0) and "never expires" (the DONT_EXPIRE_PASSWORD flag in
/// userAccountControl) as mutually exclusive, so modelling them separately would let an administrator save a
/// contradiction the target cannot honour.
/// </para>
/// <para>
/// Not every Connected System can honour every state. A Connector declares the states it supports via
/// <see cref="JIM.Models.Interfaces.IConnectorPasswordManagement.SupportedExpiryBehaviours"/> and reports a
/// downgrade on the <see cref="PasswordSetResult"/> when the target cannot honour the requested state, rather
/// than silently ignoring it.
/// </para>
/// </summary>
public enum PasswordExpiryBehaviour
{
    /// <summary>
    /// The user must choose a new password the first time they sign in.
    /// In Active Directory this is pwdLastSet = 0, with DONT_EXPIRE_PASSWORD cleared.
    /// </summary>
    RequireChangeAtNextSignIn = 0,

    /// <summary>
    /// The password ages according to whatever password policy the target applies to the account.
    /// In Active Directory this is pwdLastSet left at the time of the set, with DONT_EXPIRE_PASSWORD cleared.
    /// </summary>
    ExpiresAccordingToTargetPolicy = 1,

    /// <summary>
    /// The password stays valid until someone changes it.
    /// In Active Directory this is the DONT_EXPIRE_PASSWORD flag in userAccountControl.
    /// </summary>
    NeverExpires = 2
}

/// <summary>
/// Classifies why a password could not be set on a Connected System.
/// <para>
/// The classification drives everything downstream (whether the unit of work is retried, parked for
/// administrator attention, or abandoned), so it is part of the Connector contract rather than something
/// inferred from an error string by the caller.
/// </para>
/// </summary>
public enum PasswordSetFailureReason
{
    /// <summary>
    /// The operation succeeded; no failure occurred.
    /// </summary>
    None = 0,

    /// <summary>
    /// A temporary condition prevented the set, such as a network failure, a timeout, or the target being busy.
    /// Retrying without any configuration change is expected to succeed eventually.
    /// </summary>
    Transient = 1,

    /// <summary>
    /// The Connected System configuration prevents the set, such as invalid credentials, insufficient directory
    /// rights, or a channel requirement that is not met (for example Active Directory refusing a password write
    /// over an unencrypted connection). Retrying only helps once an administrator has corrected the configuration.
    /// </summary>
    ConfigurationFault = 2,

    /// <summary>
    /// The target accepted the request but rejected the password value itself, because it does not satisfy the
    /// password policy in force for that account. Retrying with the same value will always fail; the generator
    /// configuration has to change first.
    /// </summary>
    PolicyRejection = 3,

    /// <summary>
    /// The object to set the password on could not be found in the Connected System.
    /// </summary>
    TargetObjectNotFound = 4,

    /// <summary>
    /// The Connected System cannot set passwords for this object at all, for example because the object type has
    /// no credential of its own.
    /// </summary>
    UnsupportedOperation = 5
}

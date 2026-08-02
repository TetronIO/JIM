// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The individual questions a password channel preflight asks of a Connected System.
/// <para>
/// Each is something that can be established without writing anything, and each maps to a failure administrators
/// hit in practice. Nothing here proves a password set will succeed; only setting one does that. What these
/// answer is the larger question of whether it stands a chance.
/// </para>
/// </summary>
public enum PasswordPreflightCheck
{
    /// <summary>
    /// Whether JIM can open and bind the password channel at all, using the Connected System's credentials.
    /// Everything else depends on this, so a failure here leaves the rest undetermined rather than failed.
    /// </summary>
    Connection = 0,

    /// <summary>
    /// Whether the password channel is encrypted. A password set puts the password on the wire in the clear at the
    /// LDAP layer, so this is a warning rather than a pass or a failure: JIM allows it, having said what it costs.
    /// </summary>
    Encryption = 1,

    /// <summary>
    /// Whether the mechanism JIM would use to set a password is available on this target. Which mechanism that is
    /// depends on what the target turns out to be, so this check reports the mechanism as well as its availability.
    /// </summary>
    PasswordMechanism = 2,

    /// <summary>
    /// Whether the account JIM connects as holds the rights to reset a password on the objects it would provision.
    /// The single most common reason a password set fails, and the hardest to establish without trying it.
    /// </summary>
    ResetRights = 3,

    /// <summary>
    /// Whether the target's password policy could be read, which is what allows JIM to pre-fill a generator that
    /// produces compliant passwords rather than making the administrator retype rules the target already publishes.
    /// </summary>
    PolicyDiscovery = 4
}

/// <summary>
/// The outcome of a single preflight check.
/// <para>
/// <see cref="CouldNotDetermine"/> is deliberately distinct from <see cref="Failed"/> and carries no reassurance.
/// A check that cannot see the answer must say exactly that: directories routinely refuse a read by returning
/// nothing rather than an error, so an unknown reported as a pass is how an administrator ends up confident in a
/// channel that does not work.
/// </para>
/// </summary>
public enum PasswordPreflightState
{
    /// <summary>The check established that this will not stand in the way of setting a password.</summary>
    Passed = 0,

    /// <summary>
    /// The check found something that works but should not be relied on, and that the administrator should be
    /// making a deliberate choice about.
    /// </summary>
    Warning = 1,

    /// <summary>The check established that password setting will not work until this is corrected.</summary>
    Failed = 2,

    /// <summary>
    /// The check could not establish an answer either way. Not a pass: the answer may well be bad.
    /// </summary>
    CouldNotDetermine = 3
}

/// <summary>
/// The headline outcome of a preflight run, derived from its checks.
/// <para>
/// A summary for the top of a screen and nothing more. The individual checks are the useful output, because they
/// name what to fix; this only decides how loudly to say it.
/// </para>
/// </summary>
public enum PasswordPreflightOutcome
{
    /// <summary>Every check passed. As good an answer as a preflight can give, and still not a guarantee.</summary>
    Ready = 0,

    /// <summary>Nothing blocks a password set, but something is not as it should be.</summary>
    ReadyWithWarnings = 1,

    /// <summary>
    /// Nothing failed, but at least one check could not see enough to answer. Password setting may work; JIM
    /// cannot say so.
    /// </summary>
    Inconclusive = 2,

    /// <summary>At least one check established that password setting will not work as configured.</summary>
    NotReady = 3
}

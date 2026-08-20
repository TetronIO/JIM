// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// What one pass of initial password delivery did, for the Activity and the run's log summary.
/// <para>
/// <b>Carries no password, and no account identity.</b> Counts and the target's own reasons are what an
/// administrator needs to know whether the run went well; the detail of which account is owed what lives on
/// the outstanding records themselves, where the portal reads it.
/// </para>
/// </summary>
public class InitialPasswordRunResult
{
    /// <summary>
    /// Accounts JIM tried to set a password on this pass.
    /// </summary>
    public int AttemptedCount { get; set; }

    /// <summary>
    /// Accounts that now have their initial password.
    /// </summary>
    public int DeliveredCount { get; set; }

    /// <summary>
    /// Accounts whose delivery failed in a way that trying again could resolve: an unreachable target, a right
    /// not yet granted, an object not yet replicated. These stay outstanding and are attempted on the next run.
    /// </summary>
    public int RetryingCount { get; set; }

    /// <summary>
    /// Accounts parked for an administrator, because no number of attempts would change the answer: the target
    /// refused the password, or cannot set passwords on that kind of object at all.
    /// </summary>
    public int ParkedCount { get; set; }

    /// <summary>
    /// Records dropped without an attempt because there was nothing left to do: the Synchronisation Rule no
    /// longer asks for an initial password, or has been deleted. Not a failure; the work simply expired with
    /// the configuration that created it.
    /// </summary>
    public int NoLongerApplicableCount { get; set; }

    /// <summary>
    /// Records whose time to live passed before they could be delivered, so JIM stopped trying and said so.
    /// <para>
    /// Counted rather than quietly cleaned up. These are accounts that were provisioned and never got a working
    /// password, which is precisely the outcome an administrator has to know about; a silent removal would take
    /// the last evidence of it with them.
    /// </para>
    /// </summary>
    public int ExpiredCount { get; set; }

    /// <summary>
    /// True when the Connected System's Connector cannot set passwords at all, so nothing was attempted. The
    /// outstanding records are left exactly as they are: the capability may arrive with a Connector upgrade,
    /// and discarding the work would lose the record that the accounts still need one.
    /// </summary>
    public bool ConnectorCannotSetPasswords { get; set; }

    /// <summary>
    /// True when the password connection could not be opened, so nothing was attempted. Distinct from every
    /// account failing individually: one connection problem is one thing to fix, not N.
    /// </summary>
    public bool CouldNotOpenPasswordConnection { get; set; }

    /// <summary>
    /// Why the password connection could not be opened, where that is what happened.
    /// </summary>
    public string? PasswordConnectionErrorMessage { get; set; }

    /// <summary>
    /// True when the Connected System requires a secure transport for passwords and the Connector's password
    /// channel is not encrypted (#1119), so nothing was sent. Reported once for the pass rather than as a failure
    /// per account, for the same reason as a connection that could not be opened: the problem belongs to the
    /// channel, and counting it against every account would inflate an attempt count that is supposed to mean
    /// distinct attempts at giving that account a password. The accounts stay owed one.
    /// </summary>
    public bool PasswordChannelNotSecure { get; set; }

    /// <summary>
    /// True when anything at all happened, which is what decides whether a run is worth narrating.
    /// </summary>
    public bool HasSomethingToReport =>
        AttemptedCount > 0 || NoLongerApplicableCount > 0 || ExpiredCount > 0 ||
        CouldNotOpenPasswordConnection || ConnectorCannotSetPasswords || PasswordChannelNotSecure;
}

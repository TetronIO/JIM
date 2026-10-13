// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Application.Servers;

/// <summary>
/// What export staging says, on its Activities, about the Initial Password of an account it has just provisioned
/// where the queue already held a password change for the person on that Connected System (#2160): carried by that
/// change, replacing it, or not queued at all. Every one of these is recorded, so an account's first password is never
/// decided without a word.
/// </summary>
internal static class InitialPasswordStagingMessages
{
    /// <summary>
    /// A password change already waiting for the person is the account's first password, carrying the Initial
    /// Password's promises about the account; says what was done to it to get it delivered.
    /// </summary>
    internal static string Carried(string connectedSystemName, ProvisionedPasswordStagingOutcome outcome)
    {
        var message = $"Initial password not queued for {connectedSystemName}: a password change already waiting for this person there will be " +
                      "this Connected System Object's first password instead, keeping its own password and expiry. Its delivery applies this " +
                      "Synchronisation Rule's Initial Password settings for enabling the Connected System Object and for whether its password never " +
                      "expires, except where an export has written those itself.";

        return outcome.ExistingStatus switch
        {
            PendingPasswordChangeStatus.Parked =>
                $"{message} It had been parked ({Describe(outcome)}), and is due again now that the Connected System Object exists.",
            PendingPasswordChangeStatus.Delivering =>
                $"{message} It was being delivered as the Connected System Object was created, and is delivered again with these settings.",
            _ when outcome.ExistingFailureReason == PasswordSetFailureReason.TargetObjectNotFound =>
                $"{message} It was waiting for the Connected System Object to exist, and is due now.",
            _ => message
        };
    }

    /// <summary>
    /// Why the Initial Password replaced a password change for the person that was still waiting, ending in a full stop,
    /// or null where the row it replaced owed nothing (expired, cancelled, or an Initial Password for an account that is
    /// gone), which needs no explaining.
    /// </summary>
    internal static string? ReplacedWaitingChange(string connectedSystemName, ProvisionedPasswordStagingOutcome outcome)
    {
        if (outcome.ExistingHeld)
            return $"it is held while Password Synchronisation is switched off for {connectedSystemName}, so it had not reached it, " +
                   "and a Connected System Object's first password is delivered whatever the pause.";

        if (outcome.ExistingAccountGone)
            return "it was set on a Connected System Object that no longer exists there, or is no longer this person's.";

        if (outcome.ExistingStatus == PendingPasswordChangeStatus.Parked && outcome.ExistingOrigin != PendingPasswordChangeOrigin.Provisioned)
            return $"JIM had stopped trying to deliver it ({Describe(outcome)}).";

        return null;
    }

    /// <summary>
    /// The queue's one row for the person and Connected System holds their own password change for the Object Type the
    /// system's Password Synchronisation sets passwords on, which the new account is not, so nothing was queued. A person
    /// has at most one joined Connected System Object per Connected System, so that change cannot be delivered while this
    /// one is theirs; says what to do about it.
    /// </summary>
    internal static string Conflicted(string connectedSystemName) =>
        $"Initial password not queued for {connectedSystemName}: this person's own password change is waiting there for the Object Type that " +
        "Password Synchronisation sets passwords on, which this Connected System Object is not, and JIM holds one password change per person " +
        "per Connected System. That change cannot be delivered while this Connected System Object is the person's: cancel it on Operations > " +
        "Passwords, then set a password on this Connected System Object with the set-password action, choosing to enable it if it should be.";

    private static string Describe(ProvisionedPasswordStagingOutcome outcome)
    {
        var reason = outcome.ExistingFailureReason switch
        {
            PasswordSetFailureReason.PolicyRejection => "the password did not meet the system's password policy",
            PasswordSetFailureReason.ConfigurationFault => "a configuration problem stopped it being set",
            PasswordSetFailureReason.UnsupportedOperation => "the system cannot set a password on it",
            PasswordSetFailureReason.TargetObjectNotFound => "the Connected System Object did not exist yet",
            PasswordSetFailureReason.Transient => "the system could not be reached",
            _ => "for a reason JIM did not record"
        };

        return string.IsNullOrWhiteSpace(outcome.ExistingTargetMessage)
            ? reason
            : $"{reason}: {outcome.ExistingTargetMessage.Trim()}";
    }
}

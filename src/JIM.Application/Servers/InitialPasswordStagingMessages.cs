// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Localisation;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

namespace JIM.Application.Servers;

/// <summary>
/// What export staging says, on its Activities, about the Initial Password of an account it has just provisioned
/// where the queue already held a password change for the person on that Connected System (#2160): carried by that
/// change, or replacing it. Every one of these is recorded, so an account's first password is never decided without a
/// word.
/// </summary>
internal static class InitialPasswordStagingMessages
{
    /// <summary>
    /// A password change already waiting for the person is the account's first password, carrying the Initial
    /// Password's promises about the account; says what was done to it to get it delivered.
    /// </summary>
    internal static JimMessage Carried(string connectedSystemName, ProvisionedPasswordStagingOutcome outcome)
    {
        var message = ActivityMessage.Create(nameof(ActivityMessages.InitialPasswordCarried), connectedSystemName);

        return outcome.ExistingStatus switch
        {
            PendingPasswordChangeStatus.Parked =>
                ActivityMessage.Create(nameof(ActivityMessages.InitialPasswordCarriedParked), message, Describe(outcome)),
            PendingPasswordChangeStatus.Delivering =>
                ActivityMessage.Create(nameof(ActivityMessages.InitialPasswordCarriedDelivering), message),
            _ when outcome.ExistingFailureReason == PasswordSetFailureReason.TargetObjectNotFound =>
                ActivityMessage.Create(nameof(ActivityMessages.InitialPasswordCarriedWaiting), message),
            _ => message
        };
    }

    /// <summary>An Initial Password queued for delivery, with why it replaced a waiting change where it did.</summary>
    internal static JimMessage Queued(string connectedSystemName, JimMessage? replaced) => replaced == null
        ? ActivityMessage.Create(nameof(ActivityMessages.InitialPasswordQueued), connectedSystemName)
        : ActivityMessage.Create(nameof(ActivityMessages.InitialPasswordQueuedReplacing), connectedSystemName, replaced);

    /// <summary>Told to the person's own password change, which lost the Connected System to the Initial Password.</summary>
    internal static JimMessage NotSet(string connectedSystemName, JimMessage replaced) =>
        ActivityMessage.Create(nameof(ActivityMessages.PasswordNotSetReplaced), connectedSystemName, replaced);

    /// <summary>
    /// Why the Initial Password replaced a password change for the person that was still waiting, ending in a full stop,
    /// or null where the row it replaced owed nothing (expired, cancelled, or an Initial Password for an account that is
    /// gone), which needs no explaining.
    /// </summary>
    internal static JimMessage? ReplacedWaitingChange(string connectedSystemName, ProvisionedPasswordStagingOutcome outcome)
    {
        if (outcome.ExistingHeld)
            return ActivityMessage.Create(nameof(ActivityMessages.PasswordReplacedHeld), connectedSystemName);

        if (outcome.ExistingAccountGone)
            return ActivityMessage.Create(nameof(ActivityMessages.PasswordReplacedAccountGone));

        if (outcome.ExistingForAnotherObjectType)
            return ActivityMessage.Create(nameof(ActivityMessages.PasswordReplacedOtherObjectType), connectedSystemName);

        if (outcome.ExistingStatus == PendingPasswordChangeStatus.Parked && outcome.ExistingOrigin != PendingPasswordChangeOrigin.Provisioned)
            return ActivityMessage.Create(nameof(ActivityMessages.PasswordReplacedStoppedTrying), Describe(outcome));

        return null;
    }

    private static JimMessage Describe(ProvisionedPasswordStagingOutcome outcome)
    {
        var reason = outcome.ExistingFailureReason switch
        {
            PasswordSetFailureReason.PolicyRejection => ActivityMessage.Create(nameof(ActivityMessages.PasswordParkedPolicy)),
            PasswordSetFailureReason.ConfigurationFault => ActivityMessage.Create(nameof(ActivityMessages.PasswordParkedConfiguration)),
            PasswordSetFailureReason.UnsupportedOperation => ActivityMessage.Create(nameof(ActivityMessages.PasswordParkedUnsupported)),
            PasswordSetFailureReason.TargetObjectNotFound => ActivityMessage.Create(nameof(ActivityMessages.PasswordParkedObjectMissing)),
            PasswordSetFailureReason.Transient => ActivityMessage.Create(nameof(ActivityMessages.PasswordParkedUnreachable)),
            _ => ActivityMessage.Create(nameof(ActivityMessages.PasswordParkedUnrecorded))
        };

        return string.IsNullOrWhiteSpace(outcome.ExistingTargetMessage)
            ? reason
            : ActivityMessage.Create(nameof(ActivityMessages.PasswordParkedWithTargetMessage), reason, outcome.ExistingTargetMessage.Trim());
    }
}

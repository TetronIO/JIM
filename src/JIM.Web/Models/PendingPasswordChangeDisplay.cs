// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Resources;
using System.Globalization;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using MudBlazor;

namespace JIM.Web.Models;

/// <summary>
/// How a queued password change reads on screen (#1119).
/// <para>
/// Shared by the Passwords tab of Operations and the Metaverse Object's panel because they show the same
/// rows and must say the same thing about them. Written separately in each, they had already drifted: one named
/// the failure reason before the target's message and the other showed the message alone, so the same parked
/// change read as two different problems depending on which page an administrator opened.
/// </para>
/// </summary>
public static class PendingPasswordChangeDisplay
{
    /// <summary>
    /// What the row's state is called on screen.
    /// <para>
    /// Pending is shown as "Waiting", which is what it is from the administrator's side. "Pending" is the
    /// storage name and says nothing about whether anything is happening. Delivering is the one state where
    /// something is happening right now (the Password Delivery Service has claimed the row, #1635), and reads as
    /// itself.
    /// </para>
    /// </summary>
    public static string Status(PendingPasswordChangeHeader change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return change.Status switch
        {
            PendingPasswordChangeStatus.Pending => DisplayText.PasswordChangeStatusWaiting,
            PendingPasswordChangeStatus.Delivering => DisplayText.PasswordChangeStatusDelivering,
            PendingPasswordChangeStatus.Parked => DisplayText.PasswordChangeStatusParked,
            PendingPasswordChangeStatus.Expired => DisplayText.PasswordChangeStatusExpired,
            PendingPasswordChangeStatus.Cancelled => DisplayText.PasswordChangeStatusCancelled,
            _ => change.Status.ToString()
        };
    }

    /// <summary>
    /// The sentence that sits beneath the status: why the change is where it is, or null where the status says
    /// everything there is to say.
    /// <para>
    /// The reason is named before the target's own message rather than instead of it. The message alone is the
    /// target speaking, which is where the remedy usually lives; the reason alone is JIM's classification, which
    /// is what decides whether another attempt could ever help. An administrator needs both.
    /// </para>
    /// </summary>
    public static string? Detail(PendingPasswordChangeHeader change)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (change.Status == PendingPasswordChangeStatus.Cancelled)
        {
            return change.CancelledByName == null
                ? DisplayText.PasswordChangeCancelledByAdministrator
                : string.Format(CultureInfo.CurrentCulture, DisplayText.PasswordChangeCancelledBy, change.CancelledByName);
        }

        // Held is answered before the failure, and before returning nothing. A change waiting on a switched-off
        // system usually has no failure at all, so without this the row reads "Waiting" with no explanation,
        // which is the one state where what it is waiting for is a person rather than a retry.
        if (change.IsHeld)
        {
            if (change.FailureReason is null or PasswordSetFailureReason.None)
                return DisplayText.PasswordChangeHeld;

            var lastAttempt = Reason(change.FailureReason.Value);
            if (!string.IsNullOrWhiteSpace(change.TargetMessage))
                lastAttempt = string.Format(CultureInfo.CurrentCulture, DisplayText.PasswordChangeLastAttemptTargetMessage, lastAttempt, change.TargetMessage);
            return string.Format(CultureInfo.CurrentCulture, DisplayText.PasswordChangeHeldAfterAttempt, lastAttempt);
        }

        if (change.FailureReason is null or PasswordSetFailureReason.None)
        {
            // A provisioned row with nothing wrong yet is still worth a line: without this it reads as a bare
            // "Waiting" indistinguishable from an ordinary propagated change, when what it is actually waiting
            // for is its very first delivery attempt (#1697).
            return change.Origin == PendingPasswordChangeOrigin.Provisioned
                ? DisplayText.PasswordChangeInitial
                : null;
        }

        var reason = Reason(change.FailureReason.Value);
        return string.IsNullOrWhiteSpace(change.TargetMessage)
            ? reason
            : string.Format(CultureInfo.CurrentCulture, DisplayText.PasswordChangeLastAttemptTargetMessage, reason, change.TargetMessage);
    }

    /// <summary>
    /// How a delivery attempt failed, in words rather than in the enum's spelling.
    /// </summary>
    public static string Reason(PasswordSetFailureReason reason) => reason switch
    {
        PasswordSetFailureReason.Transient => DisplayText.PasswordFailureTransient,
        PasswordSetFailureReason.ConfigurationFault => DisplayText.PasswordFailureConfigurationFault,
        PasswordSetFailureReason.PolicyRejection => DisplayText.PasswordFailurePolicyRejection,
        PasswordSetFailureReason.TargetObjectNotFound => DisplayText.PasswordFailureTargetObjectNotFound,
        PasswordSetFailureReason.UnsupportedOperation => DisplayText.PasswordFailureUnsupportedOperation,
        _ => reason.ToString()
    };

    /// <summary>
    /// The kind chip's word for where a change came from: an administrator's explicit set, a password JIM
    /// propagated on the person's behalf, or the first password an export just provisioned (#1697).
    /// </summary>
    public static string OriginLabel(PendingPasswordChangeOrigin origin) => origin switch
    {
        PendingPasswordChangeOrigin.Explicit => DisplayText.PasswordOriginSet,
        PendingPasswordChangeOrigin.Provisioned => DisplayText.PasswordOriginInitial,
        _ => DisplayText.PasswordOriginPropagated
    };

    /// <summary>
    /// The kind chip's colour, kept in step with <see cref="OriginLabel"/> so a call site cannot show one
    /// origin's word beside another's colour.
    /// </summary>
    public static Color OriginColour(PendingPasswordChangeOrigin origin) => origin switch
    {
        PendingPasswordChangeOrigin.Explicit => Color.Primary,
        PendingPasswordChangeOrigin.Provisioned => Color.Secondary,
        _ => Color.Default
    };

    /// <summary>
    /// The secondary marker beside the kind chip of a set or propagated change that is also a Connected System Object's
    /// first password (#2160, <see cref="PendingPasswordChangeHeader.CarriesFirstPassword"/>).
    /// </summary>
    public static string FirstPasswordLabel => DisplayText.PasswordFirstPasswordLabel;

    /// <summary>
    /// What the "First password" marker means, for its tooltip.
    /// </summary>
    public static string FirstPasswordTooltip => DisplayText.PasswordFirstPasswordTooltip;

    /// <summary>
    /// The question asked before cancelling one change: whose change, to where, and what cancelling leaves behind,
    /// including, for an Initial Password or a change carrying a Connected System Object's first password (#2160), that
    /// the Connected System Object is left with no password from JIM at all.
    /// </summary>
    public static string CancelConfirmation(PendingPasswordChangeHeader change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var who = change.MetaverseObjectDisplayName ?? DisplayText.PasswordCancelThisMetaverseObject;
        var question = string.Format(CultureInfo.CurrentCulture, DisplayText.PasswordCancelQuestion, who, change.ConnectedSystemName);

        return string.Format(
            CultureInfo.CurrentCulture,
            OwesFirstPassword(change) ? DisplayText.PasswordCancelFirstPasswordConsequence : DisplayText.PasswordCancelOrdinaryConsequence,
            question);
    }

    /// <summary>
    /// Whether cancelling the change leaves a Connected System Object without its first password: an Initial Password,
    /// or a change carrying the first password in its place (#2160). Where it does, the portal asks before cancelling it.
    /// </summary>
    public static bool OwesFirstPassword(PendingPasswordChangeHeader change) =>
        change.Origin == PendingPasswordChangeOrigin.Provisioned || change.CarriesFirstPassword;
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;

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
    /// storage name and says nothing about whether anything is happening.
    /// </para>
    /// </summary>
    public static string Status(PendingPasswordChangeHeader change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return change.Status switch
        {
            PendingPasswordChangeStatus.Pending => "Waiting",
            PendingPasswordChangeStatus.Parked => "Parked",
            PendingPasswordChangeStatus.Expired => "Expired",
            PendingPasswordChangeStatus.Cancelled => "Cancelled",
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
                ? "Cancelled by an administrator"
                : $"Cancelled by {change.CancelledByName}";
        }

        // Held is answered before the failure, and before returning nothing. A change waiting on a switched-off
        // system usually has no failure at all, so without this the row reads "Waiting" with no explanation,
        // which is the one state where what it is waiting for is a person rather than a retry.
        if (change.IsHeld)
        {
            return change.FailureReason is null or PasswordSetFailureReason.None
                ? "Waiting for Password Synchronisation to be switched on for this Connected System"
                : $"Waiting for Password Synchronisation to be switched on for this Connected System. " +
                  $"Last attempt: {Reason(change.FailureReason.Value)}" +
                  (string.IsNullOrWhiteSpace(change.TargetMessage) ? string.Empty : $": {change.TargetMessage}");
        }

        if (change.FailureReason is null or PasswordSetFailureReason.None)
            return null;

        var reason = Reason(change.FailureReason.Value);
        return string.IsNullOrWhiteSpace(change.TargetMessage) ? reason : $"{reason}: {change.TargetMessage}";
    }

    /// <summary>
    /// How a delivery attempt failed, in words rather than in the enum's spelling.
    /// </summary>
    public static string Reason(PasswordSetFailureReason reason) => reason switch
    {
        PasswordSetFailureReason.Transient => "Target unavailable",
        PasswordSetFailureReason.ConfigurationFault => "Configuration fault",
        PasswordSetFailureReason.PolicyRejection => "Policy rejection",
        PasswordSetFailureReason.TargetObjectNotFound => "Account not found",
        PasswordSetFailureReason.UnsupportedOperation => "Unsupported operation",
        _ => reason.ToString()
    };
}

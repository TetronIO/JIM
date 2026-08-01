// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional;

/// <summary>
/// What came of one attempt to give a newly provisioned account its first password.
/// <para>
/// <b>Deliberately carries no password.</b> The value is generated at the moment of delivery, handed to the
/// Connector, and dropped. Returning it here would put it in the hands of the export path, which logs its
/// results and records them on Activities, and there is no reason for any of that to know it. The one place a
/// generated password is shown to a person is the administrator's own set-password dialog, which asks for it
/// explicitly.
/// </para>
/// </summary>
public class InitialPasswordDeliveryResult
{
    public required InitialPasswordDeliveryOutcome Outcome { get; init; }

    /// <summary>
    /// How the Connector classified a failure, which is what decides whether JIM tries again. Null when the
    /// password was set, or when there was nothing to do.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; init; }

    /// <summary>
    /// What to tell an administrator: the target's own reason where there is one, otherwise JIM's. Carries no
    /// password; the Connector removes anything resembling one before it reaches here.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// The expiry behaviour the target could actually honour, which is not always the one that was asked for.
    /// A directory with no equivalent of "never expires" reports the behaviour it applied instead, and that is
    /// worth recording rather than assuming the request was met.
    /// </summary>
    public PasswordExpiryBehaviour? AppliedExpiryBehaviour { get; init; }

    public static InitialPasswordDeliveryResult Delivered(PasswordExpiryBehaviour appliedExpiryBehaviour, string? message = null) =>
        new()
        {
            Outcome = InitialPasswordDeliveryOutcome.Delivered,
            AppliedExpiryBehaviour = appliedExpiryBehaviour,
            Message = message
        };

    public static InitialPasswordDeliveryResult Retry(PasswordSetFailureReason failureReason, string message) =>
        new() { Outcome = InitialPasswordDeliveryOutcome.Retry, FailureReason = failureReason, Message = message };

    public static InitialPasswordDeliveryResult Parked(PasswordSetFailureReason failureReason, string message) =>
        new() { Outcome = InitialPasswordDeliveryOutcome.Parked, FailureReason = failureReason, Message = message };

    public static InitialPasswordDeliveryResult NotApplicable(string message) =>
        new() { Outcome = InitialPasswordDeliveryOutcome.NotApplicable, Message = message };
}

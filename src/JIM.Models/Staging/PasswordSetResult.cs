// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// The outcome of a Connector setting a password on a Connected System Object.
/// <para>
/// This type never carries the password value. Nothing that flows back out of a password set may contain the
/// secret, because results are logged, recorded against Activities, and surfaced in the administration portal.
/// </para>
/// </summary>
public class PasswordSetResult
{
    /// <summary>
    /// Whether the password was set on the Connected System.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Why the password could not be set. <see cref="PasswordSetFailureReason.None"/> when the set succeeded.
    /// </summary>
    public PasswordSetFailureReason FailureReason { get; set; } = PasswordSetFailureReason.None;

    /// <summary>
    /// A human-readable explanation of the failure, ideally the target's own verbatim reason so an administrator
    /// can act on it. Null when the set succeeded. Must never contain the password.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The expiry behaviour actually applied to the account, which may differ from the one requested when the
    /// target cannot honour it. Null when the set failed.
    /// </summary>
    public PasswordExpiryBehaviour? AppliedExpiryBehaviour { get; set; }

    /// <summary>
    /// Set when the target could not honour the requested expiry behaviour and something else was applied instead.
    /// Names what was asked for and what happened, so the difference can be reported rather than silently dropped.
    /// Null when the requested behaviour was applied as asked.
    /// </summary>
    public string? ExpiryBehaviourWarning { get; set; }

    /// <summary>
    /// Whether the requested expiry behaviour was applied exactly as asked.
    /// </summary>
    public bool ExpiryBehaviourHonoured => Success && ExpiryBehaviourWarning == null;

    /// <summary>
    /// Creates a successful result where the requested expiry behaviour was applied as asked.
    /// </summary>
    public static PasswordSetResult Succeeded(PasswordExpiryBehaviour appliedExpiryBehaviour) =>
        new()
        {
            Success = true,
            AppliedExpiryBehaviour = appliedExpiryBehaviour
        };

    /// <summary>
    /// Creates a successful result where the target could not honour the requested expiry behaviour, recording
    /// what was applied instead and why. The password itself was still set, so this is a success with a caveat
    /// rather than a failure.
    /// </summary>
    public static PasswordSetResult SucceededWithExpiryDowngrade(PasswordExpiryBehaviour appliedExpiryBehaviour, string warning) =>
        new()
        {
            Success = true,
            AppliedExpiryBehaviour = appliedExpiryBehaviour,
            ExpiryBehaviourWarning = warning
        };

    /// <summary>
    /// Creates a failed result with a classification and a human-readable reason.
    /// </summary>
    public static PasswordSetResult Failed(PasswordSetFailureReason failureReason, string errorMessage) =>
        new()
        {
            Success = false,
            FailureReason = failureReason,
            ErrorMessage = errorMessage
        };
}

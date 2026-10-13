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
    /// What the account's password now does about expiry, as the Connector left it: the behaviour asked for where it
    /// was applied, otherwise what the account has instead (for example <see cref="PasswordExpiryBehaviour.NeverExpires"/>
    /// where the Connector was told to leave "Password never expires" alone and the account carries it). Null when the
    /// set failed.
    /// </summary>
    public PasswordExpiryBehaviour? AppliedExpiryBehaviour { get; set; }

    /// <summary>
    /// Set when what the Connector was asked to apply could not take effect: a behaviour the target has no equivalent
    /// for, or "must change at next sign-in" on an account whose "Password never expires" the Connector was told to
    /// leave set, which the target then ignores. Names what was asked for and what happened, so the difference is
    /// reported rather than silently dropped. Null when everything asked was applied. A difference in a flag the
    /// Connector was told to leave alone is not a warning; it is reported through <see cref="AppliedExpiryBehaviour"/>.
    /// </summary>
    public string? ExpiryBehaviourWarning { get; set; }

    /// <summary>
    /// Whether everything the Connector was asked to apply took effect. True where the account's expiry differs from the
    /// behaviour requested only because the Connector was told to leave "Password never expires" as it is; compare
    /// <see cref="AppliedExpiryBehaviour"/> with the request to see that.
    /// </summary>
    public bool ExpiryBehaviourHonoured => Success && ExpiryBehaviourWarning == null;

    /// <summary>
    /// Whether applying the password set or cleared the account's "Password never expires" flag, so that it now differs
    /// from what the account had (#2159). False when the flag was already as asked, was left alone, or the target has no
    /// such flag. Lets the caller warn where something else will put the flag back.
    /// </summary>
    public bool PasswordNeverExpiresChanged { get; set; }

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

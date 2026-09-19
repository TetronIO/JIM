// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Which password an initial-password configuration resolves to, or why none can be sent (#1697).
/// <para>
/// A refusal always parks rather than retries: every reason one is returned here (an absent static password, an
/// encryption key that no longer opens it, a value or a generator policy the target will refuse) is fixed only by
/// a person changing the configuration, never by JIM trying again unchanged.
/// </para>
/// </summary>
public class InitialPasswordResolution
{
    /// <summary>
    /// The password to send, or null when nothing can be. Present only when the resolution is usable.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// How the configuration failed to resolve, or null when the resolution is usable.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; init; }

    /// <summary>
    /// What to tell an administrator about a refusal, or null when the resolution is usable. Never carries the
    /// password or the ciphertext it was decrypted from.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Whether a password was resolved and is ready to send.
    /// </summary>
    public bool IsUsable => Password != null;

    public static InitialPasswordResolution Usable(string password) => new() { Password = password };

    public static InitialPasswordResolution Refused(PasswordSetFailureReason reason, string message) =>
        new() { FailureReason = reason, Message = message };
}

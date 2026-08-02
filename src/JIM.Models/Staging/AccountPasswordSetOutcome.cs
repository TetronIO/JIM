// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What came of setting a password on one account, within a fan-out across several (issue #1172).
/// <para>
/// Reported per account rather than rolled up, because a fan-out has no single outcome: three systems are three
/// independent writes with no transaction between them, and "two of three succeeded" leaves the administrator
/// to work out which, with somebody on the telephone whose password now works in two places out of three.
/// </para>
/// <para>
/// Carries no password value, like everything else that flows back out of a password set.
/// </para>
/// </summary>
public class AccountPasswordSetOutcome
{
    public required Guid ConnectedSystemObjectId { get; init; }

    public required int ConnectedSystemId { get; init; }

    public required string ConnectedSystemName { get; init; }

    /// <summary>
    /// The classified result, carrying the target's own reason where it refused.
    /// </summary>
    public required PasswordSetResult Result { get; init; }

    /// <summary>
    /// How long this account's attempt took, for the progress rail.
    /// </summary>
    public required TimeSpan Duration { get; init; }
}

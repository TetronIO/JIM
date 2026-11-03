// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;

namespace JIM.Models.Exchange;

/// <summary>
/// What converging one queued Exchange change came to (#2021): delivered, or how it failed.
/// </summary>
public sealed record ExchangeConvergeResult
{
    private ExchangeConvergeResult()
    {
    }

    /// <summary>Whether Exchange now has the declared state.</summary>
    public bool Delivered { get; private init; }

    /// <summary>
    /// For a delivered change, whether Exchange had to change anything; false when the recipient was already in its
    /// declared state (PRD Scenario 7).
    /// </summary>
    public bool ChangedAnything { get; private init; }

    /// <summary>How the attempt failed, or null for a delivered change.</summary>
    public ExchangeChangeFailureKind? FailureKind { get; private init; }

    /// <summary>
    /// Exchange's own words on the failure (or JIM's, where Exchange gave none), or null for a delivered change.
    /// Never carries anything secret.
    /// </summary>
    public string? Message { get; private init; }

    /// <summary>Exchange has the declared state.</summary>
    public static ExchangeConvergeResult Success(bool changedAnything) => new() { Delivered = true, ChangedAnything = changedAnything };

    /// <summary>The attempt failed in the way <paramref name="kind"/> says.</summary>
    public static ExchangeConvergeResult Failure(ExchangeChangeFailureKind kind, string? message) =>
        new() { FailureKind = kind, Message = message };
}

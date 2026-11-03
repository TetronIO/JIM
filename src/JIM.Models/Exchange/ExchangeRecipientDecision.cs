// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exchange;

/// <summary>
/// What export staging does with one object's declared Exchange recipient state (#2021), as
/// <see cref="ExchangeRecipientRules.Decide"/> decides it.
/// </summary>
public sealed record ExchangeRecipientDecision
{
    public required ExchangeRecipientDecisionKind Kind { get; init; }

    /// <summary>
    /// The sentence an administrator reads on the follow-up Activity: why the type was refused, or which type change was
    /// not made. Null when the decision is to stage, or there is nothing to do.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Whether a staged change enables the recipient or updates it. Null unless the decision is to stage.</summary>
    public ExchangeRecipientChangeKind? Change { get; init; }
}

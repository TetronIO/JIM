// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Utilities;

namespace JIM.Web.Models;

/// <summary>
/// How a queued Exchange change's outcome reads on screen (#2021, Phase 6): its title, its status pill, and the
/// sentence beneath. One helper for every surface that shows one (an export item's follow-up, the causality views, an
/// account's Exchange recipient panel), so the same change is never described in two ways.
/// </summary>
public static class ExchangeChangeOutcomeDisplay
{
    /// <summary>
    /// What the change makes the account, as a title: "Mailbox", "Shared mailbox", "Mail-enabled group". A change to
    /// None, or to a type JIM does not recognise, is titled by what it is about instead.
    /// </summary>
    public static string Title(string? recipientType)
    {
        if (string.IsNullOrEmpty(recipientType) || !ExchangeRecipientTypes.All.Contains(recipientType)
            || recipientType == ExchangeRecipientTypes.None)
            return "Exchange recipient";

        var noun = ExchangeRecipientRules.Noun(recipientType);
        return char.ToUpperInvariant(noun[0]) + noun[1..];
    }

    /// <summary>
    /// The pill: its words, its <c>jim-status-pill--</c> modifier, and whether JIM is still working on the change.
    /// </summary>
    public static (string Label, string Modifier, bool Running) Pill(ExchangeChangeState state) => state switch
    {
        ExchangeChangeState.Queued => ("Queued", "neutral", true),
        ExchangeChangeState.Delivering => ("Delivering", "neutral", true),
        ExchangeChangeState.Retrying => ("Retrying", "warn", true),
        ExchangeChangeState.Held => ("Held", "warn", true),
        ExchangeChangeState.Delivered => ("Delivered", "ok", false),
        ExchangeChangeState.Withdrawn => ("Withdrawn", "ok", false),
        ExchangeChangeState.Replaced => ("Replaced", "neutral", false),
        ExchangeChangeState.Parked => ("Parked", "err", false),
        ExchangeChangeState.Refused => ("Refused", "err", false),
        ExchangeChangeState.NotMade => ("Not made", "warn", false),
        _ => (state.ToString(), "neutral", false)
    };

    /// <summary>
    /// The sentence beneath. A failure speaks in Exchange's own words, because that is where the remedy lives; a
    /// retrying change also names its attempt and the next one, because that is what the reader is waiting for.
    /// </summary>
    public static string Words(ExchangeChangeOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var message = string.IsNullOrWhiteSpace(outcome.Message) ? null : outcome.Message.Trim();
        switch (outcome.State)
        {
            case ExchangeChangeState.Queued:
                return "Queued; the Exchange delivery lane will pick it up in a moment";

            case ExchangeChangeState.Delivering:
                return "Exchange is applying it now";

            case ExchangeChangeState.Held:
                return "Held: its Connected System does not manage Exchange recipients now; delivered once it does again";

            case ExchangeChangeState.Retrying:
            {
                var lead = outcome.FailureKind is { } kind ? Reason(kind) : "Retrying";
                var words = message == null ? lead : $"{lead}: {message.TrimEnd('.')}";
                if (outcome.AttemptCount is > 0)
                    words += $". Attempt {outcome.AttemptCount}";
                return outcome.NextAttemptAt.HasValue
                    ? $"{words}. Next attempt {outcome.NextAttemptAt.Value.ToLocalTime().ToFriendlyDate()}"
                    : words;
            }

            case ExchangeChangeState.Parked:
                return message == null ? "Parked: JIM has stopped trying until somebody retries it" : $"Parked: {message}";

            case ExchangeChangeState.Delivered:
                return message ?? "Exchange made the account the declared recipient";

            case ExchangeChangeState.Replaced:
                return message ?? "Replaced by a newer declared state before it was delivered";

            default:
                return message ?? Pill(outcome.State).Label;
        }
    }

    /// <summary>
    /// What a change on the Exchange queue is waiting for, beneath its pill on the Operations Exchange tab (#2021, Phase
    /// 6b): when the next attempt is, since when it has been parked, or why it holds.
    /// </summary>
    public static string QueueDetail(PendingExchangeChangeHeader change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return change.State switch
        {
            ExchangeChangeState.Queued => "Due now",
            ExchangeChangeState.Delivering => "Exchange is applying it now",
            ExchangeChangeState.Held => $"Waits until {change.ConnectedSystemName} manages Exchange recipients again",
            ExchangeChangeState.Retrying => change.NextRetryAt is { } next
                ? $"Attempt {change.AttemptCount}; next {next.ToLocalTime().ToFriendlyDate()}"
                : $"Attempt {change.AttemptCount}; due now",
            ExchangeChangeState.Parked => change.LastAttemptedAt is { } last
                ? $"Since {last.ToLocalTime().ToFriendlyDate()}"
                : "Waiting for somebody to retry it",
            _ => "Waits on a new declared state"
        };
    }

    /// <summary>
    /// Why a change on the Exchange queue is not delivered: how its last attempt failed and Exchange's own words, or JIM's
    /// reason for refusing it. Null for a change that has not failed.
    /// </summary>
    public static string? QueueReason(PendingExchangeChangeHeader change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var message = string.IsNullOrWhiteSpace(change.FailureMessage) ? null : change.FailureMessage.Trim();
        if (change.FailureKind is { } kind)
            return message == null ? Reason(kind) : $"{Reason(kind)}: {message}";

        return message;
    }

    /// <summary>How an attempt failed, in an administrator's words.</summary>
    public static string Reason(ExchangeChangeFailureKind kind) => kind switch
    {
        ExchangeChangeFailureKind.ObjectNotFound => "Exchange could not find the account yet",
        ExchangeChangeFailureKind.Refused => "Exchange refused the change",
        ExchangeChangeFailureKind.InsufficientPermissions => "JIM's account lacks the Exchange permissions",
        ExchangeChangeFailureKind.AuthenticationFailed => "Signing in to Exchange failed",
        ExchangeChangeFailureKind.Unreachable => "Unreachable or timed out",
        _ => kind.ToString().SplitOnCapitalLetters()
    };
}

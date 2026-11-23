// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exchange;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using System.Globalization;

using JIM.Utilities;
using JIM.Web.Resources;

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
            return MetaverseText.EchRecipientFallback;

        var noun = ExchangeRecipientRules.Noun(recipientType);
        return char.ToUpperInvariant(noun[0]) + noun[1..];
    }

    /// <summary>
    /// The pill: its words, its <c>jim-status-pill--</c> modifier, and whether JIM is still working on the change.
    /// </summary>
    public static (string Label, string Modifier, bool Running) Pill(ExchangeChangeState state) => state switch
    {
        ExchangeChangeState.Queued => (MetaverseText.EchPillQueued, "neutral", true),
        ExchangeChangeState.Delivering => (MetaverseText.EchPillDelivering, "neutral", true),
        ExchangeChangeState.Retrying => (MetaverseText.EchPillRetrying, "warn", true),
        ExchangeChangeState.Held => (MetaverseText.EchPillHeld, "warn", true),
        ExchangeChangeState.Delivered => (MetaverseText.EchPillDelivered, "ok", false),
        ExchangeChangeState.Withdrawn => (MetaverseText.EchPillWithdrawn, "ok", false),
        ExchangeChangeState.Replaced => (MetaverseText.EchPillReplaced, "neutral", false),
        ExchangeChangeState.Parked => (MetaverseText.EchPillParked, "err", false),
        ExchangeChangeState.Refused => (MetaverseText.EchPillRefused, "err", false),
        ExchangeChangeState.NotMade => (MetaverseText.EchPillNotMade, "warn", false),
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
                return MetaverseText.EchWordsQueued;

            case ExchangeChangeState.Delivering:
                return MetaverseText.EchApplyingNow;

            case ExchangeChangeState.Held:
                return MetaverseText.EchWordsHeld;

            case ExchangeChangeState.Retrying:
            {
                var lead = outcome.FailureKind is { } kind ? Reason(kind) : MetaverseText.EchPillRetrying;
                var words = message == null
                    ? lead
                    : string.Format(CultureInfo.CurrentCulture, MetaverseText.EchLeadWithMessage, lead, message.TrimEnd('.'));
                if (outcome.AttemptCount is > 0)
                    words += string.Format(CultureInfo.CurrentCulture, MetaverseText.EchAttemptSuffix, outcome.AttemptCount);
                return outcome.NextAttemptAt.HasValue
                    ? string.Format(CultureInfo.CurrentCulture, MetaverseText.EchNextAttemptSuffix, words, outcome.NextAttemptAt.Value.ToLocalTime().ToFriendlyDate())
                    : words;
            }

            case ExchangeChangeState.Parked:
                return message == null
                    ? MetaverseText.EchParkedWords
                    : string.Format(CultureInfo.CurrentCulture, MetaverseText.EchParkedWithMessage, message);

            case ExchangeChangeState.Delivered:
                return message ?? MetaverseText.EchDeliveredDefault;

            case ExchangeChangeState.Replaced:
                return message ?? MetaverseText.EchReplacedDefault;

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
            ExchangeChangeState.Queued => MetaverseText.EchDueNow,
            ExchangeChangeState.Delivering => MetaverseText.EchApplyingNow,
            ExchangeChangeState.Held => string.Format(CultureInfo.CurrentCulture, MetaverseText.EchWaitsUntilManages, change.ConnectedSystemName),
            ExchangeChangeState.Retrying => change.NextRetryAt is { } next
                ? string.Format(CultureInfo.CurrentCulture, MetaverseText.EchAttemptNext, change.AttemptCount, next.ToLocalTime().ToFriendlyDate())
                : string.Format(CultureInfo.CurrentCulture, MetaverseText.EchAttemptDueNow, change.AttemptCount),
            ExchangeChangeState.Parked => change.LastAttemptedAt is { } last
                ? string.Format(CultureInfo.CurrentCulture, MetaverseText.EchSince, last.ToLocalTime().ToFriendlyDate())
                : MetaverseText.EchWaitingRetry,
            _ => MetaverseText.EchWaitsNewState
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
            return message == null ? Reason(kind) : string.Format(CultureInfo.CurrentCulture, MetaverseText.EchLeadWithMessage, Reason(kind), message);

        return message;
    }

    /// <summary>How an attempt failed, in an administrator's words.</summary>
    public static string Reason(ExchangeChangeFailureKind kind) => kind switch
    {
        ExchangeChangeFailureKind.ObjectNotFound => MetaverseText.EchReasonObjectNotFound,
        ExchangeChangeFailureKind.Refused => MetaverseText.EchReasonRefused,
        ExchangeChangeFailureKind.InsufficientPermissions => MetaverseText.EchReasonPermissions,
        ExchangeChangeFailureKind.AuthenticationFailed => MetaverseText.EchReasonAuthentication,
        ExchangeChangeFailureKind.Unreachable => MetaverseText.EchReasonUnreachable,
        _ => kind.ToString().SplitOnCapitalLetters()
    };
}

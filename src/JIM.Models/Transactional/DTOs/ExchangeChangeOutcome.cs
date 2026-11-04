// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Exchange;

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Where one queued Exchange change stands now (#2021): what an export item's follow-up, the causality views and an
/// account's Exchange recipient panel show.
/// </summary>
/// <remarks>
/// Read from two sources and merged, as an Initial Password's standing is: the queue row describes a change still on
/// the queue, and is removed once the change is delivered or withdrawn; the Activities recorded under the change say
/// what became of it after that.
/// </remarks>
public class ExchangeChangeOutcome
{
    /// <summary>
    /// The change's own Activity.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// Where the change stands.
    /// </summary>
    public ExchangeChangeState State { get; set; }

    /// <summary>
    /// The recipient type the change declares: None, Mailbox, SharedMailbox, RoomMailbox, EquipmentMailbox, MailUser,
    /// MailContact or MailEnabledGroup, or the value exactly as declared where it is none of these.
    /// </summary>
    public string RecipientType { get; set; } = string.Empty;

    /// <summary>
    /// Whether JIM will do nothing more with the change unless a person acts or the declared state changes: true in
    /// every state but Queued, Delivering and Retrying.
    /// </summary>
    public bool IsSettled { get; set; }

    /// <summary>
    /// How many delivery attempts the change has had, while it is on the queue; null once it has left it.
    /// </summary>
    public int? AttemptCount { get; set; }

    /// <summary>
    /// When the next attempt falls due (UTC), for a change that is Retrying; null in every other state.
    /// </summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>
    /// How the last attempt failed, for a change on the queue that has failed; null otherwise. One of
    /// ObjectNotFound, Refused, InsufficientPermissions, AuthenticationFailed or Unreachable.
    /// </summary>
    public ExchangeChangeFailureKind? FailureKind { get; set; }

    /// <summary>
    /// What is known about the latest outcome: Exchange's own words on a failure, JIM's reason for a refusal or a
    /// withdrawal, or what was done. Null where nothing has been said yet.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// When the change reached this state (UTC): the latest attempt, outcome or, for a change not yet attempted, when it
    /// was queued.
    /// </summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// Describes one change from its own Activity, its queue row if it is still on the queue, and the Activities
    /// recorded under it (any order; those of other changes are ignored).
    /// </summary>
    public static ExchangeChangeOutcome Describe(Activity changeActivity, PendingExchangeChange? row, IEnumerable<ExchangeChangeEvent> events)
    {
        ArgumentNullException.ThrowIfNull(changeActivity);
        ArgumentNullException.ThrowIfNull(events);

        return row != null ? DescribeRow(changeActivity, row) : DescribeHistory(changeActivity, events);
    }

    /// <summary>
    /// A change still on the queue, described from its row: the row is the authority on what JIM intends to do next,
    /// and the latest attempt's words are on it.
    /// </summary>
    private static ExchangeChangeOutcome DescribeRow(Activity changeActivity, PendingExchangeChange row)
    {
        var state = row.Status switch
        {
            PendingExchangeChangeStatus.Delivering => ExchangeChangeState.Delivering,
            PendingExchangeChangeStatus.Parked => ExchangeChangeState.Parked,
            // Staging records both kinds of refusal as a refused row; a type change this release does not make is
            // the one recorded under a warning rather than an error.
            PendingExchangeChangeStatus.Refused => changeActivity.Status == ActivityStatus.CompleteWithWarning
                ? ExchangeChangeState.NotMade
                : ExchangeChangeState.Refused,
            // Pending: not yet attempted, or waiting out a backoff after a failure that may clear.
            _ when row.LastAttemptedAt != null => ExchangeChangeState.Retrying,
            _ => ExchangeChangeState.Queued
        };

        return new ExchangeChangeOutcome
        {
            ActivityId = changeActivity.Id,
            State = state,
            RecipientType = string.IsNullOrEmpty(row.RecipientType) ? ExchangeRecipientTypes.None : row.RecipientType,
            IsSettled = IsSettledState(state),
            AttemptCount = row.AttemptCount,
            NextAttemptAt = state == ExchangeChangeState.Retrying ? row.NextRetryAt : null,
            FailureKind = row.FailureKind,
            Message = row.FailureMessage ?? changeActivity.ErrorMessage ?? changeActivity.Message,
            OccurredAt = row.LastAttemptedAt ?? row.CreatedAt
        };
    }

    /// <summary>
    /// A change no longer on the queue, described from the newest outcome recorded under it. A retry is an
    /// administrator's request rather than an outcome, so it is passed over. A failure with no row behind it means the
    /// row has since been removed by retention after parking, so it reads as Parked. With no outcome at all, the
    /// change's own Activity is the record: a refusal, a type change not made, or, for a change that was queued, one
    /// that left the queue with nothing recorded (its Connected System deleted), which nobody is owed any more.
    /// </summary>
    private static ExchangeChangeOutcome DescribeHistory(Activity changeActivity, IEnumerable<ExchangeChangeEvent> events)
    {
        var newest = events
            .Where(e => e.ChangeActivityId == changeActivity.Id && e.Operation != ActivityTargetOperationType.RetryExchangeRecipient)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.ActivityId)
            .LastOrDefault();

        ExchangeChangeState state;
        string? message;
        DateTime occurredAt;
        if (newest != null)
        {
            var failed = newest.Status is ActivityStatus.CompleteWithError or ActivityStatus.FailedWithError;
            state = newest.Operation switch
            {
                ActivityTargetOperationType.WithdrawExchangeRecipient => ExchangeChangeState.Withdrawn,
                ActivityTargetOperationType.ReplaceExchangeRecipient => ExchangeChangeState.Replaced,
                _ => failed ? ExchangeChangeState.Parked : ExchangeChangeState.Delivered
            };
            message = failed ? newest.ErrorMessage ?? newest.Message : newest.Message ?? newest.ErrorMessage;
            occurredAt = newest.OccurredAt;
        }
        else
        {
            state = changeActivity.Status switch
            {
                ActivityStatus.CompleteWithError or ActivityStatus.FailedWithError => ExchangeChangeState.Refused,
                ActivityStatus.CompleteWithWarning => ExchangeChangeState.NotMade,
                _ => ExchangeChangeState.Withdrawn
            };
            message = state == ExchangeChangeState.Withdrawn
                ? "No longer on the queue, and no outcome was recorded; its Connected System may have been deleted. Exchange was not asked to make it."
                : changeActivity.ErrorMessage ?? changeActivity.Message;
            occurredAt = changeActivity.Created;
        }

        return new ExchangeChangeOutcome
        {
            ActivityId = changeActivity.Id,
            State = state,
            RecipientType = string.IsNullOrEmpty(changeActivity.TargetContext) ? ExchangeRecipientTypes.None : changeActivity.TargetContext,
            IsSettled = IsSettledState(state),
            Message = message,
            OccurredAt = occurredAt
        };
    }

    private static bool IsSettledState(ExchangeChangeState state) =>
        state is not (ExchangeChangeState.Queued or ExchangeChangeState.Delivering or ExchangeChangeState.Retrying);
}

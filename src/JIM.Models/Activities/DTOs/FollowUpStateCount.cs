// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// How many of a run's follow-ups of one kind stand in one state (#2021, Phase 6): one line of a run's follow-up
/// summary, such as "10 mailboxes delivered" or "1 initial password retrying".
/// </summary>
public class FollowUpStateCount
{
    /// <summary>
    /// The run the follow-ups belong to, through its execution items.
    /// </summary>
    public Guid RunActivityId { get; set; }

    /// <summary>
    /// What kind of work they are: PasswordSynchronisation for Initial Passwords, ExchangeRecipient for Exchange
    /// recipient changes.
    /// </summary>
    public ActivityTargetType Kind { get; set; }

    /// <summary>
    /// Where they stand. For an Exchange recipient change, one of Queued, Delivering, Retrying, Parked, Delivered,
    /// Withdrawn, Replaced, Refused, NotMade or Cancelled. For an Initial Password, one of Queued, Delivering, Retrying,
    /// Set, Parked, Expired or Cancelled, or Carried where a password change already waiting for the account carries it.
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// How many stand there.
    /// </summary>
    public int Count { get; set; }
}

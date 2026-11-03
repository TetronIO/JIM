// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// How many of a run's follow-ups of one kind stand in one state, such as 10 Exchange recipient changes delivered.
/// </summary>
public class FollowUpStateCountDto
{
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

    /// <summary>
    /// Creates the DTO from the count.
    /// </summary>
    public static FollowUpStateCountDto FromModel(FollowUpStateCount count) => new()
    {
        Kind = count.Kind,
        State = count.State,
        Count = count.Count
    };
}

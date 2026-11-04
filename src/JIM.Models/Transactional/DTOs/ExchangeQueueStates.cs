// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Maps between the states a queued Exchange change can be in and the change states they are (#2021, Phase 6b).
/// </summary>
public static class ExchangeQueueStates
{
    /// <summary>The change state a queue state is.</summary>
    public static ExchangeChangeState ToChangeState(this ExchangeQueueState state) => state switch
    {
        ExchangeQueueState.Queued => ExchangeChangeState.Queued,
        ExchangeQueueState.Delivering => ExchangeChangeState.Delivering,
        ExchangeQueueState.Retrying => ExchangeChangeState.Retrying,
        ExchangeQueueState.Parked => ExchangeChangeState.Parked,
        ExchangeQueueState.Refused => ExchangeChangeState.Refused,
        ExchangeQueueState.NotMade => ExchangeChangeState.NotMade,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a state a queued Exchange change can be in.")
    };

    /// <summary>
    /// The queue state of a change still on the queue. A state reached only once a change has left the queue (Delivered,
    /// Withdrawn, Replaced) has none, and throws: a row on the queue never reads as one.
    /// </summary>
    public static ExchangeQueueState ToQueueState(this ExchangeChangeState state) => state switch
    {
        ExchangeChangeState.Queued => ExchangeQueueState.Queued,
        ExchangeChangeState.Delivering => ExchangeQueueState.Delivering,
        ExchangeChangeState.Retrying => ExchangeQueueState.Retrying,
        ExchangeChangeState.Parked => ExchangeQueueState.Parked,
        ExchangeChangeState.Refused => ExchangeQueueState.Refused,
        ExchangeChangeState.NotMade => ExchangeQueueState.NotMade,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "A change in this state has left the Exchange queue.")
    };
}

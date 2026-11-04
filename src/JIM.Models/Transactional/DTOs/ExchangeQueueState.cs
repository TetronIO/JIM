// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Where a change on the Exchange queue stands: the states a queued change can be in.
/// </summary>
/// <remarks>
/// #2021, Phase 6b. The REST API's and PowerShell's view of <see cref="ExchangeChangeState"/>, short of the three states a
/// change reaches only once it has left the queue (Delivered, Withdrawn, Replaced), so the queue's contract names only
/// what a row can hold and a filter cannot ask for a state no row is in. Names match the change states exactly;
/// <see cref="ExchangeQueueStates"/> maps between them.
/// </remarks>
public enum ExchangeQueueState
{
    /// <summary>Waiting for its first attempt.</summary>
    Queued = 0,

    /// <summary>Exchange is applying it now.</summary>
    Delivering = 1,

    /// <summary>Waiting out a backoff after a failure that may clear by itself.</summary>
    Retrying = 2,

    /// <summary>JIM has stopped trying, so it waits on a person.</summary>
    Parked = 3,

    /// <summary>JIM refused the declared state before asking Exchange; it holds until the declared state changes.</summary>
    Refused = 4,

    /// <summary>It would change an existing recipient's type, which JIM does not do; it holds until the declared state changes.</summary>
    NotMade = 5
}

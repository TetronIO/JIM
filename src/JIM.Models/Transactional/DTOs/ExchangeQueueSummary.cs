// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What the whole Exchange queue holds (#2021, Phase 6b), for the summary above the Operations Exchange tab and its REST
/// and PowerShell counterparts: how many changes stand in each state, and how many a delivery pass would attempt now.
/// </summary>
public class ExchangeQueueSummary
{
    /// <summary>Changes waiting for their first attempt.</summary>
    public int QueuedCount { get; set; }

    /// <summary>Changes Exchange is applying now.</summary>
    public int DeliveringCount { get; set; }

    /// <summary>Changes waiting out a backoff after a failure that may clear by itself.</summary>
    public int RetryingCount { get; set; }

    /// <summary>Changes JIM has stopped trying, because Exchange refused them or 24 hours of retries failed. These wait on a person.</summary>
    public int ParkedCount { get; set; }

    /// <summary>Changes JIM refused before asking Exchange, which hold until their declared state changes.</summary>
    public int RefusedCount { get; set; }

    /// <summary>Changes that would change an existing recipient's type, which JIM does not do.</summary>
    public int NotMadeCount { get; set; }

    /// <summary>How many queued or retrying changes a delivery pass would attempt now.</summary>
    public int DueCount { get; set; }

    /// <summary>How many wait on a person: parked, refused or not made.</summary>
    public int WaitingOnAPersonCount => ParkedCount + RefusedCount + NotMadeCount;
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What a delivery service has ahead of it: read once per loop iteration to decide how long to sleep, and written
/// into the service's heartbeat so the Operations page can say "3 due, 1 retrying, next attempt 09:19" beside its
/// state. Each queue fills it from its own rows (the Password Synchronisation queue, #1635; the Exchange queue,
/// #2021), and counts only work a lane would attempt: a queue leaves out what it would never deliver now, so the
/// service neither reports it as a backlog nor wakes for retries it will not make.
/// </summary>
public class DeliveryQueueOutlook
{
    /// <summary>
    /// Changes a lane would attempt now: pending and due, or claimed under a lease that has run out.
    /// </summary>
    public int DueCount { get; set; }

    /// <summary>
    /// Changes waiting out a backoff, each with a scheduled next attempt still ahead.
    /// </summary>
    public int RetryingCount { get; set; }

    /// <summary>
    /// The earliest scheduled attempt still ahead (UTC), or null when nothing is waiting on the clock. The service
    /// wakes for it; without this a retry would wait for the next safety poll.
    /// </summary>
    public DateTime? NextAttemptAt { get; set; }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What the Password Delivery Service has ahead of it (#1635): read once per loop iteration to decide how long to
/// sleep, and written into the service's heartbeat so the Operations page can say "3 due, 1 retrying, next attempt
/// 09:19" beside its state.
/// <para>
/// Counts only work a lane would attempt. A change held by a switched-off Connected System is neither due nor
/// retrying here, for the reason <see cref="PasswordQueueSummary.DueCount"/> leaves it out: it would make the
/// ordinary state of a deployment with one system paused read as a queue nothing is draining, and it would have
/// the service waking for retries it will never make.
/// </para>
/// </summary>
public class PasswordQueueDeliveryOutlook
{
    /// <summary>
    /// Changes a lane would attempt now: pending and due on an enabled system, or claimed under a lease that has
    /// run out.
    /// </summary>
    public int DueCount { get; set; }

    /// <summary>
    /// Changes waiting out a backoff on an enabled system, each with a scheduled next attempt still ahead.
    /// </summary>
    public int RetryingCount { get; set; }

    /// <summary>
    /// The earliest scheduled attempt still ahead (UTC), or null when nothing is waiting on the clock. The service
    /// wakes for it; without this a retry would wait for the next safety poll.
    /// </summary>
    public DateTime? NextAttemptAt { get; set; }
}

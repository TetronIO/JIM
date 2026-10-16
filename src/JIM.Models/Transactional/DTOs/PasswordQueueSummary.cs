// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// What the whole Password Synchronisation queue holds, for the summary above the queue page (#1119).
/// <para>
/// Deliberately not the same shape as <see cref="PasswordQueueAttention"/>, which answers a narrower question
/// per Connected System: what needs a person. This answers "what is in the queue", so it counts the states that
/// need nobody as well.
/// </para>
/// </summary>
public class PasswordQueueSummary
{
    /// <summary>
    /// Changes JIM still intends to deliver, whether due now or waiting out a backoff.
    /// </summary>
    public int WaitingCount { get; set; }

    /// <summary>
    /// How many of the waiting changes a delivery pass would attempt right now. A large waiting count with nothing
    /// due is a queue working through its backoffs; a large due count is a queue that is not being drained. A
    /// change queued for a Connected System that is switched off is waiting but not due: it is held until somebody
    /// enables the system.
    /// </summary>
    /// <remarks>
    /// Held changes are left out because counting them would make the ordinary state of a deployment with one
    /// system switched off look like a queue nothing is draining.
    /// </remarks>
    public int DueCount { get; set; }

    /// <summary>
    /// Changes the target refused, or that ran out of attempts. JIM has stopped trying, so these wait on a person.
    /// </summary>
    public int ParkedCount { get; set; }

    /// <summary>
    /// Changes that outlived their Connected System's time to live. Nothing can deliver these now: the password
    /// they carried is gone.
    /// </summary>
    public int ExpiredCount { get; set; }

    /// <summary>
    /// Changes an administrator stopped. Counted rather than hidden, because the Metaverse Object's password is
    /// still divergent on that system and the count is the only thing that says so.
    /// </summary>
    public int CancelledCount { get; set; }
}

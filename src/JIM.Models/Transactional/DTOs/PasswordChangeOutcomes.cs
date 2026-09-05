// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// Where one password change stands at every Connected System it was queued for (#1635): what a caller waiting on
/// a change is shown, and what the person page and the REST response are built from.
/// <para>
/// Read from two sources and merged, because neither alone answers the question. The queue row is deleted the
/// moment a password lands, so the queue shows failures and never successes; the child Activities record every
/// outcome but say nothing about a change still waiting its turn. A target with a row is described by the row; a
/// target with no row is described by its newest child Activity.
/// </para>
/// <para>
/// <b>Carries no password.</b> Neither source ever exposes one, and no field here is derived from one.
/// </para>
/// </summary>
public class PasswordChangeOutcomes
{
    /// <summary>
    /// The Activity recording the change: the id <see cref="PasswordQueueResult.ActivityId"/> handed back when it
    /// was queued.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// The identity whose password changed.
    /// </summary>
    public Guid MetaverseObjectId { get; set; }

    /// <summary>
    /// When the change was queued (UTC).
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Whether every target has reached a state a caller need not wait on: nothing is
    /// <see cref="PasswordChangeTargetState.Queued"/> or <see cref="PasswordChangeTargetState.Delivering"/>. A
    /// change that is retrying is settled by this measure, because the next attempt is minutes away and nobody at
    /// a screen should be held for it.
    /// </summary>
    public bool IsSettled { get; set; }

    /// <summary>
    /// One entry per Connected System the change reached, in Connected System name order.
    /// </summary>
    public IReadOnlyList<PasswordChangeTargetOutcome> Targets { get; set; } = [];
}

/// <summary>
/// Where one password change stands at one Connected System.
/// </summary>
public class PasswordChangeTargetOutcome
{
    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = string.Empty;

    public PasswordChangeTargetState State { get; set; }

    /// <summary>
    /// When the next delivery attempt falls due (UTC), for a target that is
    /// <see cref="PasswordChangeTargetState.Retrying"/>; null in every other state.
    /// </summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>
    /// The target's own words on the most recent outcome, or JIM's where the target gave none: why it refused, or
    /// that the password was set. Null for a target nothing has been said about yet.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// How the most recent attempt failed, as JIM classified it, for a target still carrying a queue row; null
    /// before an attempt has been made, once the password is set, and for a target whose row is gone. The Set
    /// Password dialog chooses its remediation guidance by this, because the classification is what decides
    /// whether another attempt could ever help, and the target's words in <see cref="Message"/> do not say.
    /// </summary>
    public PasswordSetFailureReason? FailureReason { get; set; }

    /// <summary>
    /// When the most recent attempt was made (UTC), or null before one has been.
    /// </summary>
    public DateTime? OccurredAt { get; set; }

    /// <summary>
    /// How many delivery attempts this change has had against this target.
    /// </summary>
    public int AttemptCount { get; set; }
}

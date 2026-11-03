// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Models.Transactional.DTOs;

/// <summary>
/// One Activity recorded under a queued Exchange change's own Activity (#2021): an attempt that failed in a new way, a
/// park, a delivery, a withdrawal, a replacement, or an administrator's retry. A projection, so the
/// outcome read never materialises the Activities themselves.
/// </summary>
public class ExchangeChangeEvent
{
    /// <summary>
    /// The change's own Activity, which this one was recorded under.
    /// </summary>
    public Guid ChangeActivityId { get; set; }

    /// <summary>
    /// This Activity.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// What it records: a delivery or a failed attempt, a withdrawal, a replacement or a retry.
    /// </summary>
    public ActivityTargetOperationType Operation { get; set; }

    public ActivityStatus Status { get; set; }

    public string? Message { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// When it was recorded (UTC).
    /// </summary>
    public DateTime OccurredAt { get; set; }
}

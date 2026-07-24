// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;

namespace JIM.Models.Activities;

/// <summary>
/// An incremental stat counter row for a Run Profile Activity (#1078). The bulk RPEI/outcome
/// persistence paths upsert per-(Activity, Dimension, Key) count deltas as they flush, so the
/// Activity detail page's stats read a handful of counter rows instead of aggregating millions
/// of Run Profile Execution Items on every progress poll.
/// </summary>
/// <remarks>
/// While the Activity is in progress the counters are advisory (near-exact; the only known drift
/// source is post-insert RPEI error type changes during confirming-import reconciliation). On
/// completion the counters are finalised: recomputed exactly from the persisted rows and
/// replaced, with <see cref="Activity.RunProfileExecutionStatsFinalised"/> recording that the
/// stored values are authoritative. Keyed by (ActivityId, Dimension, Key); rows are removed by
/// cascade when the Activity is deleted.
/// </remarks>
public class ActivityStatCounter
{
    /// <summary>
    /// The Activity the counter belongs to. Part of the composite primary key.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// The dimension this row counts along. Part of the composite primary key.
    /// </summary>
    public ActivityStatDimension Dimension { get; set; }

    /// <summary>
    /// The dimension-specific key: the integer value of the counted enum member for enum
    /// dimensions, or the object type name for <see cref="ActivityStatDimension.ObjectTypeName"/>.
    /// Part of the composite primary key.
    /// </summary>
    [MaxLength(200)]
    public string Key { get; set; } = null!;

    /// <summary>
    /// The number of counted occurrences for this (Activity, Dimension, Key).
    /// </summary>
    public long Count { get; set; }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Preview;

/// <summary>
/// What a preview is going to cost, computed from cheap set-based SQL before any per-object evaluation begins. Two
/// decisions hang off it: whether the preview runs in JIM.Web's process or is handed to JIM.Worker, and whether the
/// administrator is asked to choose between a full and a capped set of drill-down rows.
///
/// Both decisions are better made from a rough number than not made at all, so an estimate is expected to be
/// approximate. It must never be expensive to produce: an estimate that itself scans the population has defeated
/// its own purpose.
/// </summary>
/// <param name="AffectedObjects">
/// How many objects the proposed change would touch. Reused as the stage 2 count where the adapter's counts are a
/// breakdown of the same population, so it is computed once.
/// </param>
/// <param name="DeltasPerObject">
/// How many delta rows the adapter expects to emit per affected object: 1 for a scope transition, N for an
/// Attribute Flow change that touches N attributes. A per-adapter constant, not a measurement.
/// </param>
public record PreviewCostEstimate(int AffectedObjects, int DeltasPerObject = 1)
{
    /// <summary>
    /// Estimated delta rows. Computed as a <see cref="long"/> because the multiplication is what overflows: a
    /// million objects at a handful of attributes each is past <see cref="int"/> before anyone has done anything
    /// unusual.
    /// </summary>
    public long EstimatedDeltaRows => (long)AffectedObjects * DeltasPerObject;
}

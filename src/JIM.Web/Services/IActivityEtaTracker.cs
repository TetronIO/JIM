// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models;

namespace JIM.Web.Services;

/// <summary>
/// Tracks progress samples per Activity and derives a windowed throughput rate and ETA (#202).
/// Registered as a singleton so the progress API endpoint and the Activity detail page share one
/// sample history and therefore agree on the estimate.
/// </summary>
public interface IActivityEtaTracker
{
    /// <summary>
    /// Records a progress sample for an Activity and returns the current estimate. A decrease in
    /// <paramref name="objectsProcessed"/> or a change of <paramref name="objectsToProcess"/> is
    /// treated as a new counting window (run phases reuse the progress counters), resetting the
    /// sample history for the Activity.
    /// </summary>
    ActivityEtaEstimate RecordSample(Guid activityId, int objectsProcessed, int objectsToProcess);

    /// <summary>
    /// Discards the sample history for an Activity. Call when the Activity reaches a terminal
    /// status; failing to call is safe (bounded state, least-recently-touched eviction).
    /// </summary>
    void Remove(Guid activityId);
}

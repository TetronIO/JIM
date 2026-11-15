// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Data.Repositories;

/// <summary>
/// Writes a Run Profile execution's progress on a connection of its own, never on the one the run's repository is using,
/// so a write can be made from a timer while the run carries on with its own work (#2521).
/// </summary>
public interface IActivityProgressWriter
{
    /// <summary>
    /// Records the Activity's message and counters, and the given steps, as one transaction.
    /// </summary>
    Task WriteAsync(ActivityProgressWrite progress);
}

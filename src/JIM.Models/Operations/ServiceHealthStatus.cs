// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Operations;

/// <summary>
/// How well a service is, in the three words an administrator (and a monitoring script) acts on. The numeric order
/// is severity, best first, so the worst status in a set is simply the largest value;
/// <see cref="ServiceHealthReport.Overall"/> relies on this. Why a service has its status is its
/// <see cref="ServiceHealthCondition"/>.
/// </summary>
public enum ServiceHealthStatus
{
    /// <summary>
    /// The service is alive and its work is moving. Nothing to do.
    /// </summary>
    Healthy = 0,

    /// <summary>
    /// The service is alive but something is not right: its heartbeat is late, or the work it is running has not
    /// moved forward for a long time. Worth a look; queued work is still being picked up.
    /// </summary>
    Degraded = 1,

    /// <summary>
    /// The service should be presumed down, or it has never started. Scheduled and queued work will not run until
    /// it is back.
    /// </summary>
    Unhealthy = 2
}

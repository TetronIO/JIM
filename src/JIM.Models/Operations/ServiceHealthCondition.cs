// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Operations;

/// <summary>
/// Why a service has the <see cref="ServiceHealthStatus"/> it has: the one observation about its heartbeat that
/// decided the verdict. Each condition maps to exactly one status, so a display can colour by status and explain
/// by condition without the two ever disagreeing.
/// </summary>
public enum ServiceHealthCondition
{
    /// <summary>
    /// The heartbeat arrived within its expected interval. Healthy.
    /// </summary>
    Heartbeating,

    /// <summary>
    /// A few heartbeats have been missed, not enough to conclude the process is gone: it may be paused under load,
    /// or the database may be slow. Degraded.
    /// </summary>
    HeartbeatOverdue,

    /// <summary>
    /// The heartbeat is arriving but the work in hand has not moved forward for a long time. The process is up; the
    /// task it is running may be wedged. Degraded.
    /// </summary>
    Stalled,

    /// <summary>
    /// No heartbeat for long enough that the process should be presumed down. Unhealthy.
    /// </summary>
    NoHeartbeat,

    /// <summary>
    /// The service has never written a heartbeat at all. Unhealthy.
    /// </summary>
    NeverStarted
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Operations;

/// <summary>
/// How a service is judged from its newest heartbeat. The numeric order is severity, mildest first, so the worst
/// state in a set is simply the largest value; <see cref="ServiceHealthReport.Overall"/> relies on this.
/// </summary>
public enum ServiceHealthState
{
    /// <summary>
    /// The service reported within its expected interval. Nothing to do.
    /// </summary>
    Running = 0,

    /// <summary>
    /// The service has missed a few heartbeats but not enough to be written off: it may be paused under load, or the
    /// database may be slow. Worth a glance; not yet worth an alarm.
    /// </summary>
    Stale = 1,

    /// <summary>
    /// The service is alive and reports work in flight, but that work has not moved forward for a long time. The
    /// process is up; the task it is running may be wedged.
    /// </summary>
    NoProgress = 2,

    /// <summary>
    /// The service has not reported for long enough that it should be presumed down, or it has never reported at
    /// all. Scheduled and queued work will not run until it is back.
    /// </summary>
    NotSeen = 3
}

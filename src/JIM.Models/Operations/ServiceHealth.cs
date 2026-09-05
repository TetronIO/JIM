// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Operations;

/// <summary>
/// The health of one service, as derived from its newest heartbeat at a point in time. A read model, not an entity:
/// it is built on request from <see cref="ServiceHeartbeat"/> rows and never stored.
/// </summary>
public class ServiceHealth
{
    /// <summary>
    /// The service this verdict is about.
    /// </summary>
    public JimService Service { get; set; }

    /// <summary>
    /// The verdict: how well the service is. What a display colours by and what a monitoring script alerts on.
    /// </summary>
    public ServiceHealthStatus Status { get; set; }

    /// <summary>
    /// Why the service has that status: the observation about its heartbeat that decided it.
    /// </summary>
    public ServiceHealthCondition Condition { get; set; }

    /// <summary>
    /// The condition in plain words, with the figures that matter, for example "Heartbeat 3 seconds ago",
    /// "No heartbeat for 4 minutes" or "Never started".
    /// </summary>
    public string Reason { get; set; } = null!;

    /// <summary>
    /// The instance the verdict was derived from (host name plus a per-process id), or null when the service has
    /// never reported.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// The host the reporting instance runs on, or null when the service has never reported.
    /// </summary>
    public string? HostName { get; set; }

    /// <summary>
    /// The JIM version the reporting instance is running, or null when the service has never reported. Compare it
    /// with <see cref="ServiceHealthReport.WebVersion"/>: a mismatch means a partial upgrade.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// When the reporting instance started (UTC), or null when the service has never reported.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the service last reported (UTC), or null when it never has.
    /// </summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>
    /// What the service said it was doing when it last reported, or null when idle or never seen.
    /// </summary>
    public string? CurrentWork { get; set; }

    /// <summary>
    /// When the current work began (UTC), or null when idle or never seen.
    /// </summary>
    public DateTime? CurrentWorkStartedAt { get; set; }

    /// <summary>
    /// When the current work last moved forward (UTC), or null when idle, never seen, or when the service cannot
    /// tell progress apart from liveness.
    /// </summary>
    public DateTime? LastProgressAt { get; set; }

    /// <summary>
    /// Free text the service left beside its state (queue counts, why it is waiting), or null.
    /// </summary>
    public string? Detail { get; set; }
}

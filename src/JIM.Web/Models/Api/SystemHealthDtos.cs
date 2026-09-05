// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Operations;

namespace JIM.Web.Models.Api;

/// <summary>
/// The health of JIM's background services at one moment, as <c>GET /api/v1/system/health</c> returns it. Built
/// from the same <see cref="ServiceHealthReport"/> the Operations page reads, so the portal, the REST API and
/// PowerShell always agree about whether a service is alive.
/// </summary>
public class ServiceHealthResponse
{
    /// <summary>
    /// The worst state among <see cref="Services"/>: Running, Stale, NoProgress or NotSeen. A monitoring script
    /// that alerts on anything other than Running needs to read nothing else.
    /// </summary>
    public ServiceHealthState Overall { get; set; }

    /// <summary>
    /// The version of the web tier that answered, for comparison with each service's own version; a mismatch
    /// means a partial upgrade.
    /// </summary>
    public string WebVersion { get; set; } = string.Empty;

    /// <summary>
    /// When (UTC) the verdicts were derived. Every "last seen n seconds ago" reason is relative to this moment.
    /// </summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>
    /// One entry per service, always present and always in the order WorkerSync, WorkerPasswordDelivery,
    /// Scheduler. A service that has never reported is present as NotSeen rather than missing.
    /// </summary>
    public List<ServiceHealthEntryResponse> Services { get; set; } = [];

    /// <summary>
    /// Projects the application's report for the API.
    /// </summary>
    public static ServiceHealthResponse FromReport(ServiceHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new ServiceHealthResponse
        {
            Overall = report.Overall,
            WebVersion = report.WebVersion,
            GeneratedAt = report.GeneratedAt,
            Services = report.Services.Select(ServiceHealthEntryResponse.FromHealth).ToList()
        };
    }
}

/// <summary>
/// The health of one background service, as derived from the newest heartbeat it wrote.
/// </summary>
public class ServiceHealthEntryResponse
{
    /// <summary>
    /// Which service this is: WorkerSync (the Worker's synchronisation loop), WorkerPasswordDelivery (the Worker's
    /// password delivery loop) or Scheduler.
    /// </summary>
    public JimService Service { get; set; }

    /// <summary>
    /// The verdict: Running (reported within its interval), Stale (a few heartbeats missed), NoProgress (alive,
    /// but its current work has not moved for a long time) or NotSeen (presumed down, or never reported).
    /// </summary>
    public ServiceHealthState State { get; set; }

    /// <summary>
    /// One sentence explaining the verdict, for example "Last seen 4 minutes ago; expected within 60 seconds".
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// The reporting instance (host name plus a per-process id), or null when the service has never reported.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// The host the reporting instance runs on, or null when the service has never reported.
    /// </summary>
    public string? HostName { get; set; }

    /// <summary>
    /// The JIM version the reporting instance runs, or null when the service has never reported.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// When (UTC) the reporting instance started, or null when the service has never reported.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When (UTC) the service last reported, or null when it never has.
    /// </summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>
    /// What the service said it was doing when it last reported, or null when idle or never seen.
    /// </summary>
    public string? CurrentWork { get; set; }

    /// <summary>
    /// When (UTC) the current work began, or null when idle or never seen.
    /// </summary>
    public DateTime? CurrentWorkStartedAt { get; set; }

    /// <summary>
    /// When (UTC) the current work last moved forward, or null when idle, never seen, or when the service cannot
    /// tell progress apart from liveness.
    /// </summary>
    public DateTime? LastProgressAt { get; set; }

    /// <summary>
    /// Free text the service left beside its state (queue counts, why it is waiting), or null.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Projects one service's verdict for the API.
    /// </summary>
    public static ServiceHealthEntryResponse FromHealth(ServiceHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        return new ServiceHealthEntryResponse
        {
            Service = health.Service,
            State = health.State,
            Reason = health.Reason,
            InstanceId = health.InstanceId,
            HostName = health.HostName,
            Version = health.Version,
            StartedAt = health.StartedAt,
            LastSeenAt = health.LastSeenAt,
            CurrentWork = health.CurrentWork,
            CurrentWorkStartedAt = health.CurrentWorkStartedAt,
            LastProgressAt = health.LastProgressAt,
            Detail = health.Detail
        };
    }
}

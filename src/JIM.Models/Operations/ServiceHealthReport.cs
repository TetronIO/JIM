// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Operations;

/// <summary>
/// The health of every JIM background service at one moment, plus the web tier's own version for comparison. Built
/// on request for the Operations page, the REST API and PowerShell; never stored.
/// </summary>
public class ServiceHealthReport
{
    /// <summary>
    /// One entry per <see cref="JimService"/>, always present and always in the order WorkerSync,
    /// WorkerDelivery, Scheduler, so a display can rely on the position. A service that has never reported
    /// is present as <see cref="ServiceHealthStatus.Unhealthy"/> (never started) rather than missing.
    /// </summary>
    public List<ServiceHealth> Services { get; set; } = [];

    /// <summary>
    /// The worst status among <see cref="Services"/>: what a monitoring script alerts on and what the strip's header
    /// summarises. Which condition raised it is on the service concerned.
    /// </summary>
    public ServiceHealthStatus Overall { get; set; }

    /// <summary>
    /// The version of the web tier that produced this report, so each service's version can be compared with the
    /// one the administrator is looking at.
    /// </summary>
    public string WebVersion { get; set; } = null!;

    /// <summary>
    /// The moment (UTC) the verdicts were derived. Every "last seen n seconds ago" in the report is relative to it.
    /// </summary>
    public DateTime GeneratedAt { get; set; }
}

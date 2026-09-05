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
    /// WorkerPasswordDelivery, Scheduler, so a display can rely on the position. A service that has never reported
    /// is present as <see cref="ServiceHealthState.NotSeen"/> rather than missing.
    /// </summary>
    public List<ServiceHealth> Services { get; set; } = [];

    /// <summary>
    /// The worst state among <see cref="Services"/>. This is what decides whether an administrator sees a banner.
    /// </summary>
    public ServiceHealthState Overall { get; set; }

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

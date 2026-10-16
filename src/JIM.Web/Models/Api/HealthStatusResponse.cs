// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models.Api;

/// <summary>
/// The answer to an unauthenticated health probe (<c>GET /api/v1/health</c>, <c>/health/ready</c> or
/// <c>/health/live</c>), for load balancers, orchestrators and monitors. It speaks for the web tier only;
/// <c>GET /api/v1/system/health</c> reports the Worker and the Scheduler.
/// </summary>
public class HealthStatusResponse
{
    /// <summary>
    /// The probe's verdict: healthy (the web tier answered), ready (it can serve requests: the database is
    /// reachable and JIM is not in maintenance mode) or alive (the process is running).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// When (UTC) the probe was answered.
    /// </summary>
    public DateTime Timestamp { get; set; }
}

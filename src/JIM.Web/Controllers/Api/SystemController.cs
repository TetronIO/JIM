// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Utility;
using JIM.Utilities;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for system-wide administrative operations.
/// </summary>
/// <remarks>
/// Factory reset and service health; future system-wide maintenance routines should join them here.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class SystemController(ILogger<SystemController> logger, JimApplication application) : ApiControllerBase(application, logger)
{
    private readonly ILogger<SystemController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Factory reset
    /// </summary>
    /// <remarks>
    /// Wipes all data and configuration from the database, preserving the schema,
    /// EF Core migration history, and the rows seeded at first launch (built-in metaverse
    /// attributes and object types, built-in roles, built-in connector definitions,
    /// built-in example data sets and templates, built-in predefined searches, the
    /// singleton service settings record, and infrastructure API keys).
    ///
    /// By default the Metaverse Objects holding the built-in Administrator role are preserved so the
    /// operator is not locked out of the portal; set <c>includeAdministrators</c> to remove them too.
    /// A Reset activity recording the initiating principal is always created, and every existing portal
    /// session is invalidated (the authentication epoch is advanced).
    ///
    /// Refuses with HTTP 409 if any activity is currently in progress, or if an administrator-inclusive
    /// wipe is requested with no initial administrator configured and the lockout risk is not acknowledged.
    ///
    /// **This is destructive and cannot be undone.** Callers should obtain user confirmation
    /// before invoking, and take a database backup first.
    /// </remarks>
    /// <param name="request">Reset options. An empty body performs the default (administrator-preserving) reset.</param>
    /// <returns>A summary of what was removed.</returns>
    /// <response code="200">Returns the reset summary.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="403">If the user lacks the Administrator role.</response>
    /// <response code="409">If activities are in progress, or the lockout guard refuses the wipe.</response>
    [HttpPost("reset", Name = "ResetSystem")]
    [ProducesResponseType(typeof(SystemResetResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ResetAsync([FromBody] SystemResetRequest? request = null)
    {
        request ??= new SystemResetRequest();
        var (initiatorType, initiatorId, initiatorName) = await GetInitiatorInfoAsync();
        _logger.LogWarning(
            "Factory reset initiated by {User} (includeAdministrators={IncludeAdministrators})",
            LogSanitiser.Sanitise(initiatorName),
            request.IncludeAdministrators);

        try
        {
            var result = await _application.System.ResetSystemAsync(
                initiatorType,
                initiatorId,
                initiatorName,
                request.IncludeAdministrators,
                request.AcknowledgeAdministratorLockout);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiErrorResponse.Conflict(ex.Message));
        }
    }

    /// <summary>
    /// Get service health
    /// </summary>
    /// <remarks>
    /// Reports whether JIM's background services are alive and what each is doing, from the heartbeat every service
    /// writes to the database every 5 seconds. This is the same report the Operations page shows, so a monitoring
    /// script and an administrator looking at the portal always see the same verdict.
    ///
    /// One entry is returned per service, always in the order **WorkerSync** (the Worker's synchronisation loop,
    /// which runs Run Profiles and other queued work), **WorkerPasswordDelivery** (the Worker's password delivery
    /// loop) and **Scheduler** (which starts Schedules when they fall due). Each entry's <c>state</c> is one of:
    ///
    /// - **Running**: reported within the last 15 seconds. Nothing to do.
    /// - **Stale**: more than 15 seconds since the last heartbeat, but not yet long enough to presume the process
    ///   is gone. It may be paused under load or the database may be slow; worth a glance, not yet an alarm.
    /// - **NoProgress**: alive, but its current work has not moved forward for more than 10 minutes. The process
    ///   is up; the task it is running may be wedged. Only judged for work that reports progress.
    /// - **NotSeen**: no heartbeat for 60 seconds (Worker services) or 120 seconds (Scheduler), or the service has
    ///   never reported at all (<c>reason</c> is "Never reported"). Queued and scheduled work will not run until it
    ///   is back.
    ///
    /// <c>overall</c> is the worst state present, so a script that alerts on anything other than **Running** needs
    /// to read nothing else. Each entry also carries the reporting instance, its host and version, when it started
    /// and was last seen, and its current work with when that began and last progressed. Compare each
    /// <c>version</c> with <c>webVersion</c>: a mismatch means a partial upgrade.
    ///
    /// The unauthenticated <c>/api/v1/health</c> endpoints report only the web tier that answers them and say
    /// nothing about the Worker or the Scheduler; this endpoint is how those are observed. The response is marked
    /// <c>Cache-Control: no-store</c> because a cached verdict is a wrong one.
    /// </remarks>
    /// <returns>The health of every background service, with the worst state as the overall verdict.</returns>
    /// <response code="200">Returns the service health report.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="403">If the user lacks the Administrator role.</response>
    [HttpGet("health", Name = "GetServiceHealth")]
    [ProducesResponseType(typeof(ServiceHealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetServiceHealthAsync()
    {
        var report = await _application.SystemHealth.GetServiceHealthAsync(DateTime.UtcNow);
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        return Ok(ServiceHealthResponse.FromReport(report));
    }
}

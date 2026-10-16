// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Utilities;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// Health check endpoint for load balancers and service monitors.
/// </summary>
/// <remarks>
/// This controller is intentionally unauthenticated to allow external health probes
/// from orchestrators like Kubernetes, Docker, and load balancers.
/// Note: Health endpoints are version-neutral and available at /api/v1/health.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[AllowAnonymous]
[Produces("application/json")]
public class HealthController(JimApplication application) : ControllerBase
{
    private readonly JimApplication _application = application;

    /// <summary>
    /// Get service health status
    /// </summary>
    /// <returns>Health status and timestamp.</returns>
    [HttpGet(Name = "GetHealth")]
    [ProducesResponseType(typeof(HealthStatusResponse), StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        return Ok(new HealthStatusResponse { Status = "healthy", Timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Get service readiness status
    /// </summary>
    /// <remarks>
    /// Verifies the application is ready by checking database connectivity
    /// and that the service is not in maintenance mode.
    /// Returns 503 Service Unavailable if the application is not ready.
    /// </remarks>
    /// <returns>Readiness status and timestamp.</returns>
    [HttpGet("ready", Name = "GetHealthReady")]
    [ProducesResponseType(typeof(HealthStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ReadyAsync()
    {
        try
        {
            var isReady = await _application.IsApplicationReadyAsync();

            if (!isReady)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    status = "not_ready",
                    reason = "maintenance_mode",
                    timestamp = DateTime.UtcNow
                });
            }

            return Ok(new HealthStatusResponse { Status = "ready", Timestamp = DateTime.UtcNow });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "not_ready",
                reason = "error",
                error = ex.Message,
                timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Get service liveness status
    /// </summary>
    /// <remarks>
    /// Used by orchestrators to determine if the service needs to be restarted.
    /// This check does not verify external dependencies like the database.
    /// </remarks>
    /// <returns>Liveness status and timestamp.</returns>
    [HttpGet("live", Name = "GetHealthLive")]
    [ProducesResponseType(typeof(HealthStatusResponse), StatusCodes.Status200OK)]
    public IActionResult Live()
    {
        return Ok(new HealthStatusResponse { Status = "alive", Timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Get application version
    /// </summary>
    /// <returns>Product name and version string.</returns>
    [HttpGet("version", Name = "GetHealthVersion")]
    [ProducesResponseType(typeof(VersionResponse), StatusCodes.Status200OK)]
    public IActionResult Version()
    {
        return Ok(new VersionResponse { Product = "JIM", Version = JimVersion.Current });
    }
}

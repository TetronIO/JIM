// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
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
    [ProducesResponseType(StatusCodes.Status200OK)]
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

            return Ok(new
            {
                status = "ready",
                timestamp = DateTime.UtcNow
            });
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
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Live()
    {
        return Ok(new { status = "alive", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Get application version
    /// </summary>
    /// <returns>Product name and version string.</returns>
    [HttpGet("version", Name = "GetHealthVersion")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Version()
    {
        return Ok(new { product = "JIM", version = AppVersion });
    }

    private static readonly string AppVersion = GetCleanVersion();

    private static string GetCleanVersion()
    {
        var version = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                typeof(HealthController).Assembly)
            ?.InformationalVersion ?? "unknown";

        // Strip the Source Link commit hash suffix (e.g. "+6444a6934e...")
        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version[..plusIndex] : version;
    }
}

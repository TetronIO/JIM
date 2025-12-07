using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers;

/// <summary>
/// Health check endpoint for load balancers and service monitors.
/// </summary>
/// <remarks>
/// This controller is intentionally unauthenticated to allow external health probes
/// from orchestrators like Kubernetes, Docker, and load balancers.
/// </remarks>
[Route("api/[controller]")]
[ApiController]
[AllowAnonymous]
[Produces("application/json")]
public class HealthController : ControllerBase
{
    /// <summary>
    /// Basic health check - returns 200 OK if the service is running.
    /// </summary>
    /// <returns>Health status and timestamp.</returns>
    [HttpGet(Name = "GetHealth")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Readiness check - confirms the service is ready to accept requests.
    /// </summary>
    /// <remarks>
    /// Can be extended to verify database connectivity and other dependencies.
    /// </remarks>
    /// <returns>Readiness status and timestamp.</returns>
    [HttpGet("ready", Name = "GetHealthReady")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Ready()
    {
        // TODO: Add database connectivity check if needed
        return Ok(new { status = "ready", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Liveness check - confirms the service process is alive.
    /// </summary>
    /// <remarks>
    /// Used by orchestrators to determine if the service needs to be restarted.
    /// </remarks>
    /// <returns>Liveness status and timestamp.</returns>
    [HttpGet("live", Name = "GetHealthLive")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Live()
    {
        return Ok(new { status = "alive", timestamp = DateTime.UtcNow });
    }
}

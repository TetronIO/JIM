using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers;

/// <summary>
/// Health check endpoint for load balancers and service monitors.
/// This controller is intentionally unauthenticated to allow external health probes.
/// </summary>
[Route("[controller]")]
[ApiController]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    /// <summary>
    /// Basic health check - returns 200 OK if the service is running.
    /// </summary>
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Readiness check - can be extended to verify database connectivity, etc.
    /// </summary>
    [HttpGet("ready")]
    public IActionResult Ready()
    {
        // TODO: Add database connectivity check if needed
        return Ok(new { status = "ready", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Liveness check - confirms the service process is alive.
    /// </summary>
    [HttpGet("live")]
    public IActionResult Live()
    {
        return Ok(new { status = "alive", timestamp = DateTime.UtcNow });
    }
}

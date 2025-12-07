using Asp.Versioning;
using JIM.Api.Models;
using JIM.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers;

/// <summary>
/// API controller for managing security-related configuration such as roles.
/// </summary>
/// <remarks>
/// This controller provides endpoints for:
/// - Retrieving role definitions used for access control
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrators")]
[Produces("application/json")]
public class SecurityController : ControllerBase
{
    private readonly ILogger<SecurityController> _logger;
    private readonly JimApplication _application;

    public SecurityController(ILogger<SecurityController> logger, JimApplication application)
    {
        _logger = logger;
        _application = application;
    }

    /// <summary>
    /// Gets all security roles defined in the system.
    /// </summary>
    /// <remarks>
    /// Roles define permissions that can be assigned to users or groups
    /// to control access to JIM functionality.
    /// </remarks>
    /// <returns>A list of all role definitions.</returns>
    [HttpGet("roles", Name = "GetRoles")]
    [ProducesResponseType(typeof(IEnumerable<RoleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRolesAsync()
    {
        _logger.LogTrace("Requested roles");
        var roles = await _application.Security.GetRolesAsync();
        var dtos = roles.Select(RoleDto.FromEntity);
        return Ok(dtos);
    }
}

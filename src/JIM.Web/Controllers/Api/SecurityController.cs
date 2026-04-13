// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Web.Models.Api;
using JIM.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing security-related configuration such as Roles.
/// </summary>
/// <remarks>
/// This controller provides endpoints for:
/// - Retrieving Role definitions used for access control
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class SecurityController(ILogger<SecurityController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<SecurityController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List security Roles
    /// </summary>
    /// <returns>A list of all Role definitions.</returns>
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

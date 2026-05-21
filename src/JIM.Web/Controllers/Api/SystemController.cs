// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Utility;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for system-wide administrative operations.
/// </summary>
/// <remarks>
/// Currently scoped to factory reset; future system-wide maintenance routines should join it here.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class SystemController(ILogger<SystemController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<SystemController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Factory reset
    /// </summary>
    /// <remarks>
    /// Wipes all customer data and configuration from the database, preserving the schema,
    /// EF Core migration history, and the rows seeded at first launch (built-in metaverse
    /// attributes and object types, built-in roles, built-in connector definitions,
    /// built-in example data sets and templates, built-in predefined searches, the
    /// singleton service settings record, and infrastructure API keys).
    ///
    /// Refuses with HTTP 409 if any activity is currently in progress.
    ///
    /// **This is destructive and cannot be undone.** Callers should obtain user confirmation
    /// before invoking.
    /// </remarks>
    /// <returns>A summary of what was removed.</returns>
    /// <response code="200">Returns the reset summary.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="403">If the user lacks the Administrator role.</response>
    /// <response code="409">If activities are currently in progress.</response>
    [HttpPost("reset", Name = "ResetSystem")]
    [ProducesResponseType(typeof(SystemResetResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ResetAsync()
    {
        var invokingUserName = User.Identity?.Name ?? "(unknown)";
        _logger.LogWarning("Factory reset initiated by {User}", invokingUserName);

        try
        {
            var result = await _application.System.ResetSystemAsync(invokingUserName);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ApiErrorResponse.Conflict(ex.Message));
        }
    }
}

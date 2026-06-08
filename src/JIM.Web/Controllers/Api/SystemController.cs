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
    /// Resolves the initiating security principal from the current request context.
    /// </summary>
    private async Task<(ActivityInitiatorType Type, Guid? Id, string? Name)> GetInitiatorInfoAsync()
    {
        // API key authentication: the middleware stashes the key id in HttpContext.Items.
        if (HttpContext.Items.TryGetValue("ApiKeyId", out var apiKeyIdObj) && apiKeyIdObj is Guid apiKeyId)
        {
            var apiKey = await _application.Repository.ApiKeys.GetByIdAsync(apiKeyId);
            return (ActivityInitiatorType.ApiKey, apiKeyId, apiKey?.Name ?? "API Key");
        }

        // User authentication: resolve the principal id and display name from claims.
        var userIdClaim = User.FindFirst("sub") ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        var nameClaim = User.FindFirst("name") ?? User.FindFirst(System.Security.Claims.ClaimTypes.Name);

        if (userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId))
            return (ActivityInitiatorType.User, userId, nameClaim?.Value ?? User.Identity?.Name);

        return (ActivityInitiatorType.User, null, User.Identity?.Name);
    }
}

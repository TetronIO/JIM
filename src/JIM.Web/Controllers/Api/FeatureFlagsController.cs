// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Application.Exceptions;
using JIM.Models.Core;
using JIM.Utilities;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for JIM's feature flags (#1781).
/// </summary>
/// <remarks>
/// A flag is either Preview (visible here, and in the Service Settings page's "Preview features" card) or In
/// Development (never surfaced to administrators; on only in development and in the integration harness).
/// Enabling an In Development flag requires <c>allowInDevelopment: true</c> on the update request.
/// </remarks>
[Route("api/v{version:apiVersion}/features")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class FeatureFlagsController(ILogger<FeatureFlagsController> logger, JimApplication application) : ApiControllerBase(application, logger)
{
    private readonly ILogger<FeatureFlagsController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List Feature Flags
    /// </summary>
    /// <param name="includeInDevelopment">
    /// Include In Development flags, which are otherwise omitted. Intended for development and the integration
    /// harness; a production administrator has no use for a flag this surface never lets them enable.
    /// </param>
    /// <returns>Every Preview-tier flag (and In Development flags when requested), with its current state.</returns>
    [HttpGet(Name = "GetFeatureFlags")]
    [ProducesResponseType(typeof(IEnumerable<FeatureFlagDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAllAsync([FromQuery] bool includeInDevelopment = false)
    {
        _logger.LogTrace("Requested feature flags (includeInDevelopment: {IncludeInDevelopment})", includeInDevelopment);
        var flags = await _application.FeatureFlags.GetFeatureFlagsAsync(includeInDevelopment);
        return Ok(flags.Select(FeatureFlagDto.FromState));
    }

    /// <summary>
    /// Update a Feature Flag
    /// </summary>
    /// <remarks>
    /// Enabling an In Development flag requires <c>allowInDevelopment: true</c> in the request body; without it
    /// the request fails. Disabling never needs it, whatever the flag's tier.
    /// </remarks>
    /// <param name="key">The flag's key, e.g. "Features.UniqueValueGeneration".</param>
    /// <param name="request">The new state.</param>
    /// <returns>The updated Feature Flag.</returns>
    [HttpPut("{key}", Name = "UpdateFeatureFlag")]
    [ProducesResponseType(typeof(FeatureFlagDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(string key, [FromBody] FeatureFlagUpdateRequestDto request)
    {
        _logger.LogInformation("Updating feature flag {FlagKey} to {Enabled}", LogSanitiser.Sanitise(key), request.Enabled);

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            var state = apiKey != null
                ? await _application.FeatureFlags.SetFeatureFlagAsync(key, request.Enabled, apiKey, request.AllowInDevelopment)
                : await _application.FeatureFlags.SetFeatureFlagAsync(key, request.Enabled, await GetCurrentUserAsync(), request.AllowInDevelopment);

            return Ok(FeatureFlagDto.FromState(state));
        }
        catch (FeatureFlagNotFoundException ex)
        {
            return NotFound(new ApiErrorResponse { Message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiErrorResponse { Message = ex.Message });
        }
    }
}

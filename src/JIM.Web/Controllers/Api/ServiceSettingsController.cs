using System.Security.Claims;
using Asp.Versioning;
using JIM.Application;
using JIM.Models.Security;
using JIM.Utilities;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing service-wide configuration settings.
/// </summary>
/// <remarks>
/// Service settings control behaviour such as change tracking, sync page sizes,
/// history retention, and other operational parameters. Settings are identified
/// by dot-notation keys (e.g., "ChangeTracking.CsoChanges.Enabled").
///
/// Read-only settings (mirrored from environment variables) cannot be modified
/// through this API.
/// </remarks>
[Route("api/v{version:apiVersion}/service-settings")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class ServiceSettingsController(ILogger<ServiceSettingsController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<ServiceSettingsController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Gets all service settings.
    /// </summary>
    /// <returns>A list of all service settings with their current and default values.</returns>
    [HttpGet(Name = "GetServiceSettings")]
    [ProducesResponseType(typeof(IEnumerable<ServiceSettingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAllAsync()
    {
        _logger.LogTrace("Requested all service settings");
        var settings = await _application.ServiceSettings.GetAllSettingsAsync();
        var dtos = settings.Select(ServiceSettingDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Gets a specific service setting by key.
    /// </summary>
    /// <param name="key">The setting key using dot notation (e.g., "ChangeTracking.CsoChanges.Enabled").</param>
    /// <returns>The service setting details.</returns>
    [HttpGet("{key}", Name = "GetServiceSetting")]
    [ProducesResponseType(typeof(ServiceSettingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByKeyAsync(string key)
    {
        _logger.LogTrace("Requested service setting {SettingKey}", LogSanitiser.Sanitise(key));
        var setting = await _application.ServiceSettings.GetSettingAsync(key);

        if (setting == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Service setting not found: {key}" });
        }

        return Ok(ServiceSettingDto.FromEntity(setting));
    }

    /// <summary>
    /// Updates a service setting value.
    /// </summary>
    /// <remarks>
    /// Sets the value for a configurable service setting. Read-only settings
    /// (mirrored from environment variables) cannot be modified through this endpoint.
    /// </remarks>
    /// <param name="key">The setting key using dot notation.</param>
    /// <param name="request">The update request containing the new value.</param>
    /// <returns>The updated service setting.</returns>
    [HttpPut("{key}", Name = "UpdateServiceSetting")]
    [ProducesResponseType(typeof(ServiceSettingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(string key, [FromBody] ServiceSettingUpdateRequestDto request)
    {
        _logger.LogInformation("Updating service setting {SettingKey}", LogSanitiser.Sanitise(key));

        var setting = await _application.ServiceSettings.GetSettingAsync(key);
        if (setting == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Service setting not found: {key}" });
        }

        if (setting.IsReadOnly)
        {
            return BadRequest(new ApiErrorResponse { Message = $"Setting '{setting.DisplayName}' is read-only and cannot be modified" });
        }

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ServiceSettings.UpdateSettingValueAsync(key, request.Value, apiKey);
            else
                await _application.ServiceSettings.UpdateSettingValueAsync(key, request.Value, (JIM.Models.Core.MetaverseObject?)null);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiErrorResponse { Message = ex.Message });
        }

        // Re-fetch to return the updated state
        var updated = await _application.ServiceSettings.GetSettingAsync(key);
        return Ok(ServiceSettingDto.FromEntity(updated!));
    }

    /// <summary>
    /// Reverts a service setting to its default value.
    /// </summary>
    /// <remarks>
    /// Clears any override and restores the setting to its default value.
    /// Read-only settings cannot be reverted through this endpoint.
    /// </remarks>
    /// <param name="key">The setting key using dot notation.</param>
    /// <returns>The reverted service setting.</returns>
    [HttpDelete("{key}", Name = "RevertServiceSetting")]
    [ProducesResponseType(typeof(ServiceSettingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevertAsync(string key)
    {
        _logger.LogInformation("Reverting service setting {SettingKey} to default", LogSanitiser.Sanitise(key));

        var setting = await _application.ServiceSettings.GetSettingAsync(key);
        if (setting == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"Service setting not found: {key}" });
        }

        if (setting.IsReadOnly)
        {
            return BadRequest(new ApiErrorResponse { Message = $"Setting '{setting.DisplayName}' is read-only and cannot be reverted" });
        }

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.ServiceSettings.RevertSettingToDefaultAsync(key, apiKey);
            else
                await _application.ServiceSettings.RevertSettingToDefaultAsync(key, (JIM.Models.Core.MetaverseObject?)null);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiErrorResponse { Message = ex.Message });
        }

        // Re-fetch to return the reverted state
        var reverted = await _application.ServiceSettings.GetSettingAsync(key);
        return Ok(ServiceSettingDto.FromEntity(reverted!));
    }

    private bool IsApiKeyAuthenticated()
    {
        return User.HasClaim("auth_method", "api_key");
    }

    private async Task<ApiKey?> GetCurrentApiKeyAsync()
    {
        if (!IsApiKeyAuthenticated())
            return null;

        var apiKeyIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(apiKeyIdClaim) || !Guid.TryParse(apiKeyIdClaim, out var apiKeyId))
            return null;

        return await _application.Repository.ApiKeys.GetByIdAsync(apiKeyId);
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Utilities;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing service-wide configuration settings.
/// </summary>
/// <remarks>
/// Service Settings control behaviour such as change tracking, sync page sizes,
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
public class ServiceSettingsController(ILogger<ServiceSettingsController> logger, JimApplication application) : ApiControllerBase(application, logger)
{
    private readonly ILogger<ServiceSettingsController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List Service Settings
    /// </summary>
    /// <returns>A list of all Service Settings with their current and default values.</returns>
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
    /// Get a Service Setting
    /// </summary>
    /// <param name="key">The setting key using dot notation (e.g., "ChangeTracking.CsoChanges.Enabled").</param>
    /// <returns>The Service Setting details.</returns>
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
    /// Update a Service Setting
    /// </summary>
    /// <remarks>
    /// Read-only settings (mirrored from environment variables) cannot be modified through this endpoint.
    /// </remarks>
    /// <param name="key">The setting key using dot notation.</param>
    /// <param name="request">The update request containing the new value.</param>
    /// <returns>The updated Service Setting.</returns>
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
                await _application.ServiceSettings.UpdateSettingValueAsync(key, request.Value, apiKey, request.ChangeReason);
            else
                await _application.ServiceSettings.UpdateSettingValueAsync(key, request.Value, (JIM.Models.Core.MetaverseObject?)null, request.ChangeReason);
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
    /// Revert a Service Setting to its default
    /// </summary>
    /// <remarks>
    /// Read-only settings cannot be reverted through this endpoint.
    /// </remarks>
    /// <param name="key">The setting key using dot notation.</param>
    /// <param name="changeReason">An optional reason for the revert, recorded against the change history.</param>
    /// <returns>The reverted Service Setting.</returns>
    [HttpDelete("{key}", Name = "RevertServiceSetting")]
    [ProducesResponseType(typeof(ServiceSettingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevertAsync(string key, [FromQuery] string? changeReason = null)
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
                await _application.ServiceSettings.RevertSettingToDefaultAsync(key, apiKey, changeReason);
            else
                await _application.ServiceSettings.RevertSettingToDefaultAsync(key, (JIM.Models.Core.MetaverseObject?)null, changeReason);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiErrorResponse { Message = ex.Message });
        }

        // Re-fetch to return the reverted state
        var reverted = await _application.ServiceSettings.GetSettingAsync(key);
        return Ok(ServiceSettingDto.FromEntity(reverted!));
    }

    #region Configuration Change History

    /// <summary>
    /// List the change history for a Service Setting.
    /// </summary>
    /// <param name="key">The setting key using dot notation.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the setting has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{key}/change-history", Name = "GetServiceSettingChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetServiceSettingChangeHistoryAsync(string key, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.ServiceSetting, key, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of a Service Setting's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="key">The setting key using dot notation.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the redacted snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the setting.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{key}/change-history/{changeVersion:int}", Name = "GetServiceSettingChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetServiceSettingChangeAsync(string key, int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.ServiceSetting, key, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for Service Setting {key} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of a Service Setting's configuration.
    /// </summary>
    /// <param name="key">The setting key using dot notation.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the setting.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{key}/change-history/compare", Name = "CompareServiceSettingChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompareServiceSettingChangesAsync(string key, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.ServiceSetting, key, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for Service Setting {key}."));
        return Ok(diff);
    }

    #endregion
}

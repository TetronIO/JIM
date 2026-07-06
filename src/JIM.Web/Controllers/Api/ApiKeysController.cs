// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Security;
using JIM.Utilities;
using JIM.Web.Middleware.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing API keys used for non-interactive authentication.
/// </summary>
/// <remarks>
/// API keys provide an alternative to SSO authentication for scenarios such as:
/// - CI/CD pipelines
/// - Integration testing
/// - PowerShell module automation
/// - Scheduled tasks
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class ApiKeysController(ILogger<ApiKeysController> logger, JimApplication application) : ApiControllerBase(application, logger)
{
    private readonly ILogger<ApiKeysController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List API keys
    /// </summary>
    /// <remarks>
    /// Returns all API keys with their metadata. The full key value is never returned;
    /// only the key prefix is shown for identification purposes.
    /// </remarks>
    /// <returns>A list of all API keys.</returns>
    [HttpGet(Name = "GetApiKeys")]
    [ProducesResponseType(typeof(IEnumerable<ApiKeyDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAllAsync()
    {
        _logger.LogTrace("Requested all API keys");
        var apiKeys = await _application.Security.GetApiKeysAsync();
        var dtos = apiKeys.Select(ApiKeyDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Get an API key
    /// </summary>
    /// <param name="id">The unique identifier of the API key.</param>
    /// <returns>The API key details.</returns>
    [HttpGet("{id:guid}", Name = "GetApiKey")]
    [ProducesResponseType(typeof(ApiKeyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByIdAsync(Guid id)
    {
        _logger.LogTrace("Requested API key {ApiKeyId}", id);
        var apiKey = await _application.Security.GetApiKeyAsync(id);

        if (apiKey == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"API key not found: {id}" });
        }

        return Ok(ApiKeyDto.FromEntity(apiKey));
    }

    /// <summary>
    /// Create an API key
    /// </summary>
    /// <remarks>
    /// The full API key is returned only in this response. Store it securely as it
    /// cannot be retrieved again. If lost, the key must be deleted and a new one created.
    /// </remarks>
    /// <param name="request">The API key creation request.</param>
    /// <returns>The created API key including the full key value (shown only once).</returns>
    [HttpPost(Name = "CreateApiKey")]
    [ProducesResponseType(typeof(ApiKeyCreateResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateAsync([FromBody] ApiKeyCreateRequestDto request)
    {
        _logger.LogInformation("Creating new API key: {ApiKeyName}", LogSanitiser.Sanitise(request.Name));

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new ApiErrorResponse { Message = "API key name is required" });
        }

        // Generate the API key
        var (fullKey, keyPrefix) = ApiKeyAuthenticationHandler.GenerateApiKey();
        var keyHash = ApiKeyAuthenticationHandler.HashApiKey(fullKey);

        // Get the roles
        var roles = new List<Role>();
        if (request.RoleIds.Count > 0)
        {
            var allRoles = await _application.Security.GetRolesAsync();
            roles = allRoles.Where(r => request.RoleIds.Contains(r.Id)).ToList();

            if (roles.Count != request.RoleIds.Count)
            {
                return BadRequest(new ApiErrorResponse { Message = "One or more role IDs are invalid" });
            }
        }

        // Create the API key entity
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            KeyHash = keyHash,
            KeyPrefix = keyPrefix,
            ExpiresAt = request.ExpiresAt,
            Roles = roles
        };

        var callerApiKey = await GetCurrentApiKeyAsync();
        var createdKey = callerApiKey != null
            ? await _application.Security.CreateApiKeyAsync(apiKey, callerApiKey, request.ChangeReason)
            : await _application.Security.CreateApiKeyAsync(apiKey, await GetCurrentUserAsync(), request.ChangeReason);

        _logger.LogInformation("Created API key {ApiKeyId} with prefix {KeyPrefix}", createdKey.Id, keyPrefix);

        // Return the full key only on creation
        var responseDto = new ApiKeyCreateResponseDto
        {
            Id = createdKey.Id,
            Name = createdKey.Name,
            Description = createdKey.Description,
            KeyPrefix = createdKey.KeyPrefix,
            CreatedAt = createdKey.Created,
            ExpiresAt = createdKey.ExpiresAt,
            LastUsedAt = createdKey.LastUsedAt,
            LastUsedFromIp = createdKey.LastUsedFromIp,
            IsEnabled = createdKey.IsEnabled,
            Roles = createdKey.Roles.Select(RoleDto.FromEntity).ToList(),
            Key = fullKey // Only returned on creation!
        };

        return CreatedAtRoute("GetApiKey", new { id = createdKey.Id }, responseDto);
    }

    /// <summary>
    /// Update an API key
    /// </summary>
    /// <param name="id">The unique identifier of the API key.</param>
    /// <param name="request">The update request.</param>
    /// <returns>The updated API key details.</returns>
    [HttpPut("{id:guid}", Name = "UpdateApiKey")]
    [ProducesResponseType(typeof(ApiKeyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] ApiKeyUpdateRequestDto request)
    {
        _logger.LogInformation("Updating API key {ApiKeyId}", id);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new ApiErrorResponse { Message = "API key name is required" });
        }

        var existingKey = await _application.Security.GetApiKeyAsync(id);
        if (existingKey == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"API key not found: {id}" });
        }

        // Get the roles
        var roles = new List<Role>();
        if (request.RoleIds.Count > 0)
        {
            var allRoles = await _application.Security.GetRolesAsync();
            roles = allRoles.Where(r => request.RoleIds.Contains(r.Id)).ToList();

            if (roles.Count != request.RoleIds.Count)
            {
                return BadRequest(new ApiErrorResponse { Message = "One or more role IDs are invalid" });
            }
        }

        // Update the API key
        existingKey.Name = request.Name;
        existingKey.Description = request.Description;
        existingKey.ExpiresAt = request.ExpiresAt;
        existingKey.IsEnabled = request.IsEnabled;
        existingKey.Roles = roles;

        var callerApiKey = await GetCurrentApiKeyAsync();
        var updatedKey = callerApiKey != null
            ? await _application.Security.UpdateApiKeyAsync(existingKey, callerApiKey, request.ChangeReason)
            : await _application.Security.UpdateApiKeyAsync(existingKey, await GetCurrentUserAsync(), request.ChangeReason);

        _logger.LogInformation("Updated API key {ApiKeyId}", id);

        return Ok(ApiKeyDto.FromEntity(updatedKey));
    }

    /// <summary>
    /// Delete an API key
    /// </summary>
    /// <param name="id">The unique identifier of the API key to delete.</param>
    /// <param name="changeReason">Optional reason for the deletion, recorded on the audit Activity.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("{id:guid}", Name = "DeleteApiKey")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(Guid id, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Deleting API key {ApiKeyId}", id);

        var existingKey = await _application.Security.GetApiKeyAsync(id);
        if (existingKey == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"API key not found: {id}" });
        }

        var callerApiKey = await GetCurrentApiKeyAsync();
        if (callerApiKey != null)
            await _application.Security.DeleteApiKeyAsync(id, callerApiKey, changeReason);
        else
            await _application.Security.DeleteApiKeyAsync(id, await GetCurrentUserAsync(), changeReason);

        _logger.LogInformation("Deleted API key {ApiKeyId} (prefix: {KeyPrefix})", id, existingKey.KeyPrefix);

        return NoContent();
    }

    #region Configuration Change History

    /// <summary>
    /// List the change history for an API Key.
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the API key.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the API key has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{id:guid}/change-history", Name = "GetApiKeyChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetApiKeyChangeHistoryAsync(Guid id, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.ApiKey, id, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of an API Key's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the API key.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the API key.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{id:guid}/change-history/{changeVersion:int}", Name = "GetApiKeyChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetApiKeyChangeAsync(Guid id, int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.ApiKey, id, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for API Key {id} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of an API Key's configuration.
    /// </summary>
    /// <param name="id">The unique identifier (GUID) of the API key.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the API key.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("{id:guid}/change-history/compare", Name = "CompareApiKeyChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompareApiKeyChangesAsync(Guid id, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.ApiKey, id, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for API Key {id}."));
        return Ok(diff);
    }

    #endregion
}

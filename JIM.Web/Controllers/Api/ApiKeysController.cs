using Asp.Versioning;
using JIM.Application;
using JIM.Models.Security;
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
[Authorize(Roles = "Administrators")]
[Produces("application/json")]
public class ApiKeysController(ILogger<ApiKeysController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<ApiKeysController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Gets all API keys.
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
        var apiKeys = await _application.Repository.ApiKeys.GetAllAsync();
        var dtos = apiKeys.Select(ApiKeyDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Gets a specific API key by ID.
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
        var apiKey = await _application.Repository.ApiKeys.GetByIdAsync(id);

        if (apiKey == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"API key not found: {id}" });
        }

        return Ok(ApiKeyDto.FromEntity(apiKey));
    }

    /// <summary>
    /// Creates a new API key.
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
        _logger.LogInformation("Creating new API key: {ApiKeyName}", request.Name);

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

        var createdKey = await _application.Repository.ApiKeys.CreateAsync(apiKey);

        _logger.LogInformation("Created API key {ApiKeyId} with prefix {KeyPrefix}", createdKey.Id, keyPrefix);

        // Return the full key only on creation
        var responseDto = new ApiKeyCreateResponseDto
        {
            Id = createdKey.Id,
            Name = createdKey.Name,
            Description = createdKey.Description,
            KeyPrefix = createdKey.KeyPrefix,
            CreatedAt = createdKey.CreatedAt,
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
    /// Updates an existing API key.
    /// </summary>
    /// <remarks>
    /// Updates the API key's name, description, roles, expiry, and enabled status.
    /// The key value itself cannot be changed.
    /// </remarks>
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

        var existingKey = await _application.Repository.ApiKeys.GetByIdAsync(id);
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

        var updatedKey = await _application.Repository.ApiKeys.UpdateAsync(existingKey);

        _logger.LogInformation("Updated API key {ApiKeyId}", id);

        return Ok(ApiKeyDto.FromEntity(updatedKey));
    }

    /// <summary>
    /// Deletes an API key.
    /// </summary>
    /// <remarks>
    /// Permanently deletes the API key. Any requests using this key will fail immediately.
    /// </remarks>
    /// <param name="id">The unique identifier of the API key to delete.</param>
    /// <returns>No content on success.</returns>
    [HttpDelete("{id:guid}", Name = "DeleteApiKey")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAsync(Guid id)
    {
        _logger.LogInformation("Deleting API key {ApiKeyId}", id);

        var existingKey = await _application.Repository.ApiKeys.GetByIdAsync(id);
        if (existingKey == null)
        {
            return NotFound(new ApiErrorResponse { Message = $"API key not found: {id}" });
        }

        await _application.Repository.ApiKeys.DeleteAsync(id);

        _logger.LogInformation("Deleted API key {ApiKeyId} (prefix: {KeyPrefix})", id, existingKey.KeyPrefix);

        return NoContent();
    }
}

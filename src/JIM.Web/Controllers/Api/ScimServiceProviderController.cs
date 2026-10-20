// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using Asp.Versioning;
using JIM.Application;
using JIM.Connectors;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Scim.Discovery;
using JIM.Web.Models.Api;
using JIM.Web.Scim;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for administering a Connected System's SCIM 2.0 Service Provider endpoint: the keys its SCIM clients
/// call it with, what its store holds, and the resource types and schemas it serves.
/// </summary>
/// <remarks>
/// Only for a Connected System that uses the SCIM 2.0 Service Provider Connector; any other answers 404. A key is
/// reached only through the Connected System it is scoped to. To rotate a key, create a new one, give it to the
/// identity provider, then disable or delete the old one; a Connected System has at most two usable keys so the two
/// can overlap. Every endpoint is refused while the SCIM 2.0 Service Provider feature is off.
/// </remarks>
[Route("api/v{version:apiVersion}/synchronisation/connected-systems/{connectedSystemId:int}/scim")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class ScimServiceProviderController(ILogger<ScimServiceProviderController> logger, JimApplication application)
    : ApiControllerBase(application, logger)
{
    private readonly ILogger<ScimServiceProviderController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List a Connected System's SCIM keys
    /// </summary>
    /// <remarks>
    /// Every key scoped to the Connected System's SCIM endpoint, usable or not, with its expiry and last use, so you
    /// can tell when an old key has gone quiet after a rotation. The secret is never returned.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System's ID.</param>
    /// <response code="200">The keys, by name.</response>
    /// <response code="404">No such Connected System, or it does not use the SCIM 2.0 Service Provider Connector.</response>
    [HttpGet("keys", Name = "GetScimKeys")]
    [ProducesResponseType(typeof(IEnumerable<ApiKeyDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetKeysAsync(int connectedSystemId)
    {
        if (await RequireServiceProviderAsync(connectedSystemId) is { } refusal)
            return refusal;

        var keys = await _application.Security.GetScimServiceProviderKeysAsync(connectedSystemId);
        return Ok(keys.Select(ApiKeyDto.FromEntity));
    }

    /// <summary>
    /// Get a SCIM key
    /// </summary>
    /// <param name="connectedSystemId">The Connected System's ID.</param>
    /// <param name="keyId">The key's ID.</param>
    /// <response code="200">The key. The secret is never returned.</response>
    /// <response code="404">No such Connected System or key, or the key is not scoped to this Connected System.</response>
    [HttpGet("keys/{keyId:guid}", Name = "GetScimKey")]
    [ProducesResponseType(typeof(ApiKeyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetKeyAsync(int connectedSystemId, Guid keyId)
    {
        if (await RequireServiceProviderAsync(connectedSystemId) is { } refusal)
            return refusal;

        var key = await GetScopedKeyAsync(connectedSystemId, keyId);
        return key == null ? KeyNotFound(connectedSystemId, keyId) : Ok(ApiKeyDto.FromEntity(key));
    }

    /// <summary>
    /// Create a SCIM key
    /// </summary>
    /// <remarks>
    /// Creates a key for an identity provider to call this Connected System's SCIM endpoint with. The key carries no
    /// roles, so it opens nothing else in JIM, and always expires: a year from now unless you choose otherwise, and at
    /// most two years. The secret is returned only in this response and cannot be shown again. A Connected System can
    /// have at most two usable keys, so disable or delete one before adding a third.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System's ID.</param>
    /// <param name="request">The key's name, description and expiry.</param>
    /// <response code="201">The key, with its secret.</response>
    /// <response code="400">No name, an expiry in the past or beyond two years, or two usable keys already.</response>
    /// <response code="404">No such Connected System, or it does not use the SCIM 2.0 Service Provider Connector.</response>
    [HttpPost("keys", Name = "CreateScimKey")]
    [ProducesResponseType(typeof(ApiKeyCreateResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateKeyAsync(int connectedSystemId, [FromBody] ScimKeyCreateRequestDto request)
    {
        if (await RequireServiceProviderAsync(connectedSystemId) is { } refusal)
            return refusal;
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(ApiErrorResponse.ValidationError("A SCIM key needs a name."));

        var name = request.Name.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        var callerApiKey = await GetCurrentApiKeyAsync();
        var created = callerApiKey != null
            ? await _application.Security.CreateScimServiceProviderKeyAsync(connectedSystemId, name, description, request.ExpiresAt, callerApiKey, request.ChangeReason)
            : await _application.Security.CreateScimServiceProviderKeyAsync(connectedSystemId, name, description, request.ExpiresAt, await GetCurrentUserAsync(), request.ChangeReason);

        _logger.LogInformation("Created SCIM key {ApiKeyId} with prefix {KeyPrefix} for Connected System {ConnectedSystemId}",
            created.Key.Id, created.Key.KeyPrefix, connectedSystemId);
        return CreatedAtRoute("GetScimKey", new { connectedSystemId, keyId = created.Key.Id },
            ApiKeyCreateResponseDto.FromCreated(created.Key, created.Secret));
    }

    /// <summary>
    /// Update a SCIM key
    /// </summary>
    /// <remarks>
    /// Renames, re-describes, re-dates, disables or re-enables a key; only the values given change. A new expiry is at
    /// most two years from now, and re-enabling a key is refused when the Connected System already has two usable keys.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System's ID.</param>
    /// <param name="keyId">The key's ID.</param>
    /// <param name="request">The values to change.</param>
    /// <response code="200">The key as saved.</response>
    /// <response code="400">A blank name, an expiry in the past or beyond two years, or a third usable key.</response>
    /// <response code="404">No such Connected System or key, or the key is not scoped to this Connected System.</response>
    [HttpPut("keys/{keyId:guid}", Name = "UpdateScimKey")]
    [ProducesResponseType(typeof(ApiKeyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateKeyAsync(int connectedSystemId, Guid keyId, [FromBody] ScimKeyUpdateRequestDto request)
    {
        if (await RequireServiceProviderAsync(connectedSystemId) is { } refusal)
            return refusal;
        if (request.Name != null && string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(ApiErrorResponse.ValidationError("A SCIM key's name cannot be blank."));

        var key = await GetScopedKeyAsync(connectedSystemId, keyId);
        if (key == null)
            return KeyNotFound(connectedSystemId, keyId);

        if (request.Name != null)
            key.Name = request.Name.Trim();
        if (request.Description != null)
            key.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (request.ExpiresAt.HasValue)
            key.ExpiresAt = request.ExpiresAt;
        if (request.IsEnabled.HasValue)
            key.IsEnabled = request.IsEnabled.Value;

        var callerApiKey = await GetCurrentApiKeyAsync();
        var updated = callerApiKey != null
            ? await _application.Security.UpdateApiKeyAsync(key, callerApiKey, request.ChangeReason)
            : await _application.Security.UpdateApiKeyAsync(key, await GetCurrentUserAsync(), request.ChangeReason);

        _logger.LogInformation("Updated SCIM key {ApiKeyId} for Connected System {ConnectedSystemId}", keyId, connectedSystemId);
        return Ok(ApiKeyDto.FromEntity(updated));
    }

    /// <summary>
    /// Delete a SCIM key
    /// </summary>
    /// <remarks>
    /// The key stops working at once. To keep a record of it, disable it instead.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System's ID.</param>
    /// <param name="keyId">The key's ID.</param>
    /// <param name="changeReason">Optional reason for the deletion, recorded on the audit Activity.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="404">No such Connected System or key, or the key is not scoped to this Connected System.</response>
    [HttpDelete("keys/{keyId:guid}", Name = "DeleteScimKey")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteKeyAsync(int connectedSystemId, Guid keyId, [FromQuery] string? changeReason = null)
    {
        if (await RequireServiceProviderAsync(connectedSystemId) is { } refusal)
            return refusal;
        if (await GetScopedKeyAsync(connectedSystemId, keyId) == null)
            return KeyNotFound(connectedSystemId, keyId);

        var callerApiKey = await GetCurrentApiKeyAsync();
        if (callerApiKey != null)
            await _application.Security.DeleteApiKeyAsync(keyId, callerApiKey, changeReason);
        else
            await _application.Security.DeleteApiKeyAsync(keyId, await GetCurrentUserAsync(), changeReason);

        _logger.LogInformation("Deleted SCIM key {ApiKeyId} for Connected System {ConnectedSystemId}", keyId, connectedSystemId);
        return NoContent();
    }

    /// <summary>
    /// Get a Connected System's SCIM endpoint summary
    /// </summary>
    /// <remarks>
    /// The base URL to give an identity provider, how many resources of each type the store holds, deleted resources
    /// kept until an import reads them, how many changes the next import will read, and when a SCIM client last wrote.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System's ID.</param>
    /// <response code="200">The summary.</response>
    /// <response code="404">No such Connected System, or it does not use the SCIM 2.0 Service Provider Connector.</response>
    [HttpGet("summary", Name = "GetScimSummary")]
    [ProducesResponseType(typeof(ScimServiceProviderSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSummaryAsync(int connectedSystemId)
    {
        if (await RequireServiceProviderAsync(connectedSystemId) is { } refusal)
            return refusal;

        var summary = await _application.Scim.GetStoreSummaryAsync(connectedSystemId, HttpContext.RequestAborted);
        return Ok(ScimServiceProviderSummaryDto.FromSummary(summary, BaseUrl(connectedSystemId)));
    }

    /// <summary>
    /// List the SCIM resource types a Connected System serves
    /// </summary>
    /// <remarks>
    /// Read-only: the RFC 7643 resource type documents the endpoint publishes, each naming its endpoint, base schema
    /// and schema extensions.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System's ID.</param>
    /// <response code="200">The resource types.</response>
    /// <response code="404">No such Connected System, or it does not use the SCIM 2.0 Service Provider Connector.</response>
    [HttpGet("resource-types", Name = "GetScimResourceTypes")]
    [ProducesResponseType(typeof(IEnumerable<ScimResourceType>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetResourceTypesAsync(int connectedSystemId)
    {
        if (await RequireServiceProviderAsync(connectedSystemId) is { } refusal)
            return refusal;

        var (resourceTypes, _) = await _application.Scim.GetDiscoveryDocumentsAsync(connectedSystemId, HttpContext.RequestAborted);
        return Ok(resourceTypes);
    }

    /// <summary>
    /// List the SCIM schemas a Connected System serves
    /// </summary>
    /// <remarks>
    /// Read-only: the RFC 7643 schema documents the endpoint publishes, with every attribute's characteristics.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System's ID.</param>
    /// <response code="200">The schemas.</response>
    /// <response code="404">No such Connected System, or it does not use the SCIM 2.0 Service Provider Connector.</response>
    [HttpGet("schemas", Name = "GetScimSchemas")]
    [ProducesResponseType(typeof(IEnumerable<ScimSchema>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSchemasAsync(int connectedSystemId)
    {
        if (await RequireServiceProviderAsync(connectedSystemId) is { } refusal)
            return refusal;

        var (_, schemas) = await _application.Scim.GetDiscoveryDocumentsAsync(connectedSystemId, HttpContext.RequestAborted);
        return Ok(schemas);
    }

    /// <summary>
    /// Get a SCIM resource's journal
    /// </summary>
    /// <remarks>
    /// The resource's accepted writes, oldest first: what each did, the attributes it changed or ignored (by name,
    /// never by value), and the key and address that made it. Optionally within a range of change numbers; at most
    /// 1,000 entries.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System's ID.</param>
    /// <param name="resourceId">The SCIM resource, which is the Connected System Object's external id.</param>
    /// <param name="afterSequence">Only writes after this change number.</param>
    /// <param name="upToSequence">Only writes up to and including this change number.</param>
    /// <response code="200">The entries; empty when the resource has none in range.</response>
    /// <response code="404">No such Connected System, or it does not use the SCIM 2.0 Service Provider Connector.</response>
    [HttpGet("journal", Name = "GetScimJournal")]
    [ProducesResponseType(typeof(IEnumerable<ScimJournalEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJournalAsync(int connectedSystemId, [FromQuery] Guid resourceId, [FromQuery] long? afterSequence = null,
        [FromQuery] long? upToSequence = null)
    {
        if (await RequireServiceProviderAsync(connectedSystemId) is { } refusal)
            return refusal;

        var entries = await _application.Scim.GetJournalAsync(connectedSystemId, resourceId, afterSequence, upToSequence, HttpContext.RequestAborted);
        return Ok(entries.Select(ScimJournalEntryDto.FromEntity));
    }

    /// <summary>
    /// Get the SCIM requests behind an imported change
    /// </summary>
    /// <remarks>
    /// For a change in a Connected System Object's change history, the resource's accepted writes that the import
    /// behind the change read: those after the point it started from, up to the point it reached.
    /// </remarks>
    /// <param name="connectedSystemId">The Connected System's ID.</param>
    /// <param name="changeId">The Connected System Object change's ID.</param>
    /// <response code="200">The requests.</response>
    /// <response code="404">No such change in this Connected System, or its import recorded no range to read.</response>
    [HttpGet("journal/changes/{changeId:guid}", Name = "GetScimRequestsBehindChange")]
    [ProducesResponseType(typeof(ScimRequestsBehindChangeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRequestsBehindChangeAsync(int connectedSystemId, Guid changeId)
    {
        if (await RequireServiceProviderAsync(connectedSystemId) is { } refusal)
            return refusal;

        var behind = await _application.Scim.GetRequestsBehindChangeAsync(changeId, HttpContext.RequestAborted);
        if (behind == null || behind.ConnectedSystemId != connectedSystemId)
            return NotFound(ApiErrorResponse.NotFound(
                $"Connected System {connectedSystemId} has no imported change {changeId} with SCIM requests to show."));
        return Ok(ScimRequestsBehindChangeDto.FromModel(behind));
    }

    /// <summary>
    /// Refuses the request while the feature is off (by throwing, which the API reports as 400), and answers 404 for a
    /// Connected System that does not exist or has no SCIM endpoint; null when the request may proceed.
    /// </summary>
    private async Task<IActionResult?> RequireServiceProviderAsync(int connectedSystemId)
    {
        await _application.FeatureFlags.EnsureEnabledAsync(FeatureFlagCatalogue.ScimServiceProvider.Key);

        var connectedSystem = await _application.ConnectedSystems.GetConnectedSystemCoreAsync(connectedSystemId);
        if (connectedSystem == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected System {connectedSystemId} was not found."));
        if (connectedSystem.ConnectorDefinition?.Name != ConnectorConstants.ScimServiceProviderConnectorName)
            return NotFound(ApiErrorResponse.NotFound(
                $"Connected System '{connectedSystem.Name}' does not use the {ConnectorConstants.ScimServiceProviderConnectorName}, so it has no SCIM endpoint."));
        return null;
    }

    /// <summary>
    /// The key, if it is scoped to this Connected System's SCIM endpoint; a key scoped elsewhere, or not scoped at all,
    /// is not reachable from here.
    /// </summary>
    /// <remarks>
    /// Only a SCIM 2.0 Service Provider key names a Connected System today, so the scope test is implied by the
    /// Connected System test; it stays so that a later scope naming a Connected System is not managed from here.
    /// </remarks>
    private async Task<ApiKey?> GetScopedKeyAsync(int connectedSystemId, Guid keyId)
    {
        var key = await _application.Security.GetApiKeyAsync(keyId);
        return key is { Scope: ApiKeyScope.ScimServiceProvider } && key.ScopeConnectedSystemId == connectedSystemId ? key : null;
    }

    private NotFoundObjectResult KeyNotFound(int connectedSystemId, Guid keyId) =>
        NotFound(ApiErrorResponse.NotFound($"Connected System {connectedSystemId} has no SCIM key {keyId}."));

    private string BaseUrl(int connectedSystemId) =>
        $"{Request.Scheme}://{Request.Host}{Request.PathBase}{ScimRoutes.PathBase}/v2/{connectedSystemId.ToString(CultureInfo.InvariantCulture)}";
}

using Asp.Versioning;
using JIM.Api.Extensions;
using JIM.Api.Models;
using JIM.Application;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging.DTOs;
using JIM.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Api.Controllers;

/// <summary>
/// API controller for managing synchronisation configuration including Connected Systems and Sync Rules.
/// </summary>
/// <remarks>
/// This controller provides endpoints for managing the synchronisation infrastructure:
/// - Connected Systems: External identity stores that sync with the Metaverse
/// - Sync Rules: Configuration for how data flows between Connected Systems and the Metaverse
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrators")]
[Produces("application/json")]
public class SynchronisationController(ILogger<SynchronisationController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<SynchronisationController> _logger = logger;
    private readonly JimApplication _application = application;

    #region Connected Systems

    /// <summary>
    /// Gets all connected systems with optional pagination, sorting, and filtering.
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of connected system headers.</returns>
    [HttpGet("connected-systems", Name = "GetConnectedSystems")]
    [ProducesResponseType(typeof(PaginatedResponse<ConnectedSystemHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemsAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested connected systems (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var systems = await _application.ConnectedSystems.GetConnectedSystemsAsync();
        var headers = systems.Select(ConnectedSystemHeader.FromEntity).AsQueryable();

        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// Gets a specific connected system by ID.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>The connected system details including configuration and schema.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}", Name = "GetConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested connected system: {Id}", connectedSystemId);
        var system = await _application.ConnectedSystems.GetConnectedSystemAsync(connectedSystemId);
        if (system == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        return Ok(ConnectedSystemDetailDto.FromEntity(system));
    }

    /// <summary>
    /// Gets all object types defined in a connected system's schema.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>A list of object types with their attributes.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/object-types", Name = "GetConnectedSystemObjectTypes")]
    [ProducesResponseType(typeof(IEnumerable<ConnectedSystemObjectTypeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemObjectTypesAsync(int connectedSystemId)
    {
        _logger.LogTrace("Requested object types for connected system: {Id}", connectedSystemId);
        var objectTypes = await _application.ConnectedSystems.GetObjectTypesAsync(connectedSystemId);
        if (objectTypes == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        var dtos = objectTypes.Select(ConnectedSystemObjectTypeDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Gets a specific connected system object by ID.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <param name="id">The unique identifier (GUID) of the connected system object.</param>
    /// <returns>The connected system object details including all attribute values.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/objects/{id:guid}", Name = "GetConnectedSystemObject")]
    [ProducesResponseType(typeof(ConnectedSystemObjectDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id)
    {
        _logger.LogTrace("Requested object {ObjectId} for connected system: {SystemId}", id, connectedSystemId);
        var obj = await _application.ConnectedSystems.GetConnectedSystemObjectAsync(connectedSystemId, id);
        if (obj == null)
            return NotFound(ApiErrorResponse.NotFound($"Object with ID {id} not found in connected system {connectedSystemId}."));

        return Ok(ConnectedSystemObjectDetailDto.FromEntity(obj));
    }

    /// <summary>
    /// Gets a preview of what will be affected by deleting a Connected System.
    /// </summary>
    /// <remarks>
    /// Call this before DeleteConnectedSystemAsync to inform the user of the impact.
    /// The preview includes counts of:
    /// - Connected System Objects that will be deleted
    /// - Sync Rules that will be removed
    /// - Metaverse Objects that will be disconnected
    /// - Pending exports that will be cancelled
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system.</param>
    /// <returns>A preview showing counts of affected objects and any warnings.</returns>
    [HttpGet("connected-systems/{connectedSystemId:int}/deletion-preview", Name = "GetConnectedSystemDeletionPreview")]
    [ProducesResponseType(typeof(ConnectedSystemDeletionPreview), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetConnectedSystemDeletionPreviewAsync(int connectedSystemId)
    {
        _logger.LogInformation("Deletion preview requested for connected system: {Id}", connectedSystemId);

        var preview = await _application.ConnectedSystems.GetDeletionPreviewAsync(connectedSystemId);
        if (preview == null)
            return NotFound(ApiErrorResponse.NotFound($"Connected system with ID {connectedSystemId} not found."));

        return Ok(preview);
    }

    /// <summary>
    /// Deletes a Connected System and all its related data.
    /// </summary>
    /// <remarks>
    /// This operation may execute synchronously or be queued as a background job depending on system size:
    /// - Small systems (less than 1000 CSOs): Deleted immediately, returns 200 OK
    /// - Large systems: Queued as background job, returns 202 Accepted with tracking IDs
    /// - Systems with running sync: Queued to run after sync completes, returns 202 Accepted
    ///
    /// Use the deletion-preview endpoint first to understand the impact before calling this endpoint.
    /// </remarks>
    /// <param name="connectedSystemId">The unique identifier of the connected system to delete.</param>
    /// <returns>The result of the deletion request including outcome and tracking IDs.</returns>
    /// <response code="200">Deletion completed immediately.</response>
    /// <response code="202">Deletion has been queued as a background job.</response>
    /// <response code="400">Deletion failed.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpDelete("connected-systems/{connectedSystemId:int}", Name = "DeleteConnectedSystem")]
    [ProducesResponseType(typeof(ConnectedSystemDeletionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConnectedSystemDeletionResult), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteConnectedSystemAsync(int connectedSystemId)
    {
        _logger.LogInformation("Deletion requested for connected system: {Id}", connectedSystemId);

        // Get the current user from the JWT claims
        var initiatedBy = await GetCurrentUserAsync();
        if (initiatedBy == null)
        {
            _logger.LogWarning("Could not identify user from JWT claims for deletion request");
            return Unauthorized(ApiErrorResponse.Unauthorised("Could not identify user from authentication token."));
        }

        var result = await _application.ConnectedSystems.DeleteAsync(connectedSystemId, initiatedBy);

        if (!result.Success)
            return BadRequest(ApiErrorResponse.BadRequest(result.ErrorMessage ?? "Deletion failed."));

        // Return 202 Accepted for queued operations, 200 OK for immediate completion
        if (result.Outcome == DeletionOutcome.QueuedAsBackgroundJob ||
            result.Outcome == DeletionOutcome.QueuedAfterSync)
        {
            return Accepted(result);
        }

        return Ok(result);
    }

    #endregion

    #region Sync Rules

    /// <summary>
    /// Gets all synchronisation rules with optional pagination, sorting, and filtering.
    /// </summary>
    /// <param name="pagination">Pagination parameters (page, pageSize, sortBy, sortDirection, filter).</param>
    /// <returns>A paginated list of sync rule headers.</returns>
    [HttpGet("sync-rules", Name = "GetSyncRules")]
    [ProducesResponseType(typeof(PaginatedResponse<SyncRuleHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRulesAsync([FromQuery] PaginationRequest pagination)
    {
        _logger.LogTrace("Requested synchronisation rules (Page: {Page}, PageSize: {PageSize})", pagination.Page, pagination.PageSize);
        var rules = await _application.ConnectedSystems.GetSyncRulesAsync();
        var headers = rules.Select(SyncRuleHeader.FromEntity).AsQueryable();

        var result = headers
            .ApplySortAndFilter(pagination)
            .ToPaginatedResponse(pagination);

        return Ok(result);
    }

    /// <summary>
    /// Gets a specific synchronisation rule by ID.
    /// </summary>
    /// <param name="id">The unique identifier of the sync rule.</param>
    /// <returns>The sync rule details including attribute flow configuration.</returns>
    [HttpGet("sync-rules/{id:int}", Name = "GetSyncRule")]
    [ProducesResponseType(typeof(SyncRuleHeader), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSyncRuleAsync(int id)
    {
        _logger.LogTrace("Requested sync rule: {Id}", id);
        var rule = await _application.ConnectedSystems.GetSyncRuleAsync(id);
        if (rule == null)
            return NotFound(ApiErrorResponse.NotFound($"Sync rule with ID {id} not found."));

        return Ok(SyncRuleHeader.FromEntity(rule));
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Resolves the current user from JWT claims by looking up their SSO identifier in the Metaverse.
    /// </summary>
    private async Task<JIM.Models.Core.MetaverseObject?> GetCurrentUserAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
            return null;

        // Get the service settings to know which claim type contains the unique identifier
        var serviceSettings = await _application.ServiceSettings.GetServiceSettingsAsync();
        if (serviceSettings?.SSOUniqueIdentifierClaimType == null ||
            serviceSettings.SSOUniqueIdentifierMetaverseAttribute == null)
        {
            _logger.LogError("Service settings are not configured for SSO claim mapping");
            return null;
        }

        // Get the unique identifier from the JWT claims
        var uniqueIdClaimValue = IdentityUtilities.GetSsoUniqueIdentifier(
            User,
            serviceSettings.SSOUniqueIdentifierClaimType);

        if (string.IsNullOrEmpty(uniqueIdClaimValue))
        {
            _logger.LogWarning("JWT does not contain the expected claim: {ClaimType}",
                serviceSettings.SSOUniqueIdentifierClaimType);
            return null;
        }

        // Look up the user in the Metaverse
        var userType = await _application.Metaverse.GetMetaverseObjectTypeAsync(
            JIM.Models.Core.Constants.BuiltInObjectTypes.Users,
            false);

        if (userType == null)
        {
            _logger.LogError("Could not find User object type in Metaverse");
            return null;
        }

        return await _application.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(
            userType,
            serviceSettings.SSOUniqueIdentifierMetaverseAttribute,
            uniqueIdClaimValue);
    }

    #endregion
}

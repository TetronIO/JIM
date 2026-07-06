// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using Asp.Versioning;
using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Security.DTOs;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// API controller for managing security-related configuration such as Roles and Role membership.
/// </summary>
/// <remarks>
/// This controller provides endpoints for:
/// - Retrieving Role definitions used for access control
/// - Managing Role membership (adding and removing Metaverse Objects from Roles)
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize(Roles = "Administrator")]
[Produces("application/json")]
public class SecurityController(ILogger<SecurityController> logger, JimApplication application) : ApiControllerBase(application, logger)
{
    private readonly ILogger<SecurityController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List security Roles
    /// </summary>
    /// <returns>A list of all Role definitions.</returns>
    [HttpGet("roles", Name = "GetRoles")]
    [ProducesResponseType(typeof(IEnumerable<RoleHeader>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRolesAsync()
    {
        _logger.LogTrace("Requested roles");
        var headers = await _application.Security.GetRoleHeadersAsync();
        return Ok(headers);
    }

    /// <summary>
    /// Get a security Role
    /// </summary>
    /// <remarks>
    /// Returns a single Role by its unique identifier, including the current static member count.
    /// </remarks>
    /// <param name="roleId">The unique identifier of the Role.</param>
    /// <returns>The Role details.</returns>
    /// <response code="200">Returns the requested Role.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="403">If the user lacks the Administrator role.</response>
    /// <response code="404">If no Role exists with the specified ID.</response>
    [HttpGet("roles/{roleId:int}", Name = "GetRole")]
    [ProducesResponseType(typeof(RoleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRoleByIdAsync(int roleId)
    {
        _logger.LogTrace("Requested role {RoleId}", roleId);
        var role = await _application.Security.GetRoleByIdAsync(roleId);

        if (role == null)
        {
            return NotFound(ApiErrorResponse.NotFound($"Role not found: {roleId}"));
        }

        return Ok(RoleDto.FromEntity(role));
    }

    /// <summary>
    /// List Role members
    /// </summary>
    /// <remarks>
    /// Returns all Metaverse Objects that are statically assigned to the specified Role.
    /// Members are sorted alphabetically by display name.
    /// </remarks>
    /// <param name="roleId">The unique identifier of the Role.</param>
    /// <returns>A list of Metaverse Objects assigned to the Role.</returns>
    /// <response code="200">Returns the Role members.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="403">If the user lacks the Administrator role.</response>
    /// <response code="404">If no Role exists with the specified ID.</response>
    [HttpGet("roles/{roleId:int}/members", Name = "GetRoleMembers")]
    [ProducesResponseType(typeof(IEnumerable<RoleMemberDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRoleMembersAsync(int roleId)
    {
        _logger.LogTrace("Requested members of role {RoleId}", roleId);

        var role = await _application.Security.GetRoleByIdAsync(roleId);
        if (role == null)
        {
            return NotFound(ApiErrorResponse.NotFound($"Role not found: {roleId}"));
        }

        var members = await _application.Security.GetRoleMembersAsync(roleId);
        var dtos = members.Select(RoleMemberDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// List the Roles a Metaverse Object is a member of
    /// </summary>
    /// <remarks>
    /// Returns all security Roles that the specified Metaverse Object is statically assigned to.
    /// Returns an empty list if the object is not a member of any Role.
    /// </remarks>
    /// <param name="metaverseObjectId">The unique identifier of the Metaverse Object.</param>
    /// <returns>A list of Roles the Metaverse Object is a member of.</returns>
    /// <response code="200">Returns the list of Roles.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="403">If the user lacks the Administrator role.</response>
    /// <response code="404">If no Metaverse Object exists with the specified ID.</response>
    [HttpGet("metaverse-objects/{metaverseObjectId:guid}/roles", Name = "GetMetaverseObjectRoles")]
    [ProducesResponseType(typeof(IEnumerable<RoleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMetaverseObjectRolesAsync(Guid metaverseObjectId)
    {
        _logger.LogTrace("Requested roles of Metaverse Object {MetaverseObjectId}", metaverseObjectId);

        var mvo = await _application.Metaverse.GetMetaverseObjectAsync(metaverseObjectId);
        if (mvo == null)
        {
            return NotFound(ApiErrorResponse.NotFound($"Metaverse Object not found: {metaverseObjectId}"));
        }

        var roles = await _application.Security.GetMetaverseObjectRolesAsync(metaverseObjectId);
        var dtos = roles.Select(RoleDto.FromEntity);
        return Ok(dtos);
    }

    /// <summary>
    /// Add a member to a Role
    /// </summary>
    /// <remarks>
    /// Assigns a Metaverse Object as a static member of the specified Role. If the object
    /// is already a member, a 409 Conflict response is returned.
    /// </remarks>
    /// <param name="roleId">The unique identifier of the Role.</param>
    /// <param name="metaverseObjectId">The unique identifier of the Metaverse Object to add.</param>
    /// <param name="changeReason">Optional reason for the change, recorded on the audit Activity.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">The Metaverse Object was added to the Role.</response>
    /// <response code="400">If the Metaverse Object does not exist.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="403">If the user lacks the Administrator role.</response>
    /// <response code="404">If no Role exists with the specified ID.</response>
    /// <response code="409">If the Metaverse Object is already a member of the Role.</response>
    [HttpPut("roles/{roleId:int}/members/{metaverseObjectId:guid}", Name = "AddRoleMember")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddRoleMemberAsync(int roleId, Guid metaverseObjectId, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Adding Metaverse Object {MetaverseObjectId} to role {RoleId}", metaverseObjectId, roleId);

        var role = await _application.Security.GetRoleByIdAsync(roleId);
        if (role == null)
        {
            return NotFound(ApiErrorResponse.NotFound($"Role not found: {roleId}"));
        }

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.Security.AddObjectToRoleByIdAsync(metaverseObjectId, roleId, apiKey, changeReason);
            else
                await _application.Security.AddObjectToRoleByIdAsync(metaverseObjectId, roleId, await GetCurrentUserAsync(), changeReason);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("already in that role"))
        {
            return Conflict(ApiErrorResponse.Conflict("Metaverse Object is already a member of this role"));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiErrorResponse.ValidationError(ex.Message));
        }

        _logger.LogInformation("Added Metaverse Object {MetaverseObjectId} to role {RoleId} ({RoleName})", metaverseObjectId, roleId, role.Name);
        return NoContent();
    }

    /// <summary>
    /// Remove a member from a Role
    /// </summary>
    /// <remarks>
    /// Removes a Metaverse Object from the specified Role. Safety checks prevent removing
    /// yourself from the Administrator role and removing the last Administrator, as either
    /// action would cause a lockout.
    /// </remarks>
    /// <param name="roleId">The unique identifier of the Role.</param>
    /// <param name="metaverseObjectId">The unique identifier of the Metaverse Object to remove.</param>
    /// <param name="changeReason">Optional reason for the change, recorded on the audit Activity.</param>
    /// <returns>No content on success.</returns>
    /// <response code="204">The Metaverse Object was removed from the Role.</response>
    /// <response code="400">If the removal would cause a lockout (self-removal or last administrator) or the object is not a member.</response>
    /// <response code="401">If the user is not authenticated.</response>
    /// <response code="403">If the user lacks the Administrator role.</response>
    /// <response code="404">If no Role exists with the specified ID.</response>
    [HttpDelete("roles/{roleId:int}/members/{metaverseObjectId:guid}", Name = "RemoveRoleMember")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveRoleMemberAsync(int roleId, Guid metaverseObjectId, [FromQuery] string? changeReason = null)
    {
        _logger.LogInformation("Removing Metaverse Object {MetaverseObjectId} from role {RoleId}", metaverseObjectId, roleId);

        var role = await _application.Security.GetRoleByIdAsync(roleId);
        if (role == null)
        {
            return NotFound(ApiErrorResponse.NotFound($"Role not found: {roleId}"));
        }

        // Safety checks for the Administrator role
        if (role.Name == Constants.BuiltInRoles.Administrator)
        {
            // Prevent self-removal from Administrator role
            var callerObjectId = GetCallerMetaverseObjectId();
            if (callerObjectId.HasValue && callerObjectId.Value == metaverseObjectId)
            {
                return BadRequest(ApiErrorResponse.ValidationError(
                    "You cannot remove yourself from the Administrator role. Ask another administrator to remove you."));
            }

            // Prevent removing the last Administrator
            var members = await _application.Security.GetRoleMembersAsync(roleId);
            if (members.Count <= 1)
            {
                return BadRequest(ApiErrorResponse.ValidationError(
                    "Cannot remove the last member of the Administrator role. At least one administrator must remain to prevent lockout."));
            }
        }

        try
        {
            var apiKey = await GetCurrentApiKeyAsync();
            if (apiKey != null)
                await _application.Security.RemoveObjectFromRoleAsync(metaverseObjectId, roleId, apiKey, changeReason);
            else
                await _application.Security.RemoveObjectFromRoleAsync(metaverseObjectId, roleId, await GetCurrentUserAsync(), changeReason);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiErrorResponse.ValidationError(ex.Message));
        }

        _logger.LogInformation("Removed Metaverse Object {MetaverseObjectId} from role {RoleId} ({RoleName})", metaverseObjectId, roleId, role.Name);
        return NoContent();
    }

    #region Configuration Change History

    /// <summary>
    /// List the change history for a Role.
    /// </summary>
    /// <param name="roleId">The unique identifier of the Role.</param>
    /// <param name="pagination">Pagination parameters.</param>
    /// <returns>A paged list of change-history entries, newest version first, each with a one-line summary.</returns>
    /// <response code="200">Change history returned (empty if the Role has no recorded configuration changes).</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("roles/{roleId:int}/change-history", Name = "GetRoleChangeHistory")]
    [ProducesResponseType(typeof(PaginatedResponse<ConfigurationChangeHistoryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRoleChangeHistoryAsync(int roleId, [FromQuery] PaginationRequest pagination)
    {
        var result = await _application.ChangeHistory.GetConfigurationChangeHistoryAsync(ActivityTargetType.Role, roleId, pagination.Page, pagination.PageSize);
        return Ok(PaginatedResponse<ConfigurationChangeHistoryItem>.Create(result.Results, result.TotalResults, pagination.Page, pagination.PageSize));
    }

    /// <summary>
    /// Get a single version of a Role's change history, with its snapshot and the diff against the previous version.
    /// </summary>
    /// <param name="roleId">The unique identifier of the Role.</param>
    /// <param name="changeVersion">The per-object change version to retrieve.</param>
    /// <returns>The change detail: metadata, the snapshot, and the diff against the previous version.</returns>
    /// <response code="200">The change detail.</response>
    /// <response code="404">No change with that version was found for the Role.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("roles/{roleId:int}/change-history/{changeVersion:int}", Name = "GetRoleChange")]
    [ProducesResponseType(typeof(ConfigurationChangeDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRoleChangeAsync(int roleId, int changeVersion)
    {
        var detail = await _application.ChangeHistory.GetConfigurationChangeAsync(ActivityTargetType.Role, roleId, changeVersion);
        if (detail == null)
            return NotFound(ApiErrorResponse.NotFound($"No change history found for Role {roleId} version {changeVersion}."));
        return Ok(detail);
    }

    /// <summary>
    /// Compare two versions of a Role's configuration.
    /// </summary>
    /// <param name="roleId">The unique identifier of the Role.</param>
    /// <param name="fromVersion">The earlier version to compare from.</param>
    /// <param name="toVersion">The later version to compare to.</param>
    /// <returns>The structured diff of the later version against the earlier.</returns>
    /// <response code="200">The diff.</response>
    /// <response code="404">One of the requested versions was not found for the Role.</response>
    /// <response code="401">User could not be identified from authentication token.</response>
    [HttpGet("roles/{roleId:int}/change-history/compare", Name = "CompareRoleChanges")]
    [ProducesResponseType(typeof(ConfigurationDiff), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CompareRoleChangesAsync(int roleId, [FromQuery] int fromVersion, [FromQuery] int toVersion)
    {
        var diff = await _application.ChangeHistory.CompareConfigurationChangesAsync(ActivityTargetType.Role, roleId, fromVersion, toVersion);
        if (diff == null)
            return NotFound(ApiErrorResponse.NotFound($"Could not compare versions {fromVersion} and {toVersion} for Role {roleId}."));
        return Ok(diff);
    }

    #endregion

    /// <summary>
    /// Gets the caller's Metaverse Object ID from their authentication claims.
    /// Returns null for API key callers (who are not Metaverse Objects).
    /// </summary>
    private Guid? GetCallerMetaverseObjectId()
    {
        // API key callers have auth_method = "api_key" and don't map to Metaverse Objects
        var authMethod = User.FindFirst("auth_method")?.Value;
        if (authMethod == "api_key")
        {
            return null;
        }

        // SSO users have their Metaverse Object ID in the "sub" claim
        var subClaim = User.FindFirst("sub") ?? User.FindFirst(ClaimTypes.NameIdentifier);
        if (subClaim != null && Guid.TryParse(subClaim.Value, out var objectId))
        {
            return objectId;
        }

        return null;
    }
}

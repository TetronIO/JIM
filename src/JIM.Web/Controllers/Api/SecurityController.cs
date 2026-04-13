// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using Asp.Versioning;
using JIM.Application;
using JIM.Models.Core;
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
public class SecurityController(ILogger<SecurityController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<SecurityController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// List security Roles
    /// </summary>
    /// <returns>A list of all Role definitions.</returns>
    [HttpGet("roles", Name = "GetRoles")]
    [ProducesResponseType(typeof(IEnumerable<RoleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRolesAsync()
    {
        _logger.LogTrace("Requested roles");
        var roles = await _application.Security.GetRolesAsync();
        var dtos = roles.Select(RoleDto.FromEntity);
        return Ok(dtos);
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
    /// Add a member to a Role
    /// </summary>
    /// <remarks>
    /// Assigns a Metaverse Object as a static member of the specified Role. If the object
    /// is already a member, a 409 Conflict response is returned.
    /// </remarks>
    /// <param name="roleId">The unique identifier of the Role.</param>
    /// <param name="metaverseObjectId">The unique identifier of the Metaverse Object to add.</param>
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
    public async Task<IActionResult> AddRoleMemberAsync(int roleId, Guid metaverseObjectId)
    {
        _logger.LogInformation("Adding metaverse object {MetaverseObjectId} to role {RoleId}", metaverseObjectId, roleId);

        var role = await _application.Security.GetRoleByIdAsync(roleId);
        if (role == null)
        {
            return NotFound(ApiErrorResponse.NotFound($"Role not found: {roleId}"));
        }

        try
        {
            await _application.Security.AddObjectToRoleByIdAsync(metaverseObjectId, roleId);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("already in that role"))
        {
            return Conflict(ApiErrorResponse.Conflict("Metaverse object is already a member of this role"));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiErrorResponse.ValidationError(ex.Message));
        }

        _logger.LogInformation("Added metaverse object {MetaverseObjectId} to role {RoleId} ({RoleName})", metaverseObjectId, roleId, role.Name);
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
    public async Task<IActionResult> RemoveRoleMemberAsync(int roleId, Guid metaverseObjectId)
    {
        _logger.LogInformation("Removing metaverse object {MetaverseObjectId} from role {RoleId}", metaverseObjectId, roleId);

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
            await _application.Security.RemoveObjectFromRoleAsync(metaverseObjectId, roleId);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiErrorResponse.ValidationError(ex.Message));
        }

        _logger.LogInformation("Removed metaverse object {MetaverseObjectId} from role {RoleId} ({RoleName})", metaverseObjectId, roleId, role.Name);
        return NoContent();
    }

    /// <summary>
    /// Gets the caller's Metaverse Object ID from their authentication claims.
    /// Returns null for API key callers (who are not Metaverse Objects).
    /// </summary>
    private Guid? GetCallerMetaverseObjectId()
    {
        // API key callers have auth_method = "api_key" and don't map to metaverse objects
        var authMethod = User.FindFirst("auth_method")?.Value;
        if (authMethod == "api_key")
        {
            return null;
        }

        // SSO users have their metaverse object ID in the "sub" claim
        var subClaim = User.FindFirst("sub") ?? User.FindFirst(ClaimTypes.NameIdentifier);
        if (subClaim != null && Guid.TryParse(subClaim.Value, out var objectId))
        {
            return objectId;
        }

        return null;
    }
}

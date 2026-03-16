using System.Security.Claims;
using Asp.Versioning;
using JIM.Application;
using JIM.Models.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// Returns information about the currently authenticated user, including their
/// JIM identity and roles. Used by clients to verify authorisation after authentication.
/// </summary>
/// <remarks>
/// This endpoint requires authentication but does NOT require any specific role.
/// This allows authenticated users who have not yet been provisioned in JIM
/// (no MetaverseObject) to receive a helpful response explaining their status,
/// rather than an opaque 403 Forbidden.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Authorize]
[Produces("application/json")]
public class UserInfoController(ILogger<UserInfoController> logger, JimApplication application) : ControllerBase
{
    private readonly ILogger<UserInfoController> _logger = logger;
    private readonly JimApplication _application = application;

    /// <summary>
    /// Gets information about the currently authenticated user.
    /// </summary>
    /// <remarks>
    /// Returns the user's JIM identity, assigned roles, and authorisation status.
    /// If the user is authenticated but has no JIM identity (MetaverseObject),
    /// <c>authorised</c> will be <c>false</c> with a message explaining how to resolve this.
    /// </remarks>
    /// <returns>User identity, roles, and authorisation status.</returns>
    [HttpGet(Name = "GetUserInfo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public Task<IActionResult> GetAsync()
    {
        var name = User.FindFirstValue("name") ?? User.FindFirstValue(ClaimTypes.Name);
        var authMethod = User.FindFirstValue("auth_method") ?? "oauth";

        var mvoIdClaim = User.FindFirstValue(Constants.BuiltInClaims.MetaverseObjectId);
        var hasMvoId = Guid.TryParse(mvoIdClaim, out var mvoId);

        var roles = User.FindAll(Constants.BuiltInRoles.RoleClaimType)
            .Select(c => c.Value)
            .ToList();

        var isAdministrator = roles.Contains(Constants.BuiltInRoles.Administrator);

        if (!hasMvoId)
        {
            _logger.LogDebug("UserInfo: Authenticated user '{Name}' has no JIM identity (no MetaverseObject)", name);

            return Task.FromResult<IActionResult>(Ok(new
            {
                authorised = false,
                isAdministrator = false,
                name,
                authMethod,
                metaverseObjectId = (Guid?)null,
                roles = Array.Empty<string>(),
                message = "You are authenticated but do not have a JIM identity. Please sign in to the JIM web portal first to create your identity, then retry."
            }));
        }

        _logger.LogDebug("UserInfo: User '{Name}' (MVO {MetaverseObjectId}) has roles: {Roles}",
            name, mvoId, string.Join(", ", roles));

        return Task.FromResult<IActionResult>(Ok(new
        {
            authorised = true,
            isAdministrator,
            name,
            authMethod,
            metaverseObjectId = (Guid?)mvoId,
            roles
        }));
    }
}

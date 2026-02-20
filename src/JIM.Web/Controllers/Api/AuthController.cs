using Asp.Versioning;
using JIM.Models.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JIM.Web.Controllers.Api;

/// <summary>
/// Authentication configuration endpoint for OAuth/OIDC clients.
/// </summary>
/// <remarks>
/// This controller provides OAuth configuration information for clients like the JIM PowerShell module
/// that need to perform interactive authentication via browser redirect flow.
/// The configuration endpoint is intentionally unauthenticated to allow client discovery.
/// </remarks>
[Route("api/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    /// <summary>
    /// Gets the OAuth/OIDC configuration for interactive authentication.
    /// </summary>
    /// <remarks>
    /// Returns the OAuth configuration needed by clients (e.g., PowerShell module) to perform
    /// interactive browser-based authentication using the Authorization Code flow with PKCE.
    ///
    /// This endpoint is unauthenticated to allow client discovery before authentication.
    ///
    /// If SSO is not configured, returns 503 Service Unavailable.
    /// </remarks>
    /// <returns>OAuth configuration including authority, client ID, and scopes.</returns>
    /// <response code="200">Returns the OAuth configuration.</response>
    /// <response code="503">SSO is not configured on this JIM instance.</response>
    [HttpGet("config", Name = "GetAuthConfig")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult GetConfig()
    {
        var authority = Environment.GetEnvironmentVariable(Constants.Config.SsoAuthority);
        var clientId = Environment.GetEnvironmentVariable(Constants.Config.SsoClientId);
        var apiScope = Environment.GetEnvironmentVariable(Constants.Config.SsoApiScope);

        if (string.IsNullOrEmpty(authority) || string.IsNullOrEmpty(clientId))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "sso_not_configured",
                message = "SSO is not configured on this JIM instance. Use API key authentication instead.",
                timestamp = DateTime.UtcNow
            });
        }

        // Build the scopes list
        var scopes = new List<string> { "openid", "profile" };
        if (!string.IsNullOrEmpty(apiScope))
        {
            scopes.Add(apiScope);
        }

        return Ok(new AuthConfigResponse
        {
            Authority = authority,
            ClientId = clientId,
            Scopes = scopes,
            ResponseType = "code",
            UsePkce = true,
            CodeChallengeMethod = "S256"
        });
    }
}

/// <summary>
/// OAuth configuration response for client discovery.
/// </summary>
public class AuthConfigResponse
{
    /// <summary>
    /// The OIDC authority URL (e.g., https://login.microsoftonline.com/{tenant}/v2.0).
    /// </summary>
    public required string Authority { get; set; }

    /// <summary>
    /// The OAuth client ID for interactive authentication.
    /// </summary>
    public required string ClientId { get; set; }

    /// <summary>
    /// The OAuth scopes to request (e.g., ["openid", "profile", "api://client-id/access_as_user"]).
    /// </summary>
    public required List<string> Scopes { get; set; }

    /// <summary>
    /// The OAuth response type. Always "code" for Authorization Code flow.
    /// </summary>
    public string ResponseType { get; set; } = "code";

    /// <summary>
    /// Whether PKCE is required. Always true for public clients.
    /// </summary>
    public bool UsePkce { get; set; } = true;

    /// <summary>
    /// The PKCE code challenge method. Always "S256" (SHA256).
    /// </summary>
    public string CodeChallengeMethod { get; set; } = "S256";
}

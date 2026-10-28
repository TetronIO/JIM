// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Asp.Versioning;
using JIM.Models.Core;
using JIM.Web.Authentication;
using JIM.Web.Models.Api;
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
    /// Get OAuth/OIDC configuration
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
        var publicAuthority = Environment.GetEnvironmentVariable(Constants.Config.SsoPublicAuthority);
        var clientId = Environment.GetEnvironmentVariable(Constants.Config.SsoClientId);
        var publicClientId = Environment.GetEnvironmentVariable(Constants.Config.SsoPublicClientId);
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

        // Build the scopes list. offline_access is requested so the IdP issues a refresh token;
        // the PowerShell module persists that refresh token to the platform credential store for
        // cross-session token caching (issue #305) and uses it for silent in-session refresh.
        // Without offline_access, providers such as Entra ID issue no refresh token at all.
        var scopes = new List<string> { "openid", "profile", "offline_access" };
        if (PublicClientSignIn.ApiScope(authority, apiScope, clientId) is { } publicClientApiScope)
            scopes.Add(publicClientApiScope);

        // Prefer the public/client-facing authority when the deployment exposes Keycloak on a
        // different URL to clients than to the JIM backend (e.g. dev devcontainer: backend reaches
        // Keycloak via Docker DNS jim.keycloak:8080, while browsers and the PowerShell module on
        // the host reach it via localhost:8181). Falls back to JIM_SSO_AUTHORITY when unset, which
        // is the common case for single-URL production deployments.
        var clientAuthority = !string.IsNullOrEmpty(publicAuthority) ? publicAuthority : authority;

        // Interactive clients (PowerShell module, etc.) perform public-client PKCE flows with
        // loopback redirects.
        var interactiveClientId = PublicClientSignIn.ClientId(clientId, publicClientId);

        return Ok(new AuthConfigResponse
        {
            Authority = clientAuthority,
            ClientId = interactiveClientId,
            Scopes = scopes,
            ResponseType = "code",
            UsePkce = true,
            CodeChallengeMethod = "S256"
        });
    }

    /// <summary>
    /// Get the identity provider's trust status
    /// </summary>
    /// <remarks>
    /// Whether JIM trusts its identity provider's TLS certificate, and the chain the identity provider presents. When
    /// the state is <c>Untrusted</c>, trust the certificate named by <c>suggestedThumbprint</c> with
    /// <c>POST /api/v1/certificates/identity-provider</c> (Administrator role; before anyone can sign in, use the
    /// infrastructure API key), or upload <c>unsentIssuer</c>'s certificate trusted for <c>SignIn</c>.
    ///
    /// This endpoint is unauthenticated, because it matters most when nobody can sign in. It shows only what any TLS
    /// client of the identity provider can see; what the JIM certificate store holds is returned to administrators
    /// only. JIM checks again at most every few seconds however often this is called.
    /// </remarks>
    /// <param name="monitor">The identity provider's last known status.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The identity provider's trust status.</returns>
    /// <response code="200">Returns the status.</response>
    [HttpGet("identity-provider-trust", Name = "GetIdentityProviderTrust")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IdentityProviderTrustStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetIdentityProviderTrustAsync([FromServices] IIdentityProviderTrustMonitor monitor, CancellationToken cancellationToken)
    {
        var status = await monitor.GetFreshStatusAsync(cancellationToken);
        var isAdministrator = User.IsInRole(Constants.BuiltInRoles.Administrator);
        return Ok(IdentityProviderTrustStatusDto.FromStatus(status, includeStoreDetail: isAdministrator));
    }

    /// <summary>
    /// Get the sign-in problems JIM has met
    /// </summary>
    /// <remarks>
    /// The sign-in failures a setting explains that JIM has met since it started: an ID token or access token for
    /// another audience or from another issuer, a client or scope the identity provider refused, or a sign-in carrying
    /// no claim of the type JIM identifies people by. Each names the setting involved, the value JIM expected, the value
    /// it received and where to fix it, and never holds a token, a secret or a claim's value. An access token is
    /// diagnosed only when the identity provider signed it.
    ///
    /// Also returns the redirect URIs JIM sends the identity provider when reached at the address of this request, to
    /// compare with those the identity provider has registered: a redirect URI it does not have stops sign-in on its
    /// own error page, which JIM never sees.
    ///
    /// Requires the Administrator role. Before anyone can sign in, use the infrastructure API key.
    /// </remarks>
    /// <param name="problems">The sign-in problems JIM has met.</param>
    /// <returns>The sign-in problems, and the redirect URIs.</returns>
    /// <response code="200">Returns the diagnostics.</response>
    /// <response code="401">Not authenticated.</response>
    /// <response code="403">Not an administrator.</response>
    [HttpGet("sign-in-diagnostics", Name = "GetSignInDiagnostics")]
    [Authorize(Roles = "Administrator")]
    [ProducesResponseType(typeof(SignInDiagnosticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult GetSignInDiagnostics([FromServices] SignInProblemLog problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return Ok(SignInDiagnosticsDto.From(Request, problems.GetRecent()));
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

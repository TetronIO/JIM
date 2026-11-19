// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using JIM.Models.Core;

namespace JIM.Web.Authentication;

/// <summary>
/// The scope a bearer token on <c>/api</c> must grant, beyond carrying the audience JIM requires.
/// </summary>
/// <remarks>
/// When the audience JIM requires is the portal's own client ID (Keycloak's <c>jim-api</c>, and Entra ID's version 2
/// access tokens), the portal's ID token carries that same audience, so the audience alone cannot tell an access token
/// from it. An access token for JIM's API grants JIM's API scope, in <c>scope</c> (Keycloak) or <c>scp</c> (Entra ID);
/// an ID token grants none. When the audience is the API's own identifier (an <c>api://</c> value, as for AD FS), no ID
/// token carries it, and nothing more is required. With no API scope configured and the client ID as the audience,
/// nothing can tell the two apart, so no token is accepted.
/// </remarks>
public sealed class BearerScopeRequirement
{
    private const string ApiScheme = "api://";

    private BearerScopeRequirement(bool isRequired, string? scope)
    {
        IsRequired = isRequired;
        Scope = scope;
    }

    /// <summary>Requires nothing beyond the audience.</summary>
    public static BearerScopeRequirement None { get; } = new(false, null);

    /// <summary>Whether a token must grant <see cref="Scope"/>.</summary>
    public bool IsRequired { get; }

    /// <summary>The scope a token must grant, or null when it is required and none is configured, so none can.</summary>
    public string? Scope { get; }

    /// <summary>
    /// The requirement for JIM's configuration.
    /// </summary>
    /// <param name="apiScope">The value of <c>JIM_SSO_API_SCOPE</c>.</param>
    /// <param name="clientId">The value of <c>JIM_SSO_CLIENT_ID</c>.</param>
    /// <param name="audience">The audience JIM requires on access tokens.</param>
    public static BearerScopeRequirement For(string? apiScope, string? clientId, string? audience)
    {
        if (string.IsNullOrEmpty(audience) || !string.Equals(audience, clientId, StringComparison.Ordinal))
            return None;

        if (string.IsNullOrWhiteSpace(apiScope))
            return new BearerScopeRequirement(true, null);

        // An api:// scope names an application ID URI and a permission (api://{id}/access_as_user); a token grants the
        // permission alone.
        var scope = apiScope.Trim();
        if (scope.StartsWith(ApiScheme, StringComparison.Ordinal))
        {
            var trimmed = scope.TrimEnd('/');
            var lastSlash = trimmed.LastIndexOf('/');
            scope = lastSlash >= ApiScheme.Length ? trimmed[(lastSlash + 1)..] : trimmed;
        }
        return new BearerScopeRequirement(true, scope);
    }

    /// <summary>
    /// The scopes a validated token grants: every space-separated value of its <c>scope</c> and <c>scp</c> claims.
    /// </summary>
    public static IReadOnlyList<string> GrantedScopes(ClaimsPrincipal? principal) =>
        principal == null
            ? []
            : principal.Claims
                .Where(c => c.Type is "scope" or "scp")
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToList();

    /// <summary>
    /// Whether a validated token meets the requirement. Scopes match exactly.
    /// </summary>
    public bool IsMetBy(ClaimsPrincipal? principal) =>
        !IsRequired || (Scope != null && GrantedScopes(principal).Contains(Scope, StringComparer.Ordinal));

    /// <summary>
    /// What to record when a token does not meet the requirement, so an administrator sees why it was refused.
    /// </summary>
    public SignInProblemReport DescribeRefusal(ClaimsPrincipal? principal)
    {
        var granted = GrantedScopes(principal);
        var received = granted.Count == 0 ? "no scope" : string.Join(' ', granted);
        return Scope == null
            ? new SignInProblemReport(
                SignInProblemSource.Api,
                Constants.Config.SsoApiScope,
                $"JIM requires {Constants.Config.SsoClientId} as an access token's audience, which the portal's ID token also carries, and with no API scope configured it cannot tell the two apart, so it accepts no token.",
                $"an API scope in {Constants.Config.SsoApiScope}",
                received,
                $"Set {Constants.Config.SsoApiScope} to the scope that grants access to JIM's API, as the SSO setup guide for your identity provider describes.")
            : new SignInProblemReport(
                SignInProblemSource.Api,
                Constants.Config.SsoApiScope,
                "The token does not grant JIM's API scope. It may be an ID token, or the client did not ask for the scope.",
                Scope,
                received,
                $"Use an access token that grants {Scope}: Connect-JIM asks for it, and so must any other client.");
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics.CodeAnalysis;

namespace JIM.Web.Authentication;

/// <summary>
/// How an interactive public client signs in with PKCE and no secret: the PowerShell module, through
/// <c>/api/v1/auth/config</c>, and the API reference's Try It (#2594). Both use the same client and ask for the API in
/// the same form, so a registration that lets one sign in lets the other.
/// </summary>
public static class PublicClientSignIn
{
    /// <summary>
    /// The client to sign in with: <c>JIM_SSO_PUBLIC_CLIENT_ID</c>, which an identity provider needing a separate
    /// public registration (Keycloak, AD FS) is given, otherwise <c>JIM_SSO_CLIENT_ID</c>, which Entra ID can share
    /// between its web and public platforms.
    /// </summary>
    /// <param name="clientId">The value of <c>JIM_SSO_CLIENT_ID</c>.</param>
    /// <param name="publicClientId">The value of <c>JIM_SSO_PUBLIC_CLIENT_ID</c>.</param>
    [return: NotNullIfNotNull(nameof(clientId))]
    public static string? ClientId(string? clientId, string? publicClientId) =>
        string.IsNullOrEmpty(publicClientId) ? clientId : publicClientId;

    /// <summary>
    /// The scope that asks for JIM's API. AD FS takes a resource in the scope parameter only as [resource]/[scope],
    /// and a bare identifier such as <c>api://{id}</c> would be split at its own last slash and refused (MSIS9602), so
    /// a client of AD FS asks for the Web API's <c>openid</c> scope. The portal passes AD FS the resource directly
    /// (<see cref="PortalSignInRequest"/>). Any other identity provider is asked for <c>JIM_SSO_API_SCOPE</c> as it
    /// stands.
    /// </summary>
    /// <param name="authority">The value of <c>JIM_SSO_AUTHORITY</c>.</param>
    /// <param name="apiScope">The value of <c>JIM_SSO_API_SCOPE</c>.</param>
    /// <param name="clientId">The value of <c>JIM_SSO_CLIENT_ID</c>, the audience when the scope names none.</param>
    /// <returns>The scope, or null when no API scope is configured.</returns>
    public static string? ApiScope(string? authority, string? apiScope, string? clientId)
    {
        if (string.IsNullOrEmpty(apiScope))
            return null;

        return PortalSignInRequest.IsAdfsAuthority(authority)
            ? $"{ApiAudienceResolver.Resolve(apiScope, clientId)}/openid"
            : apiScope;
    }
}

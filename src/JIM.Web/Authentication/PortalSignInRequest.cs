// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace JIM.Web.Authentication;

/// <summary>
/// What the portal asks the identity provider for when someone signs in (#2439).
/// <para>
/// Every provider is asked for <c>openid</c>, <c>profile</c> and <c>email</c>, so the person's email address reaches
/// their Metaverse Object. AD FS is also asked for JIM's Web API as the resource, with the <c>allatclaims</c> scope: AD
/// FS applies the Web API's claim rules to the ID token only then, and those rules are what issue the identifier
/// (<c>oid</c>), name and email. Without them the portal would see none of them, and would identify a person
/// differently from <c>/api</c>, which reads the access token those rules always shape.
/// </para>
/// </summary>
public static class PortalSignInRequest
{
    private const string AdfsPath = "/adfs";

    /// <summary>
    /// Sets the scopes and resource the portal's sign-in asks for.
    /// </summary>
    /// <param name="options">The OpenID Connect options to configure.</param>
    /// <param name="authority">The value of <c>JIM_SSO_AUTHORITY</c>.</param>
    /// <param name="apiScope">The value of <c>JIM_SSO_API_SCOPE</c>; with AD FS, its identifier is the resource.</param>
    /// <param name="clientId">The value of <c>JIM_SSO_CLIENT_ID</c>.</param>
    public static void Configure(OpenIdConnectOptions options, string? authority, string? apiScope, string? clientId)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Scope.Add("profile");
        options.Scope.Add("email");

        // With no Web API configured there are no claim rules to bring into the ID token.
        if (IsAdfsAuthority(authority) && !string.IsNullOrEmpty(apiScope))
        {
            options.Resource = ApiAudienceResolver.Resolve(apiScope, clientId);
            options.Scope.Add("allatclaims");
        }
    }

    /// <summary>
    /// Whether the authority is an AD FS federation service, whose authority is always its host's <c>/adfs</c> path.
    /// </summary>
    public static bool IsAdfsAuthority(string? authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri)
        && uri.AbsolutePath.TrimEnd('/').Equals(AdfsPath, StringComparison.OrdinalIgnoreCase);
}

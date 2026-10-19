// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Scim;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// The cookie scheme's forward selector: decides which scheme authenticates a request. A bearer token carrying the
/// API key prefix goes to <see cref="ApiKeyAuthenticationHandler"/>, any other bearer token to the SSO JWT bearer
/// scheme, and a request with no bearer token stays with the portal's sign-in cookie. Extracted from
/// <c>Program.cs</c> so the routing can be unit tested against a plain <see cref="HttpContext"/>.
/// </summary>
public static class ApiAuthenticationSchemeSelector
{
    /// <summary>
    /// Selects the authentication scheme for a request.
    /// </summary>
    /// <param name="context">The current request's <see cref="HttpContext"/>.</param>
    /// <returns>The scheme to forward to, or null to keep the cookie scheme.</returns>
    public static string? SelectScheme(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!BearerToken.TryRead(context.Request, out var token))
        {
            // On /scim only, a bare key with no scheme is a credential too (see ApiKeyAuthenticationHandler).
            var values = context.Request.Headers.Authorization;
            return ScimRoutes.IsScimRequest(context.Request) && values.Count == 1 && BearerToken.IsApiKey(values[0]?.Trim() ?? string.Empty)
                ? ApiKeyAuthenticationHandler.SchemeName
                : null;
        }

        return BearerToken.IsApiKey(token)
            ? ApiKeyAuthenticationHandler.SchemeName
            : JwtBearerDefaults.AuthenticationScheme;
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace JIM.Web.Authentication;

/// <summary>
/// Configures the SSO JWT bearer scheme that guards <c>/api</c>, so the options Program.cs installs are the options
/// the tests drive (the signing key rollover tests, #2442, run the real handler with them).
/// <para>
/// The configuration manager is left to the framework, with IdentityModel's refresh defaults: the identity provider's
/// discovery document and key set are fetched again every 12 hours, and early (at most every 5 minutes) when a token
/// names a key JIM has not seen. The back-channel those fetches use is set separately, by
/// <see cref="IdentityProviderTrustServiceCollectionExtensions.AddIdentityProviderTrust"/>.
/// </para>
/// </summary>
public static class JimJwtBearerOptions
{
    /// <summary>
    /// Applies JIM's settings to the JWT bearer scheme's options.
    /// </summary>
    /// <param name="options">The options to configure.</param>
    /// <param name="authority">The identity provider's authority (issuer) URL.</param>
    /// <param name="audience">The audience an access token must carry.</param>
    /// <param name="validIssuers">The issuers a token may name.</param>
    /// <param name="onTokenValidated">Resolves and attaches the caller's JIM identity once the token has validated.</param>
    public static void Configure(JwtBearerOptions options, string? authority, string? audience, string[] validIssuers,
        Func<TokenValidatedContext, Task> onTokenValidated)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(validIssuers);

        options.Authority = authority;
        options.Audience = audience;

        if (authority != null && authority.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = validIssuers,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        };

        // Preserve standard OIDC claim names for API requests
        options.MapInboundClaims = false;

        options.TokenValidationParameters.RoleClaimType = Constants.BuiltInRoles.RoleClaimType;

        // A token that fails validation is recorded in the security audit log (#2108); see JimJwtBearerEvents.
        options.Events = JimJwtBearerEvents.Create(onTokenValidated);
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace JIM.Web.Authentication;

/// <summary>
/// Builds the SSO JWT bearer scheme's events, so the set Program.cs installs is the set the tests drive.
/// </summary>
public static class JimJwtBearerEvents
{
    /// <summary>
    /// Creates the events for the JWT bearer scheme.
    /// </summary>
    /// <param name="onTokenValidated">Resolves and attaches the caller's JIM identity once the token has validated.</param>
    public static JwtBearerEvents Create(Func<TokenValidatedContext, Task> onTokenValidated)
    {
        ArgumentNullException.ThrowIfNull(onTokenValidated);

        return new JwtBearerEvents
        {
            OnTokenValidated = onTokenValidated,

            // Security audit events (#2108): a token that fails validation is recorded as failed API keys and
            // interactive sign-ins are. That includes a mistyped API key prefix, since every bearer token without
            // the key prefix is validated here.
            OnAuthenticationFailed = AccessTokenAuthenticationAudit.OnAuthenticationFailedAsync
        };
    }
}

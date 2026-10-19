// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Scim;

/// <summary>
/// Refuses a SCIM request over a connection JIM cannot confirm is encrypted, before authentication reads its key: a
/// SCIM client sends a long-lived secret and personal data on every request. The development stack runs over plain
/// HTTP and is exempt, as it is for the REST API's password endpoints; a released image runs as Production. Registered
/// for <c>/scim</c> only.
/// </summary>
public class ScimSecureTransportMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(environment);

        if (!context.Request.IsHttps && !environment.IsDevelopment())
        {
            // The remedy names the proxy case, the likeliest way a properly secured deployment lands here: TLS
            // terminates at a proxy JIM has not been told to trust, so JIM never sees the forwarded scheme.
            await ScimResponses.WriteErrorAsync(context, StatusCodes.Status403Forbidden,
                "SCIM requests must use HTTPS. If TLS terminates at a reverse proxy, set JIM_TRUSTED_PROXIES to that proxy so " +
                "JIM reads the forwarded scheme.");
            return;
        }

        await next(context);
    }
}

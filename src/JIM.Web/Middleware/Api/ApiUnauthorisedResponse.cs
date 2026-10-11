// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Middleware.Api;

/// <summary>
/// The 401 an unauthenticated /api request receives in place of the portal's sign-in redirect. It carries the
/// <c>WWW-Authenticate</c> challenge every 401 must (RFC 9110 s15.5.2), naming the Bearer scheme that both API keys
/// and SSO access tokens use.
/// </summary>
public static class ApiUnauthorisedResponse
{
    private const string Realm = "JIM";

    /// <summary>
    /// Builds the <c>WWW-Authenticate</c> challenge. The <c>invalid_token</c> error code is added only when the
    /// request presented a bearer credential (RFC 6750 s3.1): a request that carried none is told which scheme to
    /// use, not that it got something wrong. Why a credential was rejected (unknown, disabled, expired) is never
    /// disclosed; the failed-authentication audit record holds that.
    /// </summary>
    public static string BuildChallenge(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return BearerToken.TryRead(request, out _)
            ? $"Bearer realm=\"{Realm}\", error=\"invalid_token\""
            : $"Bearer realm=\"{Realm}\"";
    }

    /// <summary>
    /// Writes the 401 response: status, challenge and a JSON body.
    /// </summary>
    public static async Task WriteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = BuildChallenge(context.Request);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized", message = "Authentication required" });
    }
}

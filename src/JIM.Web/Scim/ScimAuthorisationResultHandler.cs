// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Web.Authentication;
using JIM.Web.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace JIM.Web.Scim;

/// <summary>
/// Answers every refused <c>/scim</c> request with the same <c>401</c> (D3): no credential, an unknown, disabled or
/// expired key, a key for another Connected System and an unscoped key are indistinguishable to the caller, so the
/// endpoint says nothing about which keys exist. The real reason is in the security audit log: the API key handler
/// records the authentication failures, and this records the scope ones. Never a redirect. Every other path is left to
/// the default handler.
/// </summary>
public sealed class ScimAuthorisationResultHandler(
    IFailedAuthenticationRecorder failedAuthenticationRecorder,
    IAuthorizationMiddlewareResultHandler defaultHandler) : IAuthorizationMiddlewareResultHandler
{
    private const string Challenge = "Bearer realm=\"JIM SCIM\"";

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Succeeded || !ScimRoutes.IsScimRequest(context.Request))
        {
            await defaultHandler.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
            RecordScopeRefusal(context);

        // RFC 6750 s3: error="invalid_token" only when a credential was presented and refused.
        context.Response.Headers.WWWAuthenticate = context.Request.Headers.Authorization.Count > 0
            ? $"{Challenge}, error=\"invalid_token\""
            : Challenge;
        await ScimResponses.WriteErrorAsync(context, StatusCodes.Status401Unauthorized,
            "A valid API key for this SCIM endpoint is required.");
    }

    private void RecordScopeRefusal(HttpContext context)
    {
        var reason = context.User.HasClaim(c => c.Type == Constants.BuiltInClaims.KeyScope)
            ? "API key is for another Connected System"
            : "API key is not scoped to a SCIM endpoint";
        _ = failedAuthenticationRecorder.RecordAsync(FailedAuthenticationEventNames.ApiKey, reason,
            context.User.FindFirst("key_prefix")?.Value, context.Connection.RemoteIpAddress.ToNormalisedString());
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;

namespace JIM.Web.Authentication;

/// <summary>
/// Records SSO access tokens that fail validation in the security audit log, as failed API keys and failed
/// interactive sign-ins already are (issue #2108). Since API keys became bearer tokens (#2088), every bearer token
/// without the API key prefix is validated here, so this is also what records a malformed key.
/// </summary>
public static class AccessTokenAuthenticationAudit
{
    /// <summary>
    /// The JWT bearer scheme's <c>OnAuthenticationFailed</c> event: records the failure, off the request path, and
    /// leaves the authentication outcome untouched.
    /// </summary>
    public static Task OnAuthenticationFailedAsync(AuthenticationFailedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var recorder = context.HttpContext.RequestServices.GetService<IFailedAuthenticationRecorder>();
        if (recorder == null)
        {
            Log.Warning("AccessTokenAuthenticationAudit: No IFailedAuthenticationRecorder is registered; failed access token authentication not recorded.");
            return Task.CompletedTask;
        }

        // ForwardedHeaders middleware (when configured) has already made RemoteIpAddress the real client address.
        var clientIp = context.HttpContext.Connection.RemoteIpAddress.ToNormalisedString();
        _ = recorder.RecordAsync(FailedAuthenticationEventNames.AccessToken, CategoriseFailure(context.Exception),
            apiKeyPrefix: null, clientIp);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps a token validation failure to a short, sanitised reason for the audit record. The exception's message is
    /// never used: it can quote the token, and it is unbounded in length, so it belongs in neither the audit record
    /// nor its aggregation key. Every reason begins "Access token", so none can merge with an interactive sign-in
    /// failure's ("OIDC ..."): failures aggregate on their reason, not on their event name.
    /// </summary>
    public static string CategoriseFailure(Exception? exception) => exception switch
    {
        AggregateException aggregate when aggregate.InnerExceptions.Count > 0 => CategoriseFailure(aggregate.InnerExceptions[0]),
        SecurityTokenExpiredException => "Access token expired",
        SecurityTokenNotYetValidException => "Access token not yet valid",
        SecurityTokenNoExpirationException or SecurityTokenInvalidLifetimeException => "Access token lifetime invalid",
        // Derives from SecurityTokenInvalidSignatureException, so it must be matched first.
        SecurityTokenSignatureKeyNotFoundException => "Access token signing key not found",
        SecurityTokenInvalidSignatureException => "Access token signature invalid",
        SecurityTokenInvalidSigningKeyException => "Access token signing key invalid",
        SecurityTokenInvalidAlgorithmException => "Access token algorithm not allowed",
        SecurityTokenInvalidIssuerException => "Access token issuer invalid",
        SecurityTokenInvalidAudienceException => "Access token audience invalid",
        SecurityTokenInvalidTypeException => "Access token type invalid",
        SecurityTokenReplayDetectedException => "Access token replayed",
        SecurityTokenArgumentException => "Access token malformed",
        SecurityTokenDecryptionFailedException => "Access token decryption failed",
        _ => "Access token validation failed"
    };
}

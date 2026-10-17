// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace JIM.Web.Authentication;

/// <summary>
/// Records SSO access tokens that fail validation in the security audit log, as failed API keys and failed
/// interactive sign-ins already are (issue #2108). Since API keys became bearer tokens (#2088), every bearer token
/// without the API key prefix is validated here, so this is also what records a malformed key.
/// </summary>
public static class AccessTokenAuthenticationAudit
{
    /// <summary>
    /// The JWT bearer scheme's <c>OnAuthenticationFailed</c> event: records a refused token, off the request path, and
    /// leaves the authentication outcome untouched.
    /// <para>
    /// JwtBearerHandler raises this event for anything thrown while authenticating, not only for a token that failed
    /// validation: a fault resolving the caller's JIM identity in <c>OnTokenValidated</c> (two users holding the same
    /// SSO identifier, say), or the identity provider's metadata being unreachable. Those are server-side faults; the
    /// handler rethrows them, and the global exception handler reports them. Recording them here would show a valid
    /// caller as a refused, anonymous credential, so only a token's own failures are recorded.
    /// </para>
    /// </summary>
    public static Task OnAuthenticationFailedAsync(AuthenticationFailedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsTokenValidationFailure(context.Exception))
            return Task.CompletedTask;

        // Required, not optional: a missing registration must fail at once rather than silently stop the audit.
        var recorder = context.HttpContext.RequestServices.GetRequiredService<IFailedAuthenticationRecorder>();

        // ForwardedHeaders middleware (when configured) has already made RemoteIpAddress the real client address.
        var clientIp = context.HttpContext.Connection.RemoteIpAddress.ToNormalisedString();
        _ = recorder.RecordAsync(FailedAuthenticationEventNames.AccessToken, CategoriseFailure(context.Exception),
            apiKeyPrefix: null, clientIp);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether an authentication failure is the token's own fault: IdentityModel's token exceptions, its oversized
    /// token error, or an aggregate made only of them (one per token handler that tried it). Anything else is a
    /// server-side fault.
    /// </summary>
    public static bool IsTokenValidationFailure(Exception? exception) => exception switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsTokenValidationFailure),
        SecurityTokenException or SecurityTokenArgumentException => true,
        _ => IsOversizedTokenFailure(exception)
    };

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
        _ when IsOversizedTokenFailure(exception) => "Access token malformed",
        SecurityTokenDecryptionFailedException => "Access token decryption failed",
        _ => "Access token validation failed"
    };

    /// <summary>
    /// IdentityModel reports a token over its size limit (IDX10209) as a plain <see cref="ArgumentException"/>, not a
    /// token exception, through the same event as server-side faults. The exact type and the IdentityModel "IDX" code
    /// tell the two apart, so an <see cref="ArgumentException"/> (or subclass) thrown by JIM's own code while resolving
    /// the caller is not mistaken for a malformed token. The message is read for this test only and never recorded.
    /// </summary>
    private static bool IsOversizedTokenFailure(Exception? exception) =>
        exception?.GetType() == typeof(ArgumentException) && exception.Message.StartsWith("IDX", StringComparison.Ordinal);
}

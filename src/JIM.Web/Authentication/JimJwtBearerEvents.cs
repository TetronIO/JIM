// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using JIM.Web.Middleware.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace JIM.Web.Authentication;

/// <summary>
/// Builds the SSO JWT bearer scheme's events, so the set Program.cs installs is the set the tests drive.
/// </summary>
public static class JimJwtBearerEvents
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Creates the events for the JWT bearer scheme.
    /// </summary>
    /// <param name="onTokenValidated">Resolves and attaches the caller's JIM identity once the token has validated.</param>
    /// <param name="signingKeyRecovery">Validates a token signed by a key JIM has not fetched yet against the identity
    /// provider's current key set (#2442).</param>
    public static JwtBearerEvents Create(Func<TokenValidatedContext, Task> onTokenValidated, BearerSigningKeyRecovery signingKeyRecovery)
    {
        ArgumentNullException.ThrowIfNull(onTokenValidated);
        ArgumentNullException.ThrowIfNull(signingKeyRecovery);

        return new JwtBearerEvents
        {
            OnTokenValidated = onTokenValidated,

            OnAuthenticationFailed = async context =>
            {
                // A token signed by a key the identity provider has introduced without publishing it first is accepted
                // here, once JIM has fetched the key set, rather than refused (#2442).
                if (await TryRecoverUnknownSigningKeyAsync(context, onTokenValidated, signingKeyRecovery))
                    return;

                // Security audit events (#2108): a token that fails validation is recorded as failed API keys and
                // interactive sign-ins are. That includes a mistyped API key prefix, since every bearer token without
                // the key prefix is validated here.
                await AccessTokenAuthenticationAudit.OnAuthenticationFailedAsync(context);

                // A token the identity provider issued, refused for its audience or issuer, names a setting to fix
                // (#2443). The token is refused either way.
                await RecordSignInProblemsAsync(context);
            }
        };
    }

    /// <summary>
    /// Records what is wrong with a token refused for its audience or issuer, once its signature and lifetime show the
    /// identity provider issued it; see <see cref="SignInProblemDiagnosis.DiagnoseRefusedAccessTokenAsync"/>.
    /// </summary>
    private static async Task RecordSignInProblemsAsync(AuthenticationFailedContext context)
    {
        if (!IsAudienceOrIssuerFailure(context.Exception) || !BearerToken.TryRead(context.Request, out var token))
            return;

        var reports = await SignInProblemDiagnosis.DiagnoseRefusedAccessTokenAsync(token, context.Options, context.HttpContext.RequestAborted);
        if (reports.Count == 0)
            return;

        // Required, not optional, as the audit's recorder is: a missing registration must fail at once.
        var problems = context.HttpContext.RequestServices.GetRequiredService<SignInProblemLog>();
        foreach (var report in reports)
            problems.Record(report);
    }

    /// <summary>
    /// Completes the authentication of a token that failed only because JIM did not hold its signing key, when the
    /// identity provider's current key set holds it, as JwtBearerHandler completes one that validated first time.
    /// </summary>
    private static async Task<bool> TryRecoverUnknownSigningKeyAsync(AuthenticationFailedContext context,
        Func<TokenValidatedContext, Task> onTokenValidated, BearerSigningKeyRecovery signingKeyRecovery)
    {
        if (!IsSigningKeyNotFound(context.Exception))
            return false;

        string authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var token = authorization[BearerPrefix.Length..].Trim();
        var result = await signingKeyRecovery.TryValidateAsync(token, context.Options, context.HttpContext.RequestAborted);
        if (result == null)
            return false;

        var validated = new TokenValidatedContext(context.HttpContext, context.Scheme, context.Options)
        {
            Principal = new ClaimsPrincipal(result.ClaimsIdentity),
            SecurityToken = result.SecurityToken
        };
        validated.Properties.ExpiresUtc = ToUtc(result.SecurityToken.ValidTo);
        validated.Properties.IssuedUtc = ToUtc(result.SecurityToken.ValidFrom);

        await onTokenValidated(validated);

        // OnTokenValidated may decide the outcome itself; otherwise the token is accepted.
        if (validated.Result is { Succeeded: false } decided)
        {
            if (decided.Failure != null)
                context.Fail(decided.Failure);
            else
                context.NoResult();
            return true;
        }

        context.Principal = validated.Principal;
        context.Properties = validated.Properties;
        context.Success();
        return true;
    }

    private static bool IsSigningKeyNotFound(Exception? exception) => exception switch
    {
        SecurityTokenSignatureKeyNotFoundException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsSigningKeyNotFound),
        _ => false
    };

    private static bool IsAudienceOrIssuerFailure(Exception? exception) => exception switch
    {
        SecurityTokenInvalidAudienceException or SecurityTokenInvalidIssuerException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsAudienceOrIssuerFailure),
        _ => false
    };

    private static DateTimeOffset? ToUtc(DateTime value) =>
        value == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Resources;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using JIM.Models.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Serilog;

namespace JIM.Web.Authentication;

/// <summary>
/// Turns a sign-in failure into the setting involved, the value JIM expected and the value it received (#2443), so an
/// administrator need not read the log's exceptions, or JIM's source, to find which value is wrong. Only failures a
/// setting explains are diagnosed: a person the identity provider refused, or a sign-in a retry recovers
/// (<see cref="OidcSignInRecovery"/>), is not a problem with JIM's configuration. A diagnosis never carries a token, a
/// secret or a claim's value.
/// </summary>
public static partial class SignInProblemDiagnosis
{
    /// <summary>The page that explains a portal sign-in a setting stopped.</summary>
    public const string ProblemPagePath = "/sign-in-problem";

    private static string None => SignInText.ProblemNone;

    /// <summary>
    /// Diagnoses a failed portal sign-in: an ID token for another audience or from another issuer, or an error the
    /// identity provider returned that names JIM's client or the scopes it asked for.
    /// </summary>
    /// <returns>The diagnosis, or null when no setting explains the failure.</returns>
    public static SignInProblemReport? DiagnosePortalFailure(Exception? failure, OpenIdConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return failure switch
        {
            null => null,
            AggregateException aggregate => aggregate.InnerExceptions.Select(e => DiagnosePortalFailure(e, options)).FirstOrDefault(r => r != null),
            SecurityTokenInvalidAudienceException audience => new SignInProblemReport(
                SignInProblemSource.Portal,
                Constants.Config.SsoClientId,
                SignInText.ProblemWrongAudienceDescription,
                options.ClientId ?? None,
                ValueOrNone(audience.InvalidAudience),
                string.Format(CultureInfo.CurrentCulture, SignInText.ProblemWrongAudienceFix, Constants.Config.SsoClientId)),
            SecurityTokenInvalidIssuerException issuer => new SignInProblemReport(
                SignInProblemSource.Portal,
                Constants.Config.SsoValidIssuers,
                SignInText.ProblemWrongIssuerDescription,
                JoinOrNone(PortalIssuers(options)),
                ValueOrNone(issuer.InvalidIssuer),
                IssuerFix),
            OpenIdConnectProtocolException protocol => DiagnoseProtocolError(ReadErrorCode(protocol), options),
            _ => null
        };
    }

    /// <summary>
    /// The page explaining a problem, by its identifier alone: the values stay in JIM.
    /// </summary>
    public static string PagePathFor(SignInProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return $"{ProblemPagePath}?id={problem.Id}";
    }

    /// <summary>
    /// Handles a failed portal sign-in, for the OpenID Connect handler's <c>OnRemoteFailure</c> event, when a setting
    /// explains it: records the problem, and shows the page naming it instead of the error page.
    /// </summary>
    /// <returns>Whether the failure was handled. One no setting explains is left alone.</returns>
    public static bool TryHandleRemoteFailure(RemoteFailureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Options is not OpenIdConnectOptions options || DiagnosePortalFailure(context.Failure, options) is not { } report)
            return false;

        var problem = context.HttpContext.RequestServices.GetRequiredService<SignInProblemLog>().Record(report);
        context.Response.Redirect(PagePathFor(problem));
        context.HandleResponse();
        return true;
    }

    /// <summary>
    /// Diagnoses an access token refused for its audience or issuer, once its signature and lifetime have been checked,
    /// so only a token the identity provider really issued is diagnosed. Anyone can send a token naming any audience,
    /// and diagnosing those would let them fill the diagnostics with misleading values.
    /// </summary>
    /// <returns>The diagnoses, or none when the token is not one the identity provider issued, or nothing is wrong
    /// with its audience or issuer.</returns>
    public static async Task<IReadOnlyList<SignInProblemReport>> DiagnoseRefusedAccessTokenAsync(string token, JwtBearerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            var configuration = options.ConfigurationManager != null
                ? await options.ConfigurationManager.GetConfigurationAsync(cancellationToken)
                : options.Configuration;

            // As JwtBearerHandler builds its parameters, with the two checks being diagnosed turned off.
            var parameters = options.TokenValidationParameters.Clone();
            parameters.ConfigurationManager = null;
            parameters.ValidateAudience = false;
            parameters.ValidateIssuer = false;
            if (configuration != null)
                parameters.IssuerSigningKeys = (parameters.IssuerSigningKeys ?? []).Concat(configuration.SigningKeys).ToList();

            foreach (var handler in options.TokenHandlers)
            {
                var result = await handler.ValidateTokenAsync(token, parameters);
                if (!result.IsValid)
                    continue;

                var audiences = new[] { options.Audience, options.TokenValidationParameters.ValidAudience }
                    .Concat(options.TokenValidationParameters.ValidAudiences ?? []);
                var issuers = new[] { options.TokenValidationParameters.ValidIssuer }
                    .Concat(options.TokenValidationParameters.ValidIssuers ?? [])
                    .Concat(DiscoveredIssuers.From(configuration));
                return DiagnoseAccessToken(result.SecurityToken, NonEmpty(audiences), NonEmpty(issuers), options.Authority);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Diagnosing never changes the outcome: the token is refused either way.
            Log.Debug(ex, "Could not diagnose a refused access token");
        }

        return [];
    }

    /// <summary>
    /// Diagnoses an access token the identity provider issued: whether it carries an audience JIM expects, and comes
    /// from an issuer JIM accepts. A version 1 token from Microsoft Entra ID fails both, and both are fixed by asking for
    /// version 2 tokens (#2367), so that is the fix each names.
    /// </summary>
    public static IReadOnlyList<SignInProblemReport> DiagnoseAccessToken(SecurityToken token, IReadOnlyCollection<string> expectedAudiences,
        IReadOnlyCollection<string> acceptedIssuers, string? authority = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(expectedAudiences);
        ArgumentNullException.ThrowIfNull(acceptedIssuers);

        var tokenAudiences = token switch
        {
            JsonWebToken jwt => jwt.Audiences.ToList(),
            JwtSecurityToken jwt => jwt.Audiences.ToList(),
            _ => []
        };

        var entraIdVersion1 = EntraIdAuthority.IsEntraId(authority)
            && token.Issuer.StartsWith(EntraIdVersion1IssuerPrefix, StringComparison.OrdinalIgnoreCase);

        var reports = new List<SignInProblemReport>();
        if (expectedAudiences.Count > 0 && !tokenAudiences.Intersect(expectedAudiences, StringComparer.Ordinal).Any())
        {
            reports.Add(new SignInProblemReport(
                SignInProblemSource.Api,
                Constants.Config.SsoApiScope,
                SignInText.ProblemApiWrongAudienceDescription,
                JoinOrNone(expectedAudiences),
                JoinOrNone(tokenAudiences),
                entraIdVersion1
                    ? EntraIdVersion1Fix
                    : string.Format(CultureInfo.CurrentCulture, SignInText.ProblemApiWrongAudienceFix, Constants.Config.SsoApiScope, Constants.Config.SsoClientId)));
        }

        if (acceptedIssuers.Count > 0 && !acceptedIssuers.Contains(token.Issuer, StringComparer.Ordinal))
        {
            reports.Add(new SignInProblemReport(
                SignInProblemSource.Api,
                Constants.Config.SsoValidIssuers,
                SignInText.ProblemApiWrongIssuerDescription,
                JoinOrNone(acceptedIssuers),
                ValueOrNone(token.Issuer),
                entraIdVersion1 ? EntraIdVersion1Fix : IssuerFix));
        }

        return reports;
    }

    /// <summary>
    /// Diagnoses a sign-in that carried no claim of the type JIM identifies people by. It names the claim types the
    /// sign-in did carry, never their values.
    /// </summary>
    public static SignInProblemReport MissingClaim(SignInProblemSource source, string claimType, IEnumerable<Claim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        return new SignInProblemReport(
            source,
            Constants.Config.SsoClaimType,
            string.Format(CultureInfo.CurrentCulture, SignInText.ProblemMissingClaimDescription, claimType),
            claimType,
            JoinOrNone(claims.Select(c => c.Type).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()),
            string.Format(CultureInfo.CurrentCulture, SignInText.ProblemMissingClaimFix, Constants.Config.SsoClaimType, claimType));
    }

    private const string EntraIdVersion1IssuerPrefix = "https://sts.windows.net/";

    private static string EntraIdVersion1Fix => SignInText.ProblemEntraVersion1Fix;

    private static string IssuerFix =>
        string.Format(CultureInfo.CurrentCulture, SignInText.ProblemIssuerFix, Constants.Config.SsoValidIssuers, Constants.Config.SsoAuthority);

    private static SignInProblemReport? DiagnoseProtocolError(string? error, OpenIdConnectOptions options) => error switch
    {
        "invalid_client" => new SignInProblemReport(
            SignInProblemSource.Portal,
            Constants.Config.SsoSecret,
            SignInText.ProblemInvalidClientDescription,
            string.Format(CultureInfo.CurrentCulture, SignInText.ProblemInvalidClientExpected, options.ClientId),
            error,
            string.Format(CultureInfo.CurrentCulture, SignInText.ProblemInvalidClientFix, Constants.Config.SsoClientId, Constants.Config.SsoSecret)),
        "unauthorized_client" => new SignInProblemReport(
            SignInProblemSource.Portal,
            Constants.Config.SsoClientId,
            SignInText.ProblemUnauthorizedClientDescription,
            string.Format(CultureInfo.CurrentCulture, SignInText.ProblemUnauthorizedClientExpected, options.ClientId),
            error,
            string.Format(CultureInfo.CurrentCulture, SignInText.ProblemUnauthorizedClientFix, Constants.Config.SsoClientId)),
        "invalid_scope" or "invalid_resource" => new SignInProblemReport(
            SignInProblemSource.Portal,
            Constants.Config.SsoApiScope,
            SignInText.ProblemInvalidScopeDescription,
            string.Join(' ', options.Scope),
            error,
            string.Format(CultureInfo.CurrentCulture, SignInText.ProblemInvalidScopeFix, Constants.Config.SsoApiScope)),
        _ => null
    };

    /// <summary>
    /// The error code the identity provider returned: the OpenID Connect handler puts it in the exception's data, and in
    /// its message. Only the code is read; the description the identity provider may add is never held.
    /// </summary>
    private static string? ReadErrorCode(OpenIdConnectProtocolException exception)
    {
        if (exception.Data["error"] is string error && !string.IsNullOrEmpty(error))
            return error;

        var match = ErrorCodePattern().Match(exception.Message);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("^Message contains error: '([a-z_]+)'", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodePattern();

    private static IEnumerable<string> PortalIssuers(OpenIdConnectOptions options) =>
        NonEmpty(new[] { options.TokenValidationParameters.ValidIssuer, options.Configuration?.Issuer }
            .Concat(options.TokenValidationParameters.ValidIssuers ?? []));

    private static List<string> NonEmpty(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Distinct(StringComparer.Ordinal).ToList();

    private static string ValueOrNone(string? value) => string.IsNullOrEmpty(value) ? None : value;

    private static string JoinOrNone(IEnumerable<string> values)
    {
        var joined = string.Join(", ", values);
        return joined.Length == 0 ? None : joined;
    }
}

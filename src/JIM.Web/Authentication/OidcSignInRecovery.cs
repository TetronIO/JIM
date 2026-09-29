// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using System.Net;
using JIM.Utilities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Serilog;

namespace JIM.Web.Authentication;

/// <summary>
/// What to do about a remote OIDC sign-in failure.
/// </summary>
public enum OidcRemoteFailureAction
{
    /// <summary>Leave the failure alone, so the exception page reports it.</summary>
    Surface,

    /// <summary>Start a fresh sign-in, which recovers the failure completely.</summary>
    Restart,

    /// <summary>Stop: the browser reached JIM over plain HTTP from another machine, so no restart can succeed.</summary>
    StopPlainHttp,

    /// <summary>Stop: a restart has already been tried and failed the same way.</summary>
    StopRestartLimitReached
}

/// <summary>
/// Decides how a remote OIDC sign-in failure is handled. Some failures are fully recovered by simply signing
/// in again: a replayed <c>/signin-oidc</c> callback (back button, restored tab, a refreshed error page)
/// carries a single-use authorisation code the IdP has already spent, and a lost correlation cookie has the
/// same shape. Those restart the sign-in from a clean URL instead of surfacing an exception page. Failures a
/// retry cannot fix (the IdP refused the user, or the configuration is wrong) are deliberately NOT restarted:
/// redirecting them would replace a loud, diagnosable error with a silent loop between JIM and the provider.
/// For the same reason a restart is bounded: where the cookies are lost every time (plain HTTP from another
/// machine), sign-in stops with an explanation instead of restarting forever.
/// </summary>
public static class OidcSignInRecovery
{
    /// <summary>The page that explains a sign-in JIM stopped rather than restart.</summary>
    public const string SignInFailedPath = "/sign-in-failed";

    /// <summary>
    /// How many times one sign-in is restarted. A genuinely lost or spent cookie is recovered by the first
    /// restart; a second failure in the same chain means the cookies are being lost every time.
    /// </summary>
    public const int MaxRestarts = 1;

    /// <summary>The authentication properties item that counts the restarts in a chain of sign-in attempts.</summary>
    public const string RestartCountItemKey = "jim.signin.restarts";

    public const string TlsAndReverseProxyDocsUrl = "https://docs.junctional.io/administration/deployment/#tls-and-reverse-proxy";
    public const string SignInLoopsDocsUrl = "https://docs.junctional.io/administration/troubleshooting/#sign-in-loops-between-jim-and-the-identity-provider";

    /// <summary>
    /// Whether the failure is one a fresh sign-in attempt recovers completely.
    /// </summary>
    public static bool ShouldRestartSignIn(Exception? failure)
    {
        if (failure == null)
            return false;

        // A spent or expired single-use authorisation code comes back from the token endpoint as
        // invalid_grant; anything else an OpenIdConnectProtocolException carries (access_denied,
        // invalid_client, ...) describes a decision or a misconfiguration a retry will only repeat.
        if (failure is OpenIdConnectProtocolException)
            return failure.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);

        // The callback arrived without the correlation cookie its challenge set, or with none of the
        // protocol fields at all (a bookmark or history navigation straight to /signin-oidc); the round
        // trip is broken but nothing is wrong with the user or the configuration, so a new one succeeds.
        return failure.Message.Contains("Correlation failed", StringComparison.OrdinalIgnoreCase)
               || failure.Message.Contains("message.State is null or empty", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the browser will refuse the correlation and nonce cookies a sign-in sets on this request, so that
    /// every attempt comes back without them. A browser only accepts a <c>Secure</c> cookie over HTTPS or from
    /// a loopback host, and production marks both cookies <c>Secure</c>; reaching JIM over plain HTTP from
    /// another machine therefore makes sign-in impossible, however many times it is restarted.
    /// </summary>
    public static bool WillBrowserDiscardSignInCookies(CookieSecurePolicy correlationCookiePolicy, bool isHttps, HostString host)
    {
        return correlationCookiePolicy == CookieSecurePolicy.Always
               && !isHttps
               && !IsLoopbackHost(host);
    }

    /// <summary>
    /// Decides what to do about a remote sign-in failure. Only failures <see cref="ShouldRestartSignIn"/>
    /// recognises are ever restarted, and even those stop, rather than loop, when the transport means the
    /// browser cannot keep the sign-in cookies or when a restart has already failed the same way.
    /// </summary>
    public static OidcRemoteFailureAction DecideAction(
        Exception? failure,
        AuthenticationProperties? properties,
        CookieSecurePolicy correlationCookiePolicy,
        bool isHttps,
        HostString host)
    {
        if (!ShouldRestartSignIn(failure))
            return OidcRemoteFailureAction.Surface;

        if (WillBrowserDiscardSignInCookies(correlationCookiePolicy, isHttps, host))
            return OidcRemoteFailureAction.StopPlainHttp;

        return GetRestartCount(properties) >= MaxRestarts
            ? OidcRemoteFailureAction.StopRestartLimitReached
            : OidcRemoteFailureAction.Restart;
    }

    /// <summary>
    /// Handles a remote sign-in failure for the OpenID Connect handler's <c>OnRemoteFailure</c> event: returns a
    /// user who is already signed in to the page they wanted, restarts a recoverable sign-in (at most
    /// <see cref="MaxRestarts"/> times), stops with an explanation where a restart cannot help, and leaves
    /// every other failure to surface as an exception.
    /// </summary>
    public static async Task HandleRemoteFailureAsync(RemoteFailureContext context)
    {
        var request = context.Request;
        var action = DecideAction(context.Failure, context.Properties, context.Options.CorrelationCookie.SecurePolicy, request.IsHttps, request.Host);
        if (action == OidcRemoteFailureAction.Surface)
            return;

        var returnPath = GetSafeReturnPath(context.Properties?.RedirectUri);

        // A replayed callback from a sign-in that already succeeded: the session exists, so the page is simply
        // shown again, with no round trip to the identity provider that could fail or loop.
        if (await IsSignedInAsync(context))
        {
            context.Response.Redirect(returnPath);
            context.HandleResponse();
            return;
        }

        if (action == OidcRemoteFailureAction.Restart)
        {
            // The count travels in the protected state parameter of the new round trip, not in a cookie: lost
            // cookies are what failed, and a count held in one would be lost with them.
            var properties = new AuthenticationProperties { RedirectUri = returnPath };
            properties.Items[RestartCountItemKey] = (GetRestartCount(context.Properties) + 1).ToString(CultureInfo.InvariantCulture);
            await context.HttpContext.ChallengeAsync(context.Scheme.Name, properties);
            context.HandleResponse();
            return;
        }

        if (action == OidcRemoteFailureAction.StopPlainHttp)
        {
            Log.Warning("Sign-in stopped: the browser reached JIM over plain HTTP at {Host}, so it discarded the Secure cookies sign-in depends on, and restarting would loop between JIM and the identity provider. " +
                        "Browser access from other machines requires HTTPS; if TLS already terminates at a reverse proxy, set JIM_TRUSTED_PROXIES so JIM sees the original scheme. See {DocsUrl}",
                LogSanitiser.Sanitise(request.Host.Value), TlsAndReverseProxyDocsUrl);
        }
        else
        {
            Log.Warning("Sign-in stopped after {Restarts} restart(s): the browser again returned from the identity provider without the cookies sign-in set, so restarting again would loop. " +
                        "Check that JIM is reached over HTTPS and that the browser accepts cookies from it. See {DocsUrl}",
                GetRestartCount(context.Properties), SignInLoopsDocsUrl);
        }

        context.Response.Redirect(SignInFailedPath);
        context.HandleResponse();
    }

    private static int GetRestartCount(AuthenticationProperties? properties)
    {
        return properties != null
               && properties.Items.TryGetValue(RestartCountItemKey, out var value)
               && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : 0;
    }

    private static async Task<bool> IsSignedInAsync(RemoteFailureContext context)
    {
        var signInScheme = context.Options.SignInScheme;
        var result = signInScheme == null
            ? await context.HttpContext.AuthenticateAsync()
            : await context.HttpContext.AuthenticateAsync(signInScheme);
        return result.Succeeded;
    }

    private static bool IsLoopbackHost(HostString host)
    {
        // HostString.Host keeps the brackets round an IPv6 literal ("[::1]"), which IPAddress will not parse.
        var name = host.Host.Trim('[', ']');
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
               || (IPAddress.TryParse(name, out var address) && IPAddress.IsLoopback(address));
    }

    /// <summary>
    /// The path to restart the sign-in from: the flow's original local return path when there is one, else
    /// the root. Anything that could leave the site (absolute or protocol-relative) falls back to the root,
    /// so the redirect can never be turned into an off-host jump.
    /// </summary>
    public static string GetSafeReturnPath(string? redirectUri)
    {
        return !string.IsNullOrEmpty(redirectUri)
               && redirectUri.StartsWith('/')
               && !redirectUri.StartsWith("//", StringComparison.Ordinal)
            ? redirectUri
            : "/";
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// A remote OIDC sign-in failure is either recoverable by simply signing in again (a replayed callback whose
/// single-use code the IdP has already spent, or a lost correlation cookie) or it is not (the IdP refused the
/// user, or the configuration is wrong). These tests pin the line between the two, because redirecting the
/// unrecoverable cases would turn a loud, diagnosable failure into a silent sign-in loop.
/// </summary>
[TestFixture]
public class OidcSignInRecoveryTests
{
    [Test]
    public void ShouldRestartSignIn_NullFailure_DoesNotRestart()
    {
        Assert.That(OidcSignInRecovery.ShouldRestartSignIn(null), Is.False);
    }

    [Test]
    public void ShouldRestartSignIn_SpentAuthorisationCode_Restarts()
    {
        // The exact shape Keycloak produces for a replayed /signin-oidc callback: the code is single-use,
        // so a restored tab, back button or refreshed error page re-submitting it gets invalid_grant.
        var failure = new OpenIdConnectProtocolException(
            "Message contains error: 'invalid_grant', error_description: 'Code not valid', error_uri: 'error_uri is null'.");

        Assert.That(OidcSignInRecovery.ShouldRestartSignIn(failure), Is.True);
    }

    [Test]
    public void ShouldRestartSignIn_LostCorrelationCookie_Restarts()
    {
        // The ASP.NET Core handler raises this when the callback arrives without the correlation cookie the
        // challenge set, e.g. after cookies were cleared mid-flow; a fresh challenge recovers it completely.
        var failure = new Exception("Correlation failed.");

        Assert.That(OidcSignInRecovery.ShouldRestartSignIn(failure), Is.True);
    }

    [Test]
    public void ShouldRestartSignIn_CallbackWithoutAMessage_Restarts()
    {
        // A bare navigation to /signin-oidc (a bookmark, or stepping through history to the callback) carries
        // none of the protocol fields; nothing is wrong with the user or the configuration.
        var failure = new Exception("OpenIdConnectAuthenticationHandler: message.State is null or empty.");

        Assert.That(OidcSignInRecovery.ShouldRestartSignIn(failure), Is.True);
    }

    [Test]
    public void ShouldRestartSignIn_AccessDenied_DoesNotRestart()
    {
        // The IdP refused the user; retrying would loop them between JIM and the provider forever.
        var failure = new OpenIdConnectProtocolException(
            "Message contains error: 'access_denied', error_description: 'User declined consent'.");

        Assert.That(OidcSignInRecovery.ShouldRestartSignIn(failure), Is.False);
    }

    [Test]
    public void ShouldRestartSignIn_UnrecognisedFailure_DoesNotRestart()
    {
        Assert.That(OidcSignInRecovery.ShouldRestartSignIn(new InvalidOperationException("IDX20803: unable to obtain configuration")), Is.False);
    }

    [Test]
    public void GetSafeReturnPath_NoReturnUrl_FallsBackToTheRoot()
    {
        Assert.That(OidcSignInRecovery.GetSafeReturnPath(null), Is.EqualTo("/"));
    }

    [Test]
    public void GetSafeReturnPath_LocalPath_IsKept()
    {
        Assert.That(OidcSignInRecovery.GetSafeReturnPath("/admin/connected-systems"), Is.EqualTo("/admin/connected-systems"));
    }

    [Test]
    public void GetSafeReturnPath_ProtocolRelativeUrl_FallsBackToTheRoot()
    {
        // "//evil.example" is protocol-relative: the browser would leave the site. Never redirect off-host.
        Assert.That(OidcSignInRecovery.GetSafeReturnPath("//evil.example"), Is.EqualTo("/"));
    }

    [Test]
    public void GetSafeReturnPath_AbsoluteUrl_FallsBackToTheRoot()
    {
        Assert.That(OidcSignInRecovery.GetSafeReturnPath("https://evil.example/"), Is.EqualTo("/"));
    }

    [Test]
    public void WillBrowserDiscardSignInCookies_PlainHttpToAnotherMachine_ReturnsTrue()
    {
        // Production's correlation and nonce cookies are Secure-only; a browser refuses a Secure cookie offered
        // over plain HTTP by anything but a loopback address, so every restart would lose them again.
        Assert.That(OidcSignInRecovery.WillBrowserDiscardSignInCookies(
            CookieSecurePolicy.Always, isHttps: false, new HostString("jim01.corp.example:5200")), Is.True);
    }

    [TestCase("localhost:5200")]
    [TestCase("LOCALHOST")]
    [TestCase("jim.localhost:5200")]
    [TestCase("127.0.0.1:5200")]
    [TestCase("127.10.0.1")]
    [TestCase("[::1]:5200")]
    public void WillBrowserDiscardSignInCookies_PlainHttpToLoopback_ReturnsFalse(string host)
    {
        // Browsers treat loopback as a secure context and keep Secure cookies on it (Safari aside, which the
        // restart limit covers), so a lost cookie there is still worth one restart.
        Assert.That(OidcSignInRecovery.WillBrowserDiscardSignInCookies(
            CookieSecurePolicy.Always, isHttps: false, new HostString(host)), Is.False);
    }

    [Test]
    public void WillBrowserDiscardSignInCookies_Https_ReturnsFalse()
    {
        Assert.That(OidcSignInRecovery.WillBrowserDiscardSignInCookies(
            CookieSecurePolicy.Always, isHttps: true, new HostString("jim01.corp.example")), Is.False);
    }

    [Test]
    public void WillBrowserDiscardSignInCookies_CookiesNotSecureOnlyOverHttp_ReturnsFalse()
    {
        // Development relaxes the cookies to SameAsRequest, so over plain HTTP they are not marked Secure and
        // the browser keeps them.
        Assert.That(OidcSignInRecovery.WillBrowserDiscardSignInCookies(
            CookieSecurePolicy.SameAsRequest, isHttps: false, new HostString("jim01.corp.example:5200")), Is.False);
    }

    [Test]
    public void DecideAction_LostCorrelationCookieOverHttps_Restarts()
    {
        var action = OidcSignInRecovery.DecideAction(
            new Exception("Correlation failed."), new AuthenticationProperties(),
            CookieSecurePolicy.Always, isHttps: true, new HostString("jim.example.test"));

        Assert.That(action, Is.EqualTo(OidcRemoteFailureAction.Restart));
    }

    [Test]
    public void DecideAction_LostCorrelationCookieAfterARestart_StopsInsteadOfLooping()
    {
        var properties = new AuthenticationProperties();
        properties.Items[OidcSignInRecovery.RestartCountItemKey] = "1";

        var action = OidcSignInRecovery.DecideAction(
            new Exception("Correlation failed."), properties,
            CookieSecurePolicy.Always, isHttps: true, new HostString("jim.example.test"));

        Assert.That(action, Is.EqualTo(OidcRemoteFailureAction.StopRestartLimitReached));
    }

    [Test]
    public void DecideAction_LostCorrelationCookieOverPlainHttpFromAnotherMachine_StopsWithoutRestarting()
    {
        var action = OidcSignInRecovery.DecideAction(
            new Exception("Correlation failed."), new AuthenticationProperties(),
            CookieSecurePolicy.Always, isHttps: false, new HostString("jim01.corp.example:5200"));

        Assert.That(action, Is.EqualTo(OidcRemoteFailureAction.StopPlainHttp));
    }

    [Test]
    public void DecideAction_CallbackWithoutStateOverPlainHttpFromAnotherMachine_StopsWithoutRestarting()
    {
        // No state means no properties; the transport alone decides that a restart cannot succeed.
        var action = OidcSignInRecovery.DecideAction(
            new Exception("OpenIdConnectAuthenticationHandler: message.State is null or empty."), null,
            CookieSecurePolicy.Always, isHttps: false, new HostString("jim01.corp.example:5200"));

        Assert.That(action, Is.EqualTo(OidcRemoteFailureAction.StopPlainHttp));
    }

    [Test]
    public void DecideAction_AccessDeniedOverPlainHttpFromAnotherMachine_StillSurfaces()
    {
        // Failures a restart would never have touched keep their own exception page, whatever the transport.
        var action = OidcSignInRecovery.DecideAction(
            new OpenIdConnectProtocolException("Message contains error: 'access_denied'."), new AuthenticationProperties(),
            CookieSecurePolicy.Always, isHttps: false, new HostString("jim01.corp.example:5200"));

        Assert.That(action, Is.EqualTo(OidcRemoteFailureAction.Surface));
    }

    // The tests below drive the real ASP.NET Core OpenID Connect handler: a challenge, then a callback that
    // comes back without the correlation cookie, exactly as a browser that discarded it would send it.

    [Test]
    public async Task HandleRemoteFailureAsync_LostCorrelationCookieOverHttps_RestartsOnceThenStopsAsync()
    {
        using var services = BuildAuthenticationServices();
        var state = await ChallengeAsync(services, "https", "jim.example.test", returnPath: "/admin/connected-systems");

        var firstCallback = await CallbackAsync(services, "https", "jim.example.test", state);

        Assert.That(firstCallback.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
        Assert.That(firstCallback.Location, Does.StartWith(IdentityProvider + "/authorize"),
            "the first lost cookie should restart the sign-in at the identity provider");

        var secondCallback = await CallbackAsync(services, "https", "jim.example.test", ReadState(firstCallback.Location!));

        Assert.That(secondCallback.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
        Assert.That(secondCallback.Location, Is.EqualTo(OidcSignInRecovery.SignInFailedPath),
            "the restart failed the same way, so restarting again would loop");
    }

    [Test]
    public async Task HandleRemoteFailureAsync_RestartedSignIn_KeepsTheOriginalReturnPathAsync()
    {
        using var services = BuildAuthenticationServices();
        var state = await ChallengeAsync(services, "https", "jim.example.test", returnPath: "/admin/connected-systems");

        var firstCallback = await CallbackAsync(services, "https", "jim.example.test", state);
        var restartedProperties = UnprotectState(services, ReadState(firstCallback.Location!));

        Assert.That(restartedProperties.RedirectUri, Is.EqualTo("/admin/connected-systems"));
    }

    [Test]
    public async Task HandleRemoteFailureAsync_LostCorrelationCookieOverPlainHttpFromAnotherMachine_StopsImmediatelyAsync()
    {
        using var services = BuildAuthenticationServices();
        var state = await ChallengeAsync(services, "http", "jim01.corp.example:5200", returnPath: "/");

        var callback = await CallbackAsync(services, "http", "jim01.corp.example:5200", state);

        Assert.That(callback.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
        Assert.That(callback.Location, Is.EqualTo(OidcSignInRecovery.SignInFailedPath));
    }

    [Test]
    public async Task HandleRemoteFailureAsync_ReplayedCallbackWhileSignedIn_ReturnsToThePageWithoutSigningInAgainAsync()
    {
        // A back-button replay of a callback that already succeeded: the correlation cookie was spent by the
        // first use, but the user has a session, so the page they wanted is simply shown again. This holds even
        // where JIM sees plain HTTP (a TLS-terminating proxy it has not been told to trust), because no round
        // trip to the identity provider is needed.
        using var services = BuildAuthenticationServices();
        var sessionCookie = await SignInAsync(services, "http", "jim01.corp.example");
        var state = await ChallengeAsync(services, "http", "jim01.corp.example", returnPath: "/admin/connected-systems");

        var callback = await CallbackAsync(services, "http", "jim01.corp.example", state, sessionCookie);

        Assert.That(callback.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
        Assert.That(callback.Location, Is.EqualTo("/admin/connected-systems"));
    }

    private const string IdentityProvider = "https://idp.example.test";

    private sealed record CapturedResponse(int StatusCode, string? Location, string? SetCookie);

    private static ServiceProvider BuildAuthenticationServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie()
            .AddOpenIdConnect(options =>
            {
                // Production's cookie settings (Secure-only correlation and nonce cookies) are the defaults, so
                // they are deliberately left alone here.
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.ClientId = "jim";
                options.ClientSecret = "not-a-real-secret";
                options.ResponseType = "code";
                options.UsePkce = true;
                options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = IdentityProvider,
                    AuthorizationEndpoint = IdentityProvider + "/authorize",
                    TokenEndpoint = IdentityProvider + "/token"
                };
                options.Events.OnRemoteFailure = OidcSignInRecovery.HandleRemoteFailureAsync;
            });
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        return services.BuildServiceProvider();
    }

    private static async Task<string> ChallengeAsync(IServiceProvider services, string scheme, string host, string returnPath)
    {
        var response = await SendAsync(services, scheme, host, returnPath, QueryString.Empty, cookie: null,
            context => context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = returnPath }));

        return ReadState(response.Location!);
    }

    private static async Task<CapturedResponse> CallbackAsync(IServiceProvider services, string scheme, string host, string state, string? cookie = null)
    {
        var query = QueryString.Create(new Dictionary<string, string?> { ["code"] = "authorisation-code", ["state"] = state });
        return await SendAsync(services, scheme, host, "/signin-oidc", query, cookie, async context =>
        {
            var middleware = new AuthenticationMiddleware(_ => Task.CompletedTask, context.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>());
            await middleware.Invoke(context);
        });
    }

    private static async Task<string> SignInAsync(IServiceProvider services, string scheme, string host)
    {
        var identity = new ClaimsIdentity([new Claim("sub", "signed-in-user")], "test");
        var response = await SendAsync(services, scheme, host, "/", QueryString.Empty, cookie: null,
            context => context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity)));

        // Keep only "name=value", as a browser would send it back.
        return response.SetCookie!.Split(';')[0];
    }

    private static async Task<CapturedResponse> SendAsync(
        IServiceProvider services, string scheme, string host, string path, QueryString query, string? cookie, Func<HttpContext, Task> handle)
    {
        await using var scope = services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.QueryString = query;
        if (cookie != null)
            context.Request.Headers.Cookie = cookie;

        await handle(context);

        return new CapturedResponse(
            context.Response.StatusCode,
            context.Response.Headers.Location.FirstOrDefault(),
            context.Response.Headers.SetCookie.FirstOrDefault());
    }

    private static string ReadState(string authorisationUrl)
    {
        return QueryHelpers.ParseQuery(new Uri(authorisationUrl).Query)["state"].ToString();
    }

    private static AuthenticationProperties UnprotectState(IServiceProvider services, string state)
    {
        var options = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        return options.StateDataFormat.Unprotect(state)!;
    }
}

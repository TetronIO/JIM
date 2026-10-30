// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Middleware;

/// <summary>
/// Adds defence-in-depth security response headers to every response JIM.Web sends: Blazor UI pages, static
/// assets, and the REST API alike (issue #500, OWASP Top 10:2025 A02: Security Misconfiguration).
/// </summary>
/// <remarks>
/// <para>
/// This is stage one of a staged Content Security Policy rollout: a restrictive policy compatible with Blazor
/// Server and MudBlazor as they stand today, both of which rely on inline scripts/styles (see
/// <see cref="ContentSecurityPolicy"/> for the directive-by-directive rationale). A future stage is expected to
/// move to a nonce-based policy and drop <c>'unsafe-inline'</c>; that work is tracked separately in the security
/// assessment and is out of scope here.
/// </para>
/// <para>
/// Headers are only added when the response does not already carry them, so a more specific downstream
/// handler always wins over this middleware's defaults.
/// </para>
/// </remarks>
public class SecurityHeadersMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    /// <summary>
    /// Stage-one Content Security Policy. Restrictive defaults, tailored to what JIM's Blazor Server host and
    /// MudBlazor actually render (verified against <c>Pages/_Layout.cshtml</c>, <c>Pages/_Host.cshtml</c> and
    /// the MudBlazor/self-hosted font static assets, not assumed):
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>default-src 'self'</c>: JIM is air-gap deployable (see root CLAUDE.md's design
    /// principles); nothing should load from a third-party origin, so every other directive falls back to
    /// same-origin only unless a specific exception is documented below.</description></item>
    /// <item><description><c>script-src 'self' 'unsafe-inline'</c>: <c>_Layout.cshtml</c> carries an inline
    /// <c>&lt;script&gt;</c> block implementing the custom Blazor Server reconnection handler; Blazor Server
    /// itself needs no external script host, only the self-hosted <c>_framework/blazor.server.js</c>.
    /// <c>'unsafe-inline'</c> is the documented stage-one compromise; removing it is the tracked stage-two
    /// nonce-based follow-up.</description></item>
    /// <item><description><c>style-src 'self' 'unsafe-inline'</c>: MudBlazor components render hundreds of
    /// inline <c>style="..."</c> attributes across JIM.Web's Razor pages; blocking inline styles would break the
    /// UI outright. All CSS files themselves (site.css, theme CSS, MudBlazor.min.css) are self-hosted.</description></item>
    /// <item><description><c>img-src 'self' data:</c>: images are self-hosted static assets; <c>data:</c> is
    /// allowed for any inline/generated image (for example a future chart or QR code) without weakening the
    /// policy meaningfully, since <c>data:</c> URIs cannot execute script in an <c>img</c> context.</description></item>
    /// <item><description><c>font-src 'self'</c>: IBM Plex Sans/Mono, Space Grotesk and Inter are all self-hosted
    /// under <c>wwwroot/fonts</c> and referenced by relative <c>url()</c> in the site's font CSS; there is no
    /// external font host to allow.</description></item>
    /// <item><description><c>connect-src 'self'</c>: Blazor Server's SignalR circuit (<c>MapBlazorHub</c>,
    /// default path <c>/_blazor</c>) is a same-origin WebSocket, and CSP Level 3 makes <c>'self'</c> match
    /// <c>ws:</c> and <c>wss:</c> on the page's own host, over http and https alike. The policy used to add bare
    /// <c>wss:</c> and <c>ws:</c> scheme sources to avoid relying on that matching; those allow a WebSocket to
    /// any host, so they were removed (#2603). A browser old enough to predate that matching would refuse the
    /// circuit's WebSocket.</description></item>
    /// <item><description><c>frame-ancestors 'none'</c>: JIM is never legitimately embedded in another site's
    /// frame; this is the modern, CSP-native replacement for (and is paired with) the
    /// <c>X-Frame-Options: DENY</c> header below for older user agents.</description></item>
    /// <item><description><c>base-uri 'self'</c>: prevents an injected <c>&lt;base&gt;</c> tag from rebasing
    /// relative URLs (scripts, links, form actions) to an attacker-controlled origin.</description></item>
    /// <item><description><c>form-action 'self'</c>: no page in JIM.Web submits a form to an external origin
    /// (verified by searching the codebase for <c>&lt;form&gt;</c>/<c>EditForm</c> usage; MudBlazor components
    /// only reference external URLs via plain <c>target="_blank"</c> links, which are navigations, not form
    /// submissions, and are unaffected by this directive). The OIDC sign-in/sign-out challenge to the identity
    /// provider is a server-issued HTTP redirect (a 302 Location header), not a browser-originated form
    /// submission, so it is unaffected by <c>form-action</c> too.</description></item>
    /// </list>
    /// </remarks>
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    /// <summary>
    /// The policy for the API reference (<c>/api/reference</c>): <see cref="ContentSecurityPolicy"/>, with the
    /// identity provider's token endpoint origin added to <c>connect-src</c>. Try It signs in from the browser and
    /// exchanges its code at that endpoint with <c>fetch</c> (#2594), which <c>connect-src 'self'</c> refuses. Only
    /// the origin is added, and only for an absolute http or https address, so nothing from the identity provider's
    /// configuration can add any other source or directive.
    /// </summary>
    /// <param name="tokenEndpoint">The token endpoint the API reference's OpenAPI document gives, or null when the
    /// identity provider's configuration could not be read.</param>
    /// <returns>The policy, or <see cref="ContentSecurityPolicy"/> unchanged when there is no usable endpoint.</returns>
    public static string ApiReferenceContentSecurityPolicy(string? tokenEndpoint)
    {
        if (!Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return ContentSecurityPolicy;

        return ContentSecurityPolicy.Replace("connect-src 'self'; ", $"connect-src 'self' {uri.GetLeftPart(UriPartial.Authority)}; ", StringComparison.Ordinal);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        SetIfAbsent(headers, "Content-Security-Policy", ContentSecurityPolicy);
        SetIfAbsent(headers, "X-Content-Type-Options", "nosniff");
        SetIfAbsent(headers, "X-Frame-Options", "DENY");
        SetIfAbsent(headers, "Referrer-Policy", "strict-origin-when-cross-origin");
        SetIfAbsent(headers, "Permissions-Policy", "camera=(), microphone=(), geolocation=()");

        await _next(context);
    }

    /// <summary>
    /// Sets a response header only when it is not already present, so a more specific downstream handler's
    /// value always wins over this middleware's defaults.
    /// </summary>
    private static void SetIfAbsent(IHeaderDictionary headers, string name, string value)
    {
        if (!headers.ContainsKey(name))
            headers[name] = value;
    }
}

/// <summary>
/// Extension methods for registering the security response headers middleware.
/// </summary>
public static class SecurityHeadersMiddlewareExtensions
{
    /// <summary>
    /// Adds the security response headers middleware to the pipeline. Register as early as possible, so every
    /// response path (including error responses and static files) carries the headers.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}

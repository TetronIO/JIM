// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Authentication;
using Serilog;

namespace JIM.Web.Middleware;

/// <summary>
/// Sends a browser whose sign-in failed because JIM could not reach or trust the identity provider to the sign-in
/// unavailable page, which says why and how to fix it, instead of the generic error page (#2363).
/// </summary>
/// <remarks>
/// <para>
/// Two failures qualify. Starting a sign-in needs the identity provider's OpenID Connect configuration, and the
/// handler throws <c>IDX20803</c> when it cannot read it. Finishing one redeems the code over the same connection, and
/// a refused connection there surfaces as an <see cref="AuthenticationFailureException"/>. Both mean nobody can sign
/// in, which the page explains, rather than something wrong with this request.
/// </para>
/// <para>
/// API requests are never redirected; they answer for themselves. Every other exception continues to the error page.
/// </para>
/// </remarks>
public sealed class IdentityProviderUnavailableMiddleware(RequestDelegate next)
{
    /// <summary>
    /// The page that explains the failure.
    /// </summary>
    public const string PagePath = "/sign-in-unavailable";

    /// <summary>
    /// Where the identity provider returns the browser after sign-in (the OpenID Connect handler's default).
    /// </summary>
    private const string CallbackPath = "/signin-oidc";

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (ShouldExplain(context, ex))
        {
            Log.Warning("Sign-in could not reach or trust the identity provider; sending the browser to {PagePath}: {Reason}",
                PagePath, Innermost(ex).Message);
            context.Response.Clear();
            context.Response.Redirect(PagePath);
        }
    }

    private static bool ShouldExplain(HttpContext context, Exception exception)
    {
        if (context.Response.HasStarted || context.Request.Path.StartsWithSegments("/api"))
            return false;

        var isBrowserNavigation = HttpMethods.IsGet(context.Request.Method)
            || (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals(CallbackPath, StringComparison.OrdinalIgnoreCase));

        return isBrowserNavigation && IsIdentityProviderUnavailable(exception);
    }

    private static bool IsIdentityProviderUnavailable(Exception exception)
    {
        if (exception is InvalidOperationException && exception.Message.StartsWith("IDX20803", StringComparison.Ordinal))
            return true;

        return exception is AuthenticationFailureException && Chain(exception).Any(e => e is HttpRequestException);
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
            yield return current;
    }

    private static Exception Innermost(Exception exception) => Chain(exception).Last();
}

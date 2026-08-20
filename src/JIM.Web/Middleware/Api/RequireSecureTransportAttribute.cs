// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// Refuses an API request unless JIM can confirm the transport is encrypted (#1119, requirement 34). Applied to
/// every endpoint that accepts a password.
/// <para>
/// Authorisation and the never-log invariant already held on those endpoints; this is the piece that did not.
/// The deployment's own HTTPS enforcement is not the same guarantee: an operator who has not enabled it sends
/// the one value in JIM that can never be rotated quietly straight across the network in the clear, and nothing
/// anywhere says so. Refusing is the only response that cannot be missed.
/// </para>
/// <para>
/// <c>PasswordEndpointSecureTransportTests</c> fails the build if an endpoint binds a request carrying a
/// password without this attribute, so a new one is protected by having a password rather than by its author
/// remembering: the same arrangement as the pagination depth cap.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequireSecureTransportAttribute : Attribute, IActionFilter
{
    /// <summary>
    /// Rejects before the action runs, so the password never reaches application code on a transport JIM would
    /// not have accepted it over.
    /// </summary>
    public void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.HttpContext.Request.IsHttps)
            return;

        // The development stack runs over plain HTTP, and a rule that made setting a password impossible there
        // would be worked around rather than obeyed. The exemption cannot hold in a released image: the
        // environment is Production unless somebody deliberately sets it otherwise.
        var environment = context.HttpContext.RequestServices.GetService<IWebHostEnvironment>();
        if (environment != null && environment.IsDevelopment())
            return;

        // Forbidden rather than Bad Request: the request is understood and well formed, and what is refused is
        // carrying a password over this transport. The remedy names the proxy case because that is the likeliest
        // way a properly secured deployment lands here: TLS terminates at a proxy JIM has not been told to
        // trust, so JIM never sees the forwarded scheme and cannot confirm anything.
        context.Result = new ObjectResult(ApiErrorResponse.Forbidden(
            "This endpoint accepts a password and refuses to carry one over a connection JIM cannot confirm is " +
            "encrypted. Call it over HTTPS. If TLS terminates at a reverse proxy, set JIM_TRUSTED_PROXIES to " +
            "that proxy so JIM reads the forwarded scheme rather than the hop it can see."))
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }

    /// <summary>
    /// Nothing to do once the action has run; the decision is made entirely before it.
    /// </summary>
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}

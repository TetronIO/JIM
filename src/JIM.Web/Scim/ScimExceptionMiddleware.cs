// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Serilog;

namespace JIM.Web.Scim;

/// <summary>
/// The <c>/scim</c> counterpart of the REST API's global exception handler: an unhandled failure becomes a SCIM
/// <c>500</c> that says nothing about the cause, which is logged instead. Registered for <c>/scim</c> only, outermost.
/// </summary>
public class ScimExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "ScimExceptionMiddleware: Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
                throw;

            context.Response.Clear();
            await ScimResponses.WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                "The request could not be completed. The cause has been logged.");
        }
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// Adds the <c>WWW-Authenticate</c> challenge to a 401 that an API action returns itself, for instance when an
/// authenticated caller's JIM identity cannot be resolved. RFC 9110 s15.5.2 requires the challenge on every 401, and
/// the authentication middleware only adds it to the 401s it produces (<see cref="ApiUnauthorisedResponse"/>).
/// Registered globally, so a new action that returns <c>Unauthorized(...)</c> is covered without remembering to be.
/// </summary>
public class ApiChallengeResultFilter : IAlwaysRunResultFilter
{
    /// <inheritdoc />
    public void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Result is not IStatusCodeActionResult { StatusCode: StatusCodes.Status401Unauthorized })
            return;

        var request = context.HttpContext.Request;
        if (!request.Path.StartsWithSegments("/api") || context.HttpContext.Response.Headers.ContainsKey("WWW-Authenticate"))
            return;

        context.HttpContext.Response.Headers.WWWAuthenticate = ApiUnauthorisedResponse.BuildChallenge(request);
    }

    /// <inheritdoc />
    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}

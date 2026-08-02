// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// Applies the shared pagination depth ceiling (<see cref="PaginationLimits"/>) to every API action, whatever
/// shape its pagination parameters take (issue #487).
/// <para>
/// <see cref="PaginationRequest"/> validates itself, but roughly half of JIM's paginated endpoints bind page
/// and pageSize as bare query parameters instead, and those are the highest-volume reads in the product:
/// connector space, Pending Exports, attribute values, deleted objects. Enforcing the rule in one filter means
/// a new endpoint is protected by default rather than by the author remembering, and
/// <c>PaginationGuardCoverageTests</c> fails the build if one ever adopts a parameter name this filter would
/// not recognise.
/// </para>
/// </summary>
public class PaginationDepthActionFilter : IActionFilter
{
    private const string PageParameterName = "page";
    private const string PageSizeParameterName = "pageSize";

    /// <summary>
    /// Whether a parameter name is one this filter resolves a page number from. Exposed so the coverage tests
    /// can assert every paginated action uses a name the guard recognises.
    /// </summary>
    public static bool IsRecognisedPageParameterName(string parameterName)
    {
        return string.Equals(parameterName, PageParameterName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rejects an over-deep request before the action (and therefore the query) runs.
    /// </summary>
    public void OnActionExecuting(ActionExecutingContext context)
    {
        // A bound PaginationRequest is already rejected by DataAnnotations via [ApiController]; re-checking it
        // here keeps one rule in force for every shape and covers any caller outside that pipeline.
        var overDeepRequest = context.ActionArguments.Values
            .OfType<PaginationRequest>()
            .FirstOrDefault(r => !PaginationLimits.IsWithinDepth(r.Page, r.PageSize));

        if (overDeepRequest != null)
        {
            context.Result = Reject(overDeepRequest.Page, overDeepRequest.PageSize);
            return;
        }

        if (!TryGetIntArgument(context.ActionArguments, PageParameterName, out var page))
            return;

        // With no page size bound, assume the largest the endpoint could serve, so the guard errs towards
        // protecting the database rather than towards letting a deep scan through.
        var pageSize = TryGetIntArgument(context.ActionArguments, PageSizeParameterName, out var boundPageSize)
            ? boundPageSize
            : PaginationLimits.MaxPageSize;

        if (!PaginationLimits.IsWithinDepth(page, pageSize))
            context.Result = Reject(page, pageSize);
    }

    /// <summary>
    /// No post-action work; the guard is entirely a pre-condition.
    /// </summary>
    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    private static BadRequestObjectResult Reject(int page, int pageSize)
    {
        var message = PaginationLimits.DepthExceededMessage(page, pageSize);
        return new BadRequestObjectResult(ApiErrorResponse.ValidationError(message, new Dictionary<string, string[]>
        {
            [PageParameterName] = [message]
        }));
    }

    /// <summary>
    /// Reads an integer action argument by name, tolerating a differently-cased or absent parameter and a
    /// parameter that happens to share the name but not the type.
    /// </summary>
    private static bool TryGetIntArgument(IDictionary<string, object?> arguments, string name, out int value)
    {
        var match = arguments
            .Where(a => string.Equals(a.Key, name, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Value)
            .FirstOrDefault(v => v is int);

        if (match is int intValue)
        {
            value = intValue;
            return true;
        }

        value = 0;
        return false;
    }
}

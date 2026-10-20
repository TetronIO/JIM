// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Claims;
using JIM.Models.Core;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JIM.Web.Scim;

/// <summary>
/// Counts every SCIM response towards its window's summary Activity (D17), once the response's status is settled.
/// Only a request made with a key scoped to the route's Connected System is counted; the scope policy refuses anything
/// else before it reaches here.
/// </summary>
public class ScimRequestTallyFilter(ScimRequestTallyBuffer buffer) : IAsyncResultFilter
{
    /// <inheritdoc />
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        await next();

        var http = context.HttpContext;
        if (ScimConnectedSystemScopeHandler.RouteConnectedSystemId(http) is not { } connectedSystemId
            || !Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var apiKeyId)
            || http.User.FindFirstValue(Constants.BuiltInClaims.KeyScope) == null)
            return;

        var method = http.Request.Method;
        var status = http.Response.StatusCode;
        var outcome = ScimRequestOutcomes.Classify(method, status);
        var rejection = outcome == ScimRequestOutcome.Rejected ? NameRejection(http, method, status) : null;
        buffer.Record(connectedSystemId, apiKeyId, http.User.FindFirstValue(ClaimTypes.Name) ?? apiKeyId.ToString(), outcome, rejection);
    }

    /// <summary>
    /// Names a rejection by status, SCIM error type, method and endpoint: never by the error's detail, which can quote
    /// the values a client sent.
    /// </summary>
    private static string NameRejection(HttpContext http, string method, int status)
    {
        var errorType = http.Items.TryGetValue(ScimRequestOutcomes.ErrorTypeItem, out var item) && item is string { Length: > 0 } type
            ? $" {type}"
            : string.Empty;
        var endpoint = http.Request.RouteValues.TryGetValue("endpoint", out var value) && value is string { Length: > 0 } name
            ? $" /{name}"
            : string.Empty;
        return $"{status}{errorType} on {method}{endpoint}";
    }
}

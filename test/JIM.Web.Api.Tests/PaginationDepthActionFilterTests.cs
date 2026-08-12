// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using JIM.Web.Middleware.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the global pagination depth filter (issue #487), which applies the shared depth rule to the
/// endpoints that bind page and pageSize as bare query parameters rather than as a
/// <see cref="PaginationRequest"/>. Those endpoints cover the highest-volume tables in JIM (connector space,
/// Pending Exports, attribute values), so leaving them unguarded left the runaway OFFSET scan the cap exists
/// to prevent fully reachable.
/// </summary>
[TestFixture]
public class PaginationDepthActionFilterTests
{
    private static ActionExecutingContext BuildContext(IDictionary<string, object?> arguments)
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments, controller: new object());
    }

    [Test]
    public void OnActionExecuting_ShallowPage_IsAllowedThrough()
    {
        var context = BuildContext(new Dictionary<string, object?> { ["page"] = 3, ["pageSize"] = 50 });

        new PaginationDepthActionFilter().OnActionExecuting(context);

        Assert.That(context.Result, Is.Null);
    }

    [Test]
    public void OnActionExecuting_OverDeepPage_ShortCircuitsWithBadRequest()
    {
        var context = BuildContext(new Dictionary<string, object?> { ["page"] = 999_999, ["pageSize"] = 100 });

        new PaginationDepthActionFilter().OnActionExecuting(context);

        Assert.That(context.Result, Is.TypeOf<BadRequestObjectResult>());
        var body = ((BadRequestObjectResult)context.Result!).Value as ApiErrorResponse;
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Code, Is.EqualTo(ApiErrorCodes.ValidationError));
        Assert.That(body!.Message, Does.Contain("page").IgnoreCase);
    }

    [Test]
    public void OnActionExecuting_OverDeepPage_DoesNotRunTheAction()
    {
        // Short-circuiting matters: the whole point is that the query is never issued.
        var context = BuildContext(new Dictionary<string, object?> { ["page"] = 999_999, ["pageSize"] = 100 });

        new PaginationDepthActionFilter().OnActionExecuting(context);

        Assert.That(context.Result, Is.Not.Null, "A rejected request must be short-circuited before the action executes.");
    }

    [Test]
    public void OnActionExecuting_PageWithoutAPageSizeArgument_IsEvaluatedAtTheMaximumPageSize()
    {
        // With no page size to go on, assume the largest the endpoint could use, so the guard errs towards
        // protecting the database rather than towards letting a deep scan through.
        var allowed = BuildContext(new Dictionary<string, object?> { ["page"] = 100 });
        var rejected = BuildContext(new Dictionary<string, object?> { ["page"] = 999_999 });

        new PaginationDepthActionFilter().OnActionExecuting(allowed);
        new PaginationDepthActionFilter().OnActionExecuting(rejected);

        Assert.That(allowed.Result, Is.Null);
        Assert.That(rejected.Result, Is.TypeOf<BadRequestObjectResult>());
    }

    [Test]
    public void OnActionExecuting_ActionWithNoPaginationArguments_IsIgnored()
    {
        var context = BuildContext(new Dictionary<string, object?> { ["id"] = 42 });

        new PaginationDepthActionFilter().OnActionExecuting(context);

        Assert.That(context.Result, Is.Null);
    }

    [Test]
    public void OnActionExecuting_PaginationRequestArgument_IsAlsoGuarded()
    {
        // PaginationRequest validates itself through DataAnnotations, but the filter covers it too so that a
        // single rule protects every shape; belt and braces.
        var pagination = new PaginationRequest { Page = 999_999, PageSize = 100 };
        var context = BuildContext(new Dictionary<string, object?> { ["pagination"] = pagination });

        new PaginationDepthActionFilter().OnActionExecuting(context);

        Assert.That(context.Result, Is.TypeOf<BadRequestObjectResult>());
    }

    [Test]
    public void OnActionExecuting_PaginationRequestWithinDepth_IsAllowedThrough()
    {
        var pagination = new PaginationRequest { Page = 2, PageSize = 100 };
        var context = BuildContext(new Dictionary<string, object?> { ["pagination"] = pagination });

        new PaginationDepthActionFilter().OnActionExecuting(context);

        Assert.That(context.Result, Is.Null);
    }

    [Test]
    public void OnActionExecuting_NonIntegerPageArgument_IsIgnoredRatherThanThrowing()
    {
        // A parameter that happens to be named "page" but is not an int must not break the request.
        var context = BuildContext(new Dictionary<string, object?> { ["page"] = "not-a-number" });

        Assert.That(() => new PaginationDepthActionFilter().OnActionExecuting(context), Throws.Nothing);
        Assert.That(context.Result, Is.Null);
    }

    [Test]
    public void OnActionExecuting_NullPageArgument_IsIgnored()
    {
        // Optional (nullable) pagination parameters bind as null when omitted.
        var context = BuildContext(new Dictionary<string, object?> { ["page"] = null, ["pageSize"] = null });

        new PaginationDepthActionFilter().OnActionExecuting(context);

        Assert.That(context.Result, Is.Null);
    }
}

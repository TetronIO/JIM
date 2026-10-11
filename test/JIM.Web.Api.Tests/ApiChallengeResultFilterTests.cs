// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Collections.Generic;
using JIM.Web.Middleware.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="ApiChallengeResultFilter"/>: every 401 on the API carries a WWW-Authenticate challenge (RFC
/// 9110 s15.5.2), including the ones an action returns itself rather than the authentication middleware.
/// </summary>
[TestFixture]
public class ApiChallengeResultFilterTests
{
    [Test]
    public void OnResultExecuting_ActionReturns401WithABearerCredential_AddsTheInvalidTokenChallenge()
    {
        var context = NewContext("/api/v1/metaverse/objects", new UnauthorizedObjectResult(new { error = "Unauthorized" }),
            authorization: "Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.sig");

        new ApiChallengeResultFilter().OnResultExecuting(context);

        Assert.That(context.HttpContext.Response.Headers.WWWAuthenticate.ToString(),
            Is.EqualTo("Bearer realm=\"JIM\", error=\"invalid_token\""));
    }

    [Test]
    public void OnResultExecuting_ActionReturnsBare401_AddsTheChallenge()
    {
        var context = NewContext("/api/v1/metaverse/objects", new UnauthorizedResult());

        new ApiChallengeResultFilter().OnResultExecuting(context);

        Assert.That(context.HttpContext.Response.Headers.WWWAuthenticate.ToString(), Is.EqualTo("Bearer realm=\"JIM\""));
    }

    [Test]
    public void OnResultExecuting_ChallengeAlreadySet_LeavesItAlone()
    {
        var context = NewContext("/api/v1/metaverse/objects", new UnauthorizedResult());
        context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer realm=\"JIM\", error=\"insufficient_scope\"";

        new ApiChallengeResultFilter().OnResultExecuting(context);

        Assert.That(context.HttpContext.Response.Headers.WWWAuthenticate.ToString(),
            Is.EqualTo("Bearer realm=\"JIM\", error=\"insufficient_scope\""));
    }

    [Test]
    public void OnResultExecuting_ActionReturns403_AddsNothing()
    {
        var context = NewContext("/api/v1/metaverse/objects", new ForbidResult());

        new ApiChallengeResultFilter().OnResultExecuting(context);

        Assert.That(context.HttpContext.Response.Headers.ContainsKey("WWW-Authenticate"), Is.False);
    }

    [Test]
    public void OnResultExecuting_401OutsideTheApi_AddsNothing()
    {
        var context = NewContext("/admin/api-keys", new UnauthorizedResult());

        new ApiChallengeResultFilter().OnResultExecuting(context);

        Assert.That(context.HttpContext.Response.Headers.ContainsKey("WWW-Authenticate"), Is.False);
    }

    private static ResultExecutingContext NewContext(string path, IActionResult result, string? authorization = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        if (authorization != null)
            httpContext.Request.Headers.Authorization = authorization;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ResultExecutingContext(actionContext, new List<IFilterMetadata>(), result, controller: new object());
    }
}

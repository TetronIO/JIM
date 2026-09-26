// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The page JIM shows when it stops a sign-in rather than loop. It must be reachable without signing in (the
/// visitor has just failed to), and it explains plain HTTP as the cause whenever that is how it was reached.
/// </summary>
[TestFixture]
public class SignInFailedModelTests
{
    [Test]
    public void SignInFailedModel_AllowsAnonymousAccess()
    {
        Assert.That(typeof(SignInFailedModel).IsDefined(typeof(AllowAnonymousAttribute), inherit: true), Is.True);
    }

    [Test]
    public void OnGet_ReachedOverPlainHttp_ExplainsThatHttpsIsRequired()
    {
        var model = BuildModel("http");

        model.OnGet();

        Assert.That(model.IsPlainHttp, Is.True);
    }

    [Test]
    public void OnGet_ReachedOverHttps_DoesNotBlamePlainHttp()
    {
        var model = BuildModel("https");

        model.OnGet();

        Assert.That(model.IsPlainHttp, Is.False);
    }

    private static SignInFailedModel BuildModel(string scheme)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = scheme;
        httpContext.Request.Host = new HostString("jim01.corp.example");
        return new SignInFailedModel { PageContext = new PageContext { HttpContext = httpContext } };
    }
}

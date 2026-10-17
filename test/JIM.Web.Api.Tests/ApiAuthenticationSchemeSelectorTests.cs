// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Web.Middleware.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="ApiAuthenticationSchemeSelector"/>: the cookie scheme's forward selector, which decides
/// whether a request is authenticated by an API key, an SSO bearer token, or the portal's sign-in cookie.
/// </summary>
[TestFixture]
public class ApiAuthenticationSchemeSelectorTests
{
    [TestCase("Bearer jim_ak_0123456789abcdef")]
    [TestCase("bearer jim_ak_0123456789abcdef")]
    [TestCase("BEARER jim_ak_0123456789abcdef")]
    public void SelectScheme_ApiKeyAsBearer_SelectsApiKeyScheme(string authorization)
    {
        var context = BuildContext(authorization);

        Assert.That(ApiAuthenticationSchemeSelector.SelectScheme(context), Is.EqualTo(ApiKeyAuthenticationHandler.SchemeName));
    }

    [TestCase("Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.sig")]
    [TestCase("bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.sig")]
    public void SelectScheme_SsoJwtAsBearer_SelectsJwtBearerScheme(string authorization)
    {
        var context = BuildContext(authorization);

        Assert.That(ApiAuthenticationSchemeSelector.SelectScheme(context), Is.EqualTo(JwtBearerDefaults.AuthenticationScheme));
    }

    [Test]
    public void SelectScheme_NoAuthorizationHeader_LeavesTheCookieScheme()
    {
        var context = BuildContext(authorization: null);

        Assert.That(ApiAuthenticationSchemeSelector.SelectScheme(context), Is.Null);
    }

    [Test]
    public void SelectScheme_ApiKeyInXApiKeyHeaderOnly_LeavesTheCookieScheme()
    {
        // X-API-Key is no longer an API key header; a key sent there is not a credential JIM recognises.
        var context = BuildContext(authorization: null);
        context.Request.Headers["X-API-Key"] = "jim_ak_0123456789abcdef";

        Assert.That(ApiAuthenticationSchemeSelector.SelectScheme(context), Is.Null);
    }

    [Test]
    public void SelectScheme_BasicCredential_LeavesTheCookieScheme()
    {
        var context = BuildContext("Basic dXNlcjpwYXNz");

        Assert.That(ApiAuthenticationSchemeSelector.SelectScheme(context), Is.Null);
    }

    private static DefaultHttpContext BuildContext(string? authorization)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/userinfo";
        if (authorization != null)
            context.Request.Headers.Authorization = authorization;
        return context;
    }
}

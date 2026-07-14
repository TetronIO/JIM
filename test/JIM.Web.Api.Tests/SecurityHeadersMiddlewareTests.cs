// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading.Tasks;
using JIM.Web.Middleware;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="SecurityHeadersMiddleware"/>: the defence-in-depth response header middleware behind
/// the stage-one Content Security Policy (issue #500, OWASP Top 10:2025 A02). Drives the middleware with a plain
/// <see cref="DefaultHttpContext"/> and a no-op "next" delegate, so the real pipeline is never involved.
/// </summary>
[TestFixture]
public class SecurityHeadersMiddlewareTests
{
    private static readonly RequestDelegate NoOpNext = _ => Task.CompletedTask;

    [Test]
    public async Task InvokeAsync_AnyResponse_SetsContentSecurityPolicyHeader()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(NoOpNext);

        await middleware.InvokeAsync(context);

        Assert.That(context.Response.Headers.ContainsKey("Content-Security-Policy"), Is.True);
        Assert.That(context.Response.Headers["Content-Security-Policy"].ToString(),
            Is.EqualTo(SecurityHeadersMiddleware.ContentSecurityPolicy));
    }

    [Test]
    public async Task InvokeAsync_AnyResponse_SetsXContentTypeOptionsHeader()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(NoOpNext);

        await middleware.InvokeAsync(context);

        Assert.That(context.Response.Headers["X-Content-Type-Options"].ToString(), Is.EqualTo("nosniff"));
    }

    [Test]
    public async Task InvokeAsync_AnyResponse_SetsXFrameOptionsHeader()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(NoOpNext);

        await middleware.InvokeAsync(context);

        Assert.That(context.Response.Headers["X-Frame-Options"].ToString(), Is.EqualTo("DENY"));
    }

    [Test]
    public async Task InvokeAsync_AnyResponse_SetsReferrerPolicyHeader()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(NoOpNext);

        await middleware.InvokeAsync(context);

        Assert.That(context.Response.Headers["Referrer-Policy"].ToString(), Is.EqualTo("strict-origin-when-cross-origin"));
    }

    [Test]
    public async Task InvokeAsync_AnyResponse_SetsPermissionsPolicyHeader()
    {
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(NoOpNext);

        await middleware.InvokeAsync(context);

        Assert.That(context.Response.Headers["Permissions-Policy"].ToString(),
            Is.EqualTo("camera=(), microphone=(), geolocation=()"));
    }

    [Test]
    public async Task InvokeAsync_HeaderAlreadySetDownstream_DoesNotOverwriteIt()
    {
        var context = new DefaultHttpContext();
        RequestDelegate next = ctx =>
        {
            ctx.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            return Task.CompletedTask;
        };
        var middleware = new SecurityHeadersMiddleware(next);

        await middleware.InvokeAsync(context);

        Assert.That(context.Response.Headers["X-Frame-Options"].ToString(), Is.EqualTo("SAMEORIGIN"));
    }

    [Test]
    public async Task InvokeAsync_HeaderAlreadySetDownstream_DoesNotOverwriteContentSecurityPolicy()
    {
        var context = new DefaultHttpContext();
        const string customPolicy = "default-src 'none'";
        RequestDelegate next = ctx =>
        {
            ctx.Response.Headers["Content-Security-Policy"] = customPolicy;
            return Task.CompletedTask;
        };
        var middleware = new SecurityHeadersMiddleware(next);

        await middleware.InvokeAsync(context);

        Assert.That(context.Response.Headers["Content-Security-Policy"].ToString(), Is.EqualTo(customPolicy));
    }

    [Test]
    public async Task InvokeAsync_AnyResponse_CallsNextDelegate()
    {
        var context = new DefaultHttpContext();
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };
        var middleware = new SecurityHeadersMiddleware(next);

        await middleware.InvokeAsync(context);

        Assert.That(nextCalled, Is.True);
    }

    [Test]
    public void ContentSecurityPolicy_Constant_ContainsDefaultSrcSelf()
    {
        Assert.That(SecurityHeadersMiddleware.ContentSecurityPolicy, Does.Contain("default-src 'self'"));
    }

    [Test]
    public void ContentSecurityPolicy_Constant_ContainsFrameAncestorsNone()
    {
        Assert.That(SecurityHeadersMiddleware.ContentSecurityPolicy, Does.Contain("frame-ancestors 'none'"));
    }

    [Test]
    public void ContentSecurityPolicy_Constant_ContainsBaseUriSelf()
    {
        Assert.That(SecurityHeadersMiddleware.ContentSecurityPolicy, Does.Contain("base-uri 'self'"));
    }

    [Test]
    public void ContentSecurityPolicy_Constant_ContainsFormActionSelf()
    {
        Assert.That(SecurityHeadersMiddleware.ContentSecurityPolicy, Does.Contain("form-action 'self'"));
    }

    [Test]
    public void ContentSecurityPolicy_Constant_ContainsScriptSrcSelfUnsafeInline()
    {
        Assert.That(SecurityHeadersMiddleware.ContentSecurityPolicy, Does.Contain("script-src 'self' 'unsafe-inline'"));
    }

    [Test]
    public void ContentSecurityPolicy_Constant_ContainsStyleSrcSelfUnsafeInline()
    {
        Assert.That(SecurityHeadersMiddleware.ContentSecurityPolicy, Does.Contain("style-src 'self' 'unsafe-inline'"));
    }

    [Test]
    public void ContentSecurityPolicy_Constant_ContainsConnectSrcForWebSockets()
    {
        Assert.That(SecurityHeadersMiddleware.ContentSecurityPolicy, Does.Contain("connect-src 'self' wss: ws:"));
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading.Tasks;
using JIM.Web.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// When sign-in cannot reach or trust the identity provider, the portal explains why on the sign-in unavailable page
/// instead of throwing a generic error (#2363). Only that failure is caught, and only for the browser's own requests:
/// the API answers for itself, and any other exception still reaches the error page.
/// </summary>
[TestFixture]
public class IdentityProviderUnavailableMiddlewareTests
{
    private static readonly Exception ConfigurationUnavailable = new InvalidOperationException(
        "IDX20803: Unable to obtain configuration from: 'https://adfs.corp.contoso.com/adfs/.well-known/openid-configuration'.",
        new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback.")));

    private static readonly Exception TokenRedemptionRefused = new AuthenticationFailureException(
        "An error was encountered while handling the remote login.",
        new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback.")));

    [Test]
    public async Task InvokeAsync_PortalPageCannotReachTheIdentityProvider_RedirectsToTheExplanationAsync()
    {
        var context = Request("GET", "/admin/certificates");

        await new IdentityProviderUnavailableMiddleware(_ => throw ConfigurationUnavailable).InvokeAsync(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status302Found));
            Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo(IdentityProviderUnavailableMiddleware.PagePath));
        }
    }

    [Test]
    public async Task InvokeAsync_CallbackCannotRedeemTheCodeOverAnUntrustedConnection_RedirectsAsync()
    {
        // The identity provider posts the code back (form_post), so the callback is a POST.
        var context = Request("POST", "/signin-oidc");

        await new IdentityProviderUnavailableMiddleware(_ => throw TokenRedemptionRefused).InvokeAsync(context);

        Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo(IdentityProviderUnavailableMiddleware.PagePath));
    }

    [Test]
    public void InvokeAsync_ApiRequest_LeftToTheApiAsync()
    {
        var context = Request("GET", "/api/v1/certificates");

        Assert.That(async () => await new IdentityProviderUnavailableMiddleware(_ => throw ConfigurationUnavailable).InvokeAsync(context),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void InvokeAsync_UnrelatedException_StillReachesTheErrorPage()
    {
        var context = Request("GET", "/admin/certificates");

        Assert.That(async () => await new IdentityProviderUnavailableMiddleware(_ => throw new InvalidOperationException("Sequence contains no elements")).InvokeAsync(context),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void InvokeAsync_PostToAnyOtherPath_NotRedirected()
    {
        var context = Request("POST", "/admin/certificates");

        Assert.That(async () => await new IdentityProviderUnavailableMiddleware(_ => throw ConfigurationUnavailable).InvokeAsync(context),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public async Task InvokeAsync_NoException_PassesThroughAsync()
    {
        var context = Request("GET", "/");

        await new IdentityProviderUnavailableMiddleware(c => { c.Response.StatusCode = 200; return Task.CompletedTask; }).InvokeAsync(context);

        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
    }

    private static DefaultHttpContext Request(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }
}

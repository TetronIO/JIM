// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Threading.Tasks;
using JIM.Web.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="HttpsRedirectionExtensions.UseHttpsRedirectionExceptHealthProbes"/>. In production JIM.Web
/// serves HTTPS on 8443 and plain HTTP on the container's loopback for the health probes only. A probe answered
/// with a redirect counts as healthy (<c>curl -f</c> fails only on a 4xx or 5xx), so a service that is not ready
/// would report healthy. The pipeline is built with the same server addresses as production, so the redirection
/// middleware finds its HTTPS port the way it does there.
/// </summary>
[TestFixture]
public class HttpsRedirectionExtensionsTests
{
    [TestCase("/api/v1/health")]
    [TestCase("/api/v1/health/ready")]
    [TestCase("/api/v1/health/live")]
    [TestCase("/api/v1/health/version")]
    [TestCase("/API/V1/Health/Ready")]
    public async Task UseHttpsRedirectionExceptHealthProbes_HealthProbeOverHttp_ReachesTheEndpoint(string path)
    {
        var context = await SendAsync("http", path);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
    }

    [TestCase("/")]
    [TestCase("/api/v1/metaverse/objects")]
    [TestCase("/api/v1/healthcheck")]
    public async Task UseHttpsRedirectionExceptHealthProbes_OtherRequestOverHttp_RedirectsToHttps(string path)
    {
        var context = await SendAsync("http", path);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status307TemporaryRedirect));
        Assert.That(context.Response.Headers.Location.ToString(), Is.EqualTo($"https://localhost:8443{path}"));
    }

    [Test]
    public async Task UseHttpsRedirectionExceptHealthProbes_RequestOverHttps_ReachesTheEndpoint()
    {
        var context = await SendAsync("https", "/");

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
    }

    /// <summary>
    /// Sends one request through the redirection middleware to an endpoint that always answers 503, as the
    /// readiness endpoint does when JIM is not ready.
    /// </summary>
    private static async Task<HttpContext> SendAsync(string scheme, string path)
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        var addresses = new ServerAddressesFeature();
        addresses.Addresses.Add("https://+:8443");
        addresses.Addresses.Add("http://localhost:8080");
        var serverFeatures = new FeatureCollection();
        serverFeatures.Set<IServerAddressesFeature>(addresses);

        var app = new ApplicationBuilder(services, serverFeatures);
        app.UseHttpsRedirectionExceptHealthProbes();
        app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        });

        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Scheme = scheme;
        httpContext.Request.Host = new HostString("localhost", scheme == "https" ? 8443 : 8080);
        httpContext.Request.Path = path;

        await app.Build()(httpContext);
        return httpContext;
    }
}

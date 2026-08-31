// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Pins ASP.NET Core's <see cref="ForwardedHeadersMiddleware"/> behaviour that JIM's <c>JIM_TRUSTED_PROXIES</c>
/// support depends on: a proxy configured by its plain IPv4 address (which is what <c>TrustedProxyParser</c>
/// produces) must still be recognised when Kestrel's dual-stack socket reports the connection as an IPv4-mapped
/// IPv6 address (<c>::ffff:a.b.c.d</c>). The Microsoft documentation is ambiguous on this (it suggests the
/// mapped form may need configuring explicitly), but the middleware unmaps before matching; these tests fail
/// the build if a framework upgrade ever changes that, at which point <c>TrustedProxyParser</c> must start
/// registering both forms.
/// </summary>
[TestFixture]
public class ForwardedHeadersMappedAddressTests
{
    [Test]
    public async Task ForwardedHeaders_KnownProxyConfiguredAsPlainIpv4_MatchesIpv4MappedRemoteAddressAsync()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Add(IPAddress.Parse("10.0.0.5"));

        var context = BuildContext(remoteIp: "::ffff:10.0.0.5", forwardedFor: "203.0.113.9");

        await InvokeMiddlewareAsync(options, context);

        Assert.That(context.Connection.RemoteIpAddress, Is.EqualTo(IPAddress.Parse("203.0.113.9")));
    }

    [Test]
    public async Task ForwardedHeaders_KnownNetworkConfiguredAsIpv4Cidr_MatchesIpv4MappedRemoteAddressAsync()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));

        // The Docker bridge gateway shape: an IPv4 network entry must cover the mapped form of an address in it.
        var context = BuildContext(remoteIp: "::ffff:172.19.0.1", forwardedFor: "203.0.113.9");

        await InvokeMiddlewareAsync(options, context);

        Assert.That(context.Connection.RemoteIpAddress, Is.EqualTo(IPAddress.Parse("203.0.113.9")));
    }

    [Test]
    public async Task ForwardedHeaders_UnknownProxy_DoesNotRewriteRemoteAddressAsync()
    {
        // The control case: proving the two tests above pass because of the known-proxy match, not because the
        // middleware rewrites unconditionally.
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Add(IPAddress.Parse("10.0.0.5"));

        var context = BuildContext(remoteIp: "::ffff:192.0.2.44", forwardedFor: "203.0.113.9");

        await InvokeMiddlewareAsync(options, context);

        Assert.That(context.Connection.RemoteIpAddress, Is.EqualTo(IPAddress.Parse("::ffff:192.0.2.44")));
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static DefaultHttpContext BuildContext(string remoteIp, string forwardedFor)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        return context;
    }

    private static async Task InvokeMiddlewareAsync(ForwardedHeadersOptions options, HttpContext context)
    {
        var middleware = new ForwardedHeadersMiddleware(
            next: _ => Task.CompletedTask,
            loggerFactory: NullLoggerFactory.Instance,
            options: Options.Create(options));
        await middleware.Invoke(context);
    }
}

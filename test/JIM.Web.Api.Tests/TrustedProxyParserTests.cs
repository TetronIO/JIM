// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Web.Middleware.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="TrustedProxyParser"/>, which parses the optional <c>JIM_TRUSTED_PROXIES</c> environment
/// variable into the proxy/network collections <c>ForwardedHeadersOptions</c> needs.
/// </summary>
[TestFixture]
public class TrustedProxyParserTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Parse_UnsetOrBlank_ReturnsEmpty(string? value)
    {
        var result = TrustedProxyParser.Parse(value);

        Assert.That(result.IsEmpty, Is.True);
    }

    [Test]
    public void Parse_SingleIpAddress_AddsToKnownProxies()
    {
        var result = TrustedProxyParser.Parse("10.0.0.1");

        Assert.That(result.KnownProxies, Is.EqualTo(new[] { IPAddress.Parse("10.0.0.1") }));
        Assert.That(result.KnownNetworks, Is.Empty);
    }

    [Test]
    public void Parse_CidrNetwork_AddsToKnownNetworks()
    {
        var result = TrustedProxyParser.Parse("172.16.0.0/12");

        Assert.That(result.KnownNetworks, Has.Count.EqualTo(1));
        Assert.That(result.KnownProxies, Is.Empty);
    }

    [Test]
    public void Parse_MixedCommaSeparatedList_PopulatesBothCollections()
    {
        var result = TrustedProxyParser.Parse("10.0.0.1, 172.16.0.0/12 ,192.168.1.5");

        Assert.That(result.KnownProxies, Has.Count.EqualTo(2));
        Assert.That(result.KnownNetworks, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_UnparsableEntry_IsSkippedWithoutThrowing()
    {
        Assert.DoesNotThrow(() =>
        {
            var result = TrustedProxyParser.Parse("not-an-ip, 10.0.0.1");
            Assert.That(result.KnownProxies, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void Parse_UnparsableCidrEntry_IsSkippedWithoutThrowing()
    {
        var result = TrustedProxyParser.Parse("10.0.0.0/999");

        Assert.That(result.KnownNetworks, Is.Empty);
    }
}

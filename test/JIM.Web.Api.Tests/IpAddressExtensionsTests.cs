// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Web.Extensions;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="IpAddressExtensions.ToNormalisedString"/>: the single place client IP addresses are
/// turned into strings for audit records, rate limit partition keys and logs. Kestrel listens on a dual-stack
/// IPv6 socket, so IPv4 clients arrive as IPv4-mapped IPv6 addresses (<c>::ffff:203.0.113.7</c>); the extension
/// unmaps them so the recorded form is the plain IPv4 address regardless of socket mode or proxy topology.
/// </summary>
[TestFixture]
public class IpAddressExtensionsTests
{
    [Test]
    public void ToNormalisedString_NullAddress_ReturnsNull()
    {
        // Static-form call: CodeQL reads extension syntax on a null receiver as a null dereference, but a null
        // receiver is precisely the case under test.
        Assert.That(IpAddressExtensions.ToNormalisedString(null), Is.Null);
    }

    [Test]
    public void ToNormalisedString_PlainIpv4_ReturnsUnchanged()
    {
        var address = IPAddress.Parse("203.0.113.7");

        Assert.That(address.ToNormalisedString(), Is.EqualTo("203.0.113.7"));
    }

    [Test]
    public void ToNormalisedString_Ipv4MappedIpv6_ReturnsPlainIpv4()
    {
        // What a dual-stack Kestrel socket reports for an IPv4 client (e.g. the Docker bridge gateway).
        var address = IPAddress.Parse("::ffff:172.19.0.1");

        Assert.That(address.ToNormalisedString(), Is.EqualTo("172.19.0.1"));
    }

    [Test]
    public void ToNormalisedString_RealIpv6_ReturnsUnchanged()
    {
        var address = IPAddress.Parse("2001:db8::1");

        Assert.That(address.ToNormalisedString(), Is.EqualTo("2001:db8::1"));
    }

    [Test]
    public void ToNormalisedString_Ipv6Loopback_ReturnsUnchanged()
    {
        // ::1 is a genuine IPv6 address, not a mapped one; it must not be rewritten to 127.0.0.1.
        Assert.That(IPAddress.IPv6Loopback.ToNormalisedString(), Is.EqualTo("::1"));
    }
}

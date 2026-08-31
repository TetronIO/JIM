// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;

namespace JIM.Web.Extensions;

/// <summary>
/// Extension methods for turning a client <see cref="IPAddress"/> into the string JIM records and partitions by.
/// </summary>
public static class IpAddressExtensions
{
    /// <summary>
    /// Returns the address as a string with any IPv4-mapped IPv6 form (<c>::ffff:a.b.c.d</c>) unmapped to the
    /// plain IPv4 address. Kestrel listens on a dual-stack IPv6 socket, so IPv4 clients reaching JIM directly
    /// arrive in the mapped form, while a ForwardedHeaders rewrite from <c>X-Forwarded-For</c> yields the plain
    /// form; normalising here keeps audit records readable and rate limit partition keys consistent across
    /// deployment topologies. Genuine IPv6 addresses and null pass through unchanged.
    /// </summary>
    public static string? ToNormalisedString(this IPAddress? address)
    {
        if (address == null)
            return null;

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
    }
}

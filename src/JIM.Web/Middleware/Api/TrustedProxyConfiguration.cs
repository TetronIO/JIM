// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// The parsed result of <see cref="TrustedProxyParser.Parse"/>: the exact proxy IP addresses and/or CIDR networks
/// JIM should trust to supply forwarded headers (X-Forwarded-For / X-Forwarded-Proto).
/// </summary>
public sealed record TrustedProxyConfiguration(IReadOnlyList<IPAddress> KnownProxies, IReadOnlyList<IPNetwork> KnownNetworks)
{
    public static readonly TrustedProxyConfiguration Empty = new([], []);

    /// <summary>True when no proxies or networks were configured (nothing to trust).</summary>
    public bool IsEmpty => KnownProxies.Count == 0 && KnownNetworks.Count == 0;
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using JIM.Utilities;
using Serilog;

namespace JIM.Web.Middleware.Api;

/// <summary>
/// Parses the optional <c>JIM_TRUSTED_PROXIES</c> environment variable (a comma-separated list of IP addresses
/// and/or CIDR networks, e.g. <c>"10.0.0.1,172.16.0.0/12"</c>) into the collections
/// <c>ForwardedHeadersOptions.KnownProxies</c>/<c>KnownIPNetworks</c> need. A bare address is trusted as an exact
/// match (<see cref="TrustedProxyConfiguration.KnownProxies"/>); an entry containing <c>/</c> is parsed as a CIDR
/// network (<see cref="TrustedProxyConfiguration.KnownNetworks"/>). Unparsable entries are logged and skipped
/// rather than failing startup, since a single typo in a deployment's proxy list should not prevent JIM starting.
/// </summary>
public static class TrustedProxyParser
{
    public static TrustedProxyConfiguration Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return TrustedProxyConfiguration.Empty;

        var proxies = new List<IPAddress>();
        var networks = new List<IPNetwork>();

        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Contains('/'))
            {
                if (IPNetwork.TryParse(entry, out var network))
                    networks.Add(network);
                else
                    Log.Warning("TrustedProxyParser: Could not parse '{Entry}' as a CIDR network; ignoring.", LogSanitiser.Sanitise(entry));
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                proxies.Add(address);
            }
            else
            {
                Log.Warning("TrustedProxyParser: Could not parse '{Entry}' as an IP address; ignoring.", LogSanitiser.Sanitise(entry));
            }
        }

        return new TrustedProxyConfiguration(proxies, networks);
    }
}

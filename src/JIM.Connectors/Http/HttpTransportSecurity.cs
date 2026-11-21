// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Connectors.Http;

/// <summary>
/// The transport rule every HTTP connector applies to an address it sends data or credentials to: HTTPS, or HTTP
/// to a loopback address only, so a local test service or authorisation server still works.
/// </summary>
internal static class HttpTransportSecurity
{
    /// <summary>
    /// True when the address is absolute and either HTTPS, or HTTP to a loopback host.
    /// </summary>
    public static bool IsPermitted(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
            return false;

        return uri.Scheme == Uri.UriSchemeHttps
               || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using JIM.Models.Exceptions;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Where the SCIM service provider's certificate is read from, and what the probe's remediation text calls the far
/// end. Recognising that a request failed on the certificate at all is <see cref="Http.TlsTrustFailure"/>'s, shared
/// with the other HTTP Connectors.
/// </summary>
internal static class ScimCertificateDiagnosis
{
    /// <summary>What the probe calls the far end in its remediation text.</summary>
    public const string ServerDescription = "SCIM service provider";

    /// <summary>The secure transport the probe names when nothing was presented.</summary>
    public const string SecureTransportName = "HTTPS";

    /// <summary>
    /// The host and port the probe should look at, from the Base URL the administrator configured.
    /// </summary>
    /// <returns>The endpoint, or null where the Base URL is unusable or is not HTTPS, in which case there is no certificate to explain.</returns>
    public static (string Host, int Port)? ResolveEndpoint(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        return (uri.Host, uri.IsDefaultPort ? 443 : uri.Port);
    }
}

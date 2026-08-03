// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Authentication;
using JIM.Models.Connectors;
using JIM.Models.Exceptions;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Decides whether a failed SCIM request failed because of the service provider's TLS certificate, and
/// names the far end for the probe's remediation text.
/// <para>
/// <see cref="HttpClient"/> reports a refused certificate as "The SSL connection could not be
/// established", which an administrator cannot tell from a firewall. Recognising it is what lets JIM
/// show the certificate instead, so the administrator can decide to trust that specific certificate
/// rather than reaching for the setting that turns validation off altogether.
/// </para>
/// </summary>
internal static class ScimCertificateDiagnosis
{
    /// <summary>What the probe calls the far end in its remediation text.</summary>
    public const string ServerDescription = "SCIM service provider";

    /// <summary>The secure transport the probe names when nothing was presented.</summary>
    public const string SecureTransportName = "HTTPS";

    /// <summary>
    /// Whether the failure is a TLS trust failure rather than anything else.
    /// </summary>
    /// <remarks>
    /// The chain is walked because the connector wraps transport failures in its own exception, and the
    /// handshake failure itself arrives nested inside <see cref="HttpRequestException"/>. Only an
    /// <see cref="AuthenticationException"/> counts: a refused connection, a timeout or an HTTP error
    /// are different problems and must keep their own message.
    /// </remarks>
    public static bool LooksLikeACertificateFailure(Exception? exception)
    {
        while (exception != null)
        {
            if (exception is AuthenticationException)
                return true;

            exception = exception.InnerException;
        }

        return false;
    }

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

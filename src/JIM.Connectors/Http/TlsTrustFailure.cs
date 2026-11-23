// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Authentication;

namespace JIM.Connectors.Http;

/// <summary>
/// Recognises a failed HTTPS request that failed because the server's certificate was refused.
/// </summary>
/// <remarks>
/// <see cref="HttpClient"/> reports a refused certificate as "The SSL connection could not be established", which an
/// administrator cannot tell from a firewall. Recognising it is what lets JIM show the certificate instead, so the
/// administrator can decide to trust that specific certificate.
/// </remarks>
public static class TlsTrustFailure
{
    /// <summary>
    /// Whether the failure is a TLS trust failure rather than anything else.
    /// </summary>
    /// <remarks>
    /// The chain is walked because Connectors wrap transport failures in their own exceptions, and the handshake
    /// failure itself arrives nested inside <see cref="HttpRequestException"/>. Only an
    /// <see cref="AuthenticationException"/> counts: a refused connection, a timeout or an HTTP error are different
    /// problems and must keep their own message.
    /// </remarks>
    public static bool IsTlsTrustFailure(Exception? exception)
    {
        while (exception != null)
        {
            if (exception is AuthenticationException)
                return true;

            exception = exception.InnerException;
        }

        return false;
    }
}

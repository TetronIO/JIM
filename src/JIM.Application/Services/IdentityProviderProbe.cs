// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;
using JIM.Application.Interfaces;
using JIM.Models.Core;
using JIM.Models.Security;
using JIM.Utilities;

namespace JIM.Application.Services;

/// <summary>
/// Reads the identity provider's OpenID Connect configuration document over HTTPS, judging its certificate exactly
/// as sign-in does (<see cref="CertificateStoreTrust"/> over the certificates trusted for sign-in), and records what
/// it presented so an administrator can be shown the chain and choose what to trust.
/// </summary>
/// <remarks>
/// Nothing is downloaded and nothing is trusted here: an untrusted certificate refuses the handshake, as it would at
/// sign-in, and the certificates it presented are reported.
/// </remarks>
public sealed class IdentityProviderProbe : IIdentityProviderProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<IdentityProviderProbeResult> ProbeAsync(Uri metadataAddress, IReadOnlyCollection<X509Certificate2> signInCertificates, CancellationToken cancellationToken)
    {
        X509Certificate2? serverCertificate = null;
        IReadOnlyList<X509Certificate2> sentByServer = [];
        CertificateStoreTrustDecision? decision = null;

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = Timeout,
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                {
                    serverCertificate = certificate == null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    sentByServer = SentByServer(chain, serverCertificate);
                    decision = CertificateStoreTrust.Evaluate(serverCertificate, chain, errors, signInCertificates);
                    return decision.IsTrusted;
                }
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout };

        try
        {
            using var response = await client.GetAsync(metadataAddress, cancellationToken);
            response.EnsureSuccessStatusCode();
            return new IdentityProviderProbeResult
            {
                Reached = true,
                DocumentRead = true,
                Decision = decision,
                ServerCertificate = serverCertificate,
                SentByServer = sentByServer
            };
        }
        catch (HttpRequestException ex)
        {
            return new IdentityProviderProbeResult
            {
                Reached = decision != null || ex.StatusCode != null,
                Decision = decision,
                ServerCertificate = serverCertificate,
                SentByServer = sentByServer,
                Error = ex.StatusCode != null ? $"it answered {(int)ex.StatusCode.Value} {ex.StatusCode.Value}" : (ex.InnerException ?? ex).Message
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new IdentityProviderProbeResult
            {
                Reached = decision != null,
                Decision = decision,
                ServerCertificate = serverCertificate,
                SentByServer = sentByServer,
                Error = $"it did not answer within {Timeout.TotalSeconds:0} seconds"
            };
        }
    }

    /// <summary>
    /// The certificates the server sent besides its own, as the TLS stack hands them over in the chain's extra store.
    /// </summary>
    private static List<X509Certificate2> SentByServer(System.Security.Cryptography.X509Certificates.X509Chain? chain, X509Certificate2? serverCertificate)
    {
        if (chain == null)
            return [];

        return chain.ChainPolicy.ExtraStore
            .Where(c => !string.Equals(c.Thumbprint, serverCertificate?.Thumbprint, StringComparison.OrdinalIgnoreCase))
            .Select(c => X509CertificateLoader.LoadCertificate(c.RawData))
            .ToList();
    }
}

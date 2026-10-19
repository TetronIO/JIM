// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using JIM.Models.Core;
using JIM.Utilities;
using Serilog;

namespace JIM.Web.Authentication;

/// <summary>
/// Builds the HTTP handlers JIM uses to talk to its identity provider: the OpenID Connect configuration and signing
/// keys, and token redemption at sign-in (#2363).
/// </summary>
/// <remarks>
/// <para>
/// A chain the operating system trusts is accepted as it always was. A chain it does not trust is accepted when a
/// certificate the JIM certificate store holds for sign-in vouches for it (<see cref="CertificateStoreTrust"/>, the
/// same rule the Connectors apply). Nothing in the store waives a hostname mismatch or a missing certificate, and an
/// expired or badly signed chain fails however it is anchored: the store only ever adds trust.
/// </para>
/// <para>
/// Certificates trusted for Connectors only are never consulted here. Trusting the server that issues tokens is a
/// bigger decision than trusting one that serves data, so it has to be made deliberately.
/// </para>
/// </remarks>
public sealed class IdentityProviderBackchannel
{
    /// <summary>
    /// How long a connection is reused. Bounds how long removing a certificate from the store takes to affect a
    /// connection that was already open.
    /// </summary>
    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    private readonly SignInCertificateSnapshot _snapshot;

    public IdentityProviderBackchannel(SignInCertificateSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    /// <summary>
    /// A new handler for one HTTP client. Each caller gets its own, because an HTTP client disposes the handler it owns.
    /// </summary>
    public HttpMessageHandler CreateHandler()
    {
        return new SnapshotRefreshingHandler(_snapshot)
        {
            InnerHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = PooledConnectionLifetime,
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate }
            }
        };
    }

    /// <summary>
    /// Whether a handler is one this class built: lets a test prove both authentication handlers use it.
    /// </summary>
    public static bool IsBackchannelHandler(object handler) => handler is SnapshotRefreshingHandler;

    private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var serverCertificate = certificate as X509Certificate2 ?? (certificate == null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
        var decision = CertificateStoreTrust.Evaluate(serverCertificate, chain, errors, _snapshot.Certificates);

        if (decision.Outcome == CertificateStoreTrustOutcome.TrustedByJimCertificateStore)
            Log.Debug("The identity provider's TLS certificate is trusted through the JIM certificate store ({Thumbprints})", string.Join(", ", decision.VouchingThumbprints));
        else if (!decision.IsTrusted)
            Log.Warning("Refused the identity provider's TLS certificate ({Outcome}): {Subject}, issued by {Issuer}. Trust its certificate authority for sign-in in the JIM certificate store.",
                decision.Outcome, LogSanitiser.Sanitise(serverCertificate?.Subject), LogSanitiser.Sanitise(serverCertificate?.Issuer));

        return decision.IsTrusted;
    }

    /// <summary>
    /// Brings the sign-in certificates up to date before each request, so the synchronous validation callback reads
    /// the store as it stands without querying it.
    /// </summary>
    private sealed class SnapshotRefreshingHandler(SignInCertificateSnapshot snapshot) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await snapshot.RefreshAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}

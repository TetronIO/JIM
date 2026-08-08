// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Connectors;
using JIM.Utilities;
using Serilog;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
namespace JIM.Connectors;

/// <summary>
/// Connects to a server over TLS purely to look at the certificate it presents, so a refused connection can be
/// reported as the certificate problem it is.
/// </summary>
/// <remarks>
/// This exists because the platform LDAP client tells JIM nothing when it refuses a certificate: the failure arrives
/// as "the server is unavailable", indistinguishable from an unreachable host. .NET's own TLS stack hands the
/// presented certificate to a callback, so JIM can look at a certificate it does not trust without ever trusting it:
/// the probe always refuses the connection, and is only ever used to explain a failure that already happened.
/// </remarks>
public static class ServerCertificateProbe
{
    /// <summary>
    /// Fetches the certificate a server presents and works out why it would be refused.
    /// </summary>
    /// <param name="host">Host being connected to, as configured on the Connected System. The certificate's names are checked against this.</param>
    /// <param name="port">Port being connected to.</param>
    /// <param name="trustedCertificates">Certificates from the JIM certificate store, treated as additional trust anchors when judging the issuer.</param>
    /// <param name="timeout">How long to wait for the connection and handshake.</param>
    /// <param name="logger">Logger for the calling operation.</param>
    /// <param name="serverDescription">What to call the far end in the remediation text, for example "directory server" or "SCIM service provider".</param>
    /// <param name="secureTransportName">The secure transport being attempted, for example "LDAPS" or "HTTPS", named in the remediation text.</param>
    /// <returns>What the server presented and why it fails, or null when the server could not be reached at all, which is a different problem.</returns>
    public static ServerCertificateDiagnostic? Probe(
        string host,
        int port,
        IReadOnlyCollection<X509Certificate2> trustedCertificates,
        TimeSpan timeout,
        ILogger logger,
        string serverDescription = "directory server",
        string secureTransportName = "LDAPS")
    {
        return Read(host, port, trustedCertificates, timeout, logger, serverDescription, secureTransportName)?.Diagnostic;
    }

    /// <summary>
    /// Fetches the certificate a server presents, returning both the description an administrator is shown and the
    /// certificates themselves, so that an administrator who decides to trust what they were just shown has exactly
    /// that certificate added rather than a copy taken earlier.
    /// </summary>
    /// <inheritdoc cref="Probe" path="/param"/>
    /// <returns>What the server presented, or null when it could not be reached at all, which is a different problem.</returns>
    public static ServerCertificateReading? Read(
        string host,
        int port,
        IReadOnlyCollection<X509Certificate2> trustedCertificates,
        TimeSpan timeout,
        ILogger logger,
        string serverDescription = "directory server",
        string secureTransportName = "LDAPS")
    {
        X509Certificate2? presented = null;
        var presentedChain = new List<X509Certificate2>();
        var readAt = DateTime.UtcNow;

        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(host, port).Wait(timeout))
            {
                logger.Debug("ServerCertificateProbe: no response from {Host}:{Port} within the timeout, so this is a connectivity problem rather than a certificate one", LogSanitiser.Sanitise(host), port);
                return null;
            }

            using var sslStream = new SslStream(client.GetStream(), false, (_, certificate, chain, _) =>
            {
                if (certificate != null)
                    presented = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());

                // The chain the platform built from what the server sent is disposed as soon as this callback
                // returns, so copy what is needed out of it now. Its elements are what let JIM offer the issuer,
                // which is the choice that survives the server's certificate being renewed.
                if (chain != null)
                    presentedChain.AddRange(chain.ChainElements.Select(element => X509CertificateLoader.LoadCertificate(element.Certificate.RawData)));

                // Always refuse. Nothing is being connected to here; the handshake exists only to see the certificate.
                return false;
            });

            try
            {
                sslStream.AuthenticateAsClient(host);
            }
            catch (AuthenticationException)
            {
                // Expected: the callback above refuses every certificate, by design.
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or AggregateException or ObjectDisposedException)
        {
            logger.Debug(ex, "ServerCertificateProbe: could not reach {Host}:{Port} to examine its certificate", LogSanitiser.Sanitise(host), port);
            return null;
        }

        try
        {
            if (presented == null)
            {
                return new ServerCertificateReading
                {
                    Diagnostic = new ServerCertificateDiagnostic
                    {
                        Host = host,
                        Port = port,
                        FailureReason = ServerCertificateFailureReason.NoCertificatePresented,
                        Remediation = $"The {serverDescription} offered no certificate. Check that it is configured for {secureTransportName} on this port."
                    }
                };
            }

            var issuer = FindIssuer(presented, presentedChain);
            var diagnostic = Describe(presented, host, port, trustedCertificates, serverDescription);
            diagnostic.IssuerThumbprint = issuer?.Thumbprint;

            return new ServerCertificateReading
            {
                Diagnostic = diagnostic,
                Chain = new PresentedServerCertificateChain
                {
                    Host = host,
                    Port = port,
                    ReadAt = readAt,
                    IsSelfSigned = diagnostic.IsSelfSigned,
                    Leaf = Describe(presented),
                    Issuer = issuer == null ? null : Describe(issuer)
                }
            };
        }
        finally
        {
            presented?.Dispose();
            foreach (var certificate in presentedChain)
                certificate.Dispose();
        }
    }

    /// <summary>
    /// The certificate that issued the presented one, from what the server sent alongside it.
    /// </summary>
    /// <remarks>
    /// A self-signed certificate is its own issuer, so it is deliberately not returned here: offering an
    /// administrator "the authority that issued this" when that is the same certificate would be a choice between
    /// one thing and itself.
    /// </remarks>
    private static X509Certificate2? FindIssuer(X509Certificate2 presented, List<X509Certificate2> presentedChain)
    {
        if (string.Equals(presented.Subject, presented.Issuer, StringComparison.Ordinal))
            return null;

        return presentedChain.FirstOrDefault(candidate =>
            string.Equals(candidate.Subject, presented.Issuer, StringComparison.Ordinal) &&
            !string.Equals(candidate.Thumbprint, presented.Thumbprint, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Copies a certificate into the transferable form the trust decision acts on, DER encoded.
    /// </summary>
    private static PresentedServerCertificate Describe(X509Certificate2 certificate)
    {
        return new PresentedServerCertificate
        {
            Thumbprint = certificate.Thumbprint,
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            ValidFrom = certificate.NotBefore.ToUniversalTime(),
            ValidTo = certificate.NotAfter.ToUniversalTime(),
            Data = certificate.Export(X509ContentType.Cert)
        };
    }

    /// <summary>
    /// Works out which check the certificate fails, in the order an administrator would act on them: a name mismatch
    /// is reported ahead of an untrusted issuer, because adding the certificate to the JIM certificate store fixes
    /// the second and not the first.
    /// </summary>
    private static ServerCertificateDiagnostic Describe(
        X509Certificate2 certificate,
        string host,
        int port,
        IReadOnlyCollection<X509Certificate2> trustedCertificates,
        string serverDescription)
    {
        var diagnostic = new ServerCertificateDiagnostic
        {
            Host = host,
            Port = port,
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            SubjectAlternativeNames = GetSubjectAlternativeNames(certificate),
            ValidFrom = certificate.NotBefore.ToUniversalTime(),
            ValidTo = certificate.NotAfter.ToUniversalTime(),
            Thumbprint = certificate.Thumbprint,
            SignatureAlgorithm = certificate.SignatureAlgorithm.FriendlyName,
            IsSelfSigned = string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal)
        };

        var now = DateTime.UtcNow;
        if (diagnostic.ValidTo.HasValue && diagnostic.ValidTo.Value < now)
        {
            diagnostic.FailureReason = ServerCertificateFailureReason.Expired;
            diagnostic.Remediation = $"The certificate expired. Renew it on the {serverDescription}; trusting its issuer does not waive the expiry date.";
            return diagnostic;
        }

        if (diagnostic.ValidFrom.HasValue && diagnostic.ValidFrom.Value > now)
        {
            diagnostic.FailureReason = ServerCertificateFailureReason.NotYetValid;
            diagnostic.Remediation = $"The certificate is not valid yet, which usually means the clocks on JIM and the {serverDescription} disagree.";
            return diagnostic;
        }

        if (!certificate.MatchesHostname(host, false, false))
        {
            diagnostic.FailureReason = ServerCertificateFailureReason.NameMismatch;
            diagnostic.Remediation = $"The certificate was not issued for '{host}'. Adding it to the JIM certificate store will not help; connect using a name the certificate carries, giving the JIM containers a host entry for it if that name cannot be resolved.";
            return diagnostic;
        }

        if (!ChainsToATrustedIssuer(certificate, trustedCertificates))
        {
            diagnostic.FailureReason = ServerCertificateFailureReason.UntrustedIssuer;
            diagnostic.Remediation = diagnostic.IsSelfSigned
                ? $"The certificate is self-signed and not trusted. Add this certificate to the JIM certificate store (Admin > Certificates) to trust this {serverDescription}."
                : "The issuing certificate authority is not trusted. Add it, and any intermediates, to the JIM certificate store (Admin > Certificates).";
            return diagnostic;
        }

        // Deliberately not judged on .NET's own policy errors: those are reported against the operating system's
        // trust anchors alone, so a certificate that JIM's certificate store legitimately vouches for still arrives
        // with a chain error. Everything JIM validates has now been checked, with those anchors taken into account.
        diagnostic.FailureReason = ServerCertificateFailureReason.None;
        return diagnostic;
    }

    /// <summary>
    /// Builds the certificate's chain with the JIM certificate store supplied as additional trust anchors, mirroring
    /// what the platform LDAP client is given.
    /// </summary>
    private static bool ChainsToATrustedIssuer(X509Certificate2 certificate, IReadOnlyCollection<X509Certificate2> trustedCertificates)
    {
        using var chain = new X509Chain();

        // Air-gapped deployments cannot reach a revocation list or responder, matching the LDAP connection itself.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;

        foreach (var trustedCertificate in trustedCertificates)
        {
            chain.ChainPolicy.CustomTrustStore.Add(trustedCertificate);
            chain.ChainPolicy.ExtraStore.Add(trustedCertificate);
        }

        if (chain.Build(certificate))
            return true;

        // Nothing in the JIM certificate store vouched for it; fall back to the operating system's own anchors.
        using var systemChain = new X509Chain();
        systemChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        systemChain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        return systemChain.Build(certificate);
    }

    /// <summary>
    /// Reads the names the certificate was issued for out of its subject alternative name extension.
    /// </summary>
    private static List<string> GetSubjectAlternativeNames(X509Certificate2 certificate)
    {
        var names = new List<string>();

        foreach (var extension in certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            names.AddRange(extension.EnumerateDnsNames());
            names.AddRange(extension.EnumerateIPAddresses().Select(ip => ip.ToString()));
        }

        return names;
    }
}

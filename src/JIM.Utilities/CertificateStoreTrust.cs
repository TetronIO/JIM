// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using JIM.Models.Core;

namespace JIM.Utilities;

/// <summary>
/// Whether the JIM certificate store vouches for a server's certificate.
/// </summary>
/// <remarks>
/// <para>
/// The rule is the one the SCIM Connector has always applied: the chain has to be otherwise valid, and some
/// certificate in it has to be in the store. Any one will do: the root, an intermediate certificate authority, or the
/// server's own certificate. Trusting higher up only lasts longer.
/// </para>
/// <para>
/// <see cref="X509ChainTrustMode.CustomRootTrust"/> is deliberately not used. It accepts only a self-signed
/// certificate as an anchor, so a store holding an intermediate or the server's own certificate was refused by the
/// SQL Connector and reported as untrusted by the certificate card, while the SCIM and LDAP Connectors accepted
/// the same store (#1914).
/// </para>
/// <para>
/// Nothing is downloaded: the chain is built from what the server sent and what the store holds, which is all a
/// connection in an air-gapped deployment has, and all the LDAP Connector's TLS stack ever uses. Revocation is not
/// checked, matching the connections themselves.
/// </para>
/// <para>
/// Shared by the Connectors and by sign-in (the identity provider's back-channel, #2363), so both judge a chain the
/// same way. Which certificates are passed in is the caller's business: each purpose gets only the certificates
/// trusted for it.
/// </para>
/// </remarks>
public static class CertificateStoreTrust
{
    /// <param name="certificate">The server's certificate.</param>
    /// <param name="sentByServer">The other certificates the server sent, so its intermediates can link the chain.</param>
    /// <param name="trustedCertificates">The JIM certificate store.</param>
    public static bool VouchesFor(
        X509Certificate2 certificate,
        IEnumerable<X509Certificate2> sentByServer,
        IReadOnlyCollection<X509Certificate2> trustedCertificates)
    {
        return VouchingThumbprints(certificate, sentByServer, trustedCertificates).Count > 0;
    }

    /// <summary>
    /// Judges a certificate the way a TLS validation callback must: the operating system's verdict stands when it
    /// trusts the chain, the store can answer only for an unknown certificate authority, and nothing in the store
    /// waives a name mismatch or a missing certificate.
    /// </summary>
    /// <param name="certificate">The server's certificate, as the callback received it.</param>
    /// <param name="chain">The chain the callback received; the certificates the server sent are in its extra store.</param>
    /// <param name="errors">The errors the operating system found.</param>
    /// <param name="trustedCertificates">The store's certificates trusted for the purpose at hand.</param>
    public static CertificateStoreTrustDecision Evaluate(
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors,
        IReadOnlyCollection<X509Certificate2> trustedCertificates)
    {
        if (errors == SslPolicyErrors.None)
            return new CertificateStoreTrustDecision(CertificateStoreTrustOutcome.TrustedByOperatingSystem, []);

        if (certificate == null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return new CertificateStoreTrustDecision(CertificateStoreTrustOutcome.NoCertificate, []);

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            return new CertificateStoreTrustDecision(CertificateStoreTrustOutcome.NameMismatch, []);

        var sentByServer = chain == null
            ? []
            : chain.ChainPolicy.ExtraStore.Cast<X509Certificate2>()
                .Concat(chain.ChainElements.Select(element => element.Certificate))
                .Where(c => !string.Equals(c.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var vouching = VouchingThumbprints(certificate, sentByServer, trustedCertificates);
        return vouching.Count > 0
            ? new CertificateStoreTrustDecision(CertificateStoreTrustOutcome.TrustedByJimCertificateStore, vouching)
            : new CertificateStoreTrustDecision(CertificateStoreTrustOutcome.Untrusted, []);
    }

    /// <summary>
    /// The thumbprints of the store's certificates in the chain, leaf first; empty when the chain is invalid or
    /// reaches nothing in the store.
    /// </summary>
    private static List<string> VouchingThumbprints(
        X509Certificate2 certificate,
        IEnumerable<X509Certificate2> sentByServer,
        IReadOnlyCollection<X509Certificate2> trustedCertificates)
    {
        if (trustedCertificates.Count == 0)
            return [];

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;

        // Waive the unknown authority, which is exactly what the store answers for, and nothing else: an expired
        // certificate authority or a broken signature still fails.
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.ExtraStore.AddRange(sentByServer.Concat(trustedCertificates).ToArray());

        if (!chain.Build(certificate))
            return [];

        // Building under AllowUnknownCertificateAuthority proves the chain is valid, not that it is trusted: it has to
        // reach something an administrator added.
        var trusted = trustedCertificates.Select(c => c.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return chain.ChainElements
            .Select(element => element.Certificate.Thumbprint)
            .Where(trusted.Contains)
            .ToList();
    }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography.X509Certificates;

namespace JIM.Connectors;

/// <summary>
/// Whether the JIM certificate store vouches for a server's certificate.
/// </summary>
/// <remarks>
/// <para>
/// The rule is the one the SCIM Connector has always applied (<see cref="SCIM.ScimCertificateValidator"/>): the chain
/// has to be otherwise valid, and some certificate in it has to be in the store. Any one will do: the root, an
/// intermediate certificate authority, or the server's own certificate. Trusting higher up only lasts longer.
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
/// </remarks>
internal static class JimCertificateStoreTrust
{
    /// <param name="certificate">The server's certificate.</param>
    /// <param name="sentByServer">The other certificates the server sent, so its intermediates can link the chain.</param>
    /// <param name="trustedCertificates">The JIM certificate store.</param>
    internal static bool VouchesFor(
        X509Certificate2 certificate,
        IEnumerable<X509Certificate2> sentByServer,
        IReadOnlyCollection<X509Certificate2> trustedCertificates)
    {
        if (trustedCertificates.Count == 0)
            return false;

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;

        // Waive the unknown authority, which is exactly what the store answers for, and nothing else: an expired
        // certificate authority or a broken signature still fails.
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.ExtraStore.AddRange(sentByServer.Concat(trustedCertificates).ToArray());

        if (!chain.Build(certificate))
            return false;

        // Building under AllowUnknownCertificateAuthority proves the chain is valid, not that it is trusted: it has to
        // reach something an administrator added.
        var trusted = trustedCertificates.Select(c => c.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return chain.ChainElements.Any(element => trusted.Contains(element.Certificate.Thumbprint));
    }
}

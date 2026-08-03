// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace JIM.Connectors.SCIM;

/// <summary>
/// Decides whether to trust a SCIM endpoint's TLS certificate: the platform's own validation first,
/// then JIM's trusted certificate store, which lets a deployment trust an internal CA without
/// installing it on the host.
/// <para>
/// Extracted from the HTTP handler so the trust rules are unit-testable without a TLS handshake.
/// </para>
/// <para>
/// Deliberately stricter than <c>LdapConnector.ValidateServerCertificate</c>. JIM's store answers one
/// question, "do we trust the issuer", so it waives an unknown certificate authority and nothing else:
/// an expired certificate is not a trust-configuration gap, and a hostname mismatch is an interception
/// signal rather than a missing trust anchor. The LDAP implementation inspects chain elements without
/// checking whether the chain otherwise built, and so would accept both.
/// </para>
/// </summary>
public class ScimCertificateValidator
{
    /// <summary>
    /// The only SSL policy error JIM's trusted certificates can answer for.
    /// </summary>
    private const SslPolicyErrors WaivableErrors = SslPolicyErrors.RemoteCertificateChainErrors;

    private readonly IReadOnlyList<X509Certificate2> _trustedCertificates;
    private readonly ILogger _logger;

    /// <param name="trustedCertificates">Enabled certificates from the JIM store; may be empty.</param>
    public ScimCertificateValidator(IReadOnlyList<X509Certificate2> trustedCertificates, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(trustedCertificates);
        ArgumentNullException.ThrowIfNull(logger);

        _trustedCertificates = trustedCertificates;
        _logger = logger;
    }

    /// <summary>
    /// Validates the endpoint's certificate.
    /// </summary>
    /// <param name="certificate">The certificate the endpoint presented.</param>
    /// <param name="chain">The chain the platform built, if any. Unused; a fresh chain is built against JIM's anchors.</param>
    /// <param name="sslPolicyErrors">The platform's verdict.</param>
    /// <returns>True to accept the certificate.</returns>
    public bool Validate(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        if (certificate == null)
        {
            _logger.Warning("The SCIM endpoint presented no TLS certificate.");
            return false;
        }

        // Any error outside the waivable set (hostname mismatch, no certificate) is fatal regardless of
        // what JIM trusts, so the waivable error must be the only one present.
        if ((sslPolicyErrors & ~WaivableErrors) != 0)
        {
            _logger.Warning(
                "The SCIM endpoint's TLS certificate failed validation with {SslPolicyErrors}, which JIM's trusted certificates cannot waive. Thumbprint: {Thumbprint}",
                sslPolicyErrors, certificate.Thumbprint);
            return false;
        }

        if (_trustedCertificates.Count == 0)
        {
            _logger.Warning(
                "The SCIM endpoint's TLS certificate is not trusted by the system CA store, and no trusted certificates are configured in JIM. Thumbprint: {Thumbprint}",
                certificate.Thumbprint);
            return false;
        }

        return IsTrustedByJimStore(certificate);
    }

    /// <summary>
    /// Rebuilds the chain with JIM's certificates as additional anchors, then requires both that the
    /// chain is otherwise valid and that one of its elements is a certificate JIM trusts.
    /// </summary>
    private bool IsTrustedByJimStore(X509Certificate2 certificate)
    {
        using var jimChain = new X509Chain();

        // Revocation checking is off by design: JIM supports air-gapped deployments that cannot reach a
        // CRL or OCSP responder, and a hard failure there would block synchronisation entirely.
        jimChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        // Tolerate the unknown authority (that is exactly what JIM's store is answering for) while
        // leaving every other check, notably the validity period, in force.
        jimChain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;

        foreach (var trustedCertificate in _trustedCertificates)
            jimChain.ChainPolicy.ExtraStore.Add(trustedCertificate);

        if (!jimChain.Build(certificate))
        {
            var statuses = string.Join(", ", jimChain.ChainStatus.Select(s => s.Status));
            _logger.Warning(
                "The SCIM endpoint's TLS certificate failed chain validation against JIM's trusted certificates ({ChainStatus}). Thumbprint: {Thumbprint}",
                statuses, certificate.Thumbprint);
            return false;
        }

        // A chain that builds under AllowUnknownCertificateAuthority proves validity, not trust; the
        // chain must actually reach something JIM was told to trust.
        var trustedThumbprints = _trustedCertificates.Select(c => c.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (jimChain.ChainElements.Any(element => trustedThumbprints.Contains(element.Certificate.Thumbprint)))
        {
            _logger.Debug("The SCIM endpoint's TLS certificate was validated via JIM's trusted certificate store.");
            return true;
        }

        _logger.Warning(
            "The SCIM endpoint's TLS certificate chain does not include any certificate trusted by JIM. Thumbprint: {Thumbprint}",
            certificate.Thumbprint);
        return false;
    }
}

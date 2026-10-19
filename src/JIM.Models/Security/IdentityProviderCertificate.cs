// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Security;

/// <summary>
/// One certificate in the chain the identity provider presented, with what the JIM certificate store makes of it.
/// </summary>
public class IdentityProviderCertificate
{
    public string Subject { get; init; } = string.Empty;

    /// <summary>
    /// The subject's common name, or the whole subject where it has none: what to call the certificate.
    /// </summary>
    public string CommonName { get; init; } = string.Empty;

    public string Issuer { get; init; } = string.Empty;

    public string Thumbprint { get; init; } = string.Empty;

    public DateTime ValidFrom { get; init; }

    public DateTime ValidTo { get; init; }

    /// <summary>
    /// Whether this is the identity provider's own certificate rather than a certificate authority above it.
    /// </summary>
    public bool IsServerCertificate { get; init; }

    /// <summary>
    /// The certificate in the JIM certificate store with this thumbprint, if any, and what it is trusted for.
    /// </summary>
    public Guid? TrustedCertificateId { get; init; }

    public CertificateTrustPurpose? TrustedFor { get; init; }

    /// <summary>
    /// Whether it is in the store and trusted for sign-in, so vouches for the identity provider.
    /// </summary>
    public bool TrustedForSignIn => TrustedFor?.HasFlag(CertificateTrustPurpose.SignIn) == true;

    /// <summary>
    /// The DER encoding, so the certificate can be added to the store exactly as it was presented.
    /// </summary>
    public byte[] Data { get; init; } = [];
}

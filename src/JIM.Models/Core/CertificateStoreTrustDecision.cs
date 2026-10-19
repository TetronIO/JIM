// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// The result of judging a server's TLS certificate: whether it is trusted and, where the JIM certificate store is
/// what trusts it, which of the store's certificates vouch for it.
/// </summary>
/// <param name="Outcome">How the certificate was judged.</param>
/// <param name="VouchingThumbprints">Thumbprints of the store's certificates found in the chain, leaf first. Empty
/// unless <paramref name="Outcome"/> is <see cref="CertificateStoreTrustOutcome.TrustedByJimCertificateStore"/>.</param>
public sealed record CertificateStoreTrustDecision(CertificateStoreTrustOutcome Outcome, IReadOnlyList<string> VouchingThumbprints)
{
    /// <summary>
    /// Whether a connection should go ahead.
    /// </summary>
    public bool IsTrusted => Outcome is CertificateStoreTrustOutcome.TrustedByOperatingSystem or CertificateStoreTrustOutcome.TrustedByJimCertificateStore;
}

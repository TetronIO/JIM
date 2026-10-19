// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Security;

/// <summary>
/// The result of checking JIM's identity provider: whether its TLS certificate is trusted, by what, and the chain it
/// presented (#2363).
/// </summary>
public class IdentityProviderTrustStatus
{
    /// <summary>
    /// The number of days before expiry at which a trusted certificate counts as expiring soon, matching
    /// <see cref="TrustedCertificate.IsExpiringSoon"/>.
    /// </summary>
    public const int ExpiringSoonDays = 30;

    public IdentityProviderTrustState State { get; init; }

    /// <summary>
    /// The identity provider's authority (JIM_SSO_AUTHORITY), or null where none is configured.
    /// </summary>
    public string? Authority { get; init; }

    /// <summary>
    /// The host JIM connects to.
    /// </summary>
    public string? Host { get; init; }

    /// <summary>
    /// What trusts the certificate, where it is trusted.
    /// </summary>
    public CertificateStoreTrustOutcome? TrustedBy { get; init; }

    /// <summary>
    /// The chain as presented, the identity provider's own certificate first. Empty where no certificate was presented.
    /// </summary>
    public List<IdentityProviderCertificate> PresentedChain { get; init; } = [];

    /// <summary>
    /// The certificate authority that issued the top of the presented chain, where the identity provider did not send
    /// it (servers rarely send their root). Shown so an administrator knows which certificate to fetch from their
    /// certificate authority.
    /// </summary>
    public string? UnsentIssuer { get; init; }

    /// <summary>
    /// <see cref="UnsentIssuer"/>'s common name: what to call it.
    /// </summary>
    public string? UnsentIssuerCommonName { get; init; }

    /// <summary>
    /// The store's certificates that vouch for the identity provider now, leaf first. Empty unless the JIM certificate
    /// store is what trusts it.
    /// </summary>
    public List<IdentityProviderCertificate> VouchingCertificates { get; init; } = [];

    /// <summary>
    /// A sentence explaining the state, suitable for showing.
    /// </summary>
    public string? Message { get; init; }

    public DateTime CheckedUtc { get; init; }

    /// <summary>
    /// The certificate an administrator would most usefully trust: the highest one the identity provider presented,
    /// because a certificate authority outlasts the server certificates it issues.
    /// </summary>
    public IdentityProviderCertificate? SuggestedCertificate => PresentedChain.LastOrDefault();

    /// <summary>
    /// Whether exactly one store certificate vouches for the identity provider, so disabling or deleting it, or no
    /// longer trusting it for sign-in, would stop everyone signing in.
    /// </summary>
    public bool SignInDependsOn(Guid trustedCertificateId) =>
        VouchingCertificates.Count == 1 && VouchingCertificates[0].TrustedCertificateId == trustedCertificateId;

    /// <summary>
    /// Whether a store certificate vouches for the identity provider now.
    /// </summary>
    public bool IsUsedBySignIn(Guid trustedCertificateId) =>
        VouchingCertificates.Any(c => c.TrustedCertificateId == trustedCertificateId);

    /// <summary>
    /// Whether sign-in depends on store certificates that all expire within <see cref="ExpiringSoonDays"/> days.
    /// </summary>
    public bool TrustExpiresSoon(DateTime utcNow) =>
        VouchingCertificates.Count > 0 && VouchingCertificates.Max(c => c.ValidTo) < utcNow.AddDays(ExpiringSoonDays);
}

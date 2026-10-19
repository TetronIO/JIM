// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.ComponentModel.DataAnnotations;
using JIM.Models.Core;
using JIM.Models.Security;

namespace JIM.Web.Models.Api;

/// <summary>
/// Whether JIM trusts its identity provider's TLS certificate, by what, and the chain the identity provider presented.
/// </summary>
public class IdentityProviderTrustStatusDto
{
    /// <summary>
    /// Whether JIM trusts the identity provider's certificate. <c>Untrusted</c> is fixed by trusting its certificate
    /// authority for sign-in; <c>NameMismatch</c> (the certificate does not name the host) is not.
    /// </summary>
    public IdentityProviderTrustState State { get; set; }

    /// <summary>
    /// The host JIM connects to for sign-in.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    /// What trusts the certificate: the operating system, or the JIM certificate store. Null unless the state is
    /// <c>Trusted</c>.
    /// </summary>
    public CertificateStoreTrustOutcome? TrustedBy { get; set; }

    /// <summary>
    /// A sentence explaining the state.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// When JIM last checked.
    /// </summary>
    public DateTime CheckedUtc { get; set; }

    /// <summary>
    /// The chain as presented, the identity provider's own certificate first.
    /// </summary>
    public List<IdentityProviderCertificateDto> PresentedChain { get; set; } = [];

    /// <summary>
    /// The certificate authority that issued the top of the presented chain, where the identity provider did not send
    /// it. That is usually the root, which outlasts every other certificate in the chain, so it is the best one to
    /// trust; fetch it from your certificate authority and add it with <c>POST /api/v1/certificates/upload</c>.
    /// </summary>
    public string? UnsentIssuer { get; set; }

    /// <summary>
    /// The thumbprint of the highest certificate the identity provider presented: the one to name when trusting a
    /// certificate it presents.
    /// </summary>
    public string? SuggestedThumbprint { get; set; }

    /// <summary>
    /// The certificates in the JIM certificate store that vouch for the identity provider now. Returned to
    /// administrators only; null otherwise.
    /// </summary>
    public List<IdentityProviderCertificateDto>? VouchingCertificates { get; set; }

    /// <summary>
    /// Whether every certificate that vouches for the identity provider expires within 30 days. Returned to
    /// administrators only; null otherwise.
    /// </summary>
    public bool? TrustExpiresSoon { get; set; }

    /// <param name="status">The status.</param>
    /// <param name="includeStoreDetail">Whether to include what the JIM certificate store holds, which only administrators see.</param>
    public static IdentityProviderTrustStatusDto FromStatus(IdentityProviderTrustStatus status, bool includeStoreDetail) => new()
    {
        State = status.State,
        Host = status.Host,
        TrustedBy = status.TrustedBy,
        Message = status.Message,
        CheckedUtc = status.CheckedUtc,
        PresentedChain = status.PresentedChain.Select(c => IdentityProviderCertificateDto.FromCertificate(c, includeStoreDetail)).ToList(),
        UnsentIssuer = status.UnsentIssuer,
        SuggestedThumbprint = status.SuggestedCertificate?.Thumbprint,
        VouchingCertificates = includeStoreDetail
            ? status.VouchingCertificates.Select(c => IdentityProviderCertificateDto.FromCertificate(c, includeStoreDetail)).ToList()
            : null,
        TrustExpiresSoon = includeStoreDetail ? status.TrustExpiresSoon(DateTime.UtcNow) : null
    };
}

/// <summary>
/// A certificate in the chain the identity provider presented.
/// </summary>
public class IdentityProviderCertificateDto
{
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// The subject's common name: what to call the certificate.
    /// </summary>
    public string CommonName { get; set; } = string.Empty;

    public string Issuer { get; set; } = string.Empty;

    public string Thumbprint { get; set; } = string.Empty;

    public DateTime ValidFrom { get; set; }

    public DateTime ValidTo { get; set; }

    /// <summary>
    /// Whether this is the identity provider's own certificate rather than a certificate authority above it.
    /// </summary>
    public bool IsServerCertificate { get; set; }

    /// <summary>
    /// The id of the certificate in the JIM certificate store with this thumbprint, if any. Returned to administrators
    /// only.
    /// </summary>
    public Guid? TrustedCertificateId { get; set; }

    /// <summary>
    /// What that stored certificate is trusted for. Returned to administrators only; null when it is not in the store.
    /// </summary>
    public CertificateTrustPurpose? TrustedFor { get; set; }

    public static IdentityProviderCertificateDto FromCertificate(IdentityProviderCertificate certificate, bool includeStoreDetail) => new()
    {
        Subject = certificate.Subject,
        CommonName = certificate.CommonName,
        Issuer = certificate.Issuer,
        Thumbprint = certificate.Thumbprint,
        ValidFrom = certificate.ValidFrom,
        ValidTo = certificate.ValidTo,
        IsServerCertificate = certificate.IsServerCertificate,
        TrustedCertificateId = includeStoreDetail ? certificate.TrustedCertificateId : null,
        TrustedFor = includeStoreDetail ? certificate.TrustedFor : null
    };
}

/// <summary>
/// Request to trust a certificate the identity provider presents.
/// </summary>
public class TrustIdentityProviderCertificateRequest
{
    /// <summary>
    /// The thumbprint of the certificate to trust, as the identity provider's status shows it. Spaces and colons are
    /// ignored. It must be a certificate the identity provider presents now.
    /// </summary>
    [Required]
    [StringLength(200)]
    public string Thumbprint { get; set; } = string.Empty;

    /// <summary>
    /// What to call it in the JIM certificate store. Its common name when omitted.
    /// </summary>
    [StringLength(255)]
    public string? Name { get; set; }

    /// <summary>
    /// Why, recorded on the audit Activity. A sentence naming the identity provider when omitted.
    /// </summary>
    [StringLength(1000)]
    public string? ChangeReason { get; set; }
}

/// <summary>
/// The outcome of trusting a certificate the identity provider presents.
/// </summary>
public class TrustIdentityProviderCertificateResponse
{
    /// <summary>
    /// What happened. Only <c>Trusted</c> and <c>TrustedForSignInAsWell</c> change anything.
    /// </summary>
    public IdentityProviderCertificateTrustOutcome Outcome { get; set; }

    /// <summary>
    /// A sentence explaining the outcome.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// The certificate as it now sits in the JIM certificate store, where it is there.
    /// </summary>
    public TrustedCertificateDetailDto? Certificate { get; set; }

    /// <summary>
    /// The identity provider's status after the request: <c>Trusted</c> once a certificate that vouches for it is trusted.
    /// </summary>
    public IdentityProviderTrustStatusDto? Status { get; set; }
}

// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Security;

/// <summary>
/// The result of trusting a certificate the identity provider presented.
/// </summary>
public class IdentityProviderCertificateTrustResult
{
    public IdentityProviderCertificateTrustOutcome Outcome { get; init; }

    /// <summary>
    /// The certificate as it now sits in the JIM certificate store, where it is there.
    /// </summary>
    public TrustedCertificate? Certificate { get; init; }

    /// <summary>
    /// A sentence explaining the outcome, suitable for showing.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// The identity provider's status as checked for this request, before anything was trusted.
    /// </summary>
    public IdentityProviderTrustStatus? Status { get; init; }
}
